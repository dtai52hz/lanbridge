using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LanBridge;

public sealed partial class MainForm
{
    internal void SelectSessionPreview() { if (_preview) _tabs.SelectedTab = _sessionTab; }
    readonly UserPreferencesStore? _preferencesStore;
    readonly object _preferencesWriteGate = new();
    UserPreferences? _pendingPreferences;
    Task _preferencesWriteTask = Task.CompletedTask;
    System.Windows.Forms.Timer? _preferenceSaveTimer, _desktopStatusTimer;
    NotifyIcon? _trayIcon;
    Icon? _applicationIcon;
    ContextMenuStrip? _trayMenu;
    ToolStripMenuItem? _trayDisconnect, _trayRelease, _trayStatus;
    bool _desktopInitialized, _restoringPreferences, _hiddenInTray, _preferenceFailureReported, _preferencesWriterRunning, _trayHintShown, _restoringWindow;
    FormWindowState _windowStateBeforeTray = FormWindowState.Normal;
    long _connectionStartedAt;
    string? _connectedMachineName;

    void InitializeDesktopExperience()
    {
        using (var iconSource = typeof(MainForm).Assembly.GetManifestResourceStream("LanBridge.AppIcon"))
        {
            if (iconSource is not null)
            {
                using var sourceIcon = new Icon(iconSource, new Size(32, 32));
                _applicationIcon = (Icon)sourceIcon.Clone();
                Icon = _applicationIcon;
            }
        }
        var preferences = _preferencesStore?.Load() ?? new UserPreferences();
        _restoringPreferences = true;
        try
        {
            _edge.SelectedIndex = (int)preferences.Edge;
            _screenLayout.SelectedEdge = preferences.Edge;
            _typing.SelectedIndex = (int)preferences.TypingMode;
            _input.TypingMode = preferences.TypingMode;
            _pointerSpeed.Value = preferences.MouseSpeedPercent;
            _input.MouseSpeedPercent = preferences.MouseSpeedPercent;
            _minimizeToTray.Checked = preferences.MinimizeToTray;
            ApplyPermissions();
        }
        finally { _restoringPreferences = false; }
        _preferenceSaveTimer = new System.Windows.Forms.Timer { Interval = 450 };
        _preferenceSaveTimer.Tick += (_, _) => { _preferenceSaveTimer.Stop(); EnqueuePreferencesWrite(CapturePreferences()); };
        _desktopStatusTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _desktopStatusTimer.Tick += (_, _) => RefreshDesktopStatus();
        _desktopInitialized = true;
        Resize += (_, _) => HandleWindowMinimized();
        Disposed += (_, _) => DisposeDesktopExperience();
        if (!_preview) { InitializeTrayIcon(); _desktopStatusTimer.Start(); }
        RefreshDesktopStatus();
    }

    UserPreferences CapturePreferences() => new()
    {
        Edge = (ScreenEdge)Math.Max(0, _edge.SelectedIndex),
        TypingMode = (TypingMode)Math.Max(0, _typing.SelectedIndex),
        MouseSpeedPercent = (int)_pointerSpeed.Value,
        MinimizeToTray = _minimizeToTray.Checked
    };

    void QueuePreferenceSave()
    {
        if (!_desktopInitialized || _restoringPreferences || _closing || _preferencesStore is null) return;
        _preferenceSaveTimer?.Stop();
        _preferenceSaveTimer?.Start();
    }

    void EnqueuePreferencesWrite(UserPreferences preferences)
    {
        if (_preferencesStore is null) return;
        lock (_preferencesWriteGate)
        {
            // At most one writer and one latest snapshot, even if the disk is slow.
            _pendingPreferences = preferences;
            if (!_preferencesWriterRunning)
            {
                _preferencesWriterRunning = true;
                _preferencesWriteTask = Task.Run(DrainPreferencesWrites);
            }
        }
    }

    void DrainPreferencesWrites()
    {
        while (true)
        {
            UserPreferences preferences;
            lock (_preferencesWriteGate)
            {
                if (_pendingPreferences is null) { _preferencesWriterRunning = false; return; }
                preferences = _pendingPreferences;
                _pendingPreferences = null;
            }
            if (!_preferencesStore!.TrySave(preferences) && !_preferenceFailureReported)
            {
                _preferenceFailureReported = true;
                Ui(() => Log("Chưa lưu được thiết lập. LanBridge vẫn tiếp tục hoạt động; sẽ thử lại khi bạn thay đổi hoặc đóng ứng dụng."));
            }
        }
    }

    Task FlushPreferencesAsync()
    {
        _preferenceSaveTimer?.Stop();
        if (_desktopInitialized && _preferencesStore is not null) EnqueuePreferencesWrite(CapturePreferences());
        lock (_preferencesWriteGate) return _preferencesWriteTask;
    }

