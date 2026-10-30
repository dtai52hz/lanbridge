using System.Drawing.Drawing2D;

namespace LanBridge;

/// <summary>The LAN device picker. Discovery and connection ownership stay in MainForm.</summary>
public sealed class DevicesPage : UserControl
{
    static readonly Color Background = Color.FromArgb(16, 20, 29);
    static readonly Color Card = Color.FromArgb(25, 31, 43);
    static readonly Color Accent = Color.FromArgb(47, 208, 177);
    static readonly Color Muted = Color.FromArgb(151, 166, 187);
    static readonly Color Border = Color.FromArgb(47, 59, 78);
    readonly Label _localName = new(), _localAddress = new(), _localState = new(), _localDetail = new();
    readonly Label _status = new(), _sessionLabel = new(), _count = new();
    readonly Panel _sessionBar = new();
    readonly FlowLayoutPanel _devices = new();
    readonly Panel _empty = new();
    readonly DeviceButton _refresh = new() { Text = "Làm mới" };
    readonly Dictionary<string, DeviceTile> _tiles = new(StringComparer.Ordinal);
    bool _connecting, _connected;
    string _signature = "";

    public event Action<DiscoveredDevice>? ConnectRequested;
    public event Action? RefreshRequested;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Connecting
    {
        get => _connecting;
        set
        {
            if (_connecting == value) return;
            _connecting = value;
            _refresh.Enabled = !value;
            UpdateButtons();
        }
    }

    public DevicesPage()
    {
        Name = "DevicesPage";
        BackColor = Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        Padding = new Padding(26);
        DoubleBuffered = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty,
            ColumnCount = 1, RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (var row = 0; row < 6; row++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0, 0, 0, 3)
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        var title = CreateLabel("Thiết bị trong mạng", 23, Color.White, true);
        title.AutoSize = true; title.Dock = DockStyle.Fill; title.Margin = Padding.Empty;
        header.Controls.Add(title, 0, 0);
        _refresh.Size = new Size(106, 36); _refresh.Dock = DockStyle.Top;
        _refresh.Margin = new Padding(0, 4, 0, 0); _refresh.BackColor = Card; _refresh.ForeColor = Color.White;
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        header.Controls.Add(_refresh, 1, 0); layout.Controls.Add(header, 0, 0);

        var subtitle = CreateLabel("Mở LanBridge trên hai máy để tìm nhau.", 10, Muted);
        subtitle.AutoSize = true; subtitle.Margin = new Padding(0, 0, 0, 23);
        layout.Controls.Add(subtitle, 0, 1);

        var local = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 4,
            Padding = new Padding(18, 14, 18, 13), BackColor = Card,
            Margin = new Padding(0, 0, 0, 12)
        };
        local.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        local.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var localCaption = CreateLabel("MÁY NÀY", 9, Accent, true);
        localCaption.AutoSize = true; localCaption.Margin = new Padding(0, 0, 0, 4);
        local.Controls.Add(localCaption, 0, 0);
        ConfigureLabel(_localName, 14, Color.White, true);
        _localName.AutoSize = true; _localName.Margin = new Padding(0, 0, 10, 3);
        local.Controls.Add(_localName, 0, 1);
        ConfigureLabel(_localState, 9, Accent);
        _localState.AutoSize = true; _localState.Margin = new Padding(0, 5, 0, 3);
        local.Controls.Add(_localState, 1, 1);
        ConfigureLabel(_localAddress, 10, Muted);
        _localAddress.AutoSize = true; _localAddress.Margin = new Padding(0, 0, 0, 3);
        local.Controls.Add(_localAddress, 0, 2); local.SetColumnSpan(_localAddress, 2);
        ConfigureLabel(_localDetail, 9, Muted);
        _localDetail.AutoSize = true; _localDetail.Margin = Padding.Empty;
        local.Controls.Add(_localDetail, 0, 3); local.SetColumnSpan(_localDetail, 2);
        local.SizeChanged += (_, _) =>
        {
            var width = Math.Max(100, local.ClientSize.Width - local.Padding.Horizontal);
            _localAddress.MaximumSize = _localDetail.MaximumSize = new Size(width, 0);
            _localName.MaximumSize = new Size(Math.Max(100, width - _localState.Width - 15), 0);
        };
        layout.Controls.Add(local, 0, 2);

        _sessionBar.Dock = DockStyle.Top; _sessionBar.Height = 48;
        _sessionBar.BackColor = Color.FromArgb(22, 61, 58);
        _sessionBar.Padding = new Padding(14, 0, 14, 0);
        _sessionBar.Margin = new Padding(0, 0, 0, 12); _sessionBar.Visible = false;
        ConfigureLabel(_sessionLabel, 10, Accent, true);
        _sessionLabel.AutoEllipsis = true; _sessionLabel.Dock = DockStyle.Fill;
        _sessionLabel.TextAlign = ContentAlignment.MiddleLeft; _sessionBar.Controls.Add(_sessionLabel);
        layout.Controls.Add(_sessionBar, 0, 3);

