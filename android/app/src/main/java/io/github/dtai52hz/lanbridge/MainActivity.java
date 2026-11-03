package io.github.dtai52hz.lanbridge;

import android.app.Activity;
import android.app.AlertDialog;
import android.graphics.Bitmap;
import android.graphics.BitmapFactory;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.GradientDrawable;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Bundle;
import android.text.InputFilter;
import android.text.InputType;
import android.view.Gravity;
import android.view.View;
import android.view.WindowInsets;
import android.view.WindowManager;
import android.view.inputmethod.InputMethodManager;
import android.widget.Button;
import android.widget.EditText;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import android.widget.Toast;
import org.json.JSONObject;
import java.util.Base64;
import java.util.List;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.atomic.AtomicBoolean;
import java.util.concurrent.atomic.AtomicReference;
import io.github.dtai52hz.lanbridge.net.Device;
import io.github.dtai52hz.lanbridge.net.LanBridgeClient;
import io.github.dtai52hz.lanbridge.net.LanDiscovery;

public final class MainActivity extends Activity {
    private static final int BG = Color.rgb(15, 20, 29), CARD = Color.rgb(25, 35, 48), ACCENT = Color.rgb(40, 200, 176), MUTED = Color.rgb(162, 182, 201);
    private LinearLayout root, connectPage, sessionPage, devices, keyboardPanel;
    private TextView status, hint, machine;
    private EditText code, typing;
    private RemoteSurface surface;
    private Button screenButton, padButton, dragButton, keyboardButton;
    private FilePanelController filePanel;
    private boolean keyboardVisible;
    private LanDiscovery discovery;
    private WifiManager.MulticastLock multicast;
    private LanBridgeClient client;
    private boolean ready, screenCapable, screenAllowed, inputAllowed, direct, foreground, dragging, viewing;
    private volatile boolean destroyed;
    private volatile long viewEpoch;
    private final ExecutorService decoder = Executors.newSingleThreadExecutor();
    private final AtomicReference<IncomingFrame> latestFrame = new AtomicReference<>();
    private final AtomicBoolean decoding = new AtomicBoolean();
    private final AtomicReference<DecodedFrame> latestDecoded = new AtomicReference<>();
    private final AtomicBoolean applyPosted = new AtomicBoolean();
    private record IncomingFrame(LanBridgeClient session, JSONObject packet, long epoch) { }
    private record DecodedFrame(LanBridgeClient session, Bitmap bitmap, long epoch) { }

    @Override public void onCreate(Bundle state) {
        super.onCreate(state);
        getWindow().setSoftInputMode(WindowManager.LayoutParams.SOFT_INPUT_ADJUST_RESIZE);
        root = column(); root.setPadding(dp(18), dp(12), dp(18), dp(10)); root.setBackgroundColor(BG);
        setContentView(root);
        root.setOnApplyWindowInsetsListener((view, insets) -> {
            if (Build.VERSION.SDK_INT >= 30) {
                android.graphics.Insets bars = insets.getInsets(WindowInsets.Type.systemBars() | WindowInsets.Type.displayCutout());
                int bottom = Math.max(bars.bottom, insets.getInsets(WindowInsets.Type.ime()).bottom);
                view.setPadding(dp(18) + bars.left, dp(12) + bars.top, dp(18) + bars.right, dp(10) + bottom);
            } else view.setPadding(dp(18), dp(12) + insets.getSystemWindowInsetTop(), dp(18), dp(10) + insets.getSystemWindowInsetBottom());
            return insets;
        });
        TextView title = text("LanBridge", 29, Color.WHITE); title.setTypeface(null, Typeface.BOLD); root.addView(title);
        root.addView(text("ĐIỀU KHIỂN MÁY TÍNH TỪ ĐIỆN THOẠI", 10, ACCENT));
        status = text("Cùng Wi-Fi / mạng LAN với máy tính", 13, MUTED); spaced(root, status, 12);
        buildConnectPage(); buildSessionPage(); showConnect();
        filePanel = new FilePanelController(this, this::releaseForLocalUi, state);
        if (Build.VERSION.SDK_INT >= 33) getOnBackInvokedDispatcher().registerOnBackInvokedCallback(
                android.window.OnBackInvokedDispatcher.PRIORITY_DEFAULT, this::handleBack);
    }

