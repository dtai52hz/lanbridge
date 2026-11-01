package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import java.util.ArrayDeque;
import java.util.Iterator;

/** Never block an Android touch/IME thread. Only consecutive unsent moves may be replaced. */
final class OutboundQueue {
    private final ArrayDeque<JSONObject> packets = new ArrayDeque<>();
    private final int capacity;
    private boolean closed;
    OutboundQueue(int capacity) { this.capacity = capacity; }

    synchronized boolean offer(JSONObject packet) {
        if (closed) return false;
        if ("move".equals(packet.optString("type")) && !packets.isEmpty()
                && "move".equals(packets.peekLast().optString("type"))) {
            packets.removeLast(); packets.addLast(packet); return true;
        }
        if (packets.size() >= capacity) return false;
        packets.addLast(packet); notifyAll(); return true;
    }
    synchronized JSONObject take() throws InterruptedException {
        while (packets.isEmpty() && !closed) wait();
        return closed ? null : packets.removeFirst();
    }
    synchronized void discardInput() {
        for (Iterator<JSONObject> it = packets.iterator(); it.hasNext();) if (isInput(it.next().optString("type"))) it.remove();
    }
    synchronized void close() { closed = true; packets.clear(); notifyAll(); }
    static boolean isInput(String type) {
        return "enter".equals(type) || "leave".equals(type) || "move".equals(type) || "wheel".equals(type)
                || "key".equals(type) || "button".equals(type) || "text".equals(type);
    }
}
