using System.Globalization;
using System.Text;

namespace LanBridge;

public sealed partial class MainForm
{
    InputLatencySampler? _inputLatency;
    volatile bool _inputDiagnosticsCapable;
    Form? _latencyWindow;

    internal Form ShowLatencyDiagnostics(bool showWindow = true)
    {
        if (_latencyWindow is { IsDisposed: false }) { if (showWindow) _latencyWindow.Activate(); return _latencyWindow; }
        bool hiddenPreview = _preview && !showWindow;
        var window = new LatencyDiagnosticsWindow(hiddenPreview)
        {
            Text = "LanBridge · Đo độ trễ", Size = new Size(960, 610), MinimumSize = new Size(860, 540),
            StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = Color.White,
            Font = new Font("Segoe UI", 10), ShowInTaskbar = false
        };
        _latencyWindow = window;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(20) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 40, 54 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        window.Controls.Add(layout);
        layout.Controls.Add(UiLabel("Tìm khâu gây khựng", 19, Color.White, true), 0, 0);
        var explanation = UiLabel("Mở bảng này trên A và B để kiểm tra độ trễ và đường đi của bộ gõ.\nChỉ ghi thời gian, chế độ và số lượng; không ghi nội dung đã gõ, clipboard hoặc tọa độ.", 9, Muted);
        explanation.AutoEllipsis = false; layout.Controls.Add(explanation, 0, 1);
        var report = new TextBox
        {
            Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
            BackColor = Card, ForeColor = Color.White, BorderStyle = BorderStyle.None,
            Font = new Font("Consolas", 9.5f), Margin = new Padding(0, 4, 0, 12)
        };
        layout.Controls.Add(report, 0, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = Padding.Empty };
        layout.Controls.Add(actions, 0, 3);
        var copy = new DarkButton(); StyleButton(copy, "Chép kết quả đo", 0, 0, 180, 38, Accent);
        copy.Margin = Padding.Empty; actions.Controls.Add(copy);
        var pause = new DarkButton(); StyleButton(pause, "Tạm dừng số đo", 0, 0, 160, 38, Color.FromArgb(48, 62, 80));
        pause.ForeColor = Color.White; pause.Margin = new Padding(0, 0, 10, 0); actions.Controls.Add(pause);
        copy.Click += (_, _) =>
        {
            try { _clipboard.SetText(report.Text); copy.Text = "Đã chép kết quả"; }
            catch (System.Runtime.InteropServices.ExternalException) { copy.Text = "Clipboard bận · thử lại"; }
        };
        var timer = new System.Windows.Forms.Timer { Interval = 500 };
        bool paused = false;
        pause.Click += (_, _) => { paused = !paused; pause.Text = paused ? "Tiếp tục số đo" : "Tạm dừng số đo"; };
        void RefreshReport() { report.Text = BuildLatencyReport(); }
        timer.Tick += (_, _) => { if (!paused) RefreshReport(); };
        window.Disposed += (_, _) => { timer.Dispose(); if (ReferenceEquals(_latencyWindow, window)) _latencyWindow = null; };
        RefreshReport(); timer.Start();
        if (hiddenPreview) { window.StartPosition = FormStartPosition.Manual; window.Location = new Point(-32000, -32000); }
        window.Show(this);
        return window;
    }

