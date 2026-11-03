package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import java.util.ArrayDeque;
import java.util.Iterator;
import java.io.IOException;
import java.util.concurrent.CompletableFuture;

/** Never block an Android touch/IME thread. Only consecutive unsent moves may be replaced. */
final class OutboundQueue {
    static final class Item {
        final JSONObject packet;
        final CompletableFuture<Void> completion;
        Item(JSONObject packet, CompletableFuture<Void> completion) { this.packet = packet; this.completion = completion; }
    }
    private final ArrayDeque<Item> packets = new ArrayDeque<>();
    private Item bulk;
    private final int capacity;
    private boolean closed;
    OutboundQueue(int capacity) { this.capacity = capacity; }

    synchronized boolean offer(JSONObject packet) {
        if (closed) return false;
        if ("move".equals(packet.optString("type")) && !packets.isEmpty()
                && "move".equals(packets.peekLast().packet.optString("type"))) {
            packets.removeLast(); packets.addLast(new Item(packet, null)); return true;
        }
        if (packets.size() >= capacity) return false;
        packets.addLast(new Item(packet, null)); notifyAll(); return true;
    }
    synchronized JSONObject take() throws InterruptedException {
        Item item = takeItem(); return item == null ? null : item.packet;
    }
    synchronized CompletableFuture<Void> offerAwaited(JSONObject packet, boolean isBulk) {
        if (bulk != null && bulk.completion.isCancelled()) bulk = null;
        CompletableFuture<Void> written = new CompletableFuture<>();
        if (closed || isBulk && bulk != null || !isBulk && packets.size() >= capacity) {
            written.completeExceptionally(new IOException("Hàng đợi truyền file đã đầy hoặc phiên đã đóng."));
            return written;
        }
        Item item = new Item(packet, written);
        if (isBulk) bulk = item; else packets.addLast(item);
        notifyAll(); return written;
    }
    synchronized Item takeItem() throws InterruptedException {
        while (packets.isEmpty() && bulk == null && !closed) wait();
        if (closed) return null;
        // Interactive input/control and heartbeat always precede the one waiting disk frame.
        if (!packets.isEmpty()) return packets.removeFirst();
        Item item = bulk; bulk = null; return item;
    }
    synchronized void discardInput() {
        for (Iterator<Item> it = packets.iterator(); it.hasNext();) if (isInput(it.next().packet.optString("type"))) it.remove();
    }
    synchronized void close() {
        closed = true;
        IOException error = new IOException("Phiên đã đóng.");
        if (bulk != null) { bulk.completion.completeExceptionally(error); bulk = null; }
        for (Item item : packets) if (item.completion != null) item.completion.completeExceptionally(error);
        packets.clear(); notifyAll();
    }
    static boolean isInput(String type) {
        return "enter".equals(type) || "leave".equals(type) || "move".equals(type) || "wheel".equals(type)
                || "key".equals(type) || "button".equals(type) || "text".equals(type);
    }
}
