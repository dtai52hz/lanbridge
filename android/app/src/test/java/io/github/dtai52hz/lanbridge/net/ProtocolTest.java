package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import org.junit.Test;
import java.io.ByteArrayInputStream;
import java.io.IOException;
import java.nio.ByteBuffer;
import java.nio.charset.StandardCharsets;
import java.util.List;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicReference;
import static org.junit.Assert.*;

public class ProtocolTest {
    private static final String PIN = "aB".repeat(32);
    private static final String SECRET = "12".repeat(32);
    @Test public void pairingAcceptsExistingDesktopWireAndNeverResolvesNames() {
        PairingCode parsed = PairingCode.parse(" LB1|192.168.1.8|45831|" + PIN + "|" + SECRET + " \n");
        assertEquals("192.168.1.8", parsed.address); assertEquals(45831, parsed.port);
        assertEquals(PIN.toUpperCase(), parsed.fingerprint); assertEquals(32, parsed.secretBytes().length);
        for (byte b : parsed.secretBytes()) assertEquals(0x12, b);
        assertTrue(Wire.validAddress("127.0.0.1"));
        for (String address : new String[]{"pc.example", "::1", "0.0.0.0", "255.255.255.255", "239.255.45.83", "256.1.1.1", "192.168.01.1", "1.2.3", "1.2.3.4.extra"}) {
            assertFalse(address, Wire.validAddress(address));
            assertThrows(IllegalArgumentException.class, () -> PairingCode.parse("LB1|" + address + "|45831|" + PIN + "|" + SECRET));
        }
        for (String code : new String[]{"", "LB2|1.2.3.4|1|" + PIN + "|" + SECRET,
                "LB1|1.2.3.4|0|" + PIN + "|" + SECRET, "LB1|1.2.3.4|65536|" + PIN + "|" + SECRET,
                "LB1|1.2.3.4|1|bad|" + SECRET, "LB1|1.2.3.4|1|" + PIN + "|xyz",
                "LB1|1.2.3.4|1|" + PIN + "|" + SECRET + "|extra"})
            assertThrows(IllegalArgumentException.class, () -> PairingCode.parse(code));
    }
    @Test public void approvalPreambleMatchesLbc1AndStrictUtf8Name() {
        byte[] request = LanBridgeClient.approvalRequest("Điện thoại Android");
        for (int i = 0; i < 32; i++) assertEquals(0, request[i]);
        assertEquals('L', request[32]); assertEquals('B', request[33]); assertEquals('C', request[34]); assertEquals(1, request[35]);
        int length = (request[36] & 255) * 256 + (request[37] & 255);
        assertEquals(request.length - 38, length);
        assertEquals("Điện thoại Android", new String(request, 38, length, StandardCharsets.UTF_8));
        for (String name : new String[]{"", " ", "a\nb", "x".repeat(81), "\uD800", "a\u0085b"})
            assertThrows(IllegalArgumentException.class, () -> LanBridgeClient.approvalRequest(name));
    }
    @Test public void framedUnicodeRoundTripsAcrossSingleByteReads() throws Exception {
        JSONObject packet = Wire.packet("text"); Wire.put(packet, "text", "một tiếng Việt 😀 , ? !\n");
        byte[] frame = Wire.frame(packet);
        assertEquals(frame.length - 4, ByteBuffer.wrap(frame).getInt());
        ByteArrayInputStream fragmented = new ByteArrayInputStream(frame) {
            @Override public synchronized int read(byte[] bytes, int offset, int length) { return super.read(bytes, offset, Math.min(1, length)); }
        };
        assertEquals(packet.getString("text"), Wire.read(fragmented).getString("text"));
        assertThrows(IOException.class, () -> Wire.read(new ByteArrayInputStream(new byte[]{0,0,0,0})));
        assertThrows(IOException.class, () -> Wire.read(new ByteArrayInputStream(new byte[]{127,-1,-1,-1})));
        assertThrows(IOException.class, () -> Wire.read(new ByteArrayInputStream(new byte[]{0,0,0,4,'{'})));
        assertThrows(IOException.class, () -> Wire.read(new ByteArrayInputStream(new byte[]{0,0,0,2,(byte) 0xC0,(byte) 0xAF})));
        assertThrows(IOException.class, () -> Wire.parse("{\"type\":\"hello\"}{}"));
        assertThrows(IOException.class, () -> Wire.parse("{\"type\":\"hello\",\"length\":-1}"));
        assertThrows(IOException.class, () -> Wire.parse("{\"type\":\"$ping\",\"length\":1.5}"));
        assertThrows(IOException.class, () -> Wire.parse("{\"type\":\"$ping\",\"length\":9223372036854775808}"));
        assertThrows(IOException.class, () -> Wire.parse("{\"type\":\"hello\",\"x\":" + "[".repeat(33) + "0" + "]".repeat(33) + "}"));
    }
    @Test public void heartbeatOpaqueLongIsNotRoundedThroughDouble() throws Exception {
        for (long stamp : new long[]{9007199254740993L, Long.MAX_VALUE}) {
            JSONObject ping = Wire.packet("$ping"); Wire.put(ping, "id", "opaque"); Wire.put(ping, "length", stamp);
            JSONObject decoded = Wire.read(new ByteArrayInputStream(Wire.frame(ping)));
            assertEquals(stamp, decoded.getLong("length"));
            JSONObject pong = Wire.packet("$pong"); Wire.put(pong, "id", decoded.opt("id")); Wire.put(pong, "length", decoded.optLong("length"));
            assertEquals(stamp, Wire.read(new ByteArrayInputStream(Wire.frame(pong))).getLong("length"));
        }
    }
    @Test public void outboundMovesNeverCrossInputBarriersAndCapacityIsBounded() throws Exception {
        OutboundQueue queue = new OutboundQueue(4);
        assertTrue(queue.offer(move(1))); assertTrue(queue.offer(move(2)));
        assertTrue(queue.offer(Wire.packet("button"))); assertTrue(queue.offer(move(3))); assertTrue(queue.offer(move(4)));
        assertTrue(queue.offer(Wire.packet("key"))); assertFalse(queue.offer(Wire.packet("text")));
        assertEquals(2, queue.take().getInt("x")); assertEquals("button", queue.take().getString("type"));
        assertEquals(4, queue.take().getInt("x")); assertEquals("key", queue.take().getString("type"));
        queue.offer(Wire.packet("$ping")); queue.offer(move(5)); queue.offer(Wire.packet("key"));
        queue.discardInput(); assertEquals("$ping", queue.take().getString("type"));
        CountDownLatch done = new CountDownLatch(1); AtomicReference<JSONObject> result = new AtomicReference<>();
        Thread reader = new Thread(() -> { try { result.set(queue.take()); done.countDown(); } catch (InterruptedException ignored) { } });
        reader.start(); queue.close(); assertTrue(done.await(1, TimeUnit.SECONDS)); assertNull(result.get());
        assertFalse(queue.offer(Wire.packet("key")));
    }
    private JSONObject move(int x) { JSONObject packet = Wire.packet("move"); Wire.put(packet, "x", x); return packet; }

