package io.github.dtai52hz.lanbridge;

import android.graphics.Bitmap;
import android.os.Looper;
import android.view.InputDevice;
import android.view.MotionEvent;
import android.view.ViewConfiguration;
import org.junit.Before;
import org.junit.Test;
import org.junit.runner.RunWith;
import org.robolectric.RobolectricTestRunner;
import org.robolectric.RuntimeEnvironment;
import org.robolectric.annotation.Config;
import org.robolectric.annotation.LooperMode;
import java.time.Duration;
import java.util.ArrayList;
import java.util.List;
import static org.junit.Assert.*;
import static org.robolectric.Shadows.shadowOf;

/** Synthetic bitmaps/touch events only; no emulator, network, screen or native input. */
@RunWith(RobolectricTestRunner.class)
@Config(sdk = 28, qualifiers = "mdpi")
@LooperMode(LooperMode.Mode.PAUSED)
public final class RemoteSurfaceTest {
    private record Point(int x, int y) { }
    private record Button(String name, boolean down) { }
    private record Wheel(int dx, int dy) { }
    private static final class Sink implements RemoteSurface.Sink {
        final List<Point> moves = new ArrayList<>();
        final List<Button> buttons = new ArrayList<>();
        final List<Wheel> wheels = new ArrayList<>();
        public boolean move(int x, int y) { moves.add(new Point(x, y)); return true; }
        public void button(String name, boolean down) { buttons.add(new Button(name, down)); }
        public void wheel(int dx, int dy) { wheels.add(new Wheel(dx, dy)); }
    }
    private Sink sink;
    private RemoteSurface view;
    private long time, downTime;

    @Before public void createView() {
        sink = new Sink();
        view = new RemoteSurface(RuntimeEnvironment.getApplication(), sink);
        view.layout(0, 0, 1000, 1000);
        view.setControlEnabled(true);
        time = 1000;
    }
    private void direct(int imageWidth, int imageHeight) {
        view.setImage(Bitmap.createBitmap(imageWidth, imageHeight, Bitmap.Config.ARGB_8888));
        view.setDirect(true);
    }
    private void one(int action, float x, float y) {
        time += 16;
        if (action == MotionEvent.ACTION_DOWN) downTime = time;
        MotionEvent event = MotionEvent.obtain(downTime, time, action, x, y, 0);
        try { assertTrue(view.onTouchEvent(event)); }
        finally { event.recycle(); }
    }
    private void two(int action, float x0, float y0, float x1, float y1) {
        time += 16;
        MotionEvent.PointerProperties[] properties = new MotionEvent.PointerProperties[2];
        MotionEvent.PointerCoords[] coordinates = new MotionEvent.PointerCoords[2];
        for (int i = 0; i < 2; i++) {
            properties[i] = new MotionEvent.PointerProperties(); properties[i].id = i;
            properties[i].toolType = MotionEvent.TOOL_TYPE_FINGER;
            coordinates[i] = new MotionEvent.PointerCoords(); coordinates[i].pressure = 1; coordinates[i].size = 1;
            coordinates[i].x = i == 0 ? x0 : x1; coordinates[i].y = i == 0 ? y0 : y1;
        }
        MotionEvent event = MotionEvent.obtain(downTime, time, action, 2, properties, coordinates,
                0, 0, 1, 1, 0, 0, InputDevice.SOURCE_TOUCHSCREEN, 0);
        try { assertTrue(view.onTouchEvent(event)); }
        finally { event.recycle(); }
    }
    private Point lastPoint() { assertFalse(sink.moves.isEmpty()); return sink.moves.get(sink.moves.size() - 1); }
    private void point(int x, int y) {
        assertEquals(x, lastPoint().x, 1); assertEquals(y, lastPoint().y, 1);
    }
    private static Button down() { return new Button("left", true); }
    private static Button up() { return new Button("left", false); }
    private static List<Button> rightClick() { return List.of(new Button("right", true), new Button("right", false)); }
    private void idle(long milliseconds) {
        time += milliseconds;
        shadowOf(Looper.getMainLooper()).idleFor(Duration.ofMillis(milliseconds));
    }

