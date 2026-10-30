using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Channels;

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
    readonly System.Windows.Forms.Timer _clipboardTimer = new() { Interval = 650 };
    readonly InputRouter _input = new();
    readonly AudioBridge _audio = new();
    readonly ScreenRecorder _recorder = new();
    readonly PrivacyCurtain _curtain = new();
    Permissions _permissions = new();
    Permissions _localPermissions = new();
    HostServer? _server;
    PeerConnection? _peer;
    CancellationTokenSource? _session;
    Channel<Packet>? _inbox;
    FileTransfer? _files;
    string _lastClipboard = "", _recordingPath = "";
    bool _syncChecks, _closing, _audioOn, _recordingOn, _blockingOn, _privacyOn, _connecting, _helloReceived;
    bool IsHost => _role.SelectedIndex == 1;
    readonly string _receivedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanBridge");
    readonly string _recordingDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "LanBridge");

    public MainForm(bool preview = false)
    {
        _preview = preview;
        Text = "LanBridge · Một bộ chuột phím, hai máy tính";
        BackColor = Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10);
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

    void BuildUi()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(22), BackColor = Background };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235)); shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(shell);
        var side = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(20), Margin = new Padding(0, 0, 20, 0) }; shell.Controls.Add(side, 0, 0);
        var brand = LabelAt(side, "LanBridge", 18, 24, 200, 40, 25, Color.White, true);
        LabelAt(side, "KẾT NỐI KHÔNG GIAN LÀM VIỆC", 20, 74, 190, 44, 9, Accent, true);
        foreach (var (caption, index, top) in new[] { ("Thiết bị trong mạng", 0, 150), ("Phiên điều khiển", 1, 196), ("Kết nối bằng mã", 2, 242) })
        {
            var nav = new DarkButton(); StyleButton(nav, caption, 15, top, 198, 38, Background); nav.ForeColor = Color.White;
            nav.Click += (_, _) => _tabs.SelectedIndex = index; side.Controls.Add(nav);
        }
        LabelAt(side, "THOÁT NHANH", 20, 348, 190, 26, 9, Accent, true);
        LabelAt(side, "Ctrl + Alt + F12", 20, 380, 195, 35, 16, Color.White, true);
        LabelAt(side, "Trả điều khiển về máy A.\nTrên B: tắt khóa đầu vào,\nbỏ màn che và thu hồi\nquyền chuột/phím.", 20, 428, 195, 105, 10, Muted);
        LabelAt(side, "WINDOWS 10 / 11 · LAN\nTỰ TÌM MÁY · TLS\n\nBản thử nghiệm 0.3.0", 20, 600, 195, 100, 9, Muted);

        var main = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Margin = Padding.Empty };
        foreach (var height in new[] { 64, 0, 174, 89, 58 }) main.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var workspace = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; shell.Controls.Add(workspace, 1, 0);
        var title = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        LabelAt(title, "Một bộ chuột phím. Hai máy tính.", 0, 0, 760, 42, 23, Color.White, true);
        LabelAt(title, "Đưa chuột qua mép màn hình để tiếp tục làm việc trên máy B.", 2, 47, 750, 27, 10, Muted);
        title.Dock = DockStyle.Top; title.Height = 88; workspace.Controls.Add(title);
        _tabs.Dock = DockStyle.Fill; _tabs.Font = new Font("Segoe UI", 10); _tabs.ItemSize = new Size(180, 36); _tabs.SizeMode = TabSizeMode.Fixed;
        _tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
        _tabs.DrawItem += (_, e) => { using var brush = new SolidBrush(e.Index == _tabs.SelectedIndex ? Card : Background); e.Graphics.FillRectangle(brush, e.Bounds); TextRenderer.DrawText(e.Graphics, _tabs.TabPages[e.Index].Text, Font, e.Bounds, e.Index == _tabs.SelectedIndex ? Accent : Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); };
        foreach (var tab in new[] { _deviceTab, _sessionTab, _manualTab }) { tab.BackColor = Background; tab.ForeColor = Color.White; tab.Padding = new Padding(8); _tabs.TabPages.Add(tab); }
        workspace.Controls.Add(_tabs); _tabs.BringToFront();
        _devicesPage.Dock = DockStyle.Fill; _deviceTab.Controls.Add(_devicesPage); _sessionTab.Controls.Add(main);
        _devicesPage.ConnectRequested += async device => await ConnectDeviceAsync(device);
        _devicesPage.RefreshRequested += async () => await RefreshDevicesAsync();
        _devicesPage.SetLocalState(Environment.MachineName, string.Join(" · ", LanAddresses.GetLocalIPv4()), false, _preview ? "Chế độ xem giao diện" : "Đang chuẩn bị nhận kết nối…");
        var inputSettings = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; main.Controls.Add(inputSettings, 0, 0);
        LabelAt(inputSettings, "VỊ TRÍ MÁY B", 0, 0, 260, 23, 9, Accent, true);
        LabelAt(inputSettings, "BỘ GÕ TRÊN MÁY A", 312, 0, 350, 23, 9, Accent, true);

        var connection = new Panel { Dock = DockStyle.Top, Height = 213, BackColor = Card, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 12) }; _manualTab.Controls.Add(connection);
        LabelAt(connection, "KẾT NỐI BẰNG MÃ", 18, 13, 240, 23, 9, Accent, true);
        _status.SetBounds(330, 12, 400, 24); _status.TextAlign = ContentAlignment.MiddleRight; _status.ForeColor = Muted; _status.Text = "●  Chưa kết nối"; connection.Controls.Add(_status);
        StyleCombo(_role, 18, 44, 280); _role.Items.AddRange(["Máy A · Chuột và bàn phím chính", "Máy B · Nhận điều khiển"]); _role.SelectedIndex = 0; connection.Controls.Add(_role);
        StyleCombo(_edge, 0, 25, 280); _edge.Items.AddRange(["B nằm bên phải A →", "B nằm bên trái A ←", "B nằm phía trên A ↑", "B nằm phía dưới A ↓"]); _edge.SelectedIndex = 0; inputSettings.Controls.Add(_edge);
        StyleCombo(_typing, 312, 25, 280); _typing.Items.AddRange(["Gõ: Tiếng Việt Telex", "Gõ: Tiếng Việt VNI", "Gõ: Phím gốc"]); _typing.SelectedIndex = 0; inputSettings.Controls.Add(_typing);
        _typing.SelectedIndexChanged += (_, _) => { _input.TypingMode = (TypingMode)_typing.SelectedIndex; Log(_typing.Text + ". Telex/VNI dùng bộ gõ trong LanBridge; không cần bật UniKey trên hai máy."); };
        StyleCombo(_network, 312, 44, 280); connection.Controls.Add(_network); _network.Visible = false;
        foreach (var address in LanAddresses.GetLocalIPv4()) _network.Items.Add(address.ToString());
        _network.SelectedIndex = 0;
        _hint.SetBounds(18, 86, 720, 23); _hint.Text = "Dán mã ghép nối hiển thị trên máy B vào ô bên dưới."; _hint.ForeColor = Muted; connection.Controls.Add(_hint);
        StyleText(_pairing, 18, 113, 570, 34); _pairing.PlaceholderText = "LB1|địa-chỉ-IP|cổng|dấu-vân-tay|mã-bí-mật"; _pairing.UseSystemPasswordChar = true; connection.Controls.Add(_pairing);
        StyleButton(_connect, "Kết nối máy B", 602, 110, 132, 37, Accent); connection.Controls.Add(_connect);
        StyleButton(_copy, "Sao chép mã", 602, 110, 132, 37, Accent); _copy.Visible = false; connection.Controls.Add(_copy);
        var showCode = new CheckBox { Text = "Hiện mã", ForeColor = Muted, Location = new Point(18, 157), Size = new Size(100, 26) }; showCode.CheckedChanged += (_, _) => _pairing.UseSystemPasswordChar = !showCode.Checked; connection.Controls.Add(showCode);
        _peerName.SetBounds(130, 159, 410, 24); _peerName.ForeColor = Muted; _peerName.Text = Environment.MachineName; connection.Controls.Add(_peerName);
        StyleButton(_disconnect, "Ngắt kết nối", 602, 156, 132, 30, Color.FromArgb(234, 94, 112)); _disconnect.Enabled = false; connection.Controls.Add(_disconnect);
        _role.SelectedIndexChanged += (_, _) => UpdateRole();
        _edge.SelectedIndexChanged += (_, _) => { if (!IsHost && _peer != null) _input.ConfigureController(_helloReceived && _permissions.Input, (ScreenEdge)_edge.SelectedIndex); };
        _connect.Click += async (_, _) => await ConnectOrListen();
        _copy.Click += (_, _) => { if (_pairing.TextLength > 0) { Clipboard.SetText(_pairing.Text); Log("Đã sao chép mã. Chỉ chia sẻ với máy A của bạn."); } };
        _disconnect.Click += (_, _) => Disconnect();

        var permissionCard = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 0, 0, 12) }; main.Controls.Add(permissionCard, 0, 2);
        LabelAt(permissionCard, "QUYỀN DO MÁY B CẤP", 18, 12, 450, 25, 9, Accent, true);
        _permissionsPanel.SetBounds(14, 43, 725, 108); _permissionsPanel.WrapContents = true; permissionCard.Controls.Add(_permissionsPanel);
        AddPermission("Input", "⌨  Chuột / phím"); AddPermission("Clipboard", "▣  Clipboard chữ"); AddPermission("Audio", "♪  Âm thanh B"); AddPermission("Files", "↑  Truyền file");
        AddPermission("Restart", "↻  Khởi động lại"); AddPermission("Recording", "●  Ghi màn hình B"); AddPermission("BlockInput", "⊘  Khóa phím B"); AddPermission("Privacy", "◐  Màn che B");
        var actionCard = new Panel { Dock = DockStyle.Fill, BackColor = Card, Margin = new Padding(0, 0, 0, 12) }; main.Controls.Add(actionCard, 0, 3);
        _actions.SetBounds(12, 10, 730, 62); actionCard.Controls.Add(_actions);
        AddAction("Gửi file", async () => await SendFile()); AddAction("Nghe B", () => RequestFeature("audio")); AddAction("Ghi hình", () => RequestFeature("recording")); AddAction("Khóa B", () => RequestFeature("block")); AddAction("Màn che", () => RequestFeature("privacy")); AddAction("Restart B", RestartRemote);
        var liveStatus = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; main.Controls.Add(liveStatus, 0, 4);
        _remoteState.SetBounds(0, 0, 580, 25); _remoteState.Text = "CHƯA CÓ PHIÊN KẾT NỐI"; _remoteState.ForeColor = Muted; _remoteState.Font = new Font(Font, FontStyle.Bold); liveStatus.Controls.Add(_remoteState);
        var endSession = new DarkButton(); StyleButton(endSession, "Ngắt kết nối", 602, 0, 132, 28, Color.FromArgb(234, 94, 112)); endSession.Click += (_, _) => Disconnect(); liveStatus.Controls.Add(endSession);
        _featureStatus.SetBounds(0, 27, 750, 25); _featureStatus.ForeColor = Muted; _featureStatus.Font = new Font("Segoe UI", 9); _featureStatus.AutoEllipsis = true; liveStatus.Controls.Add(_featureStatus);

        var logCard = new Panel { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(12), Margin = Padding.Empty }; main.Controls.Add(logCard, 0, 5);
        var chatRow = new Panel { Dock = DockStyle.Bottom, Height = 38 }; logCard.Controls.Add(chatRow);
        _chat.Dock = DockStyle.Fill; _chat.BackColor = Background; _chat.ForeColor = Color.White; _chat.BorderStyle = BorderStyle.FixedSingle; _chat.PlaceholderText = "Gửi tin nhắn trong phiên LAN…"; chatRow.Controls.Add(_chat);
        var sendChat = new Button { Text = "Gửi", Dock = DockStyle.Right, Width = 65, BackColor = Accent, ForeColor = Background, FlatStyle = FlatStyle.Flat }; chatRow.Controls.Add(sendChat);
        sendChat.Click += (_, _) => SendChat(); _chat.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SendChat(); e.SuppressKeyPress = true; } };
        _log.Dock = DockStyle.Fill; _log.ReadOnly = true; _log.BackColor = Card; _log.ForeColor = Muted; _log.BorderStyle = BorderStyle.None; _log.Font = new Font("Segoe UI", 9); logCard.Controls.Add(_log); _log.BringToFront();
        UpdateRole();
    }

    static Label LabelAt(Control parent, string text, int x, int y, int width, int height, float size, Color color, bool bold = false)
    { var l = new Label { Text = text, Bounds = new Rectangle(x, y, width, height), ForeColor = color, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) }; parent.Controls.Add(l); return l; }
    static void StyleCombo(ComboBox c, int x, int y, int w)
    {
        c.SetBounds(x, y, w, 32); c.DropDownStyle = ComboBoxStyle.DropDownList; c.BackColor = Background; c.ForeColor = Color.White; c.FlatStyle = FlatStyle.Flat;
        c.DrawMode = DrawMode.OwnerDrawFixed; c.ItemHeight = 26;
        c.DrawItem += (_, e) => { using var brush = new SolidBrush(Background); e.Graphics.FillRectangle(brush, e.Bounds); int index = e.Index >= 0 ? e.Index : c.SelectedIndex; if (index >= 0) TextRenderer.DrawText(e.Graphics, c.Items[index]?.ToString(), c.Font, e.Bounds, Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter); };
    }
    static void StyleText(TextBox t, int x, int y, int w, int h) { t.SetBounds(x, y, w, h); t.BackColor = Background; t.ForeColor = Color.White; t.BorderStyle = BorderStyle.FixedSingle; t.Font = new Font("Segoe UI", 11); }
    static void StyleButton(Button b, string text, int x, int y, int w, int h, Color color) { b.Text = text; b.SetBounds(x, y, w, h); b.BackColor = color; b.ForeColor = Background; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.Cursor = Cursors.Hand; b.Font = new Font("Segoe UI", 9, FontStyle.Bold); }
    void AddPermission(string key, string title)
    {
        var check = new AccessToggle { Text = title, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter, Size = new Size(169, 42), Margin = new Padding(4), FlatStyle = FlatStyle.Flat, BackColor = Background, ForeColor = Muted, Cursor = Cursors.Hand };
        check.FlatAppearance.BorderColor = Color.FromArgb(51, 63, 82); _checks[key] = check; _permissionsPanel.Controls.Add(check);
        check.CheckedChanged += (_, _) => { check.BackColor = check.Checked ? Color.FromArgb(25, 70, 68) : Background; check.ForeColor = check.Checked ? Accent : Muted; if (!_syncChecks && IsHost) { typeof(Permissions).GetProperty(key)!.SetValue(_permissions, check.Checked); ApplyPermissions(); Send(new Packet { Type = "permissions", Permissions = _permissions with { } }); } };
    }
    void AddAction(string title, Action action)
    { var b = new DarkButton(); StyleButton(b, title, 0, 0, 111, 41, Color.FromArgb(49, 62, 83)); b.ForeColor = Color.White; b.Margin = new Padding(4, 8, 4, 0); b.Click += (_, _) => { try { action(); } catch (Exception ex) { Log(ex.Message); } }; _actions.Controls.Add(b); }

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
        _peer = peer; _helloReceived = false; _session = new(); var token = _session.Token;
        _input.Send = packet => peer.TrySend(packet); _audio.Send = packet => peer.TrySend(packet);
        _inbox = Channel.CreateBounded<Packet>(new BoundedChannelOptions(512) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });
        var inbox = _inbox;
        peer.Received += packet => { if (!inbox.Writer.TryWrite(packet)) peer.Dispose(); };
        peer.Closed += reason => Ui(() => { if (_peer == peer) { Log("Phiên đã đóng" + (reason == null ? "." : ": " + reason)); Disconnect(); } });
        _files = new FileTransfer(_receivedDirectory, p => peer.SendAsync(p, token), message => Ui(() => Log(message)));
        _input.IsController = !IsHost; _input.CanControl = false;
        try { if (!_preview) _input.Start(); }
        catch { Disconnect(); throw; }
        _role.Enabled = _network.Enabled = false; _connect.Visible = _copy.Visible = false; _disconnect.Enabled = true;
        _status.Text = "●  Đã kết nối · TLS"; _status.ForeColor = Accent;
        peer.Start();
        Task.Run(async () => { try { await foreach (var p in inbox.Reader.ReadAllAsync(token)) await HandlePacket(p, token); } catch (OperationCanceledException) { } catch (Exception ex) { Ui(() => { Log("Phiên: " + ex.Message); if (_peer == peer) Disconnect(); }); } }, token);
        var bounds = SystemInformation.VirtualScreen;
        Send(new Packet { Type = "hello", Name = Environment.MachineName, Width = bounds.Width, Height = bounds.Height, Flags = 2 });
        if (IsHost) Send(new Packet { Type = "permissions", Permissions = _permissions with { } });
        ApplyPermissions(); _remoteState.Text = IsHost ? "MÁY B SẴN SÀNG NHẬN ĐIỀU KHIỂN" : "CHUỘT ĐANG Ở MÁY A";
        _devicesPage.SetSessionState(true); _devicesPage.Connecting = false; _tabs.SelectedTab = _sessionTab;
        Log("Đã ghép nối. Chạm mép đã chọn để chuyển máy ngay; Ctrl + Alt + F12 để trả điều khiển.");
        if (!IsHost) Log("Bộ gõ mặc định: Telex tích hợp. Gõ tieengs Vieetj → tiếng Việt. Chọn VNI hoặc Phím gốc trong ô Gõ.");
    }

    async Task HandlePacket(Packet p, CancellationToken token)
    {
        if (token.IsCancellationRequested) return;
        if (p.Type.StartsWith("file-", StringComparison.Ordinal)) { var files = _files; if (files != null) await files.HandleAsync(p, _permissions.Files); return; }
        await OnUi(() =>
        {
            if (_peer == null || token.IsCancellationRequested) return;
            switch (p.Type)
            {
                case "hello":
                    if (p.Flags != 2) throw new InvalidDataException("Hai máy cần dùng LanBridge 0.2.0 hoặc mới hơn. Hãy cập nhật cả A và B.");
                    if (p.Width is < 1 or > 65536 || p.Height is < 1 or > 65536) throw new InvalidDataException("Kích thước màn hình không hợp lệ.");
                    _input.RemoteWidth = p.Width; _input.RemoteHeight = p.Height; _peerName.Text = "Đối tác: " + (p.Name ?? "Máy tính")[..Math.Min(p.Name?.Length ?? 0, 100)];
                    _helloReceived = true; _devicesPage.SetSessionState(true, p.Name); ApplyPermissions(); break;
                case "permissions" when !IsHost && p.Permissions != null: _permissions = p.Permissions; ApplyPermissions(); break;
                case "enter" or "move" or "key" or "text" or "button" or "wheel" when IsHost && _permissions.Input:
                    _input.ApplyRemote(p);
                    if (_input.LastError is { } error) { Log(error); Emergency(); }
                    break;
                case "leave":
                    if (IsHost || p.Allow) { _input.ApplyRemote(p); if (IsHost) { _input.SetBlockLocalInput(false); _blockingOn = false; Send(new Packet { Type = "feature-state", Name = "block", Allow = false }); } } break;
                case "clipboard" when _permissions.Clipboard && p.Text != null && p.Text.Length <= 262144:
                    if (_lastClipboard != p.Text) { _lastClipboard = p.Text; try { if (p.Text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(p.Text); } catch (System.Runtime.InteropServices.ExternalException) { } } break;
                case "chat": Log("Đối tác: " + (p.Text ?? "")[..Math.Min(p.Text?.Length ?? 0, 2000)]); break;
                case "audio-format" or "audio" or "audio-stop" when !IsHost && _permissions.Audio: _audio.Handle(p); break;
                case "feature" when IsHost: SetFeature(p.Name, p.Allow); break;
                case "feature-state" when !IsHost:
                    switch (p.Name) { case "audio": _audioOn = p.Allow; break; case "recording": _recordingOn = p.Allow; break; case "block": _blockingOn = p.Allow; break; case "privacy": _privacyOn = p.Allow; break; }
                    UpdateFeatureButtons(); Log($"{FeatureLabel(p.Name)}: {(p.Allow ? "đang bật" : "đã tắt")}" + (p.Text == null ? "" : ". " + p.Text)); break;
                case "restart" when IsHost && _permissions.Restart: ConfirmRestart(); break;
            }
        });
    }

    void ApplyPermissions()
    {
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

    void SyncClipboard()
    {
        if (_peer == null || !_permissions.Clipboard) return;
        try { if (Clipboard.ContainsText()) { var text = Clipboard.GetText(); if (text.Length <= 262144 && text != _lastClipboard) { _lastClipboard = text; Send(new Packet { Type = "clipboard", Text = text }); } } }
        catch (System.Runtime.InteropServices.ExternalException) { }
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
        var peer = _peer; _peer = null; _helloReceived = false; _session?.Cancel(); _inbox?.Writer.TryComplete();
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
    Task OnUi(Action action)
    {
        if (_closing || IsDisposed) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() => { if (_closing || IsDisposed) { completion.TrySetCanceled(); return; } try { action(); completion.TrySetResult(); } catch (Exception ex) { completion.TrySetException(ex); } });
        }
        catch (InvalidOperationException) { completion.TrySetCanceled(); }
        return completion.Task;
    }
}

internal sealed class AccessToggle : CheckBox
{
    public AccessToggle() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Checked ? Color.FromArgb(25, 70, 68) : Color.FromArgb(16, 20, 29));
        ControlPaint.DrawBorder(e.Graphics, ClientRectangle, Checked ? Color.FromArgb(47, 208, 177) : Color.FromArgb(57, 69, 87), ButtonBorderStyle.Solid);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Checked ? Color.FromArgb(104, 235, 211) : Color.FromArgb(172, 185, 202), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }
}

internal sealed class DarkButton : Button
{
    public DarkButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Enabled ? BackColor : Color.FromArgb(39, 47, 62));
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(127, 144, 165), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        if (Focused && Enabled) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}