    @Test public void discoveryUsesDatagramSenderAndRejectsMalformedOrFakeBeacons() throws Exception {
        JSONObject beacon = beacon("1".repeat(32)); Wire.put(beacon, "address", "203.0.113.99");
        byte[] bytes = beacon.toString().getBytes(StandardCharsets.UTF_8);
        Device device = LanDiscovery.decode(bytes, bytes.length, "192.168.1.8", 123);
        assertNotNull(device); assertEquals("192.168.1.8", device.address); assertEquals(123, device.lastSeenMillis);
        assertNull(LanDiscovery.decode(bytes, bytes.length, "239.1.1.1", 123));
        for (String field : new String[]{"protocol", "version", "kind", "id", "port", "name", "fingerprint"}) {
            JSONObject bad = new JSONObject(beacon.toString()); bad.put(field, "name".equals(field) ? "" : "invalid");
            byte[] encoded = bad.toString().getBytes(StandardCharsets.UTF_8);
            assertNull(field, LanDiscovery.decode(encoded, encoded.length, "192.168.1.8", 123));
        }
        byte[] emptyId = beacon("0".repeat(32)).toString().getBytes(StandardCharsets.UTF_8);
        assertNull(LanDiscovery.decode(emptyId, emptyId.length, "192.168.1.8", 0));
        assertNull(LanDiscovery.decode(new byte[2049], 2049, "192.168.1.8", 0));
    }
    @Test public void discoveryCacheRefreshesSeenWithoutNoiseAndExpiresAtSevenSeconds() {
        LanDiscovery.Cache cache = new LanDiscovery.Cache();
        Device first = device("a", 0), refreshed = device("a", 6000);
        assertTrue(cache.accept(first)); assertFalse(cache.accept(refreshed));
        assertFalse(cache.prune(7000)); assertEquals(6000, cache.snapshot().get(0).lastSeenMillis);
        assertTrue(cache.prune(13000)); assertTrue(cache.snapshot().isEmpty());
        for (int i = 0; i < 128; i++) assertTrue(cache.accept(device(String.valueOf(i), 0)));
        assertFalse(cache.accept(device("overflow", 0))); assertEquals(128, cache.snapshot().size());
        List<Device> snapshot = cache.snapshot(); assertThrows(UnsupportedOperationException.class, snapshot::clear);
        assertTrue(cache.prune(7000)); assertTrue(cache.accept(device("new", 7001)));
    }
    private static Device device(String id, long time) { return new Device(id, "PC", "192.168.1.8", 45831, PIN, false, time); }
    private static JSONObject beacon(String id) {
        JSONObject beacon = new JSONObject(); Wire.put(beacon, "protocol", "LanBridge"); Wire.put(beacon, "version", 1);
        Wire.put(beacon, "kind", "announce"); Wire.put(beacon, "id", id); Wire.put(beacon, "name", "PC");
        Wire.put(beacon, "port", 45831); Wire.put(beacon, "fingerprint", PIN); Wire.put(beacon, "busy", false); return beacon;
    }
    @Test public void unconnectedClientNeverSendsInputsOrScreensAndCloseIsIdempotent() {
        int[] closed = {0};
        LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
            @Override public void onClosed(String reason) { closed[0]++; }
        });
        assertFalse(client.enter()); assertFalse(client.moveTo(1,2)); assertFalse(client.button("left", true));
        assertFalse(client.wheel(0, 120)); assertFalse(client.key(65, true)); assertFalse(client.text("một"));
        assertFalse(client.leave()); assertFalse(client.requestScreen(true)); assertFalse(client.requestScreen(false));
        assertThrows(IllegalArgumentException.class, () -> client.text("\uD800"));
        assertThrows(IllegalArgumentException.class, () -> client.text("bad\u0000text"));
        assertThrows(IllegalArgumentException.class, () -> client.key(0, true));
        assertThrows(IllegalArgumentException.class, () -> client.button("invalid", true));
        client.close(); client.close(); assertEquals(1, closed[0]);
    }
}