    @Test public void directCoordinatesExcludeLetterboxAndClampDragToDesktop() {
        direct(1280, 720); // image occupies y=218.75..781.25 in the square view.
        one(MotionEvent.ACTION_DOWN, 500, 100);
        one(MotionEvent.ACTION_MOVE, 500, 500);
        one(MotionEvent.ACTION_UP, 500, 500);
        assertTrue("Letterbox touches must not move or click the PC", sink.moves.isEmpty() && sink.buttons.isEmpty());

        one(MotionEvent.ACTION_DOWN, 500, 500); point(32768, 32768);
        assertEquals(List.of(down()), sink.buttons);
        one(MotionEvent.ACTION_MOVE, -100, -100); point(0, 0);
        one(MotionEvent.ACTION_MOVE, 1200, 1200); point(65535, 65535);
        one(MotionEvent.ACTION_UP, 1200, 1200);
        assertEquals(List.of(down(), up()), sink.buttons);
    }

    @Test public void portraitImageMapsItsOwnRectangleRatherThanViewBounds() {
        view.layout(0, 0, 1000, 600);
        direct(600, 1200); // image occupies x=350..650, y=0..600.
        one(MotionEvent.ACTION_DOWN, 100, 300); one(MotionEvent.ACTION_UP, 100, 300);
        assertTrue(sink.moves.isEmpty()); assertTrue(sink.buttons.isEmpty());
        one(MotionEvent.ACTION_DOWN, 425, 150); point(16384, 16384);
        one(MotionEvent.ACTION_UP, 425, 150);
        assertEquals(List.of(down(), up()), sink.buttons);
    }

    @Test public void cancelAndPermissionRevocationReleaseGestureExactlyOnce() {
        direct(1000, 1000);
        one(MotionEvent.ACTION_DOWN, 200, 200);
        one(MotionEvent.ACTION_CANCEL, 200, 200);
        view.cancelGesture(); one(MotionEvent.ACTION_UP, 200, 200);
        assertEquals(List.of(down(), up()), sink.buttons);
        one(MotionEvent.ACTION_DOWN, 300, 300);
        view.setControlEnabled(false);
        one(MotionEvent.ACTION_MOVE, 600, 600); one(MotionEvent.ACTION_UP, 600, 600);
        assertEquals(List.of(down(), up(), down(), up()), sink.buttons);
        assertEquals("Disabled touches must not continue moving the PC", 2, sink.moves.size());
    }

