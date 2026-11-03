package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;

import java.io.IOException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetSocketAddress;
import java.net.Socket;
import java.nio.charset.StandardCharsets;
import java.security.GeneralSecurityException;
import java.security.MessageDigest;
import java.security.SecureRandom;
import java.security.cert.CertificateException;
import java.security.cert.X509Certificate;
import java.util.Arrays;
import java.util.Base64;
import java.util.UUID;
import java.util.concurrent.Executors;
import java.util.concurrent.ScheduledExecutorService;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.CompletableFuture;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLParameters;
import javax.net.ssl.SSLSocket;
import javax.net.ssl.TrustManager;
import javax.net.ssl.X509TrustManager;

/**
 * One connection attempt/session. Create another instance to reconnect. Callbacks must return
 * promptly; dispatch UI work without waiting. close() can notify on the calling thread.
 * No global input is captured.
 * Coordinates are desktop normalized 0..65535. Keys are Windows virtual-key codes, also used by
 * the existing macOS/Linux key maps. Text is already committed by the Android IME: no Telex pass.
 */
public final class LanBridgeClient implements AutoCloseable {
    public interface Listener {
        default void onStatus(String status) { }
        /** The peer sent a valid hello. Input additionally needs onPermissions(true). */
        default void onReady(String remoteName, int width, int height) { }
        default void onPermissions(boolean inputAllowed) { }
        default void onScreenPermission(boolean allowed) { }
        default void onScreenCapability(boolean supported) { }
        default void onFilesPermission(boolean allowed) { }
        default void onError(String message) { }
        /** Exactly once, including a cancelled connection attempt. Null means a local close. */
        default void onClosed(String reason) { }
        /** Incoming non-heartbeat packets; do not decode screen JPEGs on this reader thread. */
        default void onPacket(JSONObject packet) { }
    }

    private final Object gate = new Object();
    private final Listener listener;
    private final OutboundQueue outbound = new OutboundQueue(128);
    private final FileTransferClient files;
    private Socket socket;
    private ScheduledExecutorService heartbeat;
    private boolean started, closed, connected, hello, inputAllowed, entered;
    private boolean screenCapable, screenAllowed, screenRequested, filesAllowed;
    private int pointerX = 32768, pointerY = 32768;
    private volatile long lastReceive;
    private String localName;

    public LanBridgeClient(Listener listener) {
        if (listener == null) throw new IllegalArgumentException("Thiếu bộ nhận trạng thái.");
        this.listener = listener;
        files = new FileTransferClient((packet, bulk) -> {
            synchronized (gate) {
                if (closed || !connected || !hello) {
                    CompletableFuture<Void> rejected = new CompletableFuture<>();
                    rejected.completeExceptionally(new IOException("Chưa kết nối với máy tính.")); return rejected;
                }
                return outbound.offerAwaited(packet, bulk);
            }
        });
    }
    public FileTransferClient files() { return files; }

    public void connectDevice(Device device, String name) {
        if (device == null) throw new IllegalArgumentException("Chưa chọn máy tính.");
        begin(device.address, device.port, device.fingerprint, null, name);
    }
    public void connectCode(String code, String name) {
        PairingCode pairing = PairingCode.parse(code);
        begin(pairing.address, pairing.port, pairing.fingerprint, pairing.secretBytes(), name);
    }
    private void begin(String address, int port, String fingerprint, byte[] secret, String name) {
        if (!Wire.validName(name)) {
            if (secret != null) Arrays.fill(secret, (byte) 0);
            throw new IllegalArgumentException("Tên điện thoại phải có 1–80 ký tự, không chứa ký tự điều khiển.");
        }
        synchronized (gate) {
            if (started || closed) {
                if (secret != null) Arrays.fill(secret, (byte) 0);
                throw new IllegalStateException("Tạo phiên mới để kết nối lại.");
            }
            started = true; localName = name;
        }
        worker("LanBridge-connect", () -> connect(address, port, fingerprint, secret));
    }