    string BuildLatencyReport()
    {
        var input = _input.GetDiagnosticsSnapshot();
        var typing = _input.GetTypingDiagnosticsSnapshot();
        var text = new StringBuilder();
        bool host = _peer == null ? IsHost : _sessionIsHost;
        text.AppendLine($"LanBridge {Application.ProductVersion} · {(host ? "Máy B" : "Máy A")}");
        text.AppendLine(_peer == null ? "Chưa có phiên kết nối." : "Phiên TLS đang kết nối.");
        AppendRoundtrip(text, "Phản hồi mạng/TLS", _peer?.Latency);
        if (!host) AppendRoundtrip(text, "Đường nhập A → B → A", _inputLatency?.Latency);
        else text.AppendLine("Đường nhập A → B → A: xem số đo trên A.");
        if (_peer != null && !_inputDiagnosticsCapable) text.AppendLine("Máy bên kia chưa hỗ trợ đo đường nhập; cập nhật cả hai máy.");
        text.AppendLine();
        AppendTiming(text, "Chờ gửi trên máy này", input.OutgoingQueue);
        AppendTiming(text, "Chờ luồng nhập Windows", input.DispatcherQueue);
        AppendTiming(text, "Gửi đầu vào tới Windows", input.NativeInjection);
        AppendTiming(text, "Thời gian callback hook", input.HookCallback);
        AppendTiming(text, "Tuổi sự kiện vật lý (*)", input.HookEventAge);
        AppendTiming(text, "Tuổi input của LanBridge (*)", input.OwnInjectedEventAge);
        text.AppendLine($"Đang chờ: gửi {input.PendingOutgoingWork}; luồng nhập {input.PendingDispatcherWork}.");
        var inbox = _inbox?.GetSnapshot();
        text.AppendLine($"Hàng đợi phiên trước luồng nhập: {inbox?.Packets ?? 0} gói; {(inbox?.Bytes ?? 0) / 1024.0:0.0} KiB.");
        text.AppendLine($"Đầu vào native: {input.NativeInputs}; lỗi: {input.NativeFailures}; {(input.NativeInputEnabled ? "đang bật" : "đang tắt / preview")}.");
        if (input.NativeInjectionInProgress) text.AppendLine($"Đang chờ Windows nhận đầu vào: {Ms(input.NativeInjectionInProgressMilliseconds)} ms.");
        if (input.HookCallbackInProgress) text.AppendLine($"Callback hook đang chạy: {Ms(input.HookCallbackInProgressMilliseconds)} ms.");
        text.AppendLine();
        text.AppendLine("BỘ GÕ · CHỈ THỐNG KÊ SỐ LƯỢNG");
        text.AppendLine($"Vai trò giao diện: {(IsHost ? "B · nhận" : "A · điều khiển")}; luồng nhập: {(typing.IsController ? "A · điều khiển" : "B · nhận")}; điều khiển trên B: {(typing.IsRemote ? "đang bật" : "đang tắt")}.");
        if (typing.IsController)
        {
            text.AppendLine($"Bộ gõ đã chọn: {TypingModeName((TypingMode)_typing.SelectedIndex)}; đang xử lý: {TypingModeName(typing.EffectiveMode)}.");
            text.AppendLine($"Phím chuyển đang dùng trên A: {_input.SwitchShortcut?.DisplayName ?? "chưa gán / đang tạm dừng"}.");
            text.AppendLine($"Dịch phím trên A: thành công {typing.PrintableTranslationSuccesses}; thất bại {typing.PrintableTranslationFailures}; dùng đường dự phòng {typing.PrintableFallbacks}.");
            text.AppendLine($"Phím xuống đi đường gốc: chế độ Phím gốc {typing.PhysicalModeKeyDowns}; tổ hợp Ctrl/Alt/Win {typing.ShortcutBypassKeyDowns}.");
            text.AppendLine($"Chữ đưa vào hàng gửi từ A: {typing.OutgoingTextPackets} gói; ngoài ASCII {typing.OutgoingNonAsciiCodeUnits}; xóa lùi để ghép dấu {typing.OutgoingBackspaces}.");
        }
        else text.AppendLine("B nhận chữ Unicode đã ghép từ A; chế độ Telex/VNI phải chọn trên A.");
        text.AppendLine($"Gói chữ hợp lệ qua luồng nhập trên máy này: {typing.IncomingTextPackets}; ngoài ASCII {typing.IncomingNonAsciiCodeUnits}.");
        text.AppendLine("Bộ đếm chữ tính từ lúc mở ứng dụng; ngoài ASCII đếm đơn vị UTF-16, không chứa nội dung. Gói được lên lịch chưa xác nhận ứng dụng đích đã hiện chữ.");
        text.AppendLine();
        text.AppendLine("Mạng/TLS gồm hàng đợi và phản hồi của LanBridge, không phải ping ICMP.");
        text.AppendLine("Đường nhập gồm lượt đi/về và qua luồng nhập B; chưa đo ứng dụng B vẽ xong.");
        text.AppendLine("Thời gian gọi SendInput chỉ đo enqueue; Windows vẫn có thể xử lý tiếp sau đó.");
        text.AppendLine("Max / trung bình tính từ lúc mở ứng dụng (RTT tính từ lúc nối phiên).");
        text.AppendLine("(*) Đồng hồ sự kiện Windows có bước thô; không dùng số nhỏ để kết luận.");
        return text.ToString();
    }

    static string TypingModeName(TypingMode mode) => mode switch
    {
        TypingMode.VietnameseTelex => "Tiếng Việt Telex",
        TypingMode.VietnameseVni => "Tiếng Việt VNI",
        TypingMode.Physical => "Phím gốc",
        _ => "chưa xác định",
    };

    static string Ms(double value) => value.ToString("0.0", CultureInfo.InvariantCulture);
    static void AppendRoundtrip(StringBuilder text, string name, ConnectionLatency? value)
    {
        if (value == null) { text.AppendLine(name + ": chưa có mẫu (cần cả hai máy hỗ trợ)."); return; }
        double age = (DateTimeOffset.UtcNow - value.SampledAt).TotalSeconds;
        text.AppendLine($"{name}: {Ms(value.LastMilliseconds)} ms; max {Ms(value.MaximumMilliseconds)}; {value.Samples} mẫu; cách đây {Math.Max(0, age):0.0}s.");
    }
    static void AppendTiming(StringBuilder text, string name, InputTimingSnapshot timing)
    {
        text.AppendLine(timing.Count == 0 ? name + ": chưa có mẫu." :
            $"{name}: gần nhất {Ms(timing.LastMilliseconds)} / TB {Ms(timing.AverageMilliseconds)} / max {Ms(timing.MaxMilliseconds)} ms; mẫu {timing.Count}; ≥50ms: {timing.SlowCount}; mẫu cũ {Ms(timing.LastSampleAgeMilliseconds / 1000)}s.");
    }

    sealed class LatencyDiagnosticsWindow(bool hiddenPreview) : Form
    {
        protected override bool ShowWithoutActivation => hiddenPreview;
    }
}
