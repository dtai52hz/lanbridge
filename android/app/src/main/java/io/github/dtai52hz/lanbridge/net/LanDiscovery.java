package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InetSocketAddress;
import java.net.MulticastSocket;
import java.net.NetworkInterface;
import java.net.SocketTimeoutException;
import java.nio.ByteBuffer;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Collections;
import java.util.Comparator;
import java.util.Enumeration;
import java.util.LinkedHashMap;
import java.util.List;
import java.util.Locale;

/**
 * Passive discovery on UDP 45832. Existing desktop hosts announce every two seconds.
 * Their query schema also registers the sender as a host, so this controller never sends a
 * pretend host/certificate merely to solicit a reply. refresh() prunes/publishes the cache;
 * the next authentic host announcement refreshes it. The Activity should own a Wi-Fi
 * MulticastLock while this service is started, and close both when discovery is not visible.
 */
public final class LanDiscovery implements AutoCloseable {
    public interface Listener {
        void onDevices(List<Device> devices);
        default void onError(String message) { }
    }
    static final int PORT = 45832, MAX_DATAGRAM = 2048, CAPACITY = 128;
    static final long TTL_MILLIS = 7000;
    private final Object gate = new Object();
    private final Listener listener;
    private final Cache cache = new Cache();
    private MulticastSocket socket;
    private boolean started, closed;

    public LanDiscovery(Listener listener) {
        if (listener == null) throw new IllegalArgumentException("Thiếu bộ nhận danh sách thiết bị.");
        this.listener = listener;
    }
    public void start() {
        synchronized (gate) {
            if (closed) throw new IllegalStateException("Tạo lượt tìm thiết bị mới để mở lại.");
            if (started) return;
            started = true;
        }
        Thread worker = new Thread(this::listen, "LanBridge-discovery"); worker.setDaemon(true); worker.start();
    }
    public void refresh() {
        boolean restart;
        synchronized (gate) { if (closed) return; restart = !started; }
        if (restart) start();
        cache.prune(Wire.now()); publish();
    }
    public List<Device> devices() { cache.prune(Wire.now()); return cache.snapshot(); }
    private void listen() {
        MulticastSocket opened = null;
        try {
            opened = new MulticastSocket(null);
            opened.setReuseAddress(true); opened.setBroadcast(true); opened.setSoTimeout(1000);
            synchronized (gate) { if (closed) { opened.close(); return; } socket = opened; }
            opened.bind(new InetSocketAddress(PORT));
            InetSocketAddress group = new InetSocketAddress(InetAddress.getByName("239.255.45.83"), PORT);
            Enumeration<NetworkInterface> interfaces = NetworkInterface.getNetworkInterfaces();
            while (interfaces != null && interfaces.hasMoreElements()) {
                NetworkInterface network = interfaces.nextElement();
                try {
                    if (!network.isUp() || network.isLoopback() || !hasIpv4(network)) continue;
                    opened.joinGroup(group, network);
                } catch (IOException ignored) { /* Per-interface multicast can fail; broadcast reception still works. */ }
            }
            byte[] buffer = new byte[MAX_DATAGRAM + 1];
            while (true) {
                synchronized (gate) { if (closed) return; }
                long now = Wire.now();
                boolean changed = cache.prune(now);
                DatagramPacket datagram = new DatagramPacket(buffer, buffer.length);
                try {
                    opened.receive(datagram);
                    if (datagram.getAddress() instanceof Inet4Address && datagram.getLength() <= MAX_DATAGRAM) {
                        Device device = decode(buffer, datagram.getLength(), datagram.getAddress().getHostAddress(), Wire.now());
                        if (device != null) synchronized (gate) {
                            if (closed) return;
                            changed |= cache.accept(device);
                        }
                    }
                } catch (SocketTimeoutException ignored) { }
                if (changed) publish();
            }
        } catch (IOException | RuntimeException error) {
            synchronized (gate) {
                if (!closed) try { listener.onError("Không thể tìm máy qua LAN. Kiểm tra Wi-Fi và UDP 45832; vẫn có thể dùng mã ghép nối."); }
                catch (RuntimeException ignored) { }
            }
        } finally {
            if (opened != null) opened.close();
            synchronized (gate) { if (socket == opened) { socket = null; started = false; } }
        }
    }
    private static boolean hasIpv4(NetworkInterface network) {
        Enumeration<InetAddress> addresses = network.getInetAddresses();
        while (addresses.hasMoreElements()) if (addresses.nextElement() instanceof Inet4Address) return true;
        return false;
    }
    static Device decode(byte[] bytes, int length, String sender, long now) {
        if (length < 1 || length > MAX_DATAGRAM || length > bytes.length || !Wire.validAddress(sender)) return null;
        try {
            String text = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .decode(ByteBuffer.wrap(bytes, 0, length)).toString();
            // Wire parser requires a "type" field; discovery beacons use a separate schema.
            JSONObject beacon = Wire.parseObject(text);
            String kind = beacon.optString("kind"), id = beacon.optString("id");
            if (!"LanBridge".equals(beacon.optString("protocol")) || beacon.optInt("version") != 1
                    || !"announce".equals(kind) && !"query".equals(kind)
                    || !id.matches("(?i)([0-9a-f]{32}|[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12})")) return null;
            id = id.replace("-", "").toLowerCase(Locale.ROOT);
            if (id.equals("00000000000000000000000000000000")) return null;
            return new Device(id, beacon.optString("name"), sender, beacon.optInt("port"),
                    beacon.optString("fingerprint"), Boolean.TRUE.equals(beacon.opt("busy")), now);
        } catch (Exception ignored) { return null; }
    }
    private void publish() {
        synchronized (gate) {
            if (closed) return;
            try { listener.onDevices(cache.snapshot()); } catch (RuntimeException ignored) { }
        }
    }
    @Override public void close() {
        MulticastSocket closeSocket;
        synchronized (gate) {
            if (closed) return;
            closed = true; closeSocket = socket; socket = null;
        }
        if (closeSocket != null) closeSocket.close();
        cache.clear();
    }

    static final class Cache {
        private final LinkedHashMap<String, Device> devices = new LinkedHashMap<>();
        synchronized boolean accept(Device device) {
            Device old = devices.get(device.id);
            if (old == null && devices.size() >= CAPACITY) return false;
            devices.put(device.id, device);
            return old == null || !old.sameAnnouncement(device);
        }
        synchronized boolean prune(long now) {
            int before = devices.size();
            devices.entrySet().removeIf(entry -> now - entry.getValue().lastSeenMillis >= TTL_MILLIS);
            return devices.size() != before;
        }
        synchronized List<Device> snapshot() {
            ArrayList<Device> snapshot = new ArrayList<>(devices.values());
            snapshot.sort(Comparator.comparing((Device device) -> device.name, String.CASE_INSENSITIVE_ORDER)
                    .thenComparing(device -> device.address));
            return Collections.unmodifiableList(snapshot);
        }
        synchronized void clear() { devices.clear(); }
    }
}