    @Test public void secondFingerReleasesDirectPressAndScrollDoesNotClickAgain() {
        direct(1000, 1000);
        one(MotionEvent.ACTION_DOWN, 200, 200);
        two(MotionEvent.ACTION_POINTER_DOWN | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT), 200, 200, 400, 200);
        assertEquals(List.of(down(), up()), sink.buttons);
        two(MotionEvent.ACTION_MOVE, 200, 232, 400, 232);
        assertEquals(List.of(new Wheel(0, 120)), sink.wheels);
        two(MotionEvent.ACTION_POINTER_UP | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT), 200, 232, 400, 232);
        one(MotionEvent.ACTION_UP, 200, 232);
        assertEquals("Ending a scroll must not synthesize an additional click", List.of(down(), up()), sink.buttons);
    }

    @Test public void trackpadTapClicksButMovementDoesNotClick() {
        view.setDirect(false); view.setDesktopSize(1920, 1080);
        one(MotionEvent.ACTION_DOWN, 100, 100); one(MotionEvent.ACTION_UP, 100, 100);
        assertEquals(List.of(down(), up()), sink.buttons);
        assertTrue(sink.moves.isEmpty());
        sink.buttons.clear();
        one(MotionEvent.ACTION_DOWN, 100, 100);
        one(MotionEvent.ACTION_MOVE, 196, 154); point(38011, 38011);
        one(MotionEvent.ACTION_UP, 196, 154);
        assertTrue("A pointer movement must not end with an unintended click", sink.buttons.isEmpty());
    }

    @Test public void twoFingerTrackpadScrollPreservesPointerAndAvoidsTapClick() {
        view.setDirect(false);
        one(MotionEvent.ACTION_DOWN, 100, 100);
        two(MotionEvent.ACTION_POINTER_DOWN | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT), 100, 100, 200, 100);
        two(MotionEvent.ACTION_MOVE, 100, 132, 200, 132);
        one(MotionEvent.ACTION_UP, 100, 132);
        assertEquals(List.of(new Wheel(0, 120)), sink.wheels);
        assertTrue("Scrolling may not warp the pointer", sink.moves.isEmpty());
        assertTrue("A two-finger scroll must not also click", sink.buttons.isEmpty());
    }

    @Test public void manualDragOwnsButtonAcrossTrackpadTapDirectGestureAndCancel() {
        // MainActivity owns the actual manual button; the surface must preserve it.
        view.setManualDrag(true); sink.button("left", true);
        one(MotionEvent.ACTION_DOWN, 100, 100); one(MotionEvent.ACTION_UP, 100, 100);
        assertEquals(List.of(down()), sink.buttons);
        direct(1000, 1000);
        one(MotionEvent.ACTION_DOWN, 300, 300);
        one(MotionEvent.ACTION_MOVE, 600, 600);
        one(MotionEvent.ACTION_CANCEL, 600, 600);
        assertEquals("The surface released a button owned by the manual drag control", List.of(down()), sink.buttons);
        point(39321, 39321);
        view.setManualDrag(false); sink.button("left", false);
        one(MotionEvent.ACTION_DOWN, 700, 700); one(MotionEvent.ACTION_UP, 700, 700);
        assertEquals(List.of(down(), up(), down(), up()), sink.buttons);
    }

    @Test public void directModeWithoutImageDoesNotSendBlindPointerInput() {
        view.setDirect(true);
        one(MotionEvent.ACTION_DOWN, 500, 500); one(MotionEvent.ACTION_MOVE, 600, 600); one(MotionEvent.ACTION_UP, 600, 600);
        assertTrue(sink.moves.isEmpty()); assertTrue(sink.buttons.isEmpty());
    }

    @Test public void trackpadLongPressFiresOneRightClickAndNeverClicksLeftOnLift() {
        int timeout = ViewConfiguration.getLongPressTimeout();
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(timeout - 1);
        assertTrue("A hold must wait for Android's long-press timeout", sink.buttons.isEmpty());
        idle(1);
        assertEquals(rightClick(), sink.buttons);
        idle(timeout * 3L);
        assertEquals("Keeping the finger down must not repeat right clicks", rightClick(), sink.buttons);
        one(MotionEvent.ACTION_UP, 100, 100);
        idle(timeout * 2L);
        assertEquals("Lifting after a long press must not also click left", rightClick(), sink.buttons);
        one(MotionEvent.ACTION_DOWN, 100, 100); one(MotionEvent.ACTION_UP, 100, 100);
        assertEquals(List.of(new Button("right", true), new Button("right", false), down(), up()), sink.buttons);
    }

    @Test public void shortTrackpadTapCancelsItsTimerAndStillClicksLeft() {
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() / 4);
        one(MotionEvent.ACTION_UP, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        assertEquals(List.of(down(), up()), sink.buttons);
        assertTrue(sink.wheels.isEmpty());
    }

    @Test public void smallFingerJitterPreservesLongPress() {
        float jitter = ViewConfiguration.get(view.getContext()).getScaledTouchSlop() / 2f;
        one(MotionEvent.ACTION_DOWN, 100, 100);
        one(MotionEvent.ACTION_MOVE, 100 + jitter, 100 + jitter);
        idle(ViewConfiguration.getLongPressTimeout());
        one(MotionEvent.ACTION_UP, 100 + jitter, 100 + jitter);
        assertEquals(rightClick(), sink.buttons);
    }

    @Test public void movingPastTouchSlopCancelsLongPressEvenAfterReturning() {
        float distance = ViewConfiguration.get(view.getContext()).getScaledTouchSlop() + 1;
        one(MotionEvent.ACTION_DOWN, 100, 100);
        one(MotionEvent.ACTION_MOVE, 100 + distance, 100);
        one(MotionEvent.ACTION_MOVE, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        one(MotionEvent.ACTION_UP, 100, 100);
        assertTrue("Moving the pointer must not turn into a delayed click", sink.buttons.isEmpty());
        assertFalse(sink.moves.isEmpty());
    }

    @Test public void secondFingerCancelsPendingLongPressAndKeepsScrolling() {
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() / 2);
        two(MotionEvent.ACTION_POINTER_DOWN | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT), 100, 100, 200, 100);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        two(MotionEvent.ACTION_MOVE, 100, 132, 200, 132);
        two(MotionEvent.ACTION_POINTER_UP | (1 << MotionEvent.ACTION_POINTER_INDEX_SHIFT), 100, 132, 200, 132);
        one(MotionEvent.ACTION_UP, 100, 132);
        assertEquals(List.of(new Wheel(0, 120)), sink.wheels);
        assertTrue("A two-finger gesture must not right-click or left-click", sink.buttons.isEmpty());
    }

    @Test public void cancelRevokeDisableFocusLossAndDetachPreventDelayedClicks() {
        List<Runnable> cancellations = List.of(
                view::cancelGesture,
                () -> one(MotionEvent.ACTION_CANCEL, 100, 100),
                () -> view.setControlEnabled(false),
                () -> view.setEnabled(false),
                () -> view.onWindowFocusChanged(false),
                () -> view.onFocusChanged(false, 0, null),
                view::onDetachedFromWindow,
                () -> view.setDirect(true));
        for (int index = 0; index < cancellations.size(); index++) {
            view.setControlEnabled(true); view.setEnabled(true); view.setDirect(false);
            one(MotionEvent.ACTION_DOWN, 100, 100);
            idle(ViewConfiguration.getLongPressTimeout() / 2);
            cancellations.get(index).run();
            idle(ViewConfiguration.getLongPressTimeout() * 2L);
            one(MotionEvent.ACTION_UP, 100, 100);
            assertTrue("Cancellation " + index + " left a phantom click", sink.buttons.isEmpty());
        }
    }

    @Test public void manualDragCancelsLongPressWithoutReleasingItsOwnedLeftButton() {
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() / 2);
        view.setManualDrag(true); sink.button("left", true);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        one(MotionEvent.ACTION_UP, 100, 100);
        assertEquals(List.of(down()), sink.buttons);
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        one(MotionEvent.ACTION_MOVE, 400, 400);
        one(MotionEvent.ACTION_UP, 400, 400);
        view.cancelGesture();
        assertEquals("A manual drag must never right-click or release its owned button", List.of(down()), sink.buttons);
        view.setManualDrag(false); sink.button("left", false);
        assertEquals(List.of(down(), up()), sink.buttons);
    }

    @Test public void directScreenHoldKeepsExistingLeftDragWithoutRightClick() {
        direct(1000, 1000);
        one(MotionEvent.ACTION_DOWN, 300, 300);
        idle(ViewConfiguration.getLongPressTimeout() * 2L);
        one(MotionEvent.ACTION_MOVE, 600, 600);
        one(MotionEvent.ACTION_UP, 600, 600);
        assertEquals(List.of(down(), up()), sink.buttons);
        point(39321, 39321);
    }

    @Test public void aCancelledHoldCannotRightClickDuringTheNextGesture() {
        int timeout = ViewConfiguration.getLongPressTimeout();
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(timeout / 2);
        view.cancelGesture(); one(MotionEvent.ACTION_UP, 100, 100);
        one(MotionEvent.ACTION_DOWN, 100, 100);
        idle(timeout / 2 + 1);
        assertTrue("The first gesture's timer fired during a later gesture", sink.buttons.isEmpty());
        idle(timeout - (timeout / 2 + 1));
        assertEquals(rightClick(), sink.buttons);
        one(MotionEvent.ACTION_UP, 100, 100);
        assertEquals(rightClick(), sink.buttons);
    }
}