        var listHeading = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 1,
            Margin = new Padding(0, 10, 0, 8)
        };
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var peersCaption = CreateLabel("THIẾT BỊ KHÁC", 9, Accent, true);
        peersCaption.AutoSize = true; peersCaption.Margin = Padding.Empty;
        listHeading.Controls.Add(peersCaption, 0, 0);
        ConfigureLabel(_count, 9, Muted); _count.AutoSize = true; _count.Margin = Padding.Empty;
        listHeading.Controls.Add(_count, 1, 0); layout.Controls.Add(listHeading, 0, 4);
        ConfigureLabel(_status, 9, Muted);
        _status.AutoSize = true; _status.Dock = DockStyle.Fill; _status.Margin = new Padding(0, 0, 0, 10);
        layout.Controls.Add(_status, 0, 5);

        _devices.Dock = DockStyle.Fill; _devices.AutoScroll = true;
        _devices.FlowDirection = FlowDirection.TopDown; _devices.WrapContents = false;
        _devices.Padding = Padding.Empty; _devices.Margin = Padding.Empty;
        _devices.SizeChanged += (_, _) => ResizeTiles();
        _devices.Layout += (_, _) => ResizeTiles();
        layout.Controls.Add(_devices, 0, 6);
        _empty.Height = 138; _empty.Margin = Padding.Empty; _empty.BackColor = Card;
        var emptyTitle = CreateLabel("Chưa thấy thiết bị nào", 13, Color.White, true);
        emptyTitle.Dock = DockStyle.Top; emptyTitle.Height = 42;
        emptyTitle.Padding = new Padding(17, 15, 17, 0);
        var emptyDetail = CreateLabel("Mở LanBridge trên máy còn lại và cùng kết nối một mạng LAN.\nNếu chưa thấy máy, bấm Làm mới và cho phép LanBridge qua tường lửa mạng Riêng tư.", 10, Muted);
        emptyDetail.Dock = DockStyle.Fill; emptyDetail.Padding = new Padding(17, 7, 17, 10);
        _empty.Controls.Add(emptyDetail); _empty.Controls.Add(emptyTitle);
        _devices.Controls.Add(_empty);

        var footnote = CreateLabel("Chỉ hiện các máy đang mở LanBridge trong cùng mạng LAN.", 9, Muted);
        footnote.AutoSize = true; footnote.Margin = new Padding(0, 14, 0, 0);
        layout.Controls.Add(footnote, 0, 7);
        layout.SizeChanged += (_, _) =>
        {
            var available = new Size(Math.Max(100, layout.ClientSize.Width), 0);
            footnote.MaximumSize = available;
            _status.MaximumSize = available;
        };

        SetLocalState(Environment.MachineName, "Đang xác định địa chỉ mạng…", false);
        SetStatus("Đang tìm thiết bị trong mạng…");
        _count.Text = "0 thiết bị";
    }

    public void SetLocalState(string name, string addresses, bool receiving, string? detail = null)
    {
        _localName.Text = name;
        _localAddress.Text = addresses;
        _localState.Text = receiving ? "●  Đang nhận kết nối" : "●  Chưa nhận kết nối";
        _localState.ForeColor = receiving ? Accent : Muted;
        _localDetail.Text = detail ?? (receiving
            ? "Máy khác sẽ thấy máy này trong danh sách. Bạn xác nhận trước khi nhận điều khiển."
            : "Đang chuẩn bị nhận kết nối từ máy khác.");
    }

    public void SetSessionState(bool connected, string? peerName = null)
    {
        _connected = connected;
        _sessionLabel.Text = "●  Đã kết nối với " + (string.IsNullOrWhiteSpace(peerName) ? "thiết bị khác" : peerName);
        _sessionBar.Visible = connected;
        UpdateButtons();
    }

    public void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? Color.FromArgb(244, 139, 151) : Muted;
        _status.MaximumSize = new Size(Math.Max(100, Width - Padding.Horizontal), 0);
    }

    public void SetDevices(IReadOnlyList<DiscoveredDevice> devices)
    {
        // A heartbeat only changes LastSeen. Avoid recreating cards or moving focus/scroll
        // on these updates. Existing cards also survive changes to availability or names.
        var peers = devices.GroupBy(DeviceKey, StringComparer.Ordinal).Select(group => group.Last())
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(device => device.Address.ToString(), StringComparer.Ordinal).ToArray();
        var signature = string.Join("\n", peers.Select(device =>
            $"{DeviceKey(device)}\t{device.Name}\t{device.Address}\t{device.Port}\t{device.Fingerprint}\t{device.Busy}"));
        if (_signature == signature && peers.Length == _tiles.Count) return;
        _signature = signature;
        var scroll = _devices.AutoScrollPosition;
        var ids = peers.Select(DeviceKey).ToHashSet(StringComparer.Ordinal);
        _devices.SuspendLayout();
        try
        {
            foreach (var id in _tiles.Keys.Where(id => !ids.Contains(id)).ToArray())
            {
                var tile = _tiles[id]; _tiles.Remove(id);
                _devices.Controls.Remove(tile); tile.Dispose();
            }
            _empty.Visible = peers.Length == 0;
            for (var index = 0; index < peers.Length; index++)
            {
                var peer = peers[index]; var key = DeviceKey(peer);
                if (!_tiles.TryGetValue(key, out var tile))
                {
                    tile = new DeviceTile();
                    tile.ConnectRequested += device =>
                    {
                        if (!_connecting && !_connected && !device.Busy) ConnectRequested?.Invoke(device);
                    };
                    _tiles.Add(key, tile); _devices.Controls.Add(tile);
                }
                tile.UpdateDevice(peer); tile.SetConnectionState(_connecting, _connected);
                _devices.Controls.SetChildIndex(tile, index);
            }
            _count.Text = $"{peers.Length} thiết bị";
        }
        finally
        {
            _devices.ResumeLayout(true);
            ResizeTiles();
            _devices.AutoScrollPosition = new Point(-scroll.X, -scroll.Y);
        }
    }

    static string DeviceKey(DiscoveredDevice device) => device.Id;

    void UpdateButtons()
    {
        foreach (var tile in _tiles.Values) tile.SetConnectionState(_connecting, _connected);
    }

    void ResizeTiles()
    {
        // Reserving one scrollbar width keeps the layout stable when the list grows.
        var width = Math.Max(180, _devices.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
        foreach (Control control in _devices.Controls)
            if (control.Width != width) control.Width = width;
    }

    static Label CreateLabel(string text, float size, Color color, bool bold = false)
    {
        var label = new Label { Text = text };
        ConfigureLabel(label, size, color, bold);
        return label;
    }

    static void ConfigureLabel(Label label, float size, Color color, bool bold = false)
    {
        label.ForeColor = color;
        label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
        label.UseMnemonic = false;
    }

    sealed class DeviceTile : Panel
    {
        readonly Label _name = new(), _address = new(), _availability = new(), _initial = new();
        readonly DeviceButton _connect = new() { Text = "Kết nối" };
        DiscoveredDevice? _device;
        public event Action<DiscoveredDevice>? ConnectRequested;

        public DeviceTile()
        {
            Name = "DeviceCard";
            Height = 100; Margin = new Padding(0, 0, 0, 10);
            BackColor = Card; Padding = new Padding(14);
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 63));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            Controls.Add(content);
            ConfigureLabel(_initial, 22, Accent, true);
            _initial.BackColor = Color.FromArgb(25, 65, 64); _initial.Size = new Size(49, 49);
            _initial.Margin = new Padding(0, 10, 13, 0); _initial.TextAlign = ContentAlignment.MiddleCenter;
            content.Controls.Add(_initial, 0, 0);
            var labels = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 1, 8, 0)
            };
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 22));
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
            ConfigureLabel(_name, 12, Color.White, true);
            ConfigureLabel(_address, 9, Muted); ConfigureLabel(_availability, 9, Accent);
            foreach (var label in new[] { _name, _address, _availability })
            {
                label.AutoEllipsis = true; label.Dock = DockStyle.Fill; label.Margin = Padding.Empty;
                label.TextAlign = ContentAlignment.MiddleLeft;
            }
            labels.Controls.Add(_name, 0, 0); labels.Controls.Add(_address, 0, 1); labels.Controls.Add(_availability, 0, 2);
            content.Controls.Add(labels, 1, 0);
            _connect.Dock = DockStyle.Top; _connect.Height = 38;
            _connect.Margin = new Padding(3, 17, 0, 0); _connect.BackColor = Accent; _connect.ForeColor = Background;
            _connect.Click += (_, _) => { if (_device != null) ConnectRequested?.Invoke(_device); };
            content.Controls.Add(_connect, 2, 0);
        }

        public void UpdateDevice(DiscoveredDevice device)
        {
            _device = device;
            _name.Text = device.Name;
            _address.Text = device.Address.ToString();
            _availability.Text = device.Busy ? "●  Đang có phiên kết nối" : "●  Sẵn sàng kết nối";
            _availability.ForeColor = device.Busy ? Muted : Accent;
            _initial.Text = string.IsNullOrWhiteSpace(device.Name) ? "PC" : device.Name[..1].ToUpperInvariant();
        }

        public void SetConnectionState(bool connecting, bool connected)
        {
            var busy = _device?.Busy ?? true;
            _connect.Enabled = !connecting && !connected && !busy;
            _connect.Text = busy ? "Đang bận" : connecting ? "Đang kết nối…" : connected ? "Đã có phiên" : "Kết nối";
        }
    }

    sealed class DeviceButton : Button
    {
        bool _hover;
        public DeviceButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 9, FontStyle.Bold);
            Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Enabled ? (_hover ? ControlPaint.Light(BackColor, .12f) : BackColor) : Color.FromArgb(43, 53, 70);
            using var brush = new SolidBrush(color); e.Graphics.FillRectangle(brush, ClientRectangle);
            using var border = new Pen(Enabled && Focused ? Accent : Border);
            e.Graphics.DrawRectangle(border, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
        }
    }
}