    void InitializeTrayIcon()
    {
        _trayMenu = new ContextMenuStrip { BackColor = Card, ForeColor = Color.White, ShowImageMargin = false };
        _trayStatus = new ToolStripMenuItem("LanBridge · Chưa kết nối") { Enabled = false };
        _trayMenu.Items.Add(_trayStatus);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Mở LanBridge", null, (_, _) => RestoreFromTray());
        _trayRelease = new ToolStripMenuItem("Trả điều khiển / mở khóa", null, (_, _) => Emergency());
        _trayDisconnect = new ToolStripMenuItem("Ngắt kết nối", null, (_, _) => Disconnect());
        _trayMenu.Items.Add(_trayRelease); _trayMenu.Items.Add(_trayDisconnect);
        _trayMenu.Items.Add(new ToolStripSeparator());
        _trayMenu.Items.Add("Thoát LanBridge", null, (_, _) => Close());
        _trayIcon = new NotifyIcon { Text = "LanBridge · Chưa kết nối", Icon = _applicationIcon ?? SystemIcons.Application, ContextMenuStrip = _trayMenu, Visible = true };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();
        _trayIcon.BalloonTipClicked += (_, _) => RestoreFromTray();
    }

    void HandleWindowMinimized()
    {
        if (WindowState != FormWindowState.Minimized) _windowStateBeforeTray = WindowState;
        if (!_closing && !_restoringWindow && _desktopInitialized && _minimizeToTray.Checked && WindowState == FormWindowState.Minimized)
            HideToTray();
    }

    void HideToTray()
    {
        if (_closing || IsDisposed || _hiddenInTray) return;
        // Preview follows the same state transitions without publishing a real tray icon
        // or showing a desktop window; no network/input services are stopped here.
        if (!_preview && _trayIcon is null) return;
        if (WindowState != FormWindowState.Minimized) _windowStateBeforeTray = WindowState;
        _hiddenInTray = true;
        Hide();
        ShowInTaskbar = false;
        if (!_preview && !_trayHintShown)
        {
            _trayHintShown = true;
            _trayIcon!.ShowBalloonTip(1800, "LanBridge đang chạy nền", "Bấm đúp biểu tượng LanBridge ở khay hệ thống để mở lại. Phiên kết nối vẫn tiếp tục.", ToolTipIcon.Info);
        }
    }

    void RestoreFromTray()
    {
        if (_closing || IsDisposed) return;
        _hiddenInTray = false;
        _restoringWindow = true;
        try
        {
            ShowInTaskbar = true;
            if (_preview) return;
            // Normalize while still hidden so a restore cannot immediately trigger the
            // opt-in minimize handler again when Show recreates a taskbar window.
            WindowState = _windowStateBeforeTray == FormWindowState.Maximized ? FormWindowState.Maximized : FormWindowState.Normal;
            Show();
            Activate();
            BringToFront();
        }
        finally { _restoringWindow = false; }
    }

    void PrepareForLocalPrompt()
    {
        if (_hiddenInTray || WindowState == FormWindowState.Minimized) RestoreFromTray();
    }

    void RefreshDesktopStatus()
    {
        bool connected = _peer is { IsClosed: false };
        string summary;
        if (!connected) summary = "Sẵn sàng · Chọn thiết bị để kết nối";
        else
        {
            var elapsed = _connectionStartedAt == 0 ? TimeSpan.Zero : Stopwatch.GetElapsedTime(_connectionStartedAt);
            string duration = elapsed.TotalHours >= 1 ? $"{(int)elapsed.TotalHours}:{elapsed.Minutes:00}:{elapsed.Seconds:00}" : $"{elapsed.Minutes:00}:{elapsed.Seconds:00}";
            string latency = _peer!.Latency is { } timing ? $"{timing.SmoothedMilliseconds:0.0} ms" : "đang đo RTT";
            summary = $"TLS · {duration} · {latency}";
        }
        _connectionSummary.Text = summary;
        _connectionSummary.ForeColor = connected ? Accent : Muted;
        if (_trayStatus is not null) _trayStatus.Text = connected ? "Đã nối · " + (_connectedMachineName ?? "Thiết bị khác") : "LanBridge · Chưa kết nối";
        if (_trayDisconnect is not null) _trayDisconnect.Enabled = connected;
        if (_trayRelease is not null) _trayRelease.Enabled = connected;
        if (_trayIcon is not null)
        {
            string tooltip = connected ? "LanBridge · " + (_connectedMachineName ?? "Đã kết nối") : "LanBridge · Chưa kết nối";
            _trayIcon.Text = tooltip.Length > 63 ? tooltip[..63] : tooltip;
        }
    }

    void DisposeDesktopExperience()
    {
        if (!_desktopInitialized) return;
        _desktopInitialized = false;
        _preferenceSaveTimer?.Stop(); _preferenceSaveTimer?.Dispose();
        _desktopStatusTimer?.Stop(); _desktopStatusTimer?.Dispose();
        if (_trayIcon is not null) { _trayIcon.Visible = false; _trayIcon.Dispose(); _trayIcon = null; }
        _trayMenu?.Dispose(); _trayMenu = null;
        if (_applicationIcon is not null) { Icon = null; _applicationIcon.Dispose(); _applicationIcon = null; }
    }
}

internal static class DesktopActivation
{
    static readonly uint ShowMessage = RegisterWindowMessage("LanBridge.ShowMainWindow.v1");
    internal static bool IsShowMessage(int message) => ShowMessage != 0 && message == ShowMessage;
    internal static bool RequestShowExisting() => ShowMessage != 0 && PostMessage(new IntPtr(0xffff), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool PostMessage(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
}
