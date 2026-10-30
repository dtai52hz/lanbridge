using System.Drawing.Drawing2D;

namespace LanBridge;

public sealed partial class MainForm
{
    void BuildUi()
    {
        var shell = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
            Padding = new Padding(20), BackColor = Background, Margin = Padding.Empty
        };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 235));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); Controls.Add(shell);
        BuildSidebar(shell);

        var workspace = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty,
            BackColor = Background
        };
        workspace.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        workspace.RowStyles.Add(new RowStyle(SizeType.Absolute, 92));
        workspace.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); shell.Controls.Add(workspace, 1, 0);
        var title = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty,
            Padding = new Padding(20, 7, 0, 12)
        };
        title.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        title.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        title.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        title.Controls.Add(UiLabel("Một bộ chuột phím. Hai máy tính.", 22, Color.White, true), 0, 0);
        title.Controls.Add(UiLabel("Chạm mép màn hình để tiếp tục công việc trên máy bên cạnh.", 9.5f, Muted), 0, 1);
        workspace.Controls.Add(title, 0, 0);

        _tabs.Dock = DockStyle.Fill; _tabs.Margin = Padding.Empty; _tabs.TabStop = false;
        foreach (var tab in new[] { _deviceTab, _sessionTab, _manualTab })
        {
            tab.BackColor = Background; tab.ForeColor = Color.White; tab.Padding = Padding.Empty;
            _tabs.TabPages.Add(tab);
        }
        workspace.Controls.Add(_tabs, 0, 1);
        _devicesPage.Dock = DockStyle.Fill; _deviceTab.Controls.Add(_devicesPage);
        _devicesPage.ConnectRequested += async device => await ConnectDeviceAsync(device);
        _devicesPage.RefreshRequested += async () => await RefreshDevicesAsync();
        _devicesPage.SetLocalState(Environment.MachineName, string.Join(" · ", LanAddresses.GetLocalIPv4()), false,
            _preview ? "Chế độ xem giao diện" : "Đang chuẩn bị nhận kết nối…");

        BuildManualPage();
        BuildSessionPage();
        _role.SelectedIndexChanged += (_, _) => UpdateRole();
        _edge.SelectedIndexChanged += (_, _) =>
        {
            if (!IsHost && _peer != null) _input.ConfigureController(_helloReceived && _permissions.Input, (ScreenEdge)_edge.SelectedIndex);
        };
        _typing.SelectedIndexChanged += (_, _) =>
        {
            _input.TypingMode = (TypingMode)_typing.SelectedIndex;
            Log(_typing.Text + ". Telex/VNI dùng bộ gõ trong LanBridge; không cần bật UniKey trên hai máy.");
        };
        _connect.Click += async (_, _) => await ConnectOrListen();
        _copy.Click += (_, _) =>
        {
            if (_pairing.TextLength > 0) { Clipboard.SetText(_pairing.Text); Log("Đã sao chép mã. Chỉ chia sẻ với máy A của bạn."); }
        };
        _disconnect.Click += (_, _) => Disconnect();
        UpdateRole();
    }

    void BuildSidebar(TableLayoutPanel shell)
    {
        var side = new UiCard
        {
            Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(18, 23, 18, 16),
            Margin = new Padding(0, 0, 16, 0)
        };
        shell.Controls.Add(side, 0, 0);
        var content = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8,
            BackColor = Card, Margin = Padding.Empty
        };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 215));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        content.RowStyles.Add(new RowStyle(SizeType.Absolute, 91)); side.Controls.Add(content);
        content.Controls.Add(UiLabel("LanBridge", 25, Color.White, true), 0, 0);
        content.Controls.Add(UiLabel("KHÔNG GIAN LÀM VIỆC\nLIỀN MẠCH", 8.5f, Accent, true), 0, 1);
        var navigation = new List<DarkButton>();
        foreach (var (caption, index) in new[] { ("01   Thiết bị trong mạng", 0), ("02   Phiên điều khiển", 1), ("03   Kết nối bằng mã", 2) })
        {
            var nav = new DarkButton(); StyleButton(nav, caption, 0, 0, 180, 42, Card);
            nav.Dock = DockStyle.Fill; nav.Margin = new Padding(0, 0, 0, 7);
            nav.TextAlign = ContentAlignment.MiddleLeft; nav.Padding = new Padding(11, 0, 5, 0);
            nav.Click += (_, _) => _tabs.SelectedIndex = index;
            content.Controls.Add(nav, 0, index + 2); navigation.Add(nav);
        }
        void HighlightNavigation()
        {
            for (int index = 0; index < navigation.Count; index++) navigation[index].SetNavigationState(index == Math.Max(0, _tabs.SelectedIndex));
        }
        _tabs.SelectedIndexChanged += (_, _) => HighlightNavigation();
        HighlightNavigation();
        var shortcut = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(1, 32, 0, 0),
            Margin = Padding.Empty, BackColor = Card
        };
        shortcut.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shortcut.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        shortcut.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        shortcut.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        shortcut.Controls.Add(UiLabel("TRỞ VỀ MÁY A", 8.5f, Accent, true), 0, 0);
        shortcut.Controls.Add(UiLabel("Ctrl + Alt + F12", 15, Color.White, true), 0, 1);
        shortcut.Controls.Add(UiLabel("Trả chuột và bàn phím về A.\nTrên B: nhả phím, tắt khóa\nvà bỏ màn che ngay.", 9, Muted), 0, 2);
        content.Controls.Add(shortcut, 0, 5);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, BackColor = Card };
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        footer.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.Controls.Add(UiLabel("●  Thu nhỏ vẫn giữ kết nối", 8.5f, Accent), 0, 0);
        footer.Controls.Add(UiLabel("WINDOWS 10 / 11  ·  LAN / TLS", 8, Muted), 0, 1);
        footer.Controls.Add(UiLabel("Bản thử nghiệm 0.3.1", 8.5f, Muted), 0, 2);
        content.Controls.Add(footer, 0, 7);
    }

    void BuildManualPage()
    {
        var page = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(22),
            Margin = Padding.Empty, BackColor = Background
        };
        page.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        page.RowStyles.Add(new RowStyle(SizeType.Absolute, 278));
        page.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); _manualTab.Controls.Add(page);
        page.Controls.Add(UiLabel("Kết nối bằng mã", 22, Color.White, true), 0, 0);
        page.Controls.Add(UiLabel("Dùng mã ghép nối khi bạn muốn chọn trực tiếp máy cần điều khiển.", 9.5f, Muted), 0, 1);
        var card = new UiCard { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(18), Margin = new Padding(0, 0, 0, 16) };
        page.Controls.Add(card, 0, 2);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, BackColor = Card, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 30, 46, 52, 48, 48 }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        card.Controls.Add(content);
        var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
        heading.Controls.Add(UiLabel("GHÉP NỐI AN TOÀN", 8.5f, Accent, true), 0, 0);
        ConfigureUiLabel(_status, 9, Muted); _status.Text = "●  Chưa kết nối"; _status.TextAlign = ContentAlignment.TopRight;
        heading.Controls.Add(_status, 1, 0); content.Controls.Add(heading, 0, 0);
        var roleRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        roleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 57)); roleRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 43));
        StyleCombo(_role); _role.Items.AddRange(["Máy A · Chuột và bàn phím chính", "Máy B · Nhận điều khiển"]); _role.SelectedIndex = 0;
        _role.Margin = new Padding(0, 0, 12, 0); roleRow.Controls.Add(_role, 0, 0);
        StyleCombo(_network); foreach (var address in LanAddresses.GetLocalIPv4()) _network.Items.Add(address.ToString());
        _network.SelectedIndex = 0; _network.Visible = false; _network.Margin = Padding.Empty; roleRow.Controls.Add(_network, 1, 0);
        content.Controls.Add(roleRow, 0, 1);
        ConfigureUiLabel(_hint, 9.5f, Muted); _hint.AutoEllipsis = false;
        _hint.Text = "Dán mã ghép nối hiển thị trên máy B vào ô bên dưới.";
        _hint.Margin = new Padding(0, 3, 0, 5); content.Controls.Add(_hint, 0, 2);
        var pairingRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        pairingRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        pairingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); pairingRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        StyleText(_pairing); _pairing.PlaceholderText = "LB1|địa-chỉ-IP|cổng|dấu-vân-tay|mã-bí-mật"; _pairing.UseSystemPasswordChar = true;
        _pairing.Margin = new Padding(0, 0, 12, 8); pairingRow.Controls.Add(_pairing, 0, 0);
        var connectionActions = new Panel { Dock = DockStyle.Fill, Height = 38, Margin = new Padding(0, 0, 0, 8), BackColor = Card };
        StyleButton(_connect, "Kết nối máy B", 0, 0, 145, 38, Accent); _connect.Dock = DockStyle.Fill; connectionActions.Controls.Add(_connect);
        StyleButton(_copy, "Sao chép mã", 0, 0, 145, 38, Accent); _copy.Dock = DockStyle.Fill; _copy.Visible = false; connectionActions.Controls.Add(_copy);
        pairingRow.Controls.Add(connectionActions, 1, 0); content.Controls.Add(pairingRow, 0, 3);
        var detailRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty };
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100)); detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        detailRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 145));
        var showCode = new CheckBox { Text = "Hiện mã", Dock = DockStyle.Fill, ForeColor = Muted, Font = new Font("Segoe UI", 9), Margin = Padding.Empty };
        showCode.CheckedChanged += (_, _) => _pairing.UseSystemPasswordChar = !showCode.Checked; detailRow.Controls.Add(showCode, 0, 0);
        ConfigureUiLabel(_peerName, 9, Muted); _peerName.Text = Environment.MachineName; _peerName.TextAlign = ContentAlignment.MiddleLeft;
        _peerName.Margin = new Padding(0, 0, 10, 0); detailRow.Controls.Add(_peerName, 1, 0);
        StyleButton(_disconnect, "Ngắt kết nối", 0, 0, 145, 36, Color.FromArgb(234, 94, 112)); _disconnect.Dock = DockStyle.Fill;
        _disconnect.Margin = new Padding(0, 6, 0, 5); _disconnect.Enabled = false; detailRow.Controls.Add(_disconnect, 2, 0);
        content.Controls.Add(detailRow, 0, 4);
        var note = UiLabel("Mã ghép nối là riêng tư. Chỉ chia sẻ mã với máy của bạn.\nBạn có thể thu nhỏ LanBridge trong khi dùng chuột, bàn phím và truyền file.", 9, Muted);
        note.Margin = new Padding(0, 4, 0, 0); note.AutoEllipsis = false; page.Controls.Add(note, 0, 3);
    }

    void BuildSessionPage()
    {
        var main = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5,
            Padding = new Padding(22, 17, 22, 22), Margin = Padding.Empty
        };
        main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 90, 182, 78, 70 }) main.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        main.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); _sessionTab.Controls.Add(main);
        var inputSettings = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        inputSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); inputSettings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        inputSettings.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); inputSettings.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        inputSettings.Controls.Add(UiLabel("VỊ TRÍ MÁY B", 8.5f, Accent, true), 0, 0);
        inputSettings.Controls.Add(UiLabel("BỘ GÕ TRÊN MÁY A", 8.5f, Accent, true), 1, 0);
        StyleCombo(_edge); _edge.Items.AddRange(["B nằm bên phải A →", "B nằm bên trái A ←", "B nằm phía trên A ↑", "B nằm phía dưới A ↓"]);
        _edge.SelectedIndex = 0; _edge.Margin = new Padding(0, 0, 10, 18); inputSettings.Controls.Add(_edge, 0, 1);
        StyleCombo(_typing); _typing.Items.AddRange(["Gõ: Tiếng Việt Telex", "Gõ: Tiếng Việt VNI", "Gõ: Phím gốc"]);
        _typing.SelectedIndex = 0; _typing.Margin = new Padding(0, 0, 0, 18); inputSettings.Controls.Add(_typing, 1, 1);
        main.Controls.Add(inputSettings, 0, 0);

        var permissionCard = new UiCard { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(13, 13, 13, 10), Margin = new Padding(0, 0, 0, 13) };
        var permissionContent = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Card, Margin = Padding.Empty };
        permissionContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        permissionContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 25));
        permissionContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        permissionContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 21)); permissionCard.Controls.Add(permissionContent);
        var permissionTitle = UiLabel("QUYỀN DO MÁY B CẤP", 8.5f, Accent, true); permissionTitle.Margin = new Padding(4, 0, 0, 0);
        permissionContent.Controls.Add(permissionTitle, 0, 0);
        _permissionsPanel.Dock = DockStyle.Fill; _permissionsPanel.WrapContents = true; _permissionsPanel.Margin = Padding.Empty;
        _permissionsPanel.BackColor = Card; _permissionsPanel.Padding = Padding.Empty; permissionContent.Controls.Add(_permissionsPanel, 0, 1);
        AddPermission("Input", "⌨  Chuột / phím"); AddPermission("Clipboard", "▣  Clipboard chữ");
        AddPermission("Audio", "♪  Âm thanh B"); AddPermission("Files", "↑  Truyền file");
        AddPermission("Restart", "↻  Khởi động lại"); AddPermission("Recording", "●  Ghi màn hình B");
        AddPermission("BlockInput", "⊘  Khóa phím B"); AddPermission("Privacy", "◐  Màn che B");
        var permissionHint = UiLabel("Chỉ máy nhận điều khiển được thay đổi các quyền này.", 8, Muted);
        permissionHint.Margin = new Padding(4, 2, 0, 0); permissionContent.Controls.Add(permissionHint, 0, 2);
        BindGridWidths(_permissionsPanel, 4); main.Controls.Add(permissionCard, 0, 1);

        var actionCard = new UiCard { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(9, 10, 9, 9), Margin = new Padding(0, 0, 0, 12) };
        _actions.Dock = DockStyle.Fill; _actions.WrapContents = false; _actions.Margin = Padding.Empty;
        _actions.BackColor = Card; actionCard.Controls.Add(_actions);
        AddAction("Gửi file", async () => await SendFile()); AddAction("Nghe B", () => RequestFeature("audio"));
        AddAction("Ghi hình", () => RequestFeature("recording")); AddAction("Khóa B", () => RequestFeature("block"));
        AddAction("Màn che", () => RequestFeature("privacy")); AddAction("Restart B", RestartRemote);
        BindGridWidths(_actions, 6); main.Controls.Add(actionCard, 0, 2);
        var liveStatus = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
        liveStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); liveStatus.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 133));
        liveStatus.RowStyles.Add(new RowStyle(SizeType.Absolute, 33)); liveStatus.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        ConfigureUiLabel(_remoteState, 9.5f, Muted, true); _remoteState.Text = "CHƯA CÓ PHIÊN KẾT NỐI";
        _remoteState.TextAlign = ContentAlignment.MiddleLeft; liveStatus.Controls.Add(_remoteState, 0, 0);
        var endSession = new DarkButton(); StyleButton(endSession, "Ngắt kết nối", 0, 0, 133, 30, Color.FromArgb(234, 94, 112));
        endSession.Dock = DockStyle.Fill; endSession.Margin = new Padding(0, 0, 0, 4); endSession.Click += (_, _) => Disconnect();
        endSession.Enabled = _disconnect.Enabled;
        _disconnect.EnabledChanged += (_, _) => endSession.Enabled = _disconnect.Enabled;
        liveStatus.Controls.Add(endSession, 1, 0);
        ConfigureUiLabel(_featureStatus, 8.5f, Muted); _featureStatus.Margin = new Padding(0, 6, 0, 6);
        liveStatus.Controls.Add(_featureStatus, 0, 1); liveStatus.SetColumnSpan(_featureStatus, 2); main.Controls.Add(liveStatus, 0, 3);

        var logCard = new UiCard { Dock = DockStyle.Fill, BackColor = Card, Padding = new Padding(14), Margin = Padding.Empty };
        var logContent = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Card, Margin = Padding.Empty };
        logContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        logContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); logContent.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logContent.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); logCard.Controls.Add(logContent);
        logContent.Controls.Add(UiLabel("NHẬT KÝ & TIN NHẮN", 8.5f, Muted, true), 0, 0);
        _log.Dock = DockStyle.Fill; _log.ReadOnly = true; _log.BackColor = Card; _log.ForeColor = Muted;
        _log.BorderStyle = BorderStyle.None; _log.Font = new Font("Segoe UI", 9); _log.Margin = new Padding(0, 0, 0, 10);
        logContent.Controls.Add(_log, 0, 1);
        var chatRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); chatRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 75));
        StyleText(_chat); _chat.Margin = new Padding(0, 0, 9, 0); _chat.PlaceholderText = "Gửi tin nhắn trong phiên LAN…";
        chatRow.Controls.Add(_chat, 0, 0);
        var sendChat = new DarkButton(); StyleButton(sendChat, "Gửi", 0, 0, 75, 38, Accent); sendChat.Dock = DockStyle.Fill;
        sendChat.Margin = Padding.Empty; chatRow.Controls.Add(sendChat, 1, 0);
        sendChat.Click += (_, _) => SendChat(); _chat.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { SendChat(); e.SuppressKeyPress = true; } };
        logContent.Controls.Add(chatRow, 0, 2); main.Controls.Add(logCard, 0, 4);
    }

    static Label UiLabel(string text, float size, Color color, bool bold = false)
    {
        var label = new Label { Text = text };
        ConfigureUiLabel(label, size, color, bold); return label;
    }

    static void ConfigureUiLabel(Label label, float size, Color color, bool bold = false)
    {
        label.ForeColor = color; label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        label.Dock = DockStyle.Fill; label.AutoEllipsis = true; label.Margin = Padding.Empty; label.UseMnemonic = false;
    }

    static void StyleCombo(ComboBox control)
    {
        control.Dock = DockStyle.Top; control.DropDownStyle = ComboBoxStyle.DropDownList;
        control.BackColor = Card; control.ForeColor = Color.White; control.FlatStyle = FlatStyle.Flat;
        control.Font = new Font("Segoe UI", 10); control.DrawMode = DrawMode.OwnerDrawFixed; control.ItemHeight = 27;
        control.DrawItem += (_, e) =>
        {
            bool selected = (e.State & DrawItemState.Selected) != 0;
            using var brush = new SolidBrush(selected ? Color.FromArgb(32, 65, 65) : Card); e.Graphics.FillRectangle(brush, e.Bounds);
            int index = e.Index >= 0 ? e.Index : control.SelectedIndex;
            if (index >= 0)
                TextRenderer.DrawText(e.Graphics, control.Items[index]?.ToString(), control.Font, Rectangle.Inflate(e.Bounds, -7, 0),
                    control.Enabled ? Color.White : Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        };
    }

    static void StyleText(TextBox control)
    {
        control.Dock = DockStyle.Fill; control.AutoSize = false; control.BackColor = Background;
        control.ForeColor = Color.White; control.BorderStyle = BorderStyle.FixedSingle; control.Font = new Font("Segoe UI", 10.5f);
    }

    static void StyleButton(Button button, string text, int x, int y, int width, int height, Color color)
    {
        button.Text = text; button.SetBounds(x, y, width, height); button.BackColor = color; button.ForeColor = Background;
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.Cursor = Cursors.Hand;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold);
    }

    void AddPermission(string key, string title)
    {
        var check = new AccessToggle
        {
            Text = title, Appearance = Appearance.Button, TextAlign = ContentAlignment.MiddleCenter,
            Size = new Size(150, 41), Margin = new Padding(4), FlatStyle = FlatStyle.Flat,
            BackColor = Background, ForeColor = Muted, Cursor = Cursors.Hand, Font = new Font("Segoe UI", 9)
        };
        check.FlatAppearance.BorderColor = Color.FromArgb(51, 63, 82); _checks[key] = check; _permissionsPanel.Controls.Add(check);
        check.CheckedChanged += (_, _) =>
        {
            check.BackColor = check.Checked ? Color.FromArgb(25, 70, 68) : Background;
            check.ForeColor = check.Checked ? Accent : Muted;
            if (!_syncChecks && IsHost)
            {
                typeof(Permissions).GetProperty(key)!.SetValue(_permissions, check.Checked); ApplyPermissions();
                Send(new Packet { Type = "permissions", Permissions = _permissions with { } });
            }
        };
    }

    void AddAction(string title, Action action)
    {
        var button = new DarkButton(); StyleButton(button, title, 0, 0, 100, 40, Color.FromArgb(39, 51, 69));
        button.ForeColor = Color.White; button.Margin = new Padding(4, 3, 4, 0);
        button.Click += (_, _) => { try { action(); } catch (Exception error) { Log(error.Message); } };
        _actions.Controls.Add(button);
    }

    static void BindGridWidths(FlowLayoutPanel panel, int columns)
    {
        bool resizing = false;
        void ResizeItems()
        {
            if (resizing || panel.ClientSize.Width <= 1) return;
            resizing = true; panel.SuspendLayout();
            try
            {
                var cell = Math.Max(1, (panel.ClientSize.Width - panel.Padding.Horizontal) / columns);
                foreach (Control item in panel.Controls)
                {
                    var width = Math.Max(1, cell - item.Margin.Horizontal);
                    if (item.Width != width) item.Width = width;
                }
            }
            finally { panel.ResumeLayout(true); resizing = false; }
        }
        panel.ClientSizeChanged += (_, _) => ResizeItems();
        ResizeItems();
    }
}

