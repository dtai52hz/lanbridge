package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import java.io.File;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.nio.file.Files;
import java.nio.file.StandardOpenOption;
import java.security.MessageDigest;
import java.security.NoSuchAlgorithmException;
import java.util.Arrays;
import java.util.Base64;
import java.util.Locale;
import java.util.UUID;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.SynchronousQueue;

/** Manual file transfer using the existing desktop protocol. No storage is touched without acceptance. */
public final class FileTransferClient implements AutoCloseable {
    public static final long MAX_FILE_SIZE = 2L * 1024 * 1024 * 1024;
    public static final int CHUNK_SIZE = 48 * 1024;
    public interface Source {
        String name();
        long size();
        /** Called exactly once on the outgoing worker after the computer accepts. */
        InputStream open() throws IOException;
    }
    public interface Destination {
        /** Create a new private temporary destination; never overwrite an existing document. */
        OutputStream open() throws IOException;
        /** Publish only after size and SHA-256 validation. */
        void finish() throws IOException;
        /** Roll back partial/just-finished output if cancellation wins before onCompleted. */
        void abort();
    }
    public interface Listener {
        default void onOffer(Offer offer) { offer.reject(); }
        default void onProgress(String id, String name, long done, long total, boolean incoming) { }
        default void onCompleted(String id, String name, boolean incoming) { }
        default void onError(String id, String name, boolean incoming, String message) { }
    }
    interface Transport { CompletableFuture<Void> send(JSONObject packet, boolean bulk); }
    private final Object gate = new Object();
    private final Transport transport;
    private volatile Listener listener = new Listener() { };
    // Legacy PCs cannot negotiate per-chunk ACKs. Buffer at most 3 MiB of disk work;
    // sustained overload cancels this file instead of blocking the TLS/input reader.
    private final ThreadPoolExecutor receiver = executor("LanBridge-file-receive", 64);
    // One active transfer, plus one bounded replacement if a cancelled document provider
    // ignores both stream close and CancellationSignal. Never queue behind a stuck provider.
    private final ThreadPoolExecutor sender = new ThreadPoolExecutor(2, 2, 30, TimeUnit.SECONDS,
            new SynchronousQueue<>(), r -> thread(r, "LanBridge-file-send"), new ThreadPoolExecutor.AbortPolicy());
    private final ThreadPoolExecutor cleanup = executor("LanBridge-file-cleanup", 32);
    private final ScheduledExecutorService watchdog = Executors.newSingleThreadScheduledExecutor(r -> thread(r, "LanBridge-file-watch"));
    private boolean remoteAllowed, receiveAllowed, closed;
    private Outgoing outgoing;
    private Incoming incoming;

    FileTransferClient(Transport transport) {
        this.transport = transport;
        watchdog.scheduleAtFixedRate(this::expire, 1, 1, TimeUnit.SECONDS);
    }
    public void setListener(Listener listener) { this.listener = listener == null ? new Listener() { } : listener; }
    public boolean canSend() { synchronized (gate) { return !closed && remoteAllowed && outgoing == null && sender.getActiveCount() < 2 && cleanup.getQueue().remainingCapacity() >= 2; } }
    public boolean isReceiveAllowed() { synchronized (gate) { return !closed && receiveAllowed; } }
    public void setReceiveAllowed(boolean allowed) {
        Incoming cancel = null;
        synchronized (gate) {
            if (closed || receiveAllowed == allowed) return;
            receiveAllowed = allowed;
            if (!allowed) { cancel = incoming; incoming = null; if (cancel != null) cancel.cancelled = true; }
        }
        if (cancel != null) stopIncoming(cancel, "Đã tắt nhận file trên điện thoại.", true);
    }
    void setRemoteAllowed(boolean allowed) {
        synchronized (gate) { if (closed) return; remoteAllowed = allowed; }
        if (!allowed) cancelWithReason("Máy tính đã thu hồi quyền truyền file.", true);
    }

