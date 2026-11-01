package io.github.dtai52hz.lanbridge.net;

import java.util.Locale;
import java.util.Objects;

/** A LAN hint, not authorization: the certificate is pinned and the computer still approves. */
public final class Device {
    public final String id, name, address, fingerprint;
    public final int port;
    public final boolean busy;
    /** Monotonic milliseconds, not wall time. */
    public final long lastSeenMillis;

    public Device(String id, String name, String address, int port, String fingerprint,
                  boolean busy, long lastSeenMillis) {
        if (!Wire.validName(name) || !Wire.validAddress(address) || port < 1 || port > 65535
                || !Wire.validHex256(fingerprint)) throw new IllegalArgumentException("Thông tin máy tính không hợp lệ.");
        this.id = Objects.requireNonNull(id);
        this.name = name;
        this.address = address;
        this.port = port;
        this.fingerprint = fingerprint.toUpperCase(Locale.ROOT);
        this.busy = busy;
        this.lastSeenMillis = lastSeenMillis;
    }

    boolean sameAnnouncement(Device other) {
        return id.equals(other.id) && name.equals(other.name) && address.equals(other.address)
                && port == other.port && fingerprint.equals(other.fingerprint) && busy == other.busy;
    }
}
