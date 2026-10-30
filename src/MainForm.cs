using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LanBridge;

public sealed partial class MainForm : Form
{
    static readonly Color Background = Color.FromArgb(16, 20, 29), Card = Color.FromArgb(25, 31, 43), Muted = Color.FromArgb(151, 166, 187), Accent = Color.FromArgb(47, 208, 177);
    readonly bool _preview;
    readonly ComboBox _role = new(), _edge = new(), _network = new(), _typing = new();
    readonly TextBox _pairing = new(), _chat = new();
    readonly Label _status = new(), _remoteState = new(), _peerName = new(), _hint = new();
    readonly Label _featureStatus = new();
    readonly Button _connect = new DarkButton(), _disconnect = new DarkButton(), _copy = new DarkButton();
    readonly FlowLayoutPanel _permissionsPanel = new(), _actions = new();
    readonly Dictionary<string, CheckBox> _checks = new();
    readonly RichTextBox _log = new();
    readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 250 };
    readonly InputRouter _input = new();
    readonly AudioBridge _audio = new();
    readonly ScreenRecorder _recorder = new();
    readonly PrivacyCurtain _curtain = new();
    Permissions _permissions = new();
    Permissions _localPermissions = new();
    HostServer? _server;
    PeerConnection? _peer;
    CancellationTokenSource? _session;
    SessionInbox? _inbox;
    FileTransfer? _files;
    string _lastClipboard = "", _recordingPath = "";
    bool _syncChecks, _closing, _audioOn, _recordingOn, _blockingOn, _privacyOn, _connecting;
    volatile bool _helloReceived, _sessionIsHost;
    volatile Permissions _sessionPermissions = new();
    bool IsHost => _role.SelectedIndex == 1;
    readonly string _receivedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanBridge");
    readonly string _recordingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LanBridge");

    public MainForm(bool preview = false, IClipboardAccess? clipboard = null, string? clipboardCacheDirectory = null)
    {
        _preview = preview;
        _clipboard = clipboard ?? new WindowsClipboardAccess();
        _clipboardCacheDirectory = clipboardCacheDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge", "Clipboard");
        Text = "LanBridge · Một bộ chuột phím, hai máy tính";
        BackColor = Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10); DoubleBuffered = true;
        Size = new Size(1120, 860); MinimumSize = new Size(1060, 780); StartPosition = FormStartPosition.CenterScreen;
        BuildUi();
        _input.RemoteControlChanged = remote => Ui(() => { _remoteState.Text = remote ? "ĐANG ĐIỀU KHIỂN MÁY B" : (_peer != null ? "CHUỘT ĐANG Ở MÁY A" : "CHƯA CÓ PHIÊN KẾT NỐI"); _remoteState.ForeColor = remote ? Accent : Muted; if (remote && _input.LastError is { } error) { Log(error); Emergency(); } });
        _input.EmergencyReleased = () => Ui(Emergency);
        _audio.Error = message => Ui(() => { _audio.StopCapture(); _audio.StopPlayback(); Log("Âm thanh: " + message); _audioOn = false; UpdateFeatureButtons(); if (IsHost) Send(new Packet { Type = "feature-state", Name = "audio", Allow = false }); });
        _recorder.Error = message => Ui(() => { Log("Ghi hình: " + message); _recordingOn = false; UpdateFeatureButtons(); Send(new Packet { Type = "feature-state", Name = "recording", Allow = false }); });
        _clipboardTimer.Tick += (_, _) => SyncClipboard(); _clipboardTimer.Start();
        Microsoft.Win32.SystemEvents.SessionSwitch += WindowsSessionChanged;
        Shown += (_, _) => { if (!_preview) StartNetworkServices(); };
        FormClosing += (_, _) => { _closing = true; Microsoft.Win32.SystemEvents.SessionSwitch -= WindowsSessionChanged; StopNetworkServices(); Disconnect(); _input.Dispose(); _audio.Dispose(); _recorder.Dispose(); _curtain.Dispose(); _clipboardTimer.Dispose(); };
        Log("Mở LanBridge trên hai máy rồi chọn máy trong trang Thiết bị để kết nối.");
        if (preview) Log("Chế độ xem giao diện: không bật hook chuột/phím và không mở cổng mạng.");
    }

    void UpdateRole()
    {
        if (IsHost) _permissions = _localPermissions with { };
        _edge.Visible = _typing.Visible = !IsHost; _network.Visible = IsHost; _pairing.ReadOnly = IsHost;
        _pairing.Text = ""; _connect.Text = IsHost ? "Bật nhận kết nối" : "Kết nối máy B"; _connect.Visible = true; _copy.Visible = false;
        _hint.Text = IsHost ? "Chọn IP mạng LAN, bật nhận kết nối rồi gửi mã ghép nối cho máy A." : "Dán mã ghép nối hiển thị trên máy B vào ô bên dưới.";
        _input.IsController = !IsHost; ApplyPermissions();
    }

    async Task ConnectOrListen()
    {
        if (_preview || _connecting || _approvalPending || _peer != null || _server != null) return;
        _connecting = true; _connect.Enabled = false; _role.Enabled = _network.Enabled = false;
        try
        {
            if (IsHost)
            {
                var listener = _autoServer;
                if (listener == null) { _server = new HostServer(IPAddress.Parse(_network.Text), 45831); _server.Connected += peer => Ui(() => AttachPeer(peer)); _server.Start(); listener = _server; }
                _pairing.Text = listener.PairingCode(_network.Text);
                _copy.Visible = true; _connect.Visible = false; _status.Text = "●  Đang chờ máy A";
                _disconnect.Enabled = true; _role.Enabled = _network.Enabled = false;
                Log($"Máy B đang chờ trên {_network.Text}:{listener.Port}. Nếu Windows hỏi tường lửa, cho phép mạng Riêng tư (Private).");
            }
            else
            {
                _status.Text = "●  Đang ghép nối…";
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var peer = await PeerConnection.ConnectAsync(_pairing.Text.Trim(), timeout.Token);
                if (_closing) { peer.Dispose(); return; } AttachPeer(peer);
            }
        }
        catch (Exception ex) { _server?.Dispose(); _server = null; _status.Text = "●  Kết nối chưa thành công"; Log("Kết nối: " + ex.Message); }
        finally { _connecting = false; _connect.Enabled = true; if (_peer == null && _server == null) _role.Enabled = _network.Enabled = true; }
    }

    void AttachPeer(PeerConnection peer)
    {
        if (_closing || _peer != null) { peer.Dispose(); return; }
        _peer = peer; _helloReceived = false; _sessionIsHost = IsHost; _session = new(); var session = _session; var token = session.Token;
        Action<Packet> inputSender = packet => peer.TrySend(packet);
        _input.Send = inputSender; _audio.Send = packet => peer.TrySend(packet);
        ClipboardFiles? sessionClipboardFiles = null;
        _inbox = new SessionInbox();
        var inbox = _inbox;
        peer.Received += packet => { if (!token.IsCancellationRequested && !inbox.TryWrite(packet)) peer.Abort("Hàng đợi xử lý đã đầy; phiên được dừng để nhả phím và chuột."); };
        peer.Closed += reason =>
        {
            try { session.Cancel(); } catch (ObjectDisposedException) { }
            inbox.Complete();
            sessionClipboardFiles?.Dispose();
            // Release on the input pump even while the main window is busy/minimized.
            // Sender identity is checked on that pump to ignore stale close callbacks.
            _input.ReleaseSession(inputSender);
            Ui(() => { if (_peer == peer) { Log("Phiên đã đóng" + (reason == null ? "." : ": " + reason)); Disconnect(); } });
        };
        _files = new FileTransfer(_receivedDirectory, p => peer.SendAsync(p, token), message => Ui(() => Log(message)));
        try { AttachClipboardFiles(peer, token); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { _clipboardFiles = null; Log("Chưa bật được clipboard file: " + error.Message); }
        sessionClipboardFiles = _clipboardFiles;
        _input.IsController = !IsHost; _input.CanControl = false;
        try { if (!_preview) _input.Start(); }
        catch { Disconnect(); throw; }
        _role.Enabled = _network.Enabled = false; _connect.Visible = _copy.Visible = false; _disconnect.Enabled = true;
        _status.Text = "●  Đã kết nối · TLS"; _status.ForeColor = Accent;
        peer.Start();
        Task.Run(async () => { try { await foreach (var p in inbox.ReadAllAsync(token)) await HandlePacket(p, token, sessionClipboardFiles); } catch (OperationCanceledException) { } catch (Exception ex) { peer.Abort("Xử lý phiên: " + ex.Message); } }, token);
        var bounds = SystemInformation.VirtualScreen;
        Send(new Packet { Type = "hello", Name = Environment.MachineName, Width = bounds.Width, Height = bounds.Height, Flags = 2, ClipboardFilesVersion = sessionClipboardFiles == null ? 0 : ClipboardFiles.ProtocolVersion });
        if (IsHost) Send(new Packet { Type = "permissions", Permissions = _permissions with { } });
        ApplyPermissions(); _remoteState.Text = IsHost ? "MÁY B SẴN SÀNG NHẬN ĐIỀU KHIỂN" : "CHUỘT ĐANG Ở MÁY A";
        _devicesPage.SetSessionState(true); _devicesPage.Connecting = false; _tabs.SelectedTab = _sessionTab;
        Log("Đã ghép nối. Chạm mép đã chọn để chuyển máy ngay; Ctrl + Alt + F12 để trả điều khiển.");
        if (!IsHost) Log("Bộ gõ mặc định: Telex tích hợp. Gõ tieengs Vieetj → tiếng Việt. Chọn VNI hoặc Phím gốc trong ô Gõ.");
    }

    async Task HandlePacket(Packet p, CancellationToken token, ClipboardFiles? clipboardFiles)
    {
        if (token.IsCancellationRequested) return;
        if (p.Type.StartsWith("clipboard-files-", StringComparison.Ordinal))
        {
            if (clipboardFiles != null) await clipboardFiles.HandleAsync(p, _helloReceived && _clipboardFilesCapable && _sessionPermissions.Clipboard && _sessionPermissions.Files);
            return;
        }
        if (p.Type.StartsWith("file-", StringComparison.Ordinal)) { var files = _files; if (files != null) await files.HandleAsync(p, _sessionPermissions.Files); return; }
        // Hot input/audio paths never wait for the window message loop. Permissions
        // and hello still pass through the ordered reader before later input.
        if (p.Type is "enter" or "move" or "key" or "text" or "button" or "wheel" or "leave")
        {
            bool host = _sessionIsHost;
            if (!_helloReceived || (!host && (p.Type != "leave" || !p.Allow)) || (p.Type != "leave" && !_sessionPermissions.Input)) return;
            await _input.ApplyRemoteAsync(p, token).ConfigureAwait(false);
            if (token.IsCancellationRequested) return;
            if (_input.LastError is { } error) throw new InvalidOperationException(error);
            if (p.Type == "leave" && host)
            {
                _input.SetBlockLocalInput(false);
                Ui(() => { if (token.IsCancellationRequested) return; _blockingOn = false; UpdateFeatureButtons(); Send(new Packet { Type = "feature-state", Name = "block", Allow = false }); });
            }
            return;
        }
        if (p.Type is "audio-format" or "audio" or "audio-stop")
        {
            if (!_sessionIsHost && _helloReceived && _sessionPermissions.Audio) _audio.Handle(p);
            return;
        }
        // These are visual notifications, so a minimized/busy window must not
        // hold later input behind them. Keep hello/permissions as ordered barriers.
        if (p.Type == "chat")
        {
            Ui(() => { if (!token.IsCancellationRequested) Log("Đối tác: " + (p.Text ?? "")[..Math.Min(p.Text?.Length ?? 0, 2000)]); });
            return;
        }
        if (p.Type == "feature-state" && !_sessionIsHost)
        {
            Ui(() =>
            {
                if (token.IsCancellationRequested) return;
                switch (p.Name) { case "audio": _audioOn = p.Allow; break; case "recording": _recordingOn = p.Allow; break; case "block": _blockingOn = p.Allow; break; case "privacy": _privacyOn = p.Allow; break; }
                UpdateFeatureButtons(); Log($"{FeatureLabel(p.Name)}: {(p.Allow ? "đang bật" : "đã tắt")}" + (p.Text == null ? "" : ". " + p.Text));
            });
            return;
        }
        await OnUi(() =>
        {
            if (_peer == null || token.IsCancellationRequested) return;
            switch (p.Type)
            {
                case "hello":
                    if (p.Flags != 2) throw new InvalidDataException("Hai máy cần dùng LanBridge 0.2.0 hoặc mới hơn. Hãy cập nhật cả A và B.");
                    if (p.Width is < 1 or > 65536 || p.Height is < 1 or > 65536) throw new InvalidDataException("Kích thước màn hình không hợp lệ.");
                    _input.RemoteWidth = p.Width; _input.RemoteHeight = p.Height; _peerName.Text = "Đối tác: " + (p.Name ?? "Máy tính")[..Math.Min(p.Name?.Length ?? 0, 100)];
                    _clipboardFilesCapable = p.ClipboardFilesVersion == 1;
                    _helloReceived = true; _devicesPage.SetSessionState(true, p.Name); ApplyPermissions(); break;
                case "permissions" when !IsHost && p.Permissions != null: _permissions = p.Permissions; ApplyPermissions(); break;
                case "clipboard": ReceiveClipboardText(p, token); break;
                case "feature" when IsHost: SetFeature(p.Name, p.Allow); break;
                case "restart" when IsHost && _permissions.Restart: ConfirmRestart(); break;
            }
        }, token);
    }

    void ApplyPermissions()
    {
        _sessionPermissions = _permissions with { };
        _syncChecks = true;
        foreach (var (key, check) in _checks) { check.Checked = (IsHost || _peer != null) && (bool)typeof(Permissions).GetProperty(key)!.GetValue(_permissions)!; check.Enabled = IsHost; }
        _syncChecks = false;
        if (IsHost)
        {
            _localPermissions = _permissions with { };
            _input.CanControl = _peer != null && _helloReceived && _permissions.Input;
            if (!_permissions.Input) _input.ForceReleaseAll();
            if (!_permissions.Audio) { _audio.StopCapture(); _audioOn = false; }
            if (!_permissions.Recording) { _recorder.Stop(); _recordingOn = false; }
            if (!_permissions.BlockInput || !_permissions.Input) { _input.SetBlockLocalInput(false); _blockingOn = false; }
            if (!_permissions.Privacy) { _curtain.Hide(); _privacyOn = false; }
        }
        else { _input.ConfigureController(_peer != null && _helloReceived && _permissions.Input, (ScreenEdge)_edge.SelectedIndex); if (!_permissions.Audio) { _audio.StopPlayback(); _audioOn = false; } }
        for (int i = 0; i < _actions.Controls.Count; i++)
        {
            bool allowed = i switch { 0 => _permissions.Files, 1 => !IsHost && _permissions.Audio, 2 => !IsHost && _permissions.Recording, 3 => !IsHost && _permissions.BlockInput && _permissions.Input, 4 => !IsHost && _permissions.Privacy, 5 => !IsHost && _permissions.Restart, _ => false };
            _actions.Controls[i].Enabled = _peer != null && allowed;
        }
        if (!_permissions.Files) _files?.CancelTransfers();
        if (!_permissions.Files || !_permissions.Clipboard) CancelClipboardFiles();
        UpdateFeatureButtons();
    }

    void UpdateFeatureButtons()
    {
        if (_actions.Controls.Count < 6) return;
        _actions.Controls[1].Text = _audioOn ? "Tắt âm B" : "Nghe B";
        _actions.Controls[2].Text = _recordingOn ? "Dừng ghi" : "Ghi hình";
        _actions.Controls[3].Text = _blockingOn ? "Mở khóa B" : "Khóa B";
        _actions.Controls[4].Text = _privacyOn ? "Bỏ màn che" : "Màn che";
        _featureStatus.ForeColor = _recordingOn ? Color.FromArgb(255, 119, 133) : Muted;
        _featureStatus.Text = _recordingOn && IsHost ? "● ĐANG GHI · " + _recordingPath :
            $"Âm thanh: {(_audioOn ? "bật" : "tắt")}   ·   Ghi hình: {(_recordingOn ? "bật" : "tắt")}   ·   Khóa B: {(_blockingOn ? "bật" : "tắt")}   ·   Màn che: {(_privacyOn ? "bật" : "tắt")}";
    }

    static string FeatureLabel(string? feature) => feature switch { "audio" => "Âm thanh B", "recording" => "Ghi hình B", "block" => "Khóa đầu vào B", "privacy" => "Màn che B", _ => feature ?? "Tính năng" };
    void RequestFeature(string feature)
    {
        if (_peer == null || IsHost) return;
        bool next = feature switch { "audio" => !_audioOn, "recording" => !_recordingOn, "block" => !_blockingOn, "privacy" => !_privacyOn, _ => false };
        Send(new Packet { Type = "feature", Name = feature, Allow = next });
    }
    void SetFeature(string? feature, bool enabled)
    {
        try
        {
            bool granted = feature switch { "audio" => _permissions.Audio, "recording" => _permissions.Recording, "block" => _permissions.BlockInput && _permissions.Input, "privacy" => _permissions.Privacy, _ => false };
            if (enabled && !granted) return;
            string? detail = null;
            switch (feature)
            {
                case "audio": if (enabled) _audio.StartCapture(); else _audio.StopCapture(); _audioOn = enabled; break;
                case "recording":
                    if (enabled && _curtain.IsVisible) { Send(new Packet { Type = "feature-state", Name = feature, Allow = false, Text = "Tắt màn che trước khi ghi hình." }); return; }
                    if (enabled) { Directory.CreateDirectory(_recordingDirectory); _recordingPath = Path.Combine(_recordingDirectory, $"LanBridge-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.avi"); _recorder.Start(_recordingPath); detail = "Lưu trên máy B: " + _recordingPath; } else _recorder.Stop(); _recordingOn = enabled; break;
                case "block": _input.SetBlockLocalInput(enabled); _blockingOn = enabled; break;
                case "privacy":
                    if (enabled) { _recorder.Stop(); _recordingOn = false; Send(new Packet { Type = "feature-state", Name = "recording", Allow = false, Text = "Đã dừng vì màn che bật." }); _curtain.Show(); } else _curtain.Hide(); _privacyOn = enabled;
                    detail = "Màn che phần mềm trên B; không phải chế độ tắt màn hình bằng driver."; break;
                default: return;
            }
            Log($"{FeatureLabel(feature)}: {(enabled ? "bật" : "tắt")}" + (detail == null ? "" : ". " + detail));
            UpdateFeatureButtons();
            Send(new Packet { Type = "feature-state", Name = feature, Allow = enabled, Text = detail });
        }
        catch (Exception ex) { Log(ex.Message); Send(new Packet { Type = "feature-state", Name = feature, Allow = false, Text = ex.Message }); }
    }

    async Task SendFile()
    {
        try { if (_files == null || !_permissions.Files) return; using var dialog = new OpenFileDialog { Title = "Chọn file gửi sang máy kia" }; if (dialog.ShowDialog(this) == DialogResult.OK && _files != null) await _files.SendFileAsync(dialog.FileName, _session!.Token); }
        catch (Exception ex) { if (!_closing) Log("Gửi file: " + ex.Message); }
    }
    void SendChat() { if (_peer == null || string.IsNullOrWhiteSpace(_chat.Text)) return; var text = _chat.Text[..Math.Min(_chat.TextLength, 2000)]; Send(new Packet { Type = "chat", Text = text }); Log("Bạn: " + text); _chat.Clear(); }
    void RestartRemote() { if (_peer != null && !IsHost && _permissions.Restart && MessageBox.Show(this, "Yêu cầu khởi động lại máy B? Máy B sẽ hiện hộp thoại xác nhận.", "Khởi động lại B", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) Send(new Packet { Type = "restart" }); }
    void ConfirmRestart()
    {
        Emergency();
        if (MessageBox.Show(this, "Máy A yêu cầu khởi động lại máy B. Lưu công việc trước khi đồng ý.\n\nKhởi động lại sau 30 giây?", "LanBridge · Xác nhận trên máy B", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        Process.Start(new ProcessStartInfo("shutdown.exe", "/r /t 30 /c \"LanBridge: khoi dong lai theo xac nhan tren may B\"") { CreateNoWindow = true, UseShellExecute = false });
        Log("Đã yêu cầu Windows khởi động lại sau 30 giây. Có thể hủy bằng shutdown /a.");
    }
    void Emergency()
    {
        _input.ReleaseRemote(); _input.ForceReleaseAll(); _input.SetBlockLocalInput(false); _curtain.Hide(); _blockingOn = _privacyOn = false;
        if (IsHost) { _permissions.Input = false; ApplyPermissions(); Send(new Packet { Type = "permissions", Permissions = _permissions with { } }); Send(new Packet { Type = "feature-state", Name = "block", Allow = false }); Send(new Packet { Type = "feature-state", Name = "privacy", Allow = false }); }
        Log("Đã trả điều khiển, mở khóa đầu vào và bỏ màn che." + (IsHost ? " Bật lại quyền Chuột / phím trên B để tiếp tục." : ""));
        UpdateFeatureButtons();
    }
    void Send(Packet p) { _peer?.TrySend(p); }
    void WindowsSessionChanged(object sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (e.Reason is Microsoft.Win32.SessionSwitchReason.SessionLock or Microsoft.Win32.SessionSwitchReason.RemoteDisconnect or Microsoft.Win32.SessionSwitchReason.ConsoleDisconnect)
            Ui(() => { if (_peer != null) { Emergency(); Log("Đã dừng điều khiển vì phiên Windows bị khóa hoặc ngắt."); } });
    }
    void Disconnect()
    {
        var peer = _peer; _peer = null; _helloReceived = false; _session?.Cancel(); _inbox?.Complete(); _inbox = null;
        CancelClipboardFiles(); _clipboardFiles?.Dispose(); _clipboardFiles = null; _clipboardFilesCapable = false;
        _input.ReleaseRemote(false); _input.ForceReleaseAll(); _input.CanControl = false; _input.SetBlockLocalInput(false); _input.Send = null;
        _audio.StopCapture(); _audio.StopPlayback(); _recorder.Stop(); _curtain.Hide();
        _audio.Send = null;
        _audioOn = _recordingOn = _blockingOn = _privacyOn = false;
        _files?.Dispose(); _files = null; peer?.Dispose(); _server?.Dispose(); _server = null; _session?.Dispose(); _session = null;
        if (_closing) return;
        _devicesPage.SetSessionState(false); _devicesPage.Connecting = false; _tabs.SelectedTab = _deviceTab;
        _role.Enabled = _network.Enabled = true; _disconnect.Enabled = false; _status.Text = "●  Chưa kết nối"; _status.ForeColor = Muted;
        _remoteState.Text = "CHƯA CÓ PHIÊN KẾT NỐI"; _remoteState.ForeColor = Muted; UpdateRole();
    }
    void Log(string message)
    { if (_closing) return; if (_log.TextLength > 60000) _log.Clear(); _log.AppendText($"{DateTime.Now:HH:mm:ss}  {message}\n"); _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
    void Ui(Action action) { if (_closing || IsDisposed) return; if (InvokeRequired) { try { BeginInvoke(() => { if (!_closing) action(); }); } catch (InvalidOperationException) { } } else action(); }
    Task OnUi(Action action, CancellationToken cancellationToken = default)
    {
        if (_closing || IsDisposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() => { if (_closing || IsDisposed) { completion.TrySetCanceled(); return; } try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } });
        }
        catch (InvalidOperationException) { completion.TrySetCanceled(); }
        return completion.Task.WaitAsync(cancellationToken);
    }
}
