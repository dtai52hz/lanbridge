package io.github.dtai52hz.lanbridge;

import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
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
import org.robolectric.annotation.GraphicsMode;
import java.io.File;
import java.io.FileOutputStream;
import java.lang.reflect.Field;
import java.lang.reflect.Method;
import static org.junit.Assert.*;

/** Create-only activity: onStart/discovery and any network session never run. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28, qualifiers = "w390dp-h844dp-mdpi")
@GraphicsMode(GraphicsMode.Mode.NATIVE)
public final class MainActivityLayoutTest {
    @Test public void connectPageFitsPhoneViewportAndRendersPreview() throws Exception {
        try (ActivityController<MainActivity> controller = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = controller.get();
            View root = field(activity, "root");
            layout(root, 390, 844);
            assertTrue("Manual code input is outside the phone viewport", within(root, field(activity, "code")));
            View connect = findText(root, "Kết nối bằng mã");
            assertNotNull(connect);
            assertTrue("Manual connect action is outside the phone viewport", within(root, connect));
            render(root, "connect-390x844.png");
        }
    }

    @Test public void controlPageFitsPhoneViewportAndKeepsTypingReachable() throws Exception {
        try (ActivityController<MainActivity> controller = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = controller.get();
            View root = session(activity);
            layout(root, 390, 844);
            RemoteSurface surface = field(activity, "surface");
            assertTrue("The remote surface collapsed at a regular phone size", surface.getHeight() >= 120);
            assertTrue("Typing input is outside the phone viewport", within(root, field(activity, "typing")));
            assertTrue("Send text action is outside the phone viewport", within(root, findText(root, "Gửi")));
            render(root, "control-390x844.png");
        }
    }

    @Test public void keyboardReducedViewportKeepsTypingAndSendActionReachable() throws Exception {
        try (ActivityController<MainActivity> controller = Robolectric.buildActivity(MainActivity.class).create()) {
            MainActivity activity = controller.get();
            View root = session(activity);
            // Equivalent content constraint to adjustResize with a 320 dp IME.
            layout(root, 390, 524);
            EditText typing = field(activity, "typing");
            render(root, "control-keyboard-390x524.png");
            assertTrue("The keyboard-reduced layout hides the typing field", within(root, typing));
            assertTrue("The keyboard-reduced layout hides the Send action", within(root, findText(root, "Gửi")));
        }
    }

    private static View session(MainActivity activity) throws Exception {
        View connect = field(activity, "connectPage"), session = field(activity, "sessionPage");
        connect.setVisibility(View.GONE); session.setVisibility(View.VISIBLE);
        set(activity, "ready", true); set(activity, "screenCapable", true);
        set(activity, "screenAllowed", true); set(activity, "inputAllowed", true); set(activity, "direct", true);
        TextView status = field(activity, "status"), machine = field(activity, "machine");
        status.setText("Đã kết nối · TLS"); machine.setText("Windows · Máy làm việc");
        Method refresh = MainActivity.class.getDeclaredMethod("refreshHint"); refresh.setAccessible(true); refresh.invoke(activity);
        RemoteSurface surface = field(activity, "surface");
        surface.setControlEnabled(true); surface.setDirect(true);
        Bitmap screen = Bitmap.createBitmap(1280, 720, Bitmap.Config.ARGB_8888);
        Canvas canvas = new Canvas(screen); canvas.drawColor(Color.rgb(32, 52, 72));
        Paint paint = new Paint(); paint.setColor(Color.rgb(40, 200, 176)); canvas.drawRect(80, 80, 650, 460, paint);
        paint.setColor(Color.WHITE); paint.setTextSize(52); canvas.drawText("LanBridge · PC", 120, 180, paint);
        surface.setImage(screen);
        Method keyboard = MainActivity.class.getDeclaredMethod("setKeyboardVisible", boolean.class); keyboard.setAccessible(true); keyboard.invoke(activity, true);
        return field(activity, "root");
    }
    private static void layout(View view, int width, int height) {
        view.measure(View.MeasureSpec.makeMeasureSpec(width, View.MeasureSpec.EXACTLY),
                View.MeasureSpec.makeMeasureSpec(height, View.MeasureSpec.EXACTLY));
        view.layout(0, 0, width, height);
    }
    private static boolean within(View root, View view) {
        if (view == null || view.getVisibility() != View.VISIBLE || view.getWidth() <= 0 || view.getHeight() <= 0) return false;
        int left = 0, top = 0;
        for (View current = view; current != root;) {
            left += current.getLeft(); top += current.getTop();
            if (!(current.getParent() instanceof View)) return false;
            current = (View) current.getParent();
        }
        return left >= 0 && top >= 0 && left + view.getWidth() <= root.getWidth() && top + view.getHeight() <= root.getHeight();
    }
    private static View findText(View view, String value) {
        if (view instanceof TextView && value.contentEquals(((TextView) view).getText())) return view;
        if (view instanceof ViewGroup) {
            ViewGroup group = (ViewGroup) view;
            for (int i = 0; i < group.getChildCount(); i++) {
                View result = findText(group.getChildAt(i), value); if (result != null) return result;
            }
        }
        return null;
    }
    @SuppressWarnings("unchecked") private static <T> T field(MainActivity activity, String name) throws Exception {
        Field field = MainActivity.class.getDeclaredField(name); field.setAccessible(true); return (T) field.get(activity);
    }
    private static void set(MainActivity activity, String name, Object value) throws Exception {
        Field field = MainActivity.class.getDeclaredField(name); field.setAccessible(true); field.set(activity, value);
    }
    private static void render(View root, String name) throws Exception {
        Bitmap bitmap = Bitmap.createBitmap(root.getWidth(), root.getHeight(), Bitmap.Config.ARGB_8888);
        root.draw(new Canvas(bitmap));
        File repository = new File(System.getProperty("user.dir")).getCanonicalFile();
        while (repository != null && !new File(repository, "src/LanBridge.csproj").isFile()) repository = repository.getParentFile();
        assertNotNull("Cannot locate the isolated preview output directory", repository);
        File directory = new File(repository, ".tools/android-ui-smoke");
        assertTrue(directory.isDirectory() || directory.mkdirs());
        try (FileOutputStream output = new FileOutputStream(new File(directory, name))) {
            assertTrue("Synthetic Android layout preview did not encode", bitmap.compress(Bitmap.CompressFormat.PNG, 100, output));
        }
        bitmap.recycle();
    }
}
