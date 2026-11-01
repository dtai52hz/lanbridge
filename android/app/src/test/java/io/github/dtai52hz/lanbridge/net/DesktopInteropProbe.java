package io.github.dtai52hz.lanbridge.net;

import org.json.JSONObject;
import java.util.Arrays;
import java.util.Base64;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;

/** Run by scripts/test-android-interop.ps1 against the actual C# transport, never a LAN host. */
public final class DesktopInteropProbe {
    public static void main(String[] args) throws Exception {
        if (args.length != 2 || !args[0].equals("device") && !args[0].equals("code")) throw new IllegalArgumentException("Choose device or code.");
        PairingCode pairing = PairingCode.parse(args[1]);
        if (!pairing.address.equals("127.0.0.1")) throw new IllegalArgumentException("This test permits loopback only.");
        CountDownLatch done = new CountDownLatch(1);
        AtomicReference<String> error = new AtomicReference<>();
        AtomicReference<LanBridgeClient> ref = new AtomicReference<>();
        AtomicBoolean frameReceived = new AtomicBoolean();
        LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() {
            @Override public void onPermissions(boolean allowed) {
                if (!allowed) return;
                // Screen permission callback follows this one; send input there so order is deterministic.
            }
            @Override public void onScreenPermission(boolean allowed) {
                if (!allowed) return;
                LanBridgeClient session = ref.get();
                if (!session.requestScreen(true) || !session.enter() || !session.moveTo(12345, 54321)
                        || !session.button("left", true) || !session.button("left", false)
                        || !session.key(65, true) || !session.key(65, false)
                        || !session.text("một tiếng Việt 😀 . , ? !")) {
                    error.set("An authorized input request failed."); done.countDown();
                }
            }
            @Override public void onPacket(JSONObject packet) {
                if (packet.optString("type").equals("screen-frame")) {
                    byte[] payload = Base64.getDecoder().decode(packet.optString("data"));
                    if (!Arrays.equals(payload, new byte[]{(byte) 0xff, (byte) 0xd8, (byte) 0xff, (byte) 0xd9})
                            || packet.optInt("width") != 1 || packet.optInt("height") != 1 || packet.optInt("encoding") != 1)
                        error.set("C# byte[] / JSON Base64 screen-frame mismatch.");
                    frameReceived.set(true);
                } else if (packet.optString("type").equals("chat") && packet.optString("text").equals("interop-ok")) done.countDown();
            }
            @Override public void onError(String reason) { error.set(reason); done.countDown(); }
        });
        ref.set(client);
        try {
            if (args[0].equals("device"))
                client.connectDevice(new Device("loopback-test", "PC", pairing.address, pairing.port, pairing.fingerprint, false, 0), "Android interop");
            else client.connectCode(args[1], "Android interop");
            if (!done.await(12, TimeUnit.SECONDS)) throw new AssertionError("Loopback interop timed out.");
            if (error.get() != null) throw new AssertionError(error.get());
            if (!frameReceived.get()) throw new AssertionError("Screen frame was not delivered.");
            if (client.isInputAllowed() || client.requestScreen(true) || client.text("blocked"))
                throw new AssertionError("Revoked permissions were not applied.");
        } finally { client.close(); }
        System.out.println("PASS Java Android protocol -> .NET: " + args[0]);
    }
}
