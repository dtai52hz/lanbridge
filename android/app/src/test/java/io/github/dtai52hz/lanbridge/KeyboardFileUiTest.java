package io.github.dtai52hz.lanbridge;

import android.app.AlertDialog;
import android.os.Handler;
import android.os.Looper;
import android.os.Bundle;
import android.content.Intent;
import android.net.Uri;
import android.view.View;
import android.view.ViewGroup;
import android.widget.EditText;
import android.widget.TextView;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.Robolectric;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.android.controller.ActivityController;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.LooperMode;
import org.robolectric.shadows.ShadowAlertDialog;
import org.robolectric.Shadows;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.shadows.ShadowContentResolver;
import java.io.OutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import java.io.File;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.util.concurrent.CompletableFuture;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;
import java.lang.reflect.Proxy;
import java.lang.reflect.Constructor;
import org.json.JSONObject;
import java.security.MessageDigest;
import java.util.Base64;
import io.github.dtai52hz.lanbridge.net.FileTransferClient;
import io.github.dtai52hz.lanbridge.net.LanBridgeClient;
import static org.junit.Assert.*;

/** Create-only activities and fake documents; no discovery, network or phone input. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28, qualifiers = "w390dp-h844dp-mdpi")
@LooperMode(LooperMode.Mode.PAUSED)
public final class KeyboardFileUiTest {
    @Test public void keyboardIsEasyToToggleAndClearsOnlyPhoneDraft() throws Exception {
        try (ActivityController<MainActivity> lifecycle = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = lifecycle.get(); View root = field(activity, "root");
            View keyboard = field(activity, "keyboardPanel"); EditText typing = field(activity, "typing");
            assertEquals(View.GONE, keyboard.getVisibility());
            find(root, "Bàn phím").performClick(); Shadows.shadowOf(Looper.getMainLooper()).idle();
            assertEquals(View.VISIBLE, keyboard.getVisibility()); assertTrue(field(activity, "keyboardVisible"));
            typing.setText("Một bản nháp tiếng Việt"); find(root, "Xóa").performClick(); assertEquals("", typing.getText().toString());
            typing.setText("Giữ lại nháp"); find(root, "Ẩn phím").performClick();
            assertEquals(View.GONE, keyboard.getVisibility()); assertEquals("Giữ lại nháp", typing.getText().toString());
        }
    }
    @Test public void filesPanelIsAvailableWithoutAConnectionAndDoesNotRequestStoragePermission() throws Exception {
        try (ActivityController<MainActivity> lifecycle = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = lifecycle.get(); find(field(activity, "root"), "Tệp đã nhận").performClick();
            AlertDialog dialog = ShadowAlertDialog.getLatestAlertDialog(); assertNotNull(dialog); assertTrue(dialog.isShowing());
            assertNotNull(find(dialog.getWindow().getDecorView(), "Gửi tệp từ điện thoại"));
            assertFalse(find(dialog.getWindow().getDecorView(), "Gửi tệp từ điện thoại").isEnabled());
            assertNull(Shadows.shadowOf(activity).getNextStartedActivityForResult());
            assertNull(field(activity, "client"));
        }
    }
    @Test public void completedReceiptSurvivesBlockedUiThenDetachAndReload() throws Exception {
        try (ActivityController<MainActivity> lifecycle = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = lifecycle.get(); FilePanelController panel = field(activity, "filePanel");
            AndroidFileStorage storage = null;
            for (int i = 0; i < 1000 && storage == null; i++) {
                Shadows.shadowOf(Looper.getMainLooper()).idle(); storage = field(panel, "storage"); if (storage == null) Thread.sleep(2);
            }
            assertNotNull(storage);
            AndroidFileStorage.ReceiptDestination destination = storage.destination("durable.txt");
            byte[] payload = "Hoàn tất trước khi UI chạy".getBytes(StandardCharsets.UTF_8);
            AtomicBoolean visualCallback = new AtomicBoolean();
            CompletableFuture.runAsync(() -> {
                try (OutputStream output = destination.open()) { output.write(payload); } catch (Exception error) { throw new RuntimeException(error); }
                try { destination.finish(); } catch (Exception error) { throw new RuntimeException(error); }
                new Handler(Looper.getMainLooper()).post(() -> visualCallback.set(true));
            }).get(5, java.util.concurrent.TimeUnit.SECONDS);
            assertFalse("The test must keep the UI completion callback blocked", visualCallback.get());
            panel.detach();
            AndroidFileStorage reloaded = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
            assertEquals(1, reloaded.receipts().size());
            assertArrayEquals(payload, Files.readAllBytes(reloaded.receipts().get(0).file.toPath()));
        }
    }
    @Test public void pendingSaveSelectionSurvivesRecreateAndExportsWithoutConnection() throws Exception {
        byte[] payload = "Lưu bản đã xác minh sau khi mở lại".getBytes(StandardCharsets.UTF_8);
        Bundle state = new Bundle();
        AndroidFileStorage.Receipt receipt;
        try (ActivityController<MainActivity> first = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = first.get(); FilePanelController panel = field(activity, "filePanel");
            AndroidFileStorage storage = awaitStorage(panel);
            AndroidFileStorage.ReceiptDestination destination = storage.destination("save-after-recreate.txt");
            try (OutputStream output = destination.open()) { output.write(payload); }
            destination.finish(); receipt = storage.receipts().get(0);
            Method save = FilePanelController.class.getDeclaredMethod("pickSave", AndroidFileStorage.Receipt.class); save.setAccessible(true); save.invoke(panel, receipt);
            assertEquals(Intent.ACTION_CREATE_DOCUMENT, Shadows.shadowOf(activity).getNextStartedActivityForResult().intent.getAction());
            panel.saveState(state); assertEquals(receipt.id, state.getString("lanbridge.pendingSaveId"));
        }
        AndroidFileStorageTest.TestDocumentsProvider provider = new AndroidFileStorageTest.TestDocumentsProvider();
        provider.output = File.createTempFile("save-recreate-", ".txt", RuntimeEnvironment.getApplication().getCacheDir());
        provider.source = provider.output;
        android.content.pm.ProviderInfo info = new android.content.pm.ProviderInfo(); info.authority = "lanbridge.documents"; info.exported = true;
        provider.attachInfo(RuntimeEnvironment.getApplication(), info); ShadowContentResolver.registerProviderInternal(info.authority, provider);
        Bundle selectedState = new Bundle();
        try (ActivityController<MainActivity> next = Robolectric.buildActivity(MainActivity.class).create(state)) {
            MainActivity activity = next.get(); FilePanelController panel = field(activity, "filePanel");
            // Deliver the picker result before the asynchronous private index load.
            panel.activityResult(FilePanelController.PICK_SAVE, android.app.Activity.RESULT_OK,
                new Intent().setData(Uri.parse("content://lanbridge.documents/destination")));
            assertNull(field(panel, "storage"));
            panel.saveState(selectedState);
            assertEquals("content://lanbridge.documents/destination", selectedState.getString("lanbridge.pendingSaveUri"));
        }
        // A second recreation occurs before the private metadata callback can run.
        // No second picker result arrives; the saved URI must be sufficient.
        try (ActivityController<MainActivity> next = Robolectric.buildActivity(MainActivity.class).create(selectedState)) {
            MainActivity activity = next.get();
            for (int i = 0; i < 1500 && provider.output.length() != payload.length; i++) {
                Shadows.shadowOf(Looper.getMainLooper()).idle(); Thread.sleep(2);
            }
            assertArrayEquals(payload, Files.readAllBytes(provider.output.toPath()));
            assertTrue(receipt.file.isFile()); assertNull(field(activity, "client"));
        }
    }
    @Test public void explicitIncomingConsentPersistsBeforeAckWhileUiAndPickerAreAway() throws Exception {
        try (ActivityController<MainActivity> lifecycle = Robolectric.buildActivity(MainActivity.class).create(); FakeFiles link = new FakeFiles()) {
            MainActivity activity = lifecycle.get(); FilePanelController panel = field(activity, "filePanel");
            AndroidFileStorage storage = awaitStorage(panel); panel.attach(link.client); panel.setPeerAllowed(true); panel.foreground(true); panel.show();
            android.widget.CheckBox allow = (android.widget.CheckBox) find(ShadowAlertDialog.getLatestAlertDialog().getWindow().getDecorView(), "Cho phép máy tính gửi tệp");
            assertFalse(allow.isChecked()); allow.performClick();
            String id = "0123456789abcdef0123456789abcdef";
            byte[] payload = "Nhận có xác nhận rồi chuyển sang bộ chọn tệp".getBytes(StandardCharsets.UTF_8);
            link.receive(new JSONObject().put("type", "file-offer").put("id", id).put("name", "incoming.txt").put("length", payload.length));
            for (int i = 0; i < 1000 && field(panel, "pendingOffer") == null; i++) { Shadows.shadowOf(Looper.getMainLooper()).idle(); Thread.sleep(2); }
            assertNotNull(field(panel, "pendingOffer"));
            assertEquals("Showing an offer must not create a partial before consent", 0, storage.incomingDirectory().listFiles().length);
            ShadowAlertDialog.getLatestAlertDialog().getButton(AlertDialog.BUTTON_POSITIVE).performClick();
            Shadows.shadowOf(Looper.getMainLooper()).idle();
            assertTrue(link.accepted.await(5, TimeUnit.SECONDS));
            assertTrue(storage.receipts().isEmpty());
            panel.foreground(false); // File picker/home pauses input, but not an already accepted transfer.
            link.receive(new JSONObject().put("type", "file-chunk").put("id", id).put("length", 0).put("data", Base64.getEncoder().encodeToString(payload)));
            StringBuilder hash = new StringBuilder(); for (byte value : MessageDigest.getInstance("SHA-256").digest(payload)) hash.append(String.format("%02x", value & 255));
            link.receive(new JSONObject().put("type", "file-end").put("id", id).put("text", hash.toString()));
            assertTrue("The accepted transfer should finish while a picker is foreground", link.received.await(5, TimeUnit.SECONDS));
            // No main-looper pumping after chunks: onCompleted is still queued here.
            panel.detach();
            AndroidFileStorage reloaded = new AndroidFileStorage(storage.incomingDirectory().getParentFile());
            assertEquals(1, reloaded.receipts().size());
            assertArrayEquals(payload, Files.readAllBytes(reloaded.receipts().get(0).file.toPath()));
        }
    }
    private static final class FakeFiles implements AutoCloseable {
        final CountDownLatch accepted = new CountDownLatch(1), received = new CountDownLatch(1);
        final LanBridgeClient client = new LanBridgeClient(new LanBridgeClient.Listener() { });
        final FileTransferClient files;
        final Method handle;
        FakeFiles() throws Exception {
            Class<?> transport = Class.forName(FileTransferClient.class.getName() + "$Transport");
            Object sender = Proxy.newProxyInstance(transport.getClassLoader(), new Class<?>[] { transport }, (proxy, method, args) -> {
                JSONObject packet = (JSONObject) args[0];
                if ("file-accept".equals(packet.optString("type")) && packet.optBoolean("allow")) accepted.countDown();
                if ("file-received".equals(packet.optString("type"))) received.countDown();
                return CompletableFuture.completedFuture(null);
            });
            Constructor<FileTransferClient> constructor = FileTransferClient.class.getDeclaredConstructor(transport); constructor.setAccessible(true);
            files = constructor.newInstance(sender); client.files().close();
            Field manager = LanBridgeClient.class.getDeclaredField("files"); manager.setAccessible(true); manager.set(client, files);
            Method permission = FileTransferClient.class.getDeclaredMethod("setRemoteAllowed", boolean.class); permission.setAccessible(true); permission.invoke(files, true);
            handle = FileTransferClient.class.getDeclaredMethod("handle", JSONObject.class); handle.setAccessible(true);
        }
        void receive(JSONObject packet) throws Exception { handle.invoke(files, packet); }
        public void close() { client.close(); files.close(); }
    }
    private static AndroidFileStorage awaitStorage(FilePanelController panel) throws Exception {
        AndroidFileStorage storage = null;
        for (int i = 0; i < 1500 && storage == null; i++) {
            Shadows.shadowOf(Looper.getMainLooper()).idle(); storage = field(panel, "storage"); if (storage == null) Thread.sleep(2);
        }
        assertNotNull(storage); return storage;
    }
    @SuppressWarnings("unchecked") private static <T> T field(Object owner, String name) throws Exception {
        Field field = owner.getClass().getDeclaredField(name); field.setAccessible(true); return (T) field.get(owner);
    }
    private static View find(View view, String value) {
        if (view instanceof TextView && value.contentEquals(((TextView) view).getText())) return view;
        if (view instanceof ViewGroup) {
            ViewGroup parent = (ViewGroup) view;
            for (int i = 0; i < parent.getChildCount(); i++) { View result = find(parent.getChildAt(i), value); if (result != null) return result; }
        }
        return null;
    }
}
