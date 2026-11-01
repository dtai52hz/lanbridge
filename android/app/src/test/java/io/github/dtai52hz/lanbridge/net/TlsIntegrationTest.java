package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import org.junit.Test;

import java.io.ByteArrayInputStream;
import java.io.EOFException;
import java.io.InputStream;
import java.io.OutputStream;
import java.net.InetAddress;
import java.security.KeyStore;
import java.security.MessageDigest;
import java.util.Base64;
import java.util.concurrent.BlockingQueue;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.LinkedBlockingQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicInteger;
import java.util.concurrent.atomic.AtomicReference;
import javax.net.ssl.KeyManagerFactory;
import javax.net.ssl.SSLContext;
import javax.net.ssl.SSLException;
import javax.net.ssl.SSLServerSocket;
import javax.net.ssl.SSLSocket;
import static org.junit.Assert.*;

/** Only isolated 127.0.0.1 sockets are opened. The embedded key is a public test fixture. */
public class TlsIntegrationTest {
    private static final String KEYSTORE = "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";
    private static final String SECRET = "12".repeat(32);

    @Test public void discoveredApprovalUnicodeScreenAndPermissionRevokeUseExistingWire() throws Exception {
        BlockingQueue<JSONObject> received = new LinkedBlockingQueue<>();
        try (FakeServer server = new FakeServer(socket -> {
            InputStream input = socket.getInputStream(); OutputStream output = socket.getOutputStream();
            byte[] preamble = new byte[38]; Wire.exact(input, preamble);
            for (int i = 0; i < 32; i++) assertEquals(0, preamble[i]);
            assertEquals('L', preamble[32]); assertEquals('B', preamble[33]); assertEquals('C', preamble[34]); assertEquals(1, preamble[35]);
            byte[] name = new byte[(preamble[36] & 255) * 256 + (preamble[37] & 255)]; Wire.exact(input, name);
            assertEquals("Điện thoại Android", new String(name, java.nio.charset.StandardCharsets.UTF_8));
            output.write(1); output.write(Wire.frame(hello())); output.write(Wire.frame(permissions(true, true))); output.flush();
            JSONObject peerHello = Wire.read(input);
            assertEquals("hello", peerHello.getString("type")); assertEquals(2, peerHello.getInt("flags"));
            assertEquals(1, peerHello.getInt("screenViewingVersion")); assertTrue(peerHello.getInt("width") >= 2);
            try {
                while (true) {
                    JSONObject packet = Wire.read(input);
                    if (replyHeartbeat(packet, output)) continue;
                    received.add(packet);
                    if ("screen-request".equals(packet.optString("type")) && packet.optBoolean("allow")) {
                        JSONObject frame = Wire.packet("screen-frame"); Wire.put(frame, "width", 1); Wire.put(frame, "height", 1);
                        Wire.put(frame, "encoding", 1); Wire.put(frame, "data", "/9j/2Q=="); output.write(Wire.frame(frame)); output.flush();
                    }
                    if ("text".equals(packet.optString("type"))) {
                        output.write(Wire.frame(permissions(false, false))); output.flush();
                    }
                }
            } catch (EOFException | java.net.SocketException expected) { }
        })) {
            CountDownLatch ready = new CountDownLatch(1), allowed = new CountDownLatch(1), screen = new CountDownLatch(1),
                    revoked = new CountDownLatch(1), closed = new CountDownLatch(1);
            AtomicInteger closes = new AtomicInteger(); AtomicReference<String> error = new AtomicReference<>();
            LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
                @Override public void onReady(String name, int width, int height) { assertEquals("Loopback PC", name); ready.countDown(); }
                @Override public void onPermissions(boolean value) { if (value) allowed.countDown(); else revoked.countDown(); }
                @Override public void onPacket(JSONObject packet) { if ("screen-frame".equals(packet.optString("type"))) screen.countDown(); }
                @Override public void onError(String message) { error.set(message); }
                @Override public void onClosed(String reason) { closes.incrementAndGet(); closed.countDown(); }
            });
            try {
                assertFalse(client.text("before approval")); client.connectDevice(server.device(), "Điện thoại Android");
                assertTrue(ready.await(5, TimeUnit.SECONDS)); assertTrue(allowed.await(5, TimeUnit.SECONDS));
                assertTrue(client.requestScreen(true)); assertTrue(screen.await(3, TimeUnit.SECONDS));
                assertTrue(client.enter()); assertTrue(client.moveTo(12345, 54321));
                assertTrue(client.button("left", true)); assertTrue(client.button("left", false));
                assertTrue(client.text("một tiếng Việt 😀 . , ? !"));
                assertTrue(revoked.await(3, TimeUnit.SECONDS));
                assertFalse(client.isEntered()); assertFalse(client.text("blocked")); assertFalse(client.key(65, true));
                assertFalse(client.requestScreen(true)); assertNull(error.get());
                String[] expected = {"screen-request", "enter", "move", "button", "button", "text"};
                JSONObject last = null;
                for (String type : expected) { last = received.poll(3, TimeUnit.SECONDS); assertNotNull(type, last); assertEquals(type, last.getString("type")); }
                assertEquals("một tiếng Việt 😀 . , ? !", last.getString("text"));
                client.close(); client.close(); assertTrue(closed.await(1, TimeUnit.SECONDS)); assertEquals(1, closes.get());
                server.await();
            } finally { client.close(); }
        }
    }

    @Test public void certificateMismatchNeverDisclosesLb1Secret() throws Exception {
        CountDownLatch attempted = new CountDownLatch(1); AtomicInteger disclosed = new AtomicInteger();
        try (FakeServer server = new FakeServer(socket -> {
            try { socket.startHandshake(); if (socket.getInputStream().read() >= 0) disclosed.incrementAndGet(); }
            catch (SSLException | java.net.SocketException expected) { }
            finally { attempted.countDown(); }
        })) {
            CountDownLatch closed = new CountDownLatch(1); AtomicInteger closes = new AtomicInteger();
            LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
                @Override public void onClosed(String reason) { closes.incrementAndGet(); closed.countDown(); }
            });
            client.connectCode("LB1|127.0.0.1|" + server.port() + "|" + "00".repeat(32) + "|" + SECRET, "Android");
            assertTrue(closed.await(5, TimeUnit.SECONDS)); assertTrue(attempted.await(5, TimeUnit.SECONDS));
            assertEquals(0, disclosed.get()); client.close(); assertEquals(1, closes.get()); server.await();
        }
    }

    @Test public void correctPinnedLb1SecretIsSentBeforeHello() throws Exception {
        CountDownLatch gotHello = new CountDownLatch(1);
        try (FakeServer server = new FakeServer(socket -> {
            byte[] secret = new byte[32]; Wire.exact(socket.getInputStream(), secret);
            for (byte value : secret) assertEquals(0x12, value);
            OutputStream output = socket.getOutputStream(); output.write(1);
            output.write(Wire.frame(hello())); output.write(Wire.frame(permissions(false, false))); output.flush();
            assertEquals("hello", Wire.read(socket.getInputStream()).getString("type")); gotHello.countDown();
        })) {
            LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() { });
            try {
                client.connectCode("LB1|127.0.0.1|" + server.port() + "|" + server.pin + "|" + SECRET, "Android");
                assertTrue(gotHello.await(5, TimeUnit.SECONDS)); server.await();
            } finally { client.close(); }
        }
    }

    @Test public void committedImeTextSplitsWithoutCuttingSurrogatePairs() throws Exception {
        String expected = "x".repeat(255) + "😀" + "một tiếng Việt . , ? ! ".repeat(200);
        try (FakeServer server = new FakeServer(socket -> {
            InputStream input = socket.getInputStream(); OutputStream output = socket.getOutputStream();
            byte[] preamble = new byte[38]; Wire.exact(input, preamble);
            byte[] name = new byte[(preamble[36] & 255) * 256 + (preamble[37] & 255)]; Wire.exact(input, name);
            output.write(1); output.write(Wire.frame(hello())); output.write(Wire.frame(permissions(true, false))); output.flush();
            assertEquals("hello", Wire.read(input).getString("type"));
            assertEquals("enter", Wire.read(input).getString("type"));
            StringBuilder combined = new StringBuilder(); int chunks = 0;
            while (combined.length() < expected.length()) {
                JSONObject packet = Wire.read(input); if (replyHeartbeat(packet, output)) continue;
                assertEquals("text", packet.getString("type")); String part = packet.getString("text");
                assertTrue(part.length() <= 256); assertTrue(Wire.validUnicode(part)); combined.append(part); chunks++;
            }
            assertTrue(chunks > 1); assertEquals(expected, combined.toString());
        })) {
            CountDownLatch allowed = new CountDownLatch(1);
            LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
                @Override public void onPermissions(boolean value) { if (value) allowed.countDown(); }
            });
            try {
                client.connectDevice(server.device(), "Android"); assertTrue(allowed.await(5, TimeUnit.SECONDS));
                assertTrue(client.enter()); assertTrue(client.text(expected)); server.await();
            } finally { client.close(); }
        }
    }

    @Test public void cancelWhileAwaitingApprovalClosesSocketAndNotifiesOnce() throws Exception {
        CountDownLatch request = new CountDownLatch(1), disconnected = new CountDownLatch(1);
        try (FakeServer server = new FakeServer(socket -> {
            byte[] preamble = new byte[38]; Wire.exact(socket.getInputStream(), preamble);
            byte[] name = new byte[(preamble[36] & 255) * 256 + (preamble[37] & 255)]; Wire.exact(socket.getInputStream(), name);
            request.countDown(); assertEquals(-1, socket.getInputStream().read()); disconnected.countDown();
        })) {
            AtomicInteger closes = new AtomicInteger();
            LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
                @Override public void onClosed(String reason) { closes.incrementAndGet(); }
            });
            client.connectDevice(server.device(), "Android"); assertTrue(request.await(5, TimeUnit.SECONDS));
            client.close(); client.close(); assertTrue(disconnected.await(3, TimeUnit.SECONDS));
            assertEquals(1, closes.get()); server.await();
        }
    }

    private static JSONObject hello() {
        JSONObject packet = Wire.packet("hello"); Wire.put(packet, "flags", 2); Wire.put(packet, "name", "Loopback PC");
        Wire.put(packet, "width", 1920); Wire.put(packet, "height", 1080); Wire.put(packet, "screenViewingVersion", 1); return packet;
    }
    private static JSONObject permissions(boolean input, boolean screen) {
        JSONObject permissions = new JSONObject(); Wire.put(permissions, "input", input); Wire.put(permissions, "screenViewing", screen);
        JSONObject packet = Wire.packet("permissions"); Wire.put(packet, "permissions", permissions); return packet;
    }
    private static boolean replyHeartbeat(JSONObject packet, OutputStream output) throws Exception {
        if (!"$ping".equals(packet.optString("type"))) return false;
        JSONObject pong = Wire.packet("$pong"); Wire.put(pong, "id", packet.opt("id")); Wire.put(pong, "length", packet.optLong("length"));
        output.write(Wire.frame(pong)); output.flush(); return true;
    }
    private interface Handler { void run(SSLSocket socket) throws Exception; }
    private static final class FakeServer implements AutoCloseable {
        final SSLServerSocket server;
        final String pin;
        final AtomicReference<Throwable> failure = new AtomicReference<>();
        final CountDownLatch finished = new CountDownLatch(1);
        FakeServer(Handler handler) throws Exception {
            KeyStore keyStore = KeyStore.getInstance("PKCS12");
            keyStore.load(new ByteArrayInputStream(Base64.getDecoder().decode(KEYSTORE)), "test-only".toCharArray());
            KeyManagerFactory keys = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm());
            keys.init(keyStore, "test-only".toCharArray()); SSLContext context = SSLContext.getInstance("TLS"); context.init(keys.getKeyManagers(), null, null);
            byte[] digest = MessageDigest.getInstance("SHA-256").digest(keyStore.getCertificate("test").getEncoded());
            StringBuilder hex = new StringBuilder(); for (byte value : digest) hex.append(String.format("%02X", value & 255)); pin = hex.toString();
            server = (SSLServerSocket) context.getServerSocketFactory().createServerSocket(0, 1, InetAddress.getByName("127.0.0.1"));
            server.setSoTimeout(6000);
            Thread worker = new Thread(() -> {
                try (SSLSocket socket = (SSLSocket) server.accept()) { socket.setSoTimeout(5000); handler.run(socket); }
                catch (Throwable error) { failure.set(error); }
                finally { finished.countDown(); }
            }, "test-loopback-TLS"); worker.setDaemon(true); worker.start();
        }
        Device device() { return new Device("test", "Loopback PC", "127.0.0.1", port(), pin, false, Wire.now()); }
        int port() { return server.getLocalPort(); }
        void await() throws Exception {
            assertTrue("server completed", finished.await(6, TimeUnit.SECONDS));
            if (failure.get() != null) throw new AssertionError("Loopback server failed", failure.get());
        }
        @Override public void close() throws Exception { server.close(); }
    }
}
