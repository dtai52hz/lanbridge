package io.github.dtai52hz.lanbridge.net;

import java.util.Locale;

/** Parses the existing desktop LB1 code without resolving untrusted host names. */
public final class PairingCode {
    public final String address, fingerprint;
    public final int port;
    private final String secret;

    private PairingCode(String address, int port, String fingerprint, String secret) {
        this.address = address; this.port = port;
        this.fingerprint = fingerprint.toUpperCase(Locale.ROOT);
        this.secret = secret.toUpperCase(Locale.ROOT);
    }

    public static PairingCode parse(String code) {
        if (code == null || code.length() > 256) throw invalid();
        String[] pieces = code.trim().split("\\|", -1);
        if (pieces.length != 5 || !"LB1".equals(pieces[0]) || !Wire.validAddress(pieces[1])
                || !pieces[2].matches("[0-9]{1,5}") || !Wire.validHex256(pieces[3])
                || !Wire.validHex256(pieces[4])) throw invalid();
        int port;
        try { port = Integer.parseInt(pieces[2]); } catch (NumberFormatException e) { throw invalid(); }
        if (port < 1 || port > 65535) throw invalid();
        return new PairingCode(pieces[1], port, pieces[3], pieces[4]);
    }

    byte[] secretBytes() { return Wire.hexBytes(secret); }
    private static IllegalArgumentException invalid() {
        return new IllegalArgumentException("Mã ghép nối không hợp lệ. Sao chép mã LB1 mới trên máy tính.");
    }
}