    public boolean send(Source source) {
        if (source == null) throw new IllegalArgumentException("Chưa chọn file.");
        String name = safeFileName(source.name()); long size = source.size();
        if (size < 0 || size > MAX_FILE_SIZE) throw new IllegalArgumentException("File phải biết kích thước và không vượt quá 2 GiB.");
        Outgoing transfer = new Outgoing(UUID.randomUUID().toString().replace("-", ""), name, size, source);
        synchronized (gate) {
            if (closed || !remoteAllowed || outgoing != null || cleanup.getQueue().remainingCapacity() < 2) return false;
            outgoing = transfer;
        }
        try { sender.execute(() -> sendFile(transfer)); return true; }
        catch (RuntimeException error) { stopOutgoing(transfer, "Nhà cung cấp tệp chưa nhả lượt gửi trước. Hãy thử lại khi tệp đã đóng hoặc kết nối lại.", false); return false; }
    }
    private void sendFile(Outgoing transfer) {
        try {
            JSONObject offer = packet("file-offer", transfer.id); Wire.put(offer, "name", transfer.name); Wire.put(offer, "length", transfer.size);
            sendAndWait(transfer, offer);
            if (!transfer.accepted.get(20, TimeUnit.SECONDS)) throw new IOException("Máy tính đã từ chối nhận file.");
            ensureOutgoing(transfer);
            InputStream opened = transfer.source.open();
            if (opened == null) throw new IOException("Không mở được file đã chọn.");
            transfer.input = opened;
            ensureOutgoing(transfer);
            MessageDigest hash = sha256(); byte[] buffer = new byte[CHUNK_SIZE]; long written = 0;
            outgoingProgress(transfer, 0);
            while (written < transfer.size) {
                ensureOutgoing(transfer);
                int count = opened.read(buffer, 0, (int) Math.min(buffer.length, transfer.size - written));
                if (count < 0) throw new IOException("File nguồn đã ngắn hơn kích thước đã gửi.");
                if (count == 0) { int value = opened.read(); if (value < 0) throw new IOException("File nguồn chưa đủ dữ liệu."); buffer[0] = (byte) value; count = 1; }
                hash.update(buffer, 0, count); written += count;
                JSONObject chunk = packet("file-chunk", transfer.id);
                Wire.put(chunk, "data", Base64.getEncoder().encodeToString(Arrays.copyOf(buffer, count)));
                // Manual transfer in existing PC releases does not negotiate chunk ACKs.
                // Await the actual TLS write before reading/retaining another chunk.
                Wire.put(chunk, "length", 0); sendAndWait(transfer, chunk);
                outgoingProgress(transfer, written);
            }
            if (opened.read() != -1) throw new IOException("File nguồn đã tăng kích thước trong lúc gửi.");
            ensureOutgoing(transfer);
            JSONObject end = packet("file-end", transfer.id); Wire.put(end, "text", hex(hash.digest())); transfer.awaitingReceipt = true; sendAndWait(transfer, end);
            transfer.receipt.get(30, TimeUnit.SECONDS);
            synchronized (gate) {
                if (closed || transfer.cancelled || outgoing != transfer || !remoteAllowed) throw new IOException("Đã hủy gửi file.");
                outgoing = null; transfer.completed = true;
            }
            safely(() -> listener.onCompleted(transfer.id, transfer.name, false));
        } catch (Exception error) { stopOutgoing(transfer, reason(error, "Không thể gửi file."), true); }
        finally { closeInput(transfer); synchronized (gate) { if (outgoing == transfer && !transfer.cancelled) outgoing = null; } }
    }
    private void sendAndWait(Outgoing transfer, JSONObject packet) throws Exception {
        ensureOutgoing(transfer);
        CompletableFuture<Void> write = transport.send(packet, true); transfer.pendingWrite = write;
        try { ensureOutgoing(transfer); write.get(30, TimeUnit.SECONDS); }
        catch (Exception error) { write.cancel(false); throw error; }
        finally { if (transfer.pendingWrite == write) transfer.pendingWrite = null; }
    }
    private void ensureOutgoing(Outgoing transfer) throws IOException {
        synchronized (gate) {
            if (closed || !remoteAllowed || transfer.cancelled || outgoing != transfer) throw new IOException("Đã hủy hoặc thu hồi quyền gửi file.");
        }
    }

