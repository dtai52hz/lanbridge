package io.github.dtai52hz.lanbridge;

import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.graphics.Color;
import android.net.Uri;
import android.os.Handler;
import android.os.Looper;
import android.os.Bundle;
import android.provider.DocumentsContract;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;
import java.io.IOException;
import java.util.HashMap;
import java.util.Map;
import java.util.LinkedHashSet;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;
import java.util.concurrent.ThreadPoolExecutor;
import java.util.concurrent.SynchronousQueue;
import java.util.concurrent.TimeUnit;
import java.util.concurrent.atomic.AtomicBoolean;
import io.github.dtai52hz.lanbridge.net.FileTransferClient;
import io.github.dtai52hz.lanbridge.net.LanBridgeClient;

/** Session file controls and private verified receipts. SAF is opened only by a user action. */
final class FilePanelController {
    static final int PICK_SEND = 7101, PICK_SAVE = 7102;
    private final Activity activity;
    private final Runnable releaseInput;
    private final Handler ui = new Handler(Looper.getMainLooper());
    private final ExecutorService io = new ThreadPoolExecutor(0, 2, 15, TimeUnit.SECONDS, new SynchronousQueue<>(),
        task -> { Thread thread = new Thread(task, "LanBridge-document-io"); thread.setDaemon(true); return thread; });
    private final ExecutorService metadata = Executors.newSingleThreadExecutor();
    private AndroidFileStorage storage;
    private LanBridgeClient client;
    private FileTransferClient files;
    private boolean peerAllowed, receiveAllowed, foreground, destroyed, preparing, exporting;
    private AndroidFileStorage.DocumentOperation operation = new AndroidFileStorage.DocumentOperation();
    private AndroidFileStorage.DocumentOperation exportOperation = new AndroidFileStorage.DocumentOperation();
    private AndroidFileStorage.PreparedSource prepared;
    private AndroidFileStorage.Receipt saveReceipt;
    private String pendingSaveId;
    private Uri pendingSaveUri;
    private final Map<String, AndroidFileStorage.ReceiptDestination> incoming = new HashMap<>();
    private final LinkedHashSet<String> finishedTransfers = new LinkedHashSet<>();
    private String outgoingId, incomingId, message = "Chọn tệp để gửi hoặc xem các tệp đã nhận.";
    private AlertDialog panel, offerDialog;
    private LinearLayout content;
    private TextView statusText;
    private FileTransferClient.Offer pendingOffer;
    private Runnable offerTimeout;
    private long lastProgressAt;