    private void connect(String address, int port, String fingerprint, byte[] secret) {
        Socket raw = new Socket();
        SSLSocket tls = null;
        try {
            synchronized (gate) {
                if (closed) { raw.close(); return; }
                socket = raw;
            }
            notifyStatus("Đang kết nối bảo mật…");
            raw.setTcpNoDelay(true);
            raw.connect(new InetSocketAddress(address, port), 10000);
            raw.setSoTimeout(10000);
            SSLContext context = pinnedContext(fingerprint);
            tls = (SSLSocket) context.getSocketFactory().createSocket(raw, "lanbridge", port, true);
            synchronized (gate) {
                if (closed) { tls.close(); return; }
                socket = tls;
            }
            tls.setUseClientMode(true);
            boolean tls13 = Arrays.asList(tls.getSupportedProtocols()).contains("TLSv1.3");
            tls.setEnabledProtocols(tls13 ? new String[]{"TLSv1.3", "TLSv1.2"} : new String[]{"TLSv1.2"});
            SSLParameters parameters = tls.getSSLParameters();
            // ALPN is optional on the desktop. Android 26–28 do not expose the standard API.
            try { SSLParameters.class.getMethod("setApplicationProtocols", String[].class)
                    .invoke(parameters, (Object) new String[]{"lanbridge/1"}); }
            catch (ReflectiveOperationException ignored) { }
            tls.setSSLParameters(parameters);
            tls.startHandshake(); // Trust manager must accept the exact pin before any secret is sent.
            OutputStream output = tls.getOutputStream();
            InputStream input = tls.getInputStream();
            if (secret == null) {
                output.write(approvalRequest(localName)); output.flush();
                tls.setSoTimeout(45000);
                notifyStatus("Hãy chấp nhận yêu cầu trên máy tính.");
            } else { output.write(secret); output.flush(); }
            int ack = input.read();
            if (ack == 2) throw new IOException("Máy tính đang bận với một phiên hoặc yêu cầu khác.");
            if (ack != 1) throw new IOException(secret == null ? "Máy tính đã từ chối yêu cầu kết nối."
                    : "Mã ghép nối đã sai hoặc hết hiệu lực. Lấy mã mới trên máy tính.");
            tls.setSoTimeout(0);
            synchronized (gate) {
                if (closed) return;
                connected = true; lastReceive = Wire.now();
                JSONObject greeting = Wire.packet("hello");
                Wire.put(greeting, "name", localName); Wire.put(greeting, "flags", 2);
                Wire.put(greeting, "width", 1920); Wire.put(greeting, "height", 1080);
                Wire.put(greeting, "screenViewingVersion", 1);
                if (!outbound.offer(greeting)) throw new IOException("Không thể tạo phiên điều khiển.");
                heartbeat = Executors.newSingleThreadScheduledExecutor(r -> daemon(r, "LanBridge-heartbeat"));
                heartbeat.scheduleAtFixedRate(this::tick, 500, 500, TimeUnit.MILLISECONDS);
            }
            final InputStream sessionInput = input;
            final OutputStream sessionOutput = output;
            worker("LanBridge-writer", () -> writeLoop(sessionOutput));
            readLoop(sessionInput);
        } catch (Exception error) { fail(message(error, "Không thể kết nối đến máy tính.")); }
        finally {
            if (secret != null) Arrays.fill(secret, (byte) 0);
            if (tls != null) try { tls.close(); } catch (IOException ignored) { }
            try { raw.close(); } catch (IOException ignored) { }
        }
    }

