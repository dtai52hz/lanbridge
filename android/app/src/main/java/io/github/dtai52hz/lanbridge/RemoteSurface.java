package io.github.dtai52hz.lanbridge;

import android.content.Context;
import android.graphics.Bitmap;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.RectF;
import android.view.MotionEvent;
import android.view.View;
import android.view.ViewConfiguration;

/** Touchpad and direct screen control share one cancellation/release path. */
public final class RemoteSurface extends View {
    public interface Sink {
        boolean move(int x, int y);
        void button(String button, boolean down);
        void wheel(int dx, int dy);
    }
    private final Sink sink;
    private final Paint paint = new Paint(Paint.ANTI_ALIAS_FLAG | Paint.FILTER_BITMAP_FLAG);
    private final RectF imageRect = new RectF();
    private Bitmap image;
    private boolean direct, enabled, touching, twoFingers, pressed, moved, manualDrag;
    private float lastX, lastY, startX, startY, wheelRemainder;
    private double pointerX = 32768, pointerY = 32768;
    private int desktopWidth = 1920, desktopHeight = 1080;
    private long downAt;

    public RemoteSurface(Context context, Sink sink) {
        super(context); this.sink = sink;
        setContentDescription("Bàn rê chuột: vuốt một ngón để di chuyển, chạm để bấm, vuốt hai ngón để cuộn.");
        setFocusable(true);
    }
    public void setDirect(boolean value) {
        cancelGesture(); direct = value;
        setContentDescription(value ? "Màn hình máy tính: chạm và kéo để điều khiển, vuốt hai ngón để cuộn."
                : "Bàn rê chuột: vuốt một ngón để di chuyển, chạm để bấm, vuốt hai ngón để cuộn.");
        invalidate();
    }
    public void setControlEnabled(boolean value) { if (!value) cancelGesture(); enabled = value; invalidate(); }
    public void setDesktopSize(int width, int height) { desktopWidth = Math.max(2, width); desktopHeight = Math.max(2, height); }
    public void setImage(Bitmap bitmap) { image = bitmap; invalidate(); }
    public void setManualDrag(boolean value) {
        if (pressed) { sink.button("left", false); pressed = false; }
        manualDrag = value;
    }
    public void clearImage() { image = null; invalidate(); }
    public void cancelGesture() {
        if (pressed) sink.button("left", false);
        pressed = touching = twoFingers = moved = false;
        wheelRemainder = 0;
    }
    private void updateRect() {
        imageRect.setEmpty();
        if (image == null) return;
        float scale = Math.min((float) getWidth() / image.getWidth(), (float) getHeight() / image.getHeight());
        float width = image.getWidth() * scale, height = image.getHeight() * scale;
        imageRect.set((getWidth() - width) / 2, (getHeight() - height) / 2,
                (getWidth() + width) / 2, (getHeight() + height) / 2);
    }
    @Override protected void onDraw(Canvas canvas) {
        canvas.drawColor(Color.rgb(22, 31, 43));
        updateRect();
        if (direct && image != null) {
            canvas.drawBitmap(image, null, imageRect, paint);
            paint.setColor(Color.rgb(40, 200, 176)); paint.setStyle(Paint.Style.STROKE); paint.setStrokeWidth(dp(2));
            canvas.drawCircle(imageRect.left + (float) (pointerX / 65535) * imageRect.width(),
                    imageRect.top + (float) (pointerY / 65535) * imageRect.height(), dp(7), paint);
            paint.setStyle(Paint.Style.FILL);
        } else {
            paint.setColor(Color.rgb(35, 50, 64)); paint.setStrokeWidth(dp(1));
            for (float x = dp(24); x < getWidth(); x += dp(24))
                for (float y = dp(24); y < getHeight(); y += dp(24)) canvas.drawCircle(x, y, dp(1), paint);
            paint.setColor(Color.rgb(154, 178, 197)); paint.setTextSize(dp(16)); paint.setTextAlign(Paint.Align.CENTER);
            canvas.drawText(direct ? "Chờ ảnh từ máy tính…" : "Vuốt để di chuyển chuột", getWidth() / 2f, getHeight() / 2f - dp(10), paint);
            paint.setTextSize(dp(12));
            canvas.drawText(direct ? "Cần bật quyền Xem màn hình trên máy tính" : "Chạm để bấm · Hai ngón để cuộn", getWidth() / 2f, getHeight() / 2f + dp(16), paint);
        }
        if (!enabled) {
            paint.setColor(0x990F141D); canvas.drawRect(0, 0, getWidth(), getHeight(), paint);
            paint.setColor(Color.WHITE); paint.setTextSize(dp(14)); paint.setTextAlign(Paint.Align.CENTER);
            canvas.drawText("Chủ máy cần bật quyền Chuột / phím", getWidth() / 2f, getHeight() - dp(24), paint);
        }
    }
    @Override public boolean onTouchEvent(MotionEvent event) {
        if (!enabled) return true;
        int action = event.getActionMasked();
        if (action == MotionEvent.ACTION_DOWN) {
            cancelGesture(); updateRect();
            if (direct && (image == null || !imageRect.contains(event.getX(), event.getY()))) return true;
            touching = true; lastX = startX = event.getX(); lastY = startY = event.getY(); downAt = event.getEventTime();
            if (direct) { moveDirect(lastX, lastY); if (!manualDrag) { sink.button("left", true); pressed = true; } }
            return true;
        }
        if (!touching) return true;
        if (action == MotionEvent.ACTION_POINTER_DOWN) {
            twoFingers = true; moved = true;
            if (pressed) { sink.button("left", false); pressed = false; }
            lastY = centerY(event); wheelRemainder = 0;
        } else if (action == MotionEvent.ACTION_MOVE) {
            if (event.getPointerCount() >= 2) {
                twoFingers = true;
                float y = centerY(event);
                wheelRemainder += (y - lastY) / dp(32) * 120; lastY = y;
                int steps = (int) wheelRemainder;
                if (Math.abs(steps) >= 20) { sink.wheel(0, steps); wheelRemainder -= steps; }
            } else if (!twoFingers) {
                float x = event.getX(), y = event.getY();
                if (Math.hypot(x - startX, y - startY) > ViewConfiguration.get(getContext()).getScaledTouchSlop()) moved = true;
                if (direct) moveDirect(x, y);
                else {
                    double density = getResources().getDisplayMetrics().density;
                    pointerX = clamp(pointerX + (x - lastX) / density * 1.6 * 65535 / desktopWidth);
                    pointerY = clamp(pointerY + (y - lastY) / density * 1.6 * 65535 / desktopHeight);
                    sink.move((int) pointerX, (int) pointerY);
                }
                lastX = x; lastY = y;
            }
        } else if (action == MotionEvent.ACTION_UP) {
            if (pressed) { sink.button("left", false); pressed = false; }
            if (!direct && !manualDrag && !moved && !twoFingers && event.getEventTime() - downAt < 400) {
                sink.button("left", true); sink.button("left", false); performClick();
            }
            cancelGesture();
        } else if (action == MotionEvent.ACTION_CANCEL) cancelGesture();
        invalidate(); return true;
    }
    private void moveDirect(float x, float y) {
        if (imageRect.isEmpty()) return;
        pointerX = clamp((x - imageRect.left) / imageRect.width() * 65535);
        pointerY = clamp((y - imageRect.top) / imageRect.height() * 65535);
        sink.move((int) pointerX, (int) pointerY);
    }
    private float centerY(MotionEvent event) { float sum = 0; for (int i = 0; i < event.getPointerCount(); i++) sum += event.getY(i); return sum / event.getPointerCount(); }
    private double clamp(double value) { return Math.max(0, Math.min(65535, value)); }
    private float dp(float value) { return value * getResources().getDisplayMetrics().density; }
    @Override public boolean performClick() { super.performClick(); return true; }
    @Override protected void onDetachedFromWindow() { cancelGesture(); super.onDetachedFromWindow(); }
}