    FilePanelController(Activity activity, Runnable releaseInput) { this(activity, releaseInput, null); }
    FilePanelController(Activity activity, Runnable releaseInput, Bundle state) {
        this.activity = activity; this.releaseInput = releaseInput;
        pendingSaveId = state == null ? null : state.getString("lanbridge.pendingSaveId");
        if (state != null && state.getString("lanbridge.pendingSaveUri") != null) {
            Uri restored = Uri.parse(state.getString("lanbridge.pendingSaveUri"));
            if (pendingSaveId != null && AndroidFileStorage.isDocumentUri(restored)) pendingSaveUri = restored;
        }
        metadata.execute(() -> {
            try {
                AndroidFileStorage ready = new AndroidFileStorage(new java.io.File(activity.getFilesDir(), "transfers"));
                ui.post(() -> {
                    if (!destroyed) {
                        storage = ready;
                        if (pendingSaveId != null) for (AndroidFileStorage.Receipt receipt : ready.receipts())
                            if (receipt.id.equals(pendingSaveId)) { saveReceipt = receipt; break; }
                        if (pendingSaveUri != null) { Uri selected = pendingSaveUri; pendingSaveUri = null; completeSaveSelection(selected); }
                        render();
                    }
                });
            } catch (IOException error) { ui.post(() -> { if (!destroyed) { message = error.getMessage(); render(); } }); }
        });
    }
    void attach(LanBridgeClient owner) {
        detach(); client = owner; files = owner.files();
        final FileTransferClient expected = files;
        files.setListener(new FileTransferClient.Listener() {
            public void onOffer(FileTransferClient.Offer offer) { post(expected, () -> incomingOffer(offer)); }
            public void onProgress(String id, String name, long done, long total, boolean receiving) {
                long now = android.os.SystemClock.uptimeMillis();
                if (done != total && now - lastProgressAt < 200) return;
                lastProgressAt = now;
                post(expected, () -> {
                    if (finishedTransfers.contains(id)) return;
                    boolean wasActive = receiving ? incomingId != null : outgoingId != null;
                    if (receiving) incomingId = id; else outgoingId = id;
                    message = (receiving ? "Đang nhận: " : "Đang gửi: ") + name + "\n" + size(done) + " / " + size(total);
                    if (!wasActive) render(); else updateMessage();
                });
            }
            public void onCompleted(String id, String name, boolean receiving) {
                post(expected, () -> {
                    rememberFinished(id);
                    dismissOffer(id);
                    if (receiving) {
                        incomingId = null;
                        incoming.remove(id);
                        message = "Đã nhận và xác minh SHA-256: " + name + "\nBấm Lưu thành… để chọn nơi lưu trên điện thoại.";
                    } else { outgoingId = null; closePrepared(); message = "Máy tính đã nhận và xác minh: " + name; }
                    render();
                });
            }
            public void onError(String id, String name, boolean receiving, String error) {
                post(expected, () -> {
                    rememberFinished(id);
                    dismissOffer(id);
                    if (receiving) { incomingId = null; incoming.remove(id); }
                    else { outgoingId = null; closePrepared(); }
                    message = error; render();
                });
            }
        });
        render();
    }
    void setPeerAllowed(boolean allowed) {
        peerAllowed = allowed;
        if (!allowed) { receiveAllowed = false; rejectOffer(); cancelPreparation(); }
        if (files != null) files.setReceiveAllowed(allowed && receiveAllowed);
        render();
    }
    void foreground(boolean value) { foreground = value; if (!value) rejectOffer(); }
    void detach() {
        if (files != null) { files.setListener(null); files.setReceiveAllowed(false); }
        rejectOffer(); cancelPreparation(); closePrepared(); incoming.clear(); finishedTransfers.clear();
        client = null; files = null; peerAllowed = receiveAllowed = false; outgoingId = incomingId = null;
        render();
    }
    void show() {
        releaseInput.run();
        if (panel != null && panel.isShowing()) return;
        content = new LinearLayout(activity); content.setOrientation(LinearLayout.VERTICAL); content.setPadding(dp(18), dp(8), dp(18), dp(8));
        ScrollView scroll = new ScrollView(activity); scroll.addView(content);
        panel = new AlertDialog.Builder(activity).setTitle("Tệp giữa điện thoại và máy tính").setView(scroll).setPositiveButton("Đóng", null).create();
        panel.setOnDismissListener(dialog -> { panel = null; content = null; statusText = null; });
        panel.show(); render();
    }
    private void render() {
        if (content == null || destroyed) return;
        content.removeAllViews();
        statusText = addText(message, 14);
        Button send = addButton(preparing ? "Đang chuẩn bị tệp…" : "Gửi tệp từ điện thoại", this::pickSend);
        send.setEnabled(storage != null && files != null && peerAllowed && !preparing && !exporting && prepared == null);
        CheckBox allow = new CheckBox(activity); allow.setText("Cho phép máy tính gửi tệp"); allow.setChecked(receiveAllowed);
        allow.setEnabled(storage != null && files != null && peerAllowed); content.addView(allow);
        allow.setOnCheckedChangeListener((view, checked) -> {
            receiveAllowed = checked;
            if (files != null) files.setReceiveAllowed(peerAllowed && checked);
            if (!checked) rejectOffer();
        });
        if (files == null) addText("Kết nối máy tính để gửi hoặc nhận tệp. Các tệp đã xác minh vẫn có thể lưu bên dưới.", 12);
        else if (!peerAllowed) addText("Trên máy tính, bật quyền Truyền file cho phiên này.", 12);
        if (preparing || exporting || prepared != null || outgoingId != null || incomingId != null) addButton("Hủy thao tác tệp", () -> {
            cancelPreparation(); exportOperation.cancel(); exporting = false; if (files != null) files.cancel(); message = "Đã yêu cầu hủy thao tác tệp."; render();
        });
        addText("ĐÃ NHẬN VÀ XÁC MINH", 12);
        if (storage == null || storage.receipts().isEmpty()) addText("Chưa có tệp đã nhận. Tệp được giữ riêng trong LanBridge cho đến khi bạn chọn Lưu thành…", 12);
        else for (AndroidFileStorage.Receipt receipt : storage.receipts()) {
            addText(receipt.name + " · " + size(receipt.size), 14);
            LinearLayout actions = new LinearLayout(activity); content.addView(actions);
            Button save = new Button(activity); save.setText("Lưu thành…"); save.setOnClickListener(view -> pickSave(receipt));
            save.setEnabled(!exporting && !preparing); actions.addView(save, new LinearLayout.LayoutParams(0, -2, 1));
            Button delete = new Button(activity); delete.setText("Xóa bản riêng"); delete.setOnClickListener(view -> {
                new AlertDialog.Builder(activity).setMessage("Xóa bản riêng của “" + receipt.name + "” khỏi LanBridge? Bản đã lưu ra ngoài vẫn còn.")
                    .setPositiveButton("Xóa", (dialog, which) -> metadata.execute(() -> {
                        String result;
                        try { storage.delete(receipt); result = "Đã xóa bản riêng."; } catch (IOException error) { result = error.getMessage(); }
                        String completed = result; ui.post(() -> { if (!destroyed) { message = completed; render(); } });
                    }))
                    .setNegativeButton("Giữ lại", null).show();
            }); actions.addView(delete, new LinearLayout.LayoutParams(0, -2, 1));
        }
        addText("Tối đa 2 GiB mỗi tệp; giữ tối đa 8 tệp đã nhận. Chỉ tệp đã đủ dữ liệu và đúng SHA-256 mới có nút Lưu thành…", 11);
    }
    private void pickSend() {
        if (files == null || !peerAllowed || preparing || exporting || prepared != null) return;
        releaseInput.run();
        Intent picker = new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);
        picker.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION);
        try { activity.startActivityForResult(picker, PICK_SEND); }
        catch (RuntimeException error) { message = "Điện thoại chưa có ứng dụng chọn tệp."; render(); }
    }
    private void pickSave(AndroidFileStorage.Receipt receipt) {
        if (exporting || preparing) return;
        releaseInput.run(); saveReceipt = receipt; pendingSaveId = receipt.id;
        Intent picker = new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").addCategory(Intent.CATEGORY_OPENABLE);
        picker.putExtra(Intent.EXTRA_TITLE, receipt.name).addFlags(Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        try { activity.startActivityForResult(picker, PICK_SAVE); }
        catch (RuntimeException error) { saveReceipt = null; pendingSaveId = null; message = "Điện thoại chưa có ứng dụng chọn nơi lưu."; render(); }
    }
    boolean activityResult(int request, int result, Intent data) {
        if (request != PICK_SEND && request != PICK_SAVE) return false;
        if (result != Activity.RESULT_OK || data == null || data.getData() == null) { if (request == PICK_SAVE) { saveReceipt = null; pendingSaveId = null; pendingSaveUri = null; } return true; }
        Uri uri = data.getData();
        if (!AndroidFileStorage.isDocumentUri(uri)) {
            if (request == PICK_SAVE) { saveReceipt = null; pendingSaveId = null; pendingSaveUri = null; }
            message = "Nơi chọn không phải tài liệu Android. Hãy mở lại bộ chọn tệp."; render(); return true;
        }
        if (request == PICK_SEND) prepareSend(uri);
        else if (pendingSaveId != null && storage == null) pendingSaveUri = uri;
        else completeSaveSelection(uri);
        return true;
    }
    void saveState(Bundle state) {
        if (pendingSaveId != null) state.putString("lanbridge.pendingSaveId", pendingSaveId);
        if (pendingSaveUri != null && AndroidFileStorage.isDocumentUri(pendingSaveUri)) state.putString("lanbridge.pendingSaveUri", pendingSaveUri.toString());
    }
    private void completeSaveSelection(Uri uri) {
        AndroidFileStorage.Receipt receipt = saveReceipt;
        boolean ours = pendingSaveId != null;
        saveReceipt = null; pendingSaveId = null;
        if (receipt != null && storage != null && storage.receipts().contains(receipt)) export(receipt, uri);
        else if (ours) {
            AndroidFileStorage.DocumentTarget target = AndroidFileStorage.DocumentTarget.acquire(uri);
            if (target != null) metadata.execute(() -> { try { deleteCreatedDocument(uri, target); } finally { target.close(); } });
            message = "Tệp đã xác minh không còn trong danh sách. Chọn lại tệp để lưu."; render();
        }
    }
    private void prepareSend(Uri uri) {
        if (storage == null || files == null || !peerAllowed || preparing) { message = "Phiên truyền tệp đã đóng hoặc máy tính đã tắt quyền."; render(); return; }
        preparing = true; message = "Đang đọc thông tin tệp…"; render();
        final FileTransferClient expected = files;
        final AndroidFileStorage.DocumentOperation cancellation = operation = new AndroidFileStorage.DocumentOperation();
        executeIo(() -> {
            AndroidFileStorage.PreparedSource source = null;
            try {
                long[] progressAt = { 0 };
                source = storage.prepare(activity.getContentResolver(), uri, cancellation, done -> {
                    long now = android.os.SystemClock.uptimeMillis();
                    if (now - progressAt[0] < 250) return;
                    progressAt[0] = now;
                    post(expected, () -> { if (operation == cancellation && preparing && !cancellation.isCancelled()) { message = "Đang chuẩn bị tệp từ ứng dụng đã chọn: " + size(done); updateMessage(); } });
                });
                AndroidFileStorage.PreparedSource ready = source;
                ui.post(() -> {
                    if (operation != cancellation) { ready.close(); return; }
                    preparing = false;
                    if (destroyed || files != expected || cancellation.isCancelled() || !peerAllowed) { ready.close(); return; }
                    prepared = ready;
                    try {
                        if (!expected.send(ready)) { closePrepared(); message = "Đang gửi một tệp khác hoặc máy tính chưa cấp quyền."; }
                        else message = "Đã gửi đề nghị: " + ready.name() + " · " + size(ready.size());
                    } catch (RuntimeException error) {
                        closePrepared(); message = "Không bắt đầu gửi được tệp. Hãy chọn lại tệp. " + (error.getMessage() == null ? "" : error.getMessage());
                    }
                    render();
                });
            } catch (Exception error) {
                cancellation.cancel();
                if (source != null) source.close();
                ui.post(() -> { if (operation == cancellation && preparing) { preparing = false; if (!destroyed && files == expected) { message = error.getMessage(); render(); } } });
            }
        }, () -> { preparing = false; cancellation.cancel(); message = "Ứng dụng cung cấp tệp đang không phản hồi. Hãy hủy hoặc chọn tệp từ ứng dụng khác."; render(); });
    }
    private void export(AndroidFileStorage.Receipt receipt, Uri uri) {
        AndroidFileStorage.DocumentTarget target = AndroidFileStorage.DocumentTarget.acquire(uri);
        if (target == null) { message = "Nơi lưu này vẫn đang được thao tác trước sử dụng. Hãy chờ thao tác đó dừng hoặc chọn tên tệp khác."; render(); return; }
        exporting = true; message = "Đang lưu bản đã xác minh: " + receipt.name; render();
        AndroidFileStorage.DocumentOperation cancellation = exportOperation = new AndroidFileStorage.DocumentOperation();
        executeIo(() -> {
            String result;
            try { AndroidFileStorage.export(activity.getContentResolver(), receipt, uri, cancellation, target::owns); result = "Đã lưu: " + receipt.name; }
            catch (Exception error) { cancellation.cancel(); deleteCreatedDocument(uri, target); result = "Chưa lưu xong: " + error.getMessage() + "\nBản đã xác minh vẫn được giữ trong LanBridge."; }
            finally { target.close(); }
            String completed = result;
            ui.post(() -> { if (exportOperation == cancellation && exporting) { exporting = false; if (!destroyed) { message = completed; render(); } } });
        }, () -> { exporting = false; cancellation.cancel(); metadata.execute(() -> { try { deleteCreatedDocument(uri, target); } finally { target.close(); } }); message = "Nơi cung cấp tệp đang bận. Bản đã xác minh vẫn còn; hãy thử lại với nơi lưu khác."; render(); });
    }
    private void incomingOffer(FileTransferClient.Offer offer) {
        if (destroyed || !foreground || !receiveAllowed || !peerAllowed || storage == null || pendingOffer != null) { offer.reject(); return; }
        releaseInput.run(); pendingOffer = offer;
        offerDialog = new AlertDialog.Builder(activity).setTitle("Nhận tệp từ máy tính?")
            .setMessage(offer.name + "\n" + size(offer.size) + "\n\nTệp được lưu riêng và kiểm tra SHA-256 trước khi bạn chọn nơi lưu.")
            .setPositiveButton("Nhận tệp", (dialog, which) -> {
                clearOfferTimeout(); pendingOffer = null; offerDialog = null;
                FileTransferClient expected = files;
                metadata.execute(() -> {
                    try {
                        storage.checkIncoming(offer.size);
                        AndroidFileStorage.ReceiptDestination destination = storage.destination(offer.name);
                        if (!offer.accept(destination)) { destination.abort(); post(expected, () -> { message = "Đề nghị nhận đã hết hạn hoặc bị hủy."; render(); }); }
                        else post(expected, () -> { if (!finishedTransfers.contains(offer.id) && destination.isPending()) { incoming.put(offer.id, destination); incomingId = offer.id; message = "Đang nhận: " + offer.name; } render(); });
                    } catch (Exception error) { offer.reject(); post(expected, () -> { message = error.getMessage(); render(); }); }
                });
            }).setNegativeButton("Từ chối", (dialog, which) -> { rejectOffer(); message = "Đã từ chối nhận tệp."; render(); }).create();
        offerDialog.setOnCancelListener(dialog -> rejectOffer());
        offerTimeout = () -> { if (pendingOffer == offer) { rejectOffer(); message = "Đề nghị nhận tệp đã hết hạn. Gửi lại từ máy tính để thử lại."; render(); } };
        ui.postDelayed(offerTimeout, 17_000); offerDialog.show();
    }
    private void dismissOffer(String id) { if (pendingOffer != null && pendingOffer.id.equals(id)) rejectOffer(); }
    private void rejectOffer() {
        FileTransferClient.Offer offer = pendingOffer; pendingOffer = null; clearOfferTimeout();
        if (offer != null) offer.reject();
        if (offerDialog != null) { offerDialog.dismiss(); offerDialog = null; }
    }
    private void clearOfferTimeout() { if (offerTimeout != null) ui.removeCallbacks(offerTimeout); offerTimeout = null; }
    private void cancelPreparation() { operation.cancel(); preparing = false; }
    private void closePrepared() { if (prepared != null) { prepared.close(); prepared = null; } }
    private void rememberFinished(String id) {
        finishedTransfers.add(id);
        if (finishedTransfers.size() > 32) finishedTransfers.remove(finishedTransfers.iterator().next());
    }
    private void deleteCreatedDocument(Uri uri, AndroidFileStorage.DocumentTarget target) {
        // Only called for a URI returned by our ACTION_CREATE_DOCUMENT request.
        // Source documents selected for sending are never deleted or modified.
        try { if (target.owns() && DocumentsContract.isDocumentUri(activity, uri)) DocumentsContract.deleteDocument(activity.getContentResolver(), uri); }
        catch (Exception ignored) { }
    }
    private void post(FileTransferClient expected, Runnable action) { ui.post(() -> { if (!destroyed && files == expected) action.run(); }); }
    private void executeIo(Runnable action, Runnable rejected) { try { io.execute(action); } catch (java.util.concurrent.RejectedExecutionException busy) { rejected.run(); } }
    void destroy() { destroyed = true; if (files != null) files.setListener(null); rejectOffer(); cancelPreparation(); exportOperation.cancel(); closePrepared(); if (panel != null) panel.dismiss(); io.shutdownNow(); metadata.shutdown(); }
    private Button addButton(String label, Runnable action) { Button button = new Button(activity); button.setText(label); button.setOnClickListener(view -> action.run()); content.addView(button); return button; }
    private TextView addText(String value, int size) { TextView text = new TextView(activity); text.setText(value); text.setTextSize(size); text.setPadding(0, dp(7), 0, dp(7)); content.addView(text); return text; }
    private void updateMessage() { if (statusText != null && !destroyed) statusText.setText(message); }
    private int dp(int value) { return Math.round(value * activity.getResources().getDisplayMetrics().density); }
    static String size(long bytes) { return bytes >= 1024L * 1024 * 1024 ? String.format(java.util.Locale.ROOT, "%.2f GiB", bytes / (1024d * 1024 * 1024)) : bytes >= 1024 * 1024 ? String.format(java.util.Locale.ROOT, "%.1f MiB", bytes / (1024d * 1024)) : bytes >= 1024 ? String.format(java.util.Locale.ROOT, "%.1f KiB", bytes / 1024d) : bytes + " B"; }
}