    /** Called by the TLS reader; all disk operations and offer observers run elsewhere. */
    void handle(JSONObject packet) {
        String type = packet.optString("type"), id = packet.optString("id");
        if (!id.matches("[a-fA-F0-9]{32}")) return;
        if ("file-accept".equals(type) || "file-received".equals(type) || "file-cancel".equals(type)) {
            Outgoing send; Incoming receive;
            synchronized (gate) { if (closed) return; send = outgoing; receive = incoming; }
            if (send != null && send.id.equals(id)) {
                if ("file-accept".equals(type)) send.accepted.complete(Boolean.TRUE.equals(packet.opt("allow")));
                else if ("file-received".equals(type) && send.awaitingReceipt) send.receipt.complete(null);
                else stopOutgoing(send, "Máy tính đã hủy truyền file.", false);
            }
            if ("file-cancel".equals(type) && receive != null && receive.id.equals(id)) cancelIncoming(receive, "Máy tính đã hủy truyền file.", false);
            return;
        }
        if ("file-offer".equals(type)) { incomingOffer(packet, id); return; }
        Incoming transfer;
        synchronized (gate) { transfer = incoming; if (closed || transfer == null || !transfer.id.equals(id) || transfer.cancelled) return; }
        if (!"file-chunk".equals(type) && !"file-end".equals(type)) return;
        if (!transfer.accepted) { cancelIncoming(transfer, "Máy tính gửi dữ liệu trước khi điện thoại chấp nhận.", true); return; }
        transfer.lastActivity = Wire.now();
        try {
            if ("file-chunk".equals(type)) {
                Object encoded = packet.opt("data");
                if (!(encoded instanceof String) || ((String) encoded).length() < 4 || ((String) encoded).length() > 65536)
                    throw new IOException("Khối file vượt giới hạn 48 KiB.");
                byte[] data = Base64.getDecoder().decode((String) encoded);
                if (data.length < 1 || data.length > CHUNK_SIZE) throw new IOException("Khối file không hợp lệ.");
                long offset = packet.optLong("length", 0);
                enqueueIncoming(transfer, () -> writeChunk(transfer, data, offset));
            } else {
                String digest = packet.optString("text");
                if (!Wire.validHex256(digest)) throw new IOException("Mã SHA-256 của file không hợp lệ.");
                enqueueIncoming(transfer, () -> finishIncoming(transfer, digest));
            }
        } catch (Exception error) { cancelIncoming(transfer, reason(error, "Dữ liệu file không hợp lệ."), true); }
    }
    private void incomingOffer(JSONObject packet, String id) {
        Incoming transfer;
        try {
            String name = safeFileName(packet.optString("name")); Object length = packet.opt("length");
            if (!(length instanceof Integer) && !(length instanceof Long)) throw new IOException("Kích thước file không hợp lệ.");
            long size = ((Number) length).longValue();
            if (size < 0 || size > MAX_FILE_SIZE) throw new IOException("File vượt giới hạn 2 GiB.");
            synchronized (gate) {
                if (closed || !remoteAllowed || !receiveAllowed || incoming != null || cleanup.getQueue().remainingCapacity() < 4) transfer = null;
                else { transfer = new Incoming(id, name, size); incoming = transfer; }
            }
            if (transfer == null) { refuse(id); return; }
            enqueueIncoming(transfer, () -> {
                if (activeIncoming(transfer)) safely(() -> listener.onOffer(new Offer(transfer)));
            });
        } catch (Exception error) { refuse(id); }
    }
    public final class Offer {
        public final String id, name;
        public final long size;
        private final Incoming transfer;
        private Offer(Incoming transfer) { this.transfer = transfer; id = transfer.id; name = transfer.name; size = transfer.size; }
        /** The UI should accept into private staging; export to a user document only after completion. */
        public boolean accept(Destination destination) {
            if (destination == null) throw new IllegalArgumentException("Chưa chọn nơi nhận file.");
            synchronized (gate) {
                if (!activeIncomingLocked(transfer) || transfer.decided) return false;
                transfer.decided = true; transfer.destination = destination;
            }
            enqueueIncoming(transfer, () -> openIncoming(transfer)); return true;
        }
        public void reject() { cancelIncoming(transfer, "Đã từ chối nhận file.", true); }
    }
    private void openIncoming(Incoming transfer) {
        try {
            if (!activeIncoming(transfer)) return;
            if (transfer.destination instanceof StagingDestination) {
                StagingDestination staging = (StagingDestination) transfer.destination;
                staging.prepare(transfer.size);
            }
            OutputStream output = transfer.destination.open();
            if (output == null) throw new IOException("Không mở được nơi nhận file.");
            transfer.output = output; transfer.hash = sha256();
            if (!activeIncoming(transfer)) { stopIncoming(transfer, "Đã hủy nhận file.", true); return; }
            transfer.accepted = true; transfer.lastActivity = Wire.now();
            JSONObject response = packet("file-accept", transfer.id); Wire.put(response, "allow", true); respond(response);
            incomingProgress(transfer, 0);
        } catch (Exception error) { cancelIncoming(transfer, reason(error, "Không thể tạo file nhận."), true); }
    }
    private void writeChunk(Incoming transfer, byte[] bytes, long offset) {
        try {
            if (!activeIncoming(transfer)) return;
            if (transfer.written + bytes.length > transfer.size || offset != 0 && offset != transfer.written + bytes.length)
                throw new IOException("Kích thước hoặc thứ tự khối file không hợp lệ.");
            transfer.output.write(bytes); transfer.hash.update(bytes); transfer.written += bytes.length;
            if (!activeIncoming(transfer)) return;
            if (offset > 0) { JSONObject ack = packet("file-chunk-received", transfer.id); Wire.put(ack, "length", transfer.written); Wire.put(ack, "allow", true); respond(ack); }
            incomingProgress(transfer, transfer.written);
        } catch (Exception error) { cancelIncoming(transfer, reason(error, "Không thể ghi file nhận."), true); }
    }
    private void finishIncoming(Incoming transfer, String digest) {
        try {
            if (!activeIncoming(transfer)) return;
            if (transfer.written != transfer.size || !MessageDigest.isEqual(transfer.hash.digest(), Wire.hexBytes(digest)))
                throw new IOException("File nhận chưa đủ hoặc sai SHA-256.");
            transfer.output.close(); transfer.output = null;
            if (!activeIncoming(transfer)) return;
            transfer.destination.finish();
            boolean committed;
            synchronized (gate) {
                committed = activeIncomingLocked(transfer);
                if (committed) { incoming = null; transfer.completed = true; }
            }
            if (!committed) { cleanupIncoming(transfer); return; }
            JSONObject receipt = packet("file-received", transfer.id); Wire.put(receipt, "name", transfer.name); respond(receipt);
            safely(() -> listener.onCompleted(transfer.id, transfer.name, true));
        } catch (Exception error) { cancelIncoming(transfer, reason(error, "Không thể xác minh file nhận."), true); }
    }
    private boolean activeIncoming(Incoming transfer) { synchronized (gate) { return activeIncomingLocked(transfer); } }
    private boolean activeIncomingLocked(Incoming transfer) { return !closed && remoteAllowed && receiveAllowed && incoming == transfer && !transfer.cancelled; }
    private void enqueueIncoming(Incoming transfer, Runnable work) {
        try { receiver.execute(new ReceiveWork(transfer, work)); }
        catch (RuntimeException error) { cancelIncoming(transfer, "Bộ nhớ đệm file đã đầy; đã dừng file để giữ chuột và phím hoạt động.", true); }
    }
    private void respond(JSONObject packet) {
        transport.send(packet, false).whenComplete((nothing, error) -> { });
    }
    private void refuse(String id) { JSONObject response = packet("file-accept", id); Wire.put(response, "allow", false); respond(response); }