    private void buildConnectPage() {
        connectPage = column(); root.addView(connectPage, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout heading = row(); heading.addView(text("Máy tính trong mạng", 18, Color.WHITE), new LinearLayout.LayoutParams(0, -2, 1));
        heading.addView(button("Làm mới", v -> { if (discovery != null) discovery.refresh(); else startDiscovery(); })); connectPage.addView(heading);
        connectPage.addView(text("Mở LanBridge trên máy tính. Chọn máy rồi chấp nhận yêu cầu ở máy đó.", 13, MUTED));
        ScrollView list = new ScrollView(this); devices = column(); list.addView(devices); spaced(connectPage, list, 10, new LinearLayout.LayoutParams(-1, 0, 1));
        renderDevices(java.util.Collections.emptyList());
        TextView manual = text("KẾT NỐI BẰNG MÃ", 11, ACCENT); spaced(connectPage, manual, 12);
        code = edit("Dán mã LB1 từ máy tính", false); code.setFilters(new InputFilter[]{new InputFilter.LengthFilter(256)});
        code.setInputType(InputType.TYPE_CLASS_TEXT | InputType.TYPE_TEXT_VARIATION_VISIBLE_PASSWORD);
        code.setImportantForAutofill(View.IMPORTANT_FOR_AUTOFILL_NO); spaced(connectPage, code, 8);
        spaced(connectPage, button("Kết nối bằng mã", v -> connect(null)), 8);
        spaced(connectPage, button("Tệp đã nhận", v -> { if (filePanel != null) filePanel.show(); }), 4);
        spaced(connectPage, text(androidVersion() + " · Xem màn hình cần Windows 0.7.3 hoặc Desktop 0.8 preview.2.", 11, MUTED), 12);
    }
    private void buildSessionPage() {
        sessionPage = column(); root.addView(sessionPage, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout top = row(); machine = text("Máy tính", 17, Color.WHITE); top.addView(machine, new LinearLayout.LayoutParams(0, -2, 1));
        top.addView(button("Ngắt", v -> disconnect())); sessionPage.addView(top);
        LinearLayout modes = row(); screenButton = button("Xem màn hình", v -> setDirect(true)); padButton = button("Bàn rê chuột", v -> setDirect(false));
        modes.addView(screenButton, equal()); modes.addView(padButton, equal()); spaced(sessionPage, modes, 6);
        hint = text("Chờ máy tính cấp quyền…", 12, MUTED); spaced(sessionPage, hint, 7);
        surface = new RemoteSurface(this, new RemoteSurface.Sink() {
            public boolean move(int x, int y) { return ensureControl() && client.moveTo(x, y); }
            public void button(String button, boolean down) { if (ensureControl()) client.button(button, down); }
            public void wheel(int dx, int dy) { if (ensureControl()) client.wheel(dx, dy); }
        });
        spaced(sessionPage, surface, 8, new LinearLayout.LayoutParams(-1, 0, 1));
        LinearLayout buttons = row(); buttons.addView(button("Trái", v -> click("left")), equal());
        buttons.addView(button("Phải", v -> click("right")), equal()); dragButton = button("Giữ trái", v -> toggleDrag()); buttons.addView(dragButton, equal());
        spaced(sessionPage, buttons, 5);
        LinearLayout tools = row();
        keyboardButton = button("Bàn phím", v -> setKeyboardVisible(!keyboardVisible)); tools.addView(keyboardButton, equal());
        tools.addView(button("Tệp", v -> { if (filePanel != null) filePanel.show(); }), equal());
        tools.addView(button("Phím", v -> showSpecialKeys()), equal()); spaced(sessionPage, tools, 3);
        keyboardPanel = column(); keyboardPanel.setVisibility(View.GONE); sessionPage.addView(keyboardPanel);
        LinearLayout input = row(); typing = edit("Nhập tiếng Việt ở đây…", true); typing.setMaxLines(3);
        typing.setFilters(new InputFilter[]{new InputFilter.LengthFilter(4096)}); input.addView(typing, new LinearLayout.LayoutParams(0, -2, 1));
        input.addView(button("Xóa", v -> typing.setText(""))); input.addView(button("Gửi", v -> sendText())); spaced(keyboardPanel, input, 4);
    }
    private void connect(Device device) {
        if (client != null) { toast("Đang có phiên hoặc yêu cầu kết nối. Bấm Ngắt để thử lại."); return; }
        stopDiscovery(); ready = screenCapable = screenAllowed = inputAllowed = false; direct = false;
        LanBridgeClient next = new LanBridgeClient(new LanBridgeClient.Listener() {
            private LanBridgeClient owner;
            private LanBridgeClient owner() { if (owner == null) owner = client; return owner; }
            private void ui(Runnable action) {
                LanBridgeClient expected = owner();
                runOnUiThread(() -> { if (!destroyed && client == expected) action.run(); });
            }
            public void onStatus(String value) { ui(() -> status.setText(value)); }
            public void onReady(String name, int width, int height) { ui(() -> {
                ready = true; machine.setText(name); surface.setDesktopSize(width, height);
                connectPage.setVisibility(View.GONE); sessionPage.setVisibility(View.VISIBLE);
                status.setText("Đã kết nối · TLS"); surface.setDirect(false); refreshHint();
                getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
            }); }
            public void onPermissions(boolean allowed) { ui(() -> { inputAllowed = allowed; surface.setControlEnabled(allowed); if (!allowed) resetDrag(); refreshHint(); }); }
            public void onFilesPermission(boolean allowed) { ui(() -> filePanel.setPeerAllowed(allowed)); }
            public void onScreenCapability(boolean supported) { ui(() -> { screenCapable = supported; refreshHint(); }); }
            public void onScreenPermission(boolean allowed) { ui(() -> {
                screenAllowed = allowed; if (!allowed) { stopFrames(); surface.cancelGesture(); resetDrag(); }
                else if (direct && foreground) client.requestScreen(true);
                refreshHint();
            }); }
            public void onError(String message) { ui(() -> status.setText(message)); }
            public void onClosed(String reason) { ui(() -> {
                client = null; ready = false; stopFrames(); resetDrag(); surface.cancelGesture();
                filePanel.detach(); setKeyboardVisible(false);
                showConnect(); status.setText(reason == null ? "Đã ngắt kết nối" : reason); if (foreground) startDiscovery();
            }); }
            public void onPacket(JSONObject packet) {
                LanBridgeClient expected = owner();
                if ("screen-frame".equals(packet.optString("type"))) enqueueFrame(new IncomingFrame(expected, packet, viewEpoch));
                else if ("screen-state".equals(packet.optString("type"))) ui(() -> {
                    if (direct) hint.setText(packet.optString("text", "Chờ ảnh từ máy tính…"));
                    if (!packet.optBoolean("allow")) { stopFrames(); surface.cancelGesture(); resetDrag(); }
                    else if (direct && screenAllowed && foreground) viewing = true;
                });
            }
        });
        client = next;
        filePanel.attach(next);
        try {
            String name = "Android · " + Build.MODEL; if (name.length() > 70) name = name.substring(0, 70);
            if (device == null) next.connectCode(code.getText().toString().trim(), name); else next.connectDevice(device, name);
            sessionPage.setVisibility(View.VISIBLE); connectPage.setVisibility(View.GONE); machine.setText("Đang ghép nối…");
            surface.setControlEnabled(false); surface.clearImage(); refreshHint();
        } catch (IllegalArgumentException exception) { next.close(); client = null; filePanel.detach(); showConnect(); toast(exception.getMessage()); if (foreground) startDiscovery(); }
    }
    private void disconnect() {
        LanBridgeClient previous = client;
        if (previous == null) return;
        surface.cancelGesture(); resetDrag(); previous.requestScreen(false); previous.leave(); previous.close();
    }
    private boolean ensureControl() {
        return foreground && ready && inputAllowed && client != null && (client.isEntered() || client.enter());
    }
    private void setDirect(boolean value) {
        surface.cancelGesture(); resetDrag(); stopFrames(); direct = value; surface.setDirect(value);
        if (client != null) client.requestScreen(value && screenCapable && screenAllowed && foreground);
        refreshHint();
    }
    private void refreshHint() {
        screenButton.setTextColor(direct ? ACCENT : Color.WHITE); padButton.setTextColor(direct ? Color.WHITE : ACCENT);
        if (!ready) hint.setText("Chấp nhận yêu cầu kết nối trên máy tính.");
        else if (direct && !screenCapable) hint.setText("Cập nhật máy tính: Windows 0.7.3 / Desktop 0.8 preview.2 để xem màn hình.");
        else if (direct && !screenAllowed) hint.setText("Trên máy tính, bật quyền Xem màn hình cho phiên này.");
        else if (!inputAllowed) hint.setText("Trên máy tính, bật quyền Chuột / phím để điều khiển.");
        else hint.setText(direct ? "Chạm và kéo để điều khiển · Hai ngón để cuộn" : "Vuốt để rê chuột · Hai ngón để cuộn · Nhấn giữ để bấm phải");
    }
    private void click(String button) { if (ensureControl()) { client.button(button, true); client.button(button, false); } }
    private void toggleDrag() {
        if (!ensureControl()) return;
        surface.cancelGesture(); dragging = !dragging; surface.setManualDrag(dragging);
        client.button("left", dragging); dragButton.setText(dragging ? "Nhả trái" : "Giữ trái");
    }
    private void resetDrag() { if (dragging && client != null) client.button("left", false); dragging = false; if (surface != null) surface.setManualDrag(false); if (dragButton != null) dragButton.setText("Giữ trái"); }
    private void press(int key) { if (ensureControl()) { client.key(key, true); client.key(key, false); } }
    private void shortcut(int modifier, int key) {
        if (ensureControl()) { client.key(modifier, true); client.key(key, true); client.key(key, false); client.key(modifier, false); }
    }
    private void setKeyboardVisible(boolean visible) {
        surface.cancelGesture(); resetDrag(); keyboardVisible = visible;
        keyboardPanel.setVisibility(visible ? View.VISIBLE : View.GONE);
        keyboardButton.setText(visible ? "Ẩn phím" : "Bàn phím");
        InputMethodManager keyboard = (InputMethodManager) getSystemService(INPUT_METHOD_SERVICE);
        if (visible) { typing.requestFocus(); typing.post(() -> { if (keyboardVisible && keyboard != null) keyboard.showSoftInput(typing, InputMethodManager.SHOW_IMPLICIT); }); }
        else { typing.clearFocus(); if (keyboard != null) keyboard.hideSoftInputFromWindow(typing.getWindowToken(), 0); }
    }
    private void releaseForLocalUi() {
        surface.cancelGesture(); resetDrag();
        if (client != null) client.leave();
        setKeyboardVisible(false);
    }
    private void showSpecialKeys() {
        releaseForLocalUi();
        String[] labels = { "Esc", "Tab", "Xóa lùi ⌫", "Enter", "Mũi tên ↑", "Mũi tên ↓", "Mũi tên ←", "Mũi tên →",
            "Ctrl + C", "Ctrl + V", "Ctrl + A", "Alt + Tab", "Shift + Tab", "⌘ + C", "⌘ + V" };
        int[] keys = { 0x1B, 0x09, 0x08, 0x0D, 0x26, 0x28, 0x25, 0x27, 0x43, 0x56, 0x41, 0x09, 0x09, 0x43, 0x56 };
        new AlertDialog.Builder(this).setTitle("Phím và tổ hợp trên máy tính").setItems(labels, (dialog, index) -> {
            if (index < 8) press(keys[index]);
            else shortcut(index < 11 ? 0x11 : index == 11 ? 0x12 : index == 12 ? 0x10 : 0x5B, keys[index]);
        }).setNegativeButton("Đóng", null).show();
    }
    @Override protected void onActivityResult(int request, int result, android.content.Intent data) {
        super.onActivityResult(request, result, data);
        if (filePanel != null) filePanel.activityResult(request, result, data);
    }
    @Override protected void onSaveInstanceState(Bundle state) { if (filePanel != null) filePanel.saveState(state); super.onSaveInstanceState(state); }
    private void sendText() {
        if (!ensureControl()) { toast("Máy tính chưa cấp quyền Chuột / phím."); return; }
        String value = typing.getText().toString(); if (value.isEmpty()) return;
        // Send committed IME text in bounded runs. Enter/Tab are physical keys, never text controls.
        StringBuilder run = new StringBuilder(); boolean sent = true;
        for (int i = 0; i < value.length() && sent;) {
            int cp = value.codePointAt(i); i += Character.charCount(cp);
            if (cp == '\r') continue;
            if (cp == '\n' || cp == '\t') {
                if (run.length() > 0) { sent = client.text(run.toString()); run.setLength(0); }
                int key = cp == '\n' ? 0x0D : 0x09;
                if (sent) sent = client.key(key, true) && client.key(key, false);
            } else if (!Character.isISOControl(cp)) {
                if (run.length() + Character.charCount(cp) > 240) { sent = client.text(run.toString()); run.setLength(0); }
                run.appendCodePoint(cp);
            }
        }
        if (sent && run.length() > 0) sent = client.text(run.toString());
        if (sent) typing.setText(""); else toast("Chưa gửi hết chữ. Kiểm tra quyền và kết nối trước khi gửi lại.");
    }
    private void enqueueFrame(IncomingFrame frame) {
        if (destroyed) return;
        latestFrame.set(frame);
        scheduleDecode();
    }
    private void scheduleDecode() {
        if (decoding.compareAndSet(false, true)) {
            try { decoder.execute(this::decodeFrames); }
            catch (java.util.concurrent.RejectedExecutionException ignored) { decoding.set(false); latestFrame.set(null); }
        }
    }
    private void decodeFrames() {
        try {
            IncomingFrame frame;
            while ((frame = latestFrame.getAndSet(null)) != null) {
                IncomingFrame current = frame;
                try {
                    byte[] jpeg = Base64.getDecoder().decode(frame.packet.optString("data"));
                    if (jpeg.length < 4 || jpeg.length > 512 * 1024 || jpeg[0] != (byte) 0xff || jpeg[1] != (byte) 0xd8) throw new IllegalArgumentException();
                    BitmapFactory.Options bounds = new BitmapFactory.Options(); bounds.inJustDecodeBounds = true;
                    BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length, bounds);
                    if (bounds.outWidth < 1 || bounds.outWidth > 1280 || bounds.outHeight < 1 || bounds.outHeight > 800
                            || bounds.outWidth != frame.packet.optInt("width") || bounds.outHeight != frame.packet.optInt("height")) throw new IllegalArgumentException();
                    Bitmap bitmap = BitmapFactory.decodeByteArray(jpeg, 0, jpeg.length);
                    if (bitmap == null) throw new IllegalArgumentException();
                    latestDecoded.set(new DecodedFrame(current.session, bitmap, current.epoch));
                    scheduleApply();
                } catch (IllegalArgumentException exception) {
                    runOnUiThread(() -> {
                        if (client == current.session && current.epoch == viewEpoch && direct) {
                            stopFrames(); surface.cancelGesture(); resetDrag(); client.requestScreen(false);
                            hint.setText("Không đọc được ảnh từ máy tính; đã dừng xem màn hình.");
                        }
                    });
                }
            }
        } finally {
            decoding.set(false);
            if (latestFrame.get() != null) scheduleDecode();
        }
    }
    private void scheduleApply() {
        if (!applyPosted.compareAndSet(false, true)) return;
        root.post(() -> {
            DecodedFrame next = latestDecoded.getAndSet(null);
            if (next != null && !destroyed && client == next.session && next.epoch == viewEpoch
                    && foreground && direct && screenAllowed && viewing) surface.setImage(next.bitmap);
            applyPosted.set(false);
            if (latestDecoded.get() != null && !destroyed) scheduleApply();
        });
    }
    private void stopFrames() { viewing = false; ++viewEpoch; latestFrame.set(null); latestDecoded.set(null); surface.clearImage(); }
    private void showConnect() {
        connectPage.setVisibility(View.VISIBLE); sessionPage.setVisibility(View.GONE);
        getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
    }
    private void startDiscovery() {
        if (discovery != null || client != null || destroyed) return;
        try {
            WifiManager wifi = (WifiManager) getApplicationContext().getSystemService(WIFI_SERVICE);
            if (wifi != null) { multicast = wifi.createMulticastLock("LanBridge-discovery"); multicast.setReferenceCounted(false); multicast.acquire(); }
            discovery = new LanDiscovery(new LanDiscovery.Listener() {
                public void onDevices(List<Device> values) { runOnUiThread(() -> { if (!destroyed && client == null) renderDevices(values); }); }
                public void onError(String message) { runOnUiThread(() -> { if (!destroyed && client == null) status.setText(message); }); }
            }); discovery.start();
        } catch (RuntimeException exception) { stopDiscovery(); status.setText("Chưa tìm được máy. Bạn có thể kết nối bằng mã."); }
    }
    private void stopDiscovery() {
        if (discovery != null) { discovery.close(); discovery = null; }
        if (multicast != null) { if (multicast.isHeld()) multicast.release(); multicast = null; }
    }
    private void renderDevices(List<Device> values) {
        devices.removeAllViews();
        if (values.isEmpty()) { spaced(devices, text("Đang tìm máy…\n\nNếu chưa thấy: mở LanBridge trên máy tính, kiểm tra cùng mạng và tường lửa, hoặc dán mã LB1 bên dưới.", 14, MUTED), 20); return; }
        for (Device device : values) {
            Button item = button(device.name + "\n" + device.address + (device.busy ? " · Đang bận" : " · Sẵn sàng"), v -> {
                if (device.busy) toast("Máy này đang bận. Thử lại sau khi phiên cũ kết thúc."); else connect(device);
            }); item.setGravity(Gravity.START | Gravity.CENTER_VERTICAL); item.setMinHeight(dp(76)); spaced(devices, item, 8);
        }
    }
    @Override protected void onStart() { super.onStart(); foreground = true; if (filePanel != null) filePanel.foreground(true); if (client == null) startDiscovery(); else if (direct && screenAllowed) client.requestScreen(true); }
    @Override protected void onStop() {
        foreground = false; stopDiscovery(); surface.cancelGesture(); resetDrag(); stopFrames();
        if (filePanel != null) filePanel.foreground(false);
        if (client != null) { client.requestScreen(false); client.leave(); }
        super.onStop();
    }
    @Override protected void onDestroy() { destroyed = true; stopDiscovery(); if (client != null) client.close(); client = null; stopFrames(); if (filePanel != null) filePanel.destroy(); decoder.shutdownNow(); super.onDestroy(); }
    // API 33+ uses the native predictive-back callback registered above; this is the API 26–32 fallback.
    @android.annotation.SuppressLint("GestureBackNavigation")
    @Override public void onBackPressed() { handleBack(); }
    private void handleBack() {
        if (keyboardVisible) { setKeyboardVisible(false); return; }
        if (client == null) { finish(); return; }
        new AlertDialog.Builder(this).setMessage("Ngắt kết nối với máy tính?").setPositiveButton("Ngắt kết nối", (dialog, which) -> disconnect()).setNegativeButton("Tiếp tục", null).show();
    }
    private LinearLayout column() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.VERTICAL); return view; }
    private LinearLayout row() { LinearLayout view = new LinearLayout(this); view.setOrientation(LinearLayout.HORIZONTAL); view.setGravity(Gravity.CENTER_VERTICAL); return view; }
    private TextView text(String value, float size, int color) { TextView view = new TextView(this); view.setText(value); view.setTextSize(size); view.setTextColor(color); return view; }
    private EditText edit(String placeholder, boolean multiline) {
        EditText view = new EditText(this); view.setHint(placeholder); view.setHintTextColor(MUTED); view.setTextColor(Color.WHITE); view.setTextSize(14);
        view.setInputType(InputType.TYPE_CLASS_TEXT | (multiline ? InputType.TYPE_TEXT_FLAG_MULTI_LINE : 0)); view.setPadding(dp(12), dp(10), dp(12), dp(10));
        view.setBackground(background(CARD)); return view;
    }
    private Button button(String value, View.OnClickListener listener) {
        Button view = new Button(this); view.setText(value); view.setTextColor(Color.WHITE); view.setTextSize(12); view.setAllCaps(false); view.setMinHeight(dp(44));
        view.setMinimumWidth(0); view.setMinWidth(0); view.setPadding(dp(9), dp(6), dp(9), dp(6)); view.setBackground(background(CARD)); view.setOnClickListener(listener); return view;
    }
    private GradientDrawable background(int color) { GradientDrawable shape = new GradientDrawable(); shape.setColor(color); shape.setCornerRadius(dp(10)); shape.setStroke(dp(1), Color.rgb(40, 55, 71)); return shape; }
    private LinearLayout.LayoutParams equal() { LinearLayout.LayoutParams params = new LinearLayout.LayoutParams(0, -2, 1); params.setMargins(dp(2), 0, dp(2), 0); return params; }
    private void spaced(LinearLayout parent, View child, int margin) { spaced(parent, child, margin, new LinearLayout.LayoutParams(-1, -2)); }
    private void spaced(LinearLayout parent, View child, int margin, LinearLayout.LayoutParams params) { params.topMargin = dp(margin); parent.addView(child, params); }
    private int dp(float value) { return Math.round(value * getResources().getDisplayMetrics().density); }
    private String androidVersion() {
        try { String version = getPackageManager().getPackageInfo(getPackageName(), 0).versionName; return version == null ? "Android" : "Android " + version; }
        catch (android.content.pm.PackageManager.NameNotFoundException ignored) { return "Android"; }
    }
    private void toast(String message) { Toast.makeText(this, message, Toast.LENGTH_LONG).show(); }
}
