package io.github.dtai52hz.lanbridge.net;

import org.json.JSONException;
import org.json.JSONObject;
import org.json.JSONTokener;

import java.io.EOFException;
import java.io.IOException;
import java.io.InputStream;
import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.StandardCharsets;

final class Wire {
    static final int MAX_FRAME = 4 * 1024 * 1024;
    private Wire() { }

    static boolean validHex256(String value) { return value != null && value.matches("[0-9a-fA-F]{64}"); }
    static byte[] hexBytes(String value) {
        byte[] bytes = new byte[value.length() / 2];
        for (int i = 0; i < bytes.length; i++) bytes[i] = (byte) Integer.parseInt(value.substring(i * 2, i * 2 + 2), 16);
        return bytes;
    }
    static boolean validAddress(String address) {
        if (address == null || !address.matches("[0-9]{1,3}(\\.[0-9]{1,3}){3}")) return false;
        String[] pieces = address.split("\\.");
        int first = 0;
        boolean any = false, all255 = true;
        for (int i = 0; i < 4; i++) {
            int octet = Integer.parseInt(pieces[i]);
            if (octet > 255 || pieces[i].length() > 1 && pieces[i].charAt(0) == '0') return false;
            if (i == 0) first = octet;
            any |= octet != 0; all255 &= octet == 255;
        }
        return any && !all255 && first < 224;
    }
    static boolean validName(String value) {
        if (value == null || value.length() < 1 || value.length() > 80 || value.trim().isEmpty()
                || value.getBytes(StandardCharsets.UTF_8).length > 256 || !validUnicode(value)) return false;
        for (int i = 0; i < value.length(); i++) if (Character.isISOControl(value.charAt(i))) return false;
        return true;
    }
    static boolean validUnicode(String value) {
        for (int i = 0; i < value.length(); i++) {
            char c = value.charAt(i);
            if (Character.isHighSurrogate(c)) {
                if (++i == value.length() || !Character.isLowSurrogate(value.charAt(i))) return false;
            } else if (Character.isLowSurrogate(c)) return false;
        }
        return true;
    }
    static JSONObject packet(String type) {
        JSONObject packet = new JSONObject(); put(packet, "type", type); return packet;
    }
    static JSONObject put(JSONObject target, String name, Object value) {
        try { target.put(name, value); return target; }
        catch (JSONException e) { throw new IllegalArgumentException("Gói dữ liệu không hợp lệ.", e); }
    }
    static byte[] frame(JSONObject packet) throws IOException {
        validate(packet);
        byte[] body = packet.toString().getBytes(StandardCharsets.UTF_8);
        if (body.length < 1 || body.length > MAX_FRAME) throw new IOException("Gói dữ liệu vượt giới hạn 4 MiB.");
        byte[] frame = new byte[body.length + 4];
        ByteBuffer.wrap(frame).putInt(body.length).put(body);
        return frame;
    }
    static JSONObject read(InputStream stream) throws IOException {
        byte[] header = new byte[4]; exact(stream, header);
        int size = ByteBuffer.wrap(header).getInt();
        if (size < 1 || size > MAX_FRAME) throw new IOException("Kích thước gói dữ liệu không hợp lệ.");
        byte[] body = new byte[size]; exact(stream, body);
        String json;
        try {
            json = StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT)
                    .onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(body)).toString();
        } catch (CharacterCodingException e) { throw new IOException("Dữ liệu UTF-8 không hợp lệ.", e); }
        return parse(json);
    }
    static JSONObject parse(String json) throws IOException {
        JSONObject packet = parseObject(json); validate(packet); return packet;
    }
    static JSONObject parseObject(String json) throws IOException {
        // Bound parser recursion before org.json touches an authenticated but potentially bad packet.
        int depth = 0; boolean quoted = false, escaped = false;
        for (int i = 0; i < json.length(); i++) {
            char c = json.charAt(i);
            if (quoted) {
                if (escaped) escaped = false;
                else if (c == '\\') escaped = true;
                else if (c == '"') quoted = false;
            } else if (c == '"') quoted = true;
            else if (c == '{' || c == '[') { if (++depth > 32) throw new IOException("Gói dữ liệu lồng quá sâu."); }
            else if (c == '}' || c == ']') { if (--depth < 0) throw new IOException("Gói dữ liệu không hợp lệ."); }
        }
        if (depth != 0 || quoted) throw new IOException("Gói dữ liệu không hợp lệ.");
        try {
            JSONTokener tokener = new JSONTokener(json);
            Object value = tokener.nextValue();
            if (!(value instanceof JSONObject) || tokener.nextClean() != 0) throw new IOException("Gói JSON không hợp lệ.");
            return (JSONObject) value;
        } catch (JSONException e) { throw new IOException("Gói JSON không hợp lệ.", e); }
    }
    static void validate(JSONObject packet) throws IOException {
        Object type = packet.opt("type");
        if (!(type instanceof String) || ((String) type).length() < 1 || ((String) type).length() > 64)
            throw new IOException("Loại gói dữ liệu không hợp lệ.");
        for (int i = 0; i < ((String) type).length(); i++) if (Character.isISOControl(((String) type).charAt(i)))
            throw new IOException("Loại gói dữ liệu không hợp lệ.");
        if (packet.has("length")) {
            Object length = packet.opt("length");
            if (!(length instanceof Integer) && !(length instanceof Long) || ((Number) length).longValue() < 0)
                throw new IOException("Độ dài gói dữ liệu không hợp lệ.");
        }
    }
    static void exact(InputStream stream, byte[] bytes) throws IOException {
        int offset = 0;
        while (offset < bytes.length) {
            int read = stream.read(bytes, offset, bytes.length - offset);
            if (read < 0) throw new EOFException("Máy tính đã ngắt kết nối.");
            if (read == 0) continue;
            offset += read;
        }
    }
    static long now() { return System.nanoTime() / 1_000_000L; }
}