internal sealed class WorkspaceTabs : TabControl
{
    public WorkspaceTabs()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        Margin = System.Windows.Forms.Padding.Empty; Padding = Point.Empty;
    }
    public override Rectangle DisplayRectangle => new(0, 0, ClientSize.Width, ClientSize.Height);
    protected override void WndProc(ref Message message)
    {
        const int AdjustClientRectangle = 0x1328;
        if (message.Msg == AdjustClientRectangle && !DesignMode) { message.Result = new IntPtr(1); return; }
        base.WndProc(ref message);
    }
}

internal sealed class UiCard : Panel
{
    public UiCard() => DoubleBuffered = true;
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(16, 20, 29));
        if (Width < 2 || Height < 2) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
        using var fill = new SolidBrush(BackColor); using var edge = new Pen(Color.FromArgb(43, 56, 74));
        e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path);
    }
}

internal sealed class AccessToggle : CheckBox
{
    bool _hover;
    public AccessToggle() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(25, 31, 43));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 7);
        var fill = Checked ? Color.FromArgb(22, 61, 58) : Color.FromArgb(20, 26, 37);
        if (Enabled && _hover) fill = ControlPaint.Light(fill, .08f);
        using var brush = new SolidBrush(fill);
        using var border = new Pen(Checked ? Color.FromArgb(35, 133, 115) : Color.FromArgb(47, 60, 79));
        e.Graphics.FillPath(brush, path); e.Graphics.DrawPath(border, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, Rectangle.Inflate(ClientRectangle, -4, 0),
            Checked ? Color.FromArgb(104, 235, 211) : Color.FromArgb(151, 166, 187),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && Enabled && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class DarkButton : Button
{
    bool _hover, _pressed, _navigation, _selected;
    public DarkButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; UseVisualStyleBackColor = false;
        Font = new Font("Segoe UI", 9, FontStyle.Bold); Cursor = Cursors.Hand;
    }
    internal void SetNavigationState(bool selected)
    {
        _navigation = true; _selected = selected; Invalidate();
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width < 2 || Height < 2) return;
        e.Graphics.Clear(Parent?.BackColor ?? Color.FromArgb(16, 20, 29));
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = !Enabled ? Color.FromArgb(33, 43, 58) : _navigation
            ? (_selected ? Color.FromArgb(24, 66, 63) : _hover ? Color.FromArgb(33, 44, 60) : BackColor)
            : _pressed ? ControlPaint.Dark(BackColor, .1f) : _hover ? ControlPaint.Light(BackColor, .1f) : BackColor;
        using var path = UiGeometry.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8);
        using var brush = new SolidBrush(color); e.Graphics.FillPath(brush, path);
        if (!_navigation || _selected)
        {
            using var edge = new Pen(_navigation && _selected ? Color.FromArgb(33, 105, 93) : color);
            e.Graphics.DrawPath(edge, path);
        }
        var foreground = !Enabled ? Color.FromArgb(127, 144, 165) : _navigation
            ? (_selected ? Color.FromArgb(104, 235, 211) : Color.FromArgb(174, 188, 207)) : ForeColor;
        var bounds = new Rectangle(Padding.Left, Padding.Top, Math.Max(0, Width - Padding.Horizontal), Math.Max(0, Height - Padding.Vertical));
        var alignment = TextAlign is ContentAlignment.MiddleLeft or ContentAlignment.TopLeft or ContentAlignment.BottomLeft
            ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
        TextRenderer.DrawText(e.Graphics, Text, Font, bounds, foreground,
            alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        if (Focused && Enabled && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
    }
}

internal static class UiGeometry
{
    internal static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}