    public void cancel() { cancelWithReason("Đã hủy truyền file.", true); }
    public void cancel(String id) {
        Outgoing send; Incoming receive;
        synchronized (gate) { send = outgoing; receive = incoming; }
        if (send != null && send.id.equals(id)) stopOutgoing(send, "Đã hủy gửi file.", true);
        if (receive != null && receive.id.equals(id)) cancelIncoming(receive, "Đã hủy nhận file.", true);
    }
    private void cancelWithReason(String reason, boolean notifyPeer) {
        Outgoing send; Incoming receive;
        synchronized (gate) { send = outgoing; receive = incoming; }
        if (send != null) stopOutgoing(send, reason, notifyPeer);
        if (receive != null) cancelIncoming(receive, reason, notifyPeer);
    }
    private void stopOutgoing(Outgoing transfer, String reason, boolean notifyPeer) {
        synchronized (gate) {
            if (transfer.cancelled || transfer.completed) return;
            transfer.cancelled = true;
        }
        IOException error = new IOException(reason); transfer.accepted.completeExceptionally(error); transfer.receipt.completeExceptionally(error);
        CompletableFuture<Void> pending = transfer.pendingWrite; if (pending != null) pending.cancel(false);
        executeCleanup(() -> closeInput(transfer));
        if (notifyPeer) sendCancel(transfer.id, reason);
        // Release only after the old cancellation is queued, so a replacement offer cannot
        // overtake it at the receiving PC. A stale worker's finally never clears a new slot.
        synchronized (gate) { if (outgoing == transfer) outgoing = null; }
        safely(() -> listener.onError(transfer.id, transfer.name, false, reason));
    }
    private void cancelIncoming(Incoming transfer, String reason, boolean notifyPeer) {
        synchronized (gate) {
            if (transfer.cancelled || transfer.completed) return;
            transfer.cancelled = true; if (incoming == transfer) incoming = null;
        }
        stopIncoming(transfer, reason, notifyPeer);
    }
    private void stopIncoming(Incoming transfer, String reason, boolean notifyPeer) {
        boolean report;
        synchronized (gate) { report = !transfer.errorNotified; transfer.errorNotified = true; }
        receiver.getQueue().removeIf(work -> work instanceof ReceiveWork && ((ReceiveWork) work).transfer == transfer);
        executeCleanup(() -> cleanupIncoming(transfer));
        if (!report) return;
        if (notifyPeer) {
            if (!transfer.accepted) refuse(transfer.id); else sendCancel(transfer.id, reason);
        }
        safely(() -> listener.onError(transfer.id, transfer.name, true, reason));
    }
    private void sendCancel(String id, String reason) { JSONObject cancel = packet("file-cancel", id); Wire.put(cancel, "text", reason); respond(cancel); }
    private void expire() {
        Incoming receive;
        synchronized (gate) { if (closed) return; receive = incoming; }
        if (receive != null && Wire.now() - receive.lastActivity > (receive.accepted ? 30000 : 18000))
            cancelIncoming(receive, receive.accepted ? "Quá thời gian chờ dữ liệu file." : "Hết thời gian chấp nhận file.", true);
    }
    @Override public void close() {
        synchronized (gate) { if (closed) return; closed = true; remoteAllowed = false; receiveAllowed = false; }
        cancelWithReason("Phiên đã đóng; đã hủy truyền file.", false);
        watchdog.shutdownNow(); sender.shutdown(); receiver.shutdown(); cleanup.shutdown();
    }
    private void executeCleanup(Runnable work) {
        try { cleanup.execute(work); }
        catch (RuntimeException error) {
            // Only a disk worker can publish a late-opened stream after close/shutdown.
            // Finish its cleanup in that same worker; never create an unbounded thread fan-out.
            if (Thread.currentThread().getName().startsWith("LanBridge-file-")) work.run();
        }
    }
    private static void cleanupIncoming(Incoming transfer) {
        OutputStream output = transfer.output; transfer.output = null;
        if (output != null) try { output.close(); } catch (IOException ignored) { }
        if (transfer.destination != null) try { transfer.destination.abort(); } catch (RuntimeException ignored) { }
    }
    private static void closeInput(Outgoing transfer) {
        InputStream input = transfer.input; transfer.input = null;
        if (input != null) try { input.close(); } catch (IOException ignored) { }
    }
    private void notifyProgress(String id, String name, long done, long total, boolean incoming) { safely(() -> listener.onProgress(id, name, done, total, incoming)); }
    private void outgoingProgress(Outgoing transfer, long done) {
        long now = Wire.now();
        if (done == 0 || done == transfer.size || transfer.lastProgress == 0 || now - transfer.lastProgress >= 100) {
            if (done > 0) transfer.lastProgress = now;
            notifyProgress(transfer.id, transfer.name, done, transfer.size, false);
        }
    }
    private void incomingProgress(Incoming transfer, long done) {
        long now = Wire.now();
        if (done == 0 || done == transfer.size || transfer.lastProgress == 0 || now - transfer.lastProgress >= 100) {
            if (done > 0) transfer.lastProgress = now;
            notifyProgress(transfer.id, transfer.name, done, transfer.size, true);
        }
    }
    private static JSONObject packet(String type, String id) { JSONObject packet = Wire.packet(type); Wire.put(packet, "id", id); return packet; }
    private static MessageDigest sha256() {
        try { return MessageDigest.getInstance("SHA-256"); } catch (NoSuchAlgorithmException error) { throw new IllegalStateException(error); }
    }
    private static String hex(byte[] bytes) { StringBuilder result = new StringBuilder(bytes.length * 2); for (byte value : bytes) result.append(String.format(Locale.ROOT, "%02X", value & 255)); return result.toString(); }
    private static String reason(Exception error, String fallback) {
        Throwable cause = error;
        while (cause.getCause() != null) cause = cause.getCause();
        return cause.getMessage() == null ? fallback : cause.getMessage();
    }
    private static void safely(Runnable callback) { try { callback.run(); } catch (RuntimeException ignored) { } }
    private static Thread thread(Runnable action, String name) { Thread thread = new Thread(action, name); thread.setDaemon(true); return thread; }
    private static ThreadPoolExecutor executor(String name, int capacity) {
        return new ThreadPoolExecutor(1, 1, 30, TimeUnit.SECONDS, new ArrayBlockingQueue<>(capacity), r -> thread(r, name), new ThreadPoolExecutor.AbortPolicy());
    }
    private static final class Outgoing {
        final String id, name; final long size; final Source source;
        final CompletableFuture<Boolean> accepted = new CompletableFuture<>();
        final CompletableFuture<Void> receipt = new CompletableFuture<>();
        volatile boolean cancelled, completed, awaitingReceipt; long lastProgress; volatile InputStream input; volatile CompletableFuture<Void> pendingWrite;
        Outgoing(String id, String name, long size, Source source) { this.id=id; this.name=name; this.size=size; this.source=source; }
    }
    private static final class Incoming {
        final String id, name; final long size;
        volatile boolean cancelled, completed, decided, accepted, errorNotified;
        volatile long lastActivity = Wire.now(); long written, lastProgress;
        volatile Destination destination; volatile OutputStream output; MessageDigest hash;
        Incoming(String id, String name, long size) { this.id=id; this.name=name; this.size=size; }
    }
    private static final class ReceiveWork implements Runnable {
        final Incoming transfer; final Runnable work;
        ReceiveWork(Incoming transfer, Runnable work) { this.transfer = transfer; this.work = work; }
        public void run() { work.run(); }
    }

