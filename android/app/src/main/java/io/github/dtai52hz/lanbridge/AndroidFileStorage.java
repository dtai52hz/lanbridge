package io.github.dtai52hz.lanbridge;

import android.content.ContentResolver;
import android.database.Cursor;
import android.net.Uri;
import android.provider.OpenableColumns;
import android.util.AtomicFile;
import android.os.CancellationSignal;
import android.content.res.AssetFileDescriptor;
import org.json.JSONArray;
import org.json.JSONObject;
import java.io.File;
import java.io.FileInputStream;
import java.io.FileNotFoundException;
import java.io.FileOutputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.io.Closeable;
import java.io.FilterInputStream;
import java.io.FilterOutputStream;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.List;
import java.util.Map;
import java.util.HashMap;
import java.util.UUID;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.ArrayBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.function.BooleanSupplier;
import io.github.dtai52hz.lanbridge.net.FileTransferClient;

/** Private verified receipts and Storage Access Framework streams; no broad storage permission. */
final class AndroidFileStorage {
    static final long MAX_FILE = 2L * 1024 * 1024 * 1024, SPACE_RESERVE = 16L * 1024 * 1024;
    static final int MAX_RECEIPTS = 8;
    private static final Object INDEX_GATE = new Object();
    private static final Map<String, SharedReceipts> INDICES = new HashMap<>();
    private static final class SharedReceipts { volatile List<Receipt> entries = Collections.emptyList(); }
    private static final Map<String, DocumentTarget> TARGETS = new HashMap<>();
    static final class DocumentTarget implements AutoCloseable {
        private final String key;
        private DocumentTarget(String key) { this.key = key; }
        static DocumentTarget acquire(Uri uri) {
            if (!isDocumentUri(uri)) return null;
            synchronized (TARGETS) {
                String key = uri.normalizeScheme().toString();
                if (TARGETS.containsKey(key) || TARGETS.size() >= 8) return null;
                DocumentTarget target = new DocumentTarget(key); TARGETS.put(key, target); return target;
            }
        }
        boolean owns() { synchronized (TARGETS) { return TARGETS.get(key) == this; } }
        public void close() { synchronized (TARGETS) { if (TARGETS.get(key) == this) TARGETS.remove(key); } }
    }
    interface Progress { void update(long bytes); }
    static final class DocumentOperation {
        private static final ThreadPoolExecutor CLEANUP = new ThreadPoolExecutor(2, 2, 15, TimeUnit.SECONDS,
            new ArrayBlockingQueue<>(8), task -> { Thread thread = new Thread(task, "LanBridge-document-cleanup"); thread.setDaemon(true); return thread; });
        final CancellationSignal signal = new CancellationSignal();
        final AtomicBoolean cancelled;
        final AtomicBoolean cleanupRequested = new AtomicBoolean();
        private final List<Closeable> resources = new ArrayList<>();
        DocumentOperation() { this(new AtomicBoolean()); }
        DocumentOperation(AtomicBoolean cancelled) { this.cancelled = cancelled; }
        boolean isCancelled() { return cancelled.get(); }
        void check() throws IOException { checkCancelled(cancelled); }
        void cancel() {
            cancelled.set(true);
            if (!cleanupRequested.compareAndSet(false, true)) return;
            cleanup(() -> { try { signal.cancel(); } catch (RuntimeException ignored) { } });
            cleanup(() -> { List<Closeable> active; synchronized (resources) { active = new ArrayList<>(resources); resources.clear(); } for (Closeable resource : active) closeQuietly(resource); });
        }
        private static void cleanup(Runnable action) { try { CLEANUP.execute(action); } catch (java.util.concurrent.RejectedExecutionException ignored) { } }
        <T extends Closeable> T track(T resource) throws IOException {
            synchronized (resources) {
                if (!isCancelled()) { resources.add(resource); return resource; }
            }
            cleanup(() -> closeQuietly(resource)); throw new IOException("Đã hủy thao tác tệp.");
        }
        void untrack(Closeable resource) { synchronized (resources) { resources.remove(resource); } }
        InputStream input(ContentResolver resolver, Uri uri) throws IOException {
            check(); AssetFileDescriptor descriptor = resolver.openAssetFileDescriptor(uri, "r", signal);
            if (descriptor == null) throw new IOException("Không mở được tệp đã chọn.");
            track(descriptor); check();
            InputStream input = track(descriptor.createInputStream());
            return new FilterInputStream(input) { public void close() throws IOException { try { super.close(); } finally { untrack(input); untrack(descriptor); descriptor.close(); } } };
        }
        OutputStream output(ContentResolver resolver, Uri uri) throws IOException {
            check(); AssetFileDescriptor descriptor = resolver.openAssetFileDescriptor(uri, "wt", signal);
            if (descriptor == null) throw new IOException("Không mở được nơi lưu đã chọn.");
            track(descriptor); check();
            OutputStream output = track(descriptor.createOutputStream());
            return new FilterOutputStream(output) {
                public void write(byte[] bytes, int offset, int length) throws IOException { out.write(bytes, offset, length); }
                public void close() throws IOException { try { super.close(); } finally { untrack(output); untrack(descriptor); descriptor.close(); } }
            };
        }
        private static void closeQuietly(Closeable resource) { try { resource.close(); } catch (Exception ignored) { } }
    }
    static final class Receipt {
        final String id, name; final File file; final long size;
        Receipt(String id, String name, File file, long size) { this.id = id; this.name = name; this.file = file; this.size = size; }
        @Override public boolean equals(Object value) { return value instanceof Receipt && id.equals(((Receipt) value).id); }
        @Override public int hashCode() { return id.hashCode(); }
    }
    static final class PreparedSource implements FileTransferClient.Source, AutoCloseable {
        final String name; final long size; final ContentResolver resolver; final Uri uri; final File staged;
        final DocumentOperation operation;
        PreparedSource(String name, long size, ContentResolver resolver, Uri uri, File staged, DocumentOperation operation) {
            this.name = name; this.size = size; this.resolver = resolver; this.uri = uri; this.staged = staged;
            this.operation = operation;
        }
        public String name() { return name; }
        public long size() { return size; }
        public InputStream open() throws IOException {
            operation.check(); InputStream stream = staged != null ? operation.track(new FileInputStream(staged)) : operation.input(resolver, uri);
            return stream;
        }
        public void close() { operation.cancel(); if (staged != null) DocumentOperation.cleanup(() -> staged.delete()); }
    }
    private final File incoming, outgoing;
    private final AtomicFile index;
    private final SharedReceipts shared;
    /** The protocol calls finish only after SHA-256 passes; persistence precedes its success ACK. */
    final class ReceiptDestination implements FileTransferClient.Destination {
        private final String name;
        private final FileTransferClient.StagingDestination staging;
        private Receipt receipt;
        private volatile boolean aborted, published;
        ReceiptDestination(String name) throws IOException { this.name = name; staging = FileTransferClient.staging(incoming, name); }
        public synchronized OutputStream open() throws IOException {
            if (aborted) throw new IOException("Đã hủy nhận tệp.");
            return staging.open();
        }
        public synchronized void finish() throws IOException {
            if (aborted) throw new IOException("Đã hủy nhận tệp.");
            staging.finish();
            try { receipt = addVerified(name, staging.file()); published = true; }
            catch (IOException error) { staging.abort(); throw error; }
        }
        boolean isPending() { return !aborted && !published; }
        public synchronized void abort() {
            if (aborted) return;
            aborted = true; staging.abort();
            if (receipt != null) {
                synchronized (INDEX_GATE) {
                    List<Receipt> remaining = new ArrayList<>(shared.entries); remaining.remove(receipt);
                    try { save(remaining); } catch (IOException ignored) { /* Missing files are filtered on reload. */ }
                    shared.entries = Collections.unmodifiableList(remaining);
                }
            }
        }
    }
    AndroidFileStorage(File privateDirectory) throws IOException {
        incoming = new File(privateDirectory, "received").getCanonicalFile();
        outgoing = new File(privateDirectory, "outgoing").getCanonicalFile();
        if ((!incoming.isDirectory() && !incoming.mkdirs()) || (!outgoing.isDirectory() && !outgoing.mkdirs()))
            throw new IOException("Không tạo được nơi lưu tệp riêng của LanBridge.");
        index = new AtomicFile(new File(privateDirectory, "received-index.json"));
        synchronized (INDEX_GATE) {
            String key = index.getBaseFile().getCanonicalPath();
            SharedReceipts existing = INDICES.get(key);
            shared = existing == null ? new SharedReceipts() : existing;
            shared.entries = Collections.unmodifiableList(load()); INDICES.put(key, shared);
        }
    }
    File incomingDirectory() { return incoming; }
    ReceiptDestination destination(String name) throws IOException { return new ReceiptDestination(name); }
    List<Receipt> receipts() { return shared.entries; }
    void checkIncoming(long size) throws IOException {
        if (shared.entries.size() >= MAX_RECEIPTS) throw new IOException("Đã giữ 8 tệp đã nhận. Lưu rồi xóa một tệp trong danh sách trước khi nhận thêm.");
        if (size < 0 || size > MAX_FILE) throw new IOException("Mỗi tệp tối đa 2 GiB.");
        if (incoming.getUsableSpace() < size + SPACE_RESERVE) throw new IOException("Điện thoại không đủ chỗ trống để nhận tệp này.");
    }
    Receipt addVerified(String name, File file) throws IOException {
        synchronized (INDEX_GATE) {
            File checked = checkedReceipt(file.getName());
            if (!checked.equals(file.getCanonicalFile()) || !checked.isFile() || checked.getName().endsWith(".partial"))
                throw new IOException("Tệp chưa hoàn tất xác minh.");
            if (shared.entries.size() >= MAX_RECEIPTS) throw new IOException("Danh sách tệp đã nhận đã đầy.");
            Receipt receipt = new Receipt(UUID.randomUUID().toString(), portableName(name), checked, checked.length());
            List<Receipt> entries = new ArrayList<>(shared.entries); entries.add(0, receipt);
            save(entries); shared.entries = Collections.unmodifiableList(entries); return receipt;
        }
    }
    void delete(Receipt receipt) throws IOException {
        synchronized (INDEX_GATE) {
            if (!shared.entries.contains(receipt)) return;
            File checked = checkedReceipt(receipt.file.getName());
            if (checked.exists() && !checked.delete()) throw new IOException("Chưa xóa được tệp riêng trên điện thoại.");
            List<Receipt> entries = new ArrayList<>(shared.entries); entries.remove(receipt);
            try { save(entries); } finally { shared.entries = Collections.unmodifiableList(entries); }
        }
    }
    PreparedSource prepare(ContentResolver resolver, Uri uri, AtomicBoolean cancelled, Progress progress) throws IOException {
        return prepare(resolver, uri, new DocumentOperation(cancelled), progress);
    }
    PreparedSource prepare(ContentResolver resolver, Uri uri, DocumentOperation operation, Progress progress) throws IOException {
        requireDocumentUri(uri);
        String name = "tep-da-chon"; long size = -1;
        operation.check();
        try (Cursor cursor = resolver.query(uri, new String[] { OpenableColumns.DISPLAY_NAME, OpenableColumns.SIZE }, null, null, null, operation.signal)) {
            if (cursor != null) operation.track(cursor);
            if (cursor != null && cursor.moveToFirst()) {
                int nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME), sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE);
                if (nameIndex >= 0 && !cursor.isNull(nameIndex)) name = portableName(cursor.getString(nameIndex));
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex);
            }
        } catch (RuntimeException error) { throw new IOException("Không đọc được thông tin tệp. Hãy chọn lại tệp trên điện thoại.", error); }
        operation.check();
        if (size > MAX_FILE) throw new IOException("Mỗi tệp tối đa 2 GiB.");
        if (size >= 0) return new PreparedSource(name, size, resolver, uri, null, operation);
        File temporary = File.createTempFile("source-", ".partial", outgoing);
        boolean complete = false;
        try (InputStream input = operation.input(resolver, uri); OutputStream output = new FileOutputStream(temporary)) {
            if (input == null) throw new IOException("Không mở được tệp đã chọn.");
            long copied = 0; byte[] buffer = new byte[48 * 1024]; int count;
            while ((count = input.read(buffer)) >= 0) {
                operation.check();
                if (count == 0) continue;
                if (copied > MAX_FILE - count) throw new IOException("Mỗi tệp tối đa 2 GiB.");
                if (outgoing.getUsableSpace() < count + SPACE_RESERVE) throw new IOException("Không đủ chỗ trống để chuẩn bị tệp từ ứng dụng này.");
                output.write(buffer, 0, count); copied += count; progress.update(copied);
            }
            operation.check(); complete = true;
            return new PreparedSource(name, copied, resolver, uri, temporary, operation);
        } finally { if (!complete) temporary.delete(); }
    }
    static void export(ContentResolver resolver, Receipt receipt, Uri destination, AtomicBoolean cancelled) throws IOException {
        export(resolver, receipt, destination, new DocumentOperation(cancelled));
    }
    static void export(ContentResolver resolver, Receipt receipt, Uri destination, DocumentOperation operation) throws IOException {
        export(resolver, receipt, destination, operation, () -> true);
    }
    static void export(ContentResolver resolver, Receipt receipt, Uri destination, DocumentOperation operation, BooleanSupplier owner) throws IOException {
        requireDocumentUri(destination);
        operation.check();
        if (!owner.getAsBoolean()) throw new IOException("Nơi lưu đang thuộc một thao tác khác.");
        if (!receipt.file.isFile() || receipt.file.getName().endsWith(".partial")) throw new IOException("Tệp đã xác minh không còn trong bộ nhớ riêng.");
        try (InputStream input = operation.track(new FileInputStream(receipt.file)); OutputStream output = operation.output(resolver, destination)) {
            if (output == null) throw new IOException("Không mở được nơi lưu đã chọn.");
            byte[] buffer = new byte[48 * 1024]; int count; long copied = 0;
            while ((count = input.read(buffer)) >= 0) {
                operation.check();
                if (!owner.getAsBoolean()) throw new IOException("Thao tác lưu đã hết hiệu lực.");
                if (count > 0) { output.write(buffer, 0, count); copied += count; }
            }
            operation.check();
            if (copied != receipt.size) throw new IOException("Bản tệp riêng đã thay đổi trong lúc lưu.");
            output.flush();
        }
    }
    private static void checkCancelled(AtomicBoolean cancelled) throws IOException {
        if (cancelled.get() || Thread.currentThread().isInterrupted()) throw new IOException("Đã hủy thao tác tệp.");
    }
    static boolean isDocumentUri(Uri uri) { return uri != null && "content".equals(uri.getScheme()) && uri.getAuthority() != null && !uri.getAuthority().trim().isEmpty(); }
    private static void requireDocumentUri(Uri uri) throws IOException {
        if (!isDocumentUri(uri)) throw new IOException("Hãy chọn tệp hoặc nơi lưu bằng bộ chọn tệp của Android.");
    }
    static String portableName(String name) {
        if (name == null) return "tep-da-chon";
        StringBuilder safe = new StringBuilder();
        for (int i = 0; i < name.length() && safe.length() < 140;) {
            int point = name.codePointAt(i); i += Character.charCount(point);
            safe.appendCodePoint(point < 32 || point >= 0xd800 && point <= 0xdfff || "\\/:*?\"<>|".indexOf(point) >= 0 ? '_' : point);
        }
        String result = safe.toString().replaceAll("[. ]+$", "");
        if (result.trim().isEmpty() || result.equals(".")) result = "tep-da-chon";
        String stem = result.split("\\.", 2)[0].replaceFirst(" +$", "").toUpperCase(java.util.Locale.ROOT);
        if (stem.matches("CON|PRN|AUX|NUL|CONIN\\$|CONOUT\\$|COM[1-9¹²³]|LPT[1-9¹²³]")) result = "tep-" + result;
        try { return FileTransferClient.safeFileName(result); }
        catch (IllegalArgumentException ignored) { return "tep-da-chon"; }
    }
    private File checkedReceipt(String name) throws IOException {
        File file = new File(incoming, name).getCanonicalFile();
        if (!file.getParentFile().equals(incoming) || !name.equals(file.getName())) throw new IOException("Đường dẫn tệp riêng không hợp lệ.");
        return file;
    }
    private List<Receipt> load() throws IOException {
        List<Receipt> receipts = new ArrayList<>();
        try (InputStream input = index.openRead()) {
            ByteArrayOutputStream bytes = new ByteArrayOutputStream(); byte[] buffer = new byte[4096]; int count;
            while ((count = input.read(buffer)) > 0) { if (bytes.size() + count > 32 * 1024) throw new IOException("Danh sách tệp đã nhận quá lớn."); bytes.write(buffer, 0, count); }
            JSONArray entries = new JSONArray(new String(bytes.toByteArray(), StandardCharsets.UTF_8));
            for (int i = 0; i < entries.length() && receipts.size() < MAX_RECEIPTS; i++) {
                JSONObject item = entries.getJSONObject(i); File file = checkedReceipt(item.getString("file"));
                if (file.isFile() && !file.getName().endsWith(".partial"))
                    receipts.add(new Receipt(item.getString("id"), portableName(item.getString("name")), file, file.length()));
            }
        } catch (FileNotFoundException missing) { return receipts; }
        catch (org.json.JSONException error) { throw new IOException("Không đọc được danh sách tệp đã nhận.", error); }
        return receipts;
    }
    private void save(List<Receipt> receipts) throws IOException {
        JSONArray entries = new JSONArray();
        try {
            for (Receipt receipt : receipts) entries.put(new JSONObject().put("id", receipt.id).put("name", receipt.name).put("file", receipt.file.getName()));
        } catch (org.json.JSONException error) { throw new IOException(error); }
        FileOutputStream output = null;
        try { output = index.startWrite(); output.write(entries.toString().getBytes(StandardCharsets.UTF_8)); index.finishWrite(output); }
        catch (IOException error) { if (output != null) index.failWrite(output); throw error; }
    }
}