    static SSLContext pinnedContext(String fingerprint) throws GeneralSecurityException {
        byte[] expected = Wire.hexBytes(fingerprint);
        X509TrustManager trust = new X509TrustManager() {
            public X509Certificate[] getAcceptedIssuers() { return new X509Certificate[0]; }
            public void checkClientTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                throw new CertificateException("Client certificates are not used.");
            }
            public void checkServerTrusted(X509Certificate[] chain, String authType) throws CertificateException {
                try {
                    if (chain == null || chain.length < 1 || !MessageDigest.isEqual(expected,
                            MessageDigest.getInstance("SHA-256").digest(chain[0].getEncoded())))
                        throw new CertificateException("Chứng thư máy tính đã thay đổi. Tìm máy hoặc lấy mã mới.");
                } catch (GeneralSecurityException e) { throw new CertificateException("Chứng thư không khớp mã ghép nối.", e); }
            }
        };
        SSLContext context = SSLContext.getInstance("TLS");
        context.init(null, new TrustManager[]{trust}, new SecureRandom());
        return context;
    }

    static byte[] approvalRequest(String name) {
        if (!Wire.validName(name)) throw new IllegalArgumentException("Tên điện thoại không hợp lệ.");
        byte[] utf8 = name.getBytes(StandardCharsets.UTF_8);
        byte[] request = new byte[38 + utf8.length];
        request[32] = 'L'; request[33] = 'B'; request[34] = 'C'; request[35] = 1;
        request[36] = (byte) (utf8.length >>> 8); request[37] = (byte) utf8.length;
        System.arraycopy(utf8, 0, request, 38, utf8.length);
        return request;
    }

    private void readLoop(InputStream input) throws IOException {
        while (true) {
            synchronized (gate) { if (closed) return; }
            JSONObject packet = Wire.read(input);
            lastReceive = Wire.now();
            String type = packet.optString("type");
            if ("$ping".equals(type)) {
                JSONObject pong = Wire.packet("$pong");
                Wire.put(pong, "id", packet.opt("id")); Wire.put(pong, "length", packet.optLong("length", 0));
                offer(pong); continue;
            }
            if ("$pong".equals(type)) continue;
            receive(packet);
        }
    }

    private void receive(JSONObject packet) throws IOException {
        String type = packet.optString("type");
        if ("hello".equals(type)) {
            int width = packet.optInt("width"), height = packet.optInt("height");
            String name = packet.optString("name", "Máy tính");
            synchronized (gate) {
                if (closed) return;
                if (hello || packet.optInt("flags") != 2 || width < 2 || width > 65536 || height < 2 || height > 65536
                        || name.length() > 256) throw new IOException("Máy tính cần phiên bản LanBridge hỗ trợ Unicode.");
                hello = true; screenCapable = packet.optInt("screenViewingVersion") == 1;
            }
            notifyOpen(() -> listener.onScreenCapability(screenCapable));
            notifyOpen(() -> listener.onReady(name, width, height));
            return;
        }
        synchronized (gate) {
            if (closed) return;
            if (!hello) throw new IOException("Máy tính gửi dữ liệu trước khi khai báo giao thức.");
        }
        if ("permissions".equals(type)) {
            JSONObject permissions = packet.optJSONObject("permissions");
            if (permissions == null) throw new IOException("Máy tính gửi quyền không hợp lệ.");
            boolean canInput = Boolean.TRUE.equals(permissions.opt("input"));
            boolean canView = Boolean.TRUE.equals(permissions.opt("screenViewing"));
            boolean canFiles = Boolean.TRUE.equals(permissions.opt("files"));
            synchronized (gate) {
                if (closed) return;
                inputAllowed = canInput; screenAllowed = canView; filesAllowed = canFiles;
                if (!canInput) { entered = false; outbound.discardInput(); }
                if (!canView) screenRequested = false;
            }
            files.setRemoteAllowed(canFiles);
            notifyOpen(() -> listener.onPermissions(canInput));
            notifyOpen(() -> listener.onScreenPermission(canView));
            notifyOpen(() -> listener.onFilesPermission(canFiles));
        } else if (type.startsWith("file-")) {
            files.handle(packet);
            return;
        } else if ("leave".equals(type)) {
            synchronized (gate) { entered = false; outbound.discardInput(); }
        } else if ("screen-frame".equals(type)) {
            synchronized (gate) { if (!screenRequested || !screenAllowed || !screenCapable) return; }
            Object data = packet.opt("data");
            int width = packet.optInt("width"), height = packet.optInt("height");
            if (!(data instanceof String) || ((String) data).length() < 4 || ((String) data).length() > 699052
                    || width < 1 || width > 1280 || height < 1 || height > 800 || width * height > 1024000
                    || packet.optInt("encoding") != 1) throw new IOException("Khung hình nhận được không hợp lệ.");
            try {
                byte[] jpeg = Base64.getDecoder().decode((String) data);
                if (jpeg.length < 4 || jpeg.length > 512 * 1024
                        || jpeg[0] != (byte) 0xff || jpeg[1] != (byte) 0xd8
                        || jpeg[jpeg.length - 2] != (byte) 0xff || jpeg[jpeg.length - 1] != (byte) 0xd9)
                    throw new IOException("Khung hình vượt giới hạn 512 KiB hoặc không đúng định dạng JPEG.");
            } catch (IllegalArgumentException e) { throw new IOException("Khung hình không đúng mã Base64.", e); }
        }
        notifyOpen(() -> listener.onPacket(packet));
    }

    private void writeLoop(OutputStream stream) {
        try {
            OutboundQueue.Item item;
            while ((item = outbound.takeItem()) != null) {
                if (item.completion != null && item.completion.isCancelled()) continue;
                try {
                    stream.write(Wire.frame(item.packet)); stream.flush();
                    if (item.completion != null) item.completion.complete(null);
                } catch (Exception error) {
                    if (item.completion != null) item.completion.completeExceptionally(error);
                    throw error;
                }
            }
        } catch (InterruptedException e) { Thread.currentThread().interrupt(); fail("Phiên điều khiển đã dừng."); }
        catch (Exception e) { fail(message(e, "Không thể gửi dữ liệu đến máy tính.")); }
    }
    private void tick() {
        synchronized (gate) { if (closed || !connected) return; }
        if (Wire.now() - lastReceive >= 3500) { fail("Máy tính không phản hồi; phiên điều khiển đã dừng."); return; }
        JSONObject ping = Wire.packet("$ping");
        Wire.put(ping, "id", UUID.randomUUID().toString().replace("-", ""));
        Wire.put(ping, "length", System.nanoTime() & Long.MAX_VALUE);
        offer(ping);
    }

    public boolean enter() {
        synchronized (gate) {
            if (!canInput()) return false;
            if (entered) return true;
            JSONObject packet = pointer("enter"); Wire.put(packet, "name", "Right");
            if (!offer(packet)) return false;
            entered = true; return true;
        }
    }
    public boolean leave() {
        synchronized (gate) {
            if (closed || !connected || !hello) return false;
            entered = false;
            // Backgrounding the phone should release held input, not finish a queued typing run.
            outbound.discardInput();
            return offer(pointer("leave"));
        }
    }
    public boolean moveTo(int x, int y) {
        synchronized (gate) {
            if (!canSendInput()) return false;
            pointerX = clamp(x); pointerY = clamp(y); return offer(pointer("move"));
        }
    }
    public boolean button(String button, boolean down) {
        if (!"left".equals(button) && !"right".equals(button) && !"middle".equals(button)
                && !"x1".equals(button) && !"x2".equals(button)) throw new IllegalArgumentException("Nút chuột không hợp lệ.");
        synchronized (gate) {
            if (!canSendInput()) return false;
            JSONObject packet = pointer("button"); Wire.put(packet, "button", button); Wire.put(packet, "down", down);
            return offer(packet);
        }
    }
    /** Wheel deltas use Windows units (120 per notch), positive dy scrolls upward. */
    public boolean wheel(int dx, int dy) {
        synchronized (gate) {
            if (!canSendInput()) return false;
            JSONObject packet = pointer("wheel");
            Wire.put(packet, "dx", Math.max(-12000, Math.min(12000, dx)));
            Wire.put(packet, "dy", Math.max(-12000, Math.min(12000, dy)));
            return offer(packet);
        }
    }
    public boolean key(int vk, boolean down) {
        if (vk < 1 || vk > 254) throw new IllegalArgumentException("Mã phím không hợp lệ.");
        synchronized (gate) {
            if (!canSendInput()) return false;
            JSONObject packet = Wire.packet("key"); Wire.put(packet, "key", vk); Wire.put(packet, "down", down);
            return offer(packet);
        }
    }
    public boolean text(String text) {
        if (text == null || text.length() > 16384 || !Wire.validUnicode(text))
            throw new IllegalArgumentException("Văn bản không hợp lệ hoặc vượt quá 16.384 ký tự.");
        for (int i = 0; i < text.length(); i++) {
            char c = text.charAt(i);
            if (Character.isISOControl(c) && c != '\r' && c != '\n' && c != '\t')
                throw new IllegalArgumentException("Văn bản có ký tự điều khiển không được hỗ trợ.");
        }
        synchronized (gate) {
            if (!canSendInput()) return false;
            for (int start = 0; start < text.length();) {
                int end = Math.min(start + 256, text.length());
                if (end < text.length() && Character.isHighSurrogate(text.charAt(end - 1))) end--;
                JSONObject packet = Wire.packet("text"); Wire.put(packet, "text", text.substring(start, end));
                if (!offer(packet)) return false;
                start = end;
            }
            return true;
        }
    }
    public boolean requestScreen(boolean enabled) {
        synchronized (gate) {
            if (closed || !connected || !hello || enabled && (!screenCapable || !screenAllowed)) return false;
            JSONObject packet = Wire.packet("screen-request"); Wire.put(packet, "allow", enabled);
            if (!offer(packet)) return false;
            screenRequested = enabled; return true;
        }
    }
    public boolean isInputAllowed() { synchronized (gate) { return canInput(); } }
    public boolean isEntered() { synchronized (gate) { return entered && canInput(); } }
    private boolean canInput() { return !closed && connected && hello && inputAllowed; }
    private boolean canSendInput() { return entered && canInput(); }
    private JSONObject pointer(String type) {
        JSONObject packet = Wire.packet(type); Wire.put(packet, "x", pointerX); Wire.put(packet, "y", pointerY); return packet;
    }
    private static int clamp(int value) { return Math.max(0, Math.min(65535, value)); }
    private boolean offer(JSONObject packet) {
        synchronized (gate) { if (closed || !connected) return false; }
        if (outbound.offer(packet)) return true;
        // Dropping a key-up/button-up is unsafe. Disconnect releases all input on the computer.
        fail("Đường truyền đang nghẽn; đã ngắt để nhả chuột và phím."); return false;
    }
    private void notifyStatus(String message) { notifyOpen(() -> listener.onStatus(message)); }
    private void notifyOpen(Runnable callback) {
        synchronized (gate) { if (!closed) safely(callback); }
    }
    private void fail(String reason) { finish(reason, true); }
    @Override public void close() { finish(null, false); }
    private void finish(String reason, boolean error) {
        Socket closeSocket; ScheduledExecutorService closeHeartbeat;
        synchronized (gate) {
            if (closed) return;
            closed = true; connected = false; inputAllowed = false; entered = false; filesAllowed = false;
            screenRequested = false; screenAllowed = false;
            closeSocket = socket; socket = null; closeHeartbeat = heartbeat; heartbeat = null;
            outbound.close();
        }
        files.close();
        if (closeHeartbeat != null) closeHeartbeat.shutdownNow();
        if (closeSocket != null) try { closeSocket.close(); } catch (IOException ignored) { }
        if (error) safely(() -> listener.onError(reason));
        safely(() -> listener.onClosed(reason));
    }
    private static String message(Exception error, String fallback) {
        return error.getMessage() == null || error.getMessage().isEmpty() ? fallback : error.getMessage();
    }
    private static void safely(Runnable callback) { try { callback.run(); } catch (RuntimeException ignored) { } }
    private static Thread daemon(Runnable task, String name) { Thread thread = new Thread(task, name); thread.setDaemon(true); return thread; }
    private static void worker(String name, Runnable task) { daemon(task, name).start(); }
}