    public static String safeFileName(String name) {
        if (name == null || name.trim().isEmpty() || name.length() > 160 || !Wire.validUnicode(name)
                || name.endsWith(".") || name.endsWith(" "))
            throw new IllegalArgumentException("Tên file không hợp lệ.");
        for (int i=0; i<name.length(); i++) if (name.charAt(i) < 32 || "\\/:*?\"<>|".indexOf(name.charAt(i)) >= 0)
            throw new IllegalArgumentException("Tên file có ký tự không hợp lệ.");
        String stem = name.split("\\.", -1)[0].replaceFirst(" +$", "").toUpperCase(Locale.ROOT);
        if (stem.matches("CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|(COM|LPT)[1-9¹²³]")) throw new IllegalArgumentException("Tên file dành riêng của Windows.");
        return name;
    }
    public static StagingDestination staging(File directory, String name) throws IOException {
        return new StagingDestination(directory, safeFileName(name));
    }
    /** Private app directory only. The received name never chooses a directory or overwrites a file. */
    public static final class StagingDestination implements Destination {
        private final File directory, partial, destination;
        private OutputStream stream; private boolean aborted, finished;
        private StagingDestination(File directory, String name) throws IOException {
            this.directory = directory.getCanonicalFile();
            String unique = UUID.randomUUID().toString();
            // Display name remains metadata. A short generated path also supports valid 160-char
            // wire names whose UTF-8 encoding exceeds Android/ext4's component byte limit.
            partial = new File(this.directory, unique + ".partial"); destination = new File(this.directory, unique + ".received");
        }
        public synchronized File file() { return finished && !aborted ? destination : null; }
        public synchronized long usableSpace() { return directory.getUsableSpace(); }
        private synchronized void prepare(long size) throws IOException {
            Files.createDirectories(directory.toPath());
            if (directory.getUsableSpace() < size + 16L * 1024 * 1024) throw new IOException("Không đủ dung lượng nhận file.");
        }
        @Override public synchronized OutputStream open() throws IOException {
            if (aborted || finished || stream != null) throw new IOException("Nơi nhận file đã đóng.");
            Files.createDirectories(directory.toPath()); checkContained(partial); checkContained(destination);
            stream = Files.newOutputStream(partial.toPath(), StandardOpenOption.CREATE_NEW, StandardOpenOption.WRITE); return stream;
        }
        @Override public synchronized void finish() throws IOException {
            if (aborted || finished) throw new IOException("File đã bị hủy hoặc đóng.");
            if (stream != null) { stream.close(); stream = null; }
            checkContained(partial); checkContained(destination);
            Files.move(partial.toPath(), destination.toPath()); finished = true;
        }
        @Override public synchronized void abort() {
            aborted = true;
            if (stream != null) { try { stream.close(); } catch (IOException ignored) { } stream = null; }
            try { Files.deleteIfExists(partial.toPath()); } catch (IOException ignored) { }
            if (finished) try { Files.deleteIfExists(destination.toPath()); } catch (IOException ignored) { }
        }
        private void checkContained(File file) throws IOException {
            if (!file.getCanonicalFile().getParentFile().equals(directory) || !directory.getCanonicalFile().equals(directory))
                throw new IOException("Nơi nhận file nằm ngoài thư mục đã chọn.");
        }
    }
}
