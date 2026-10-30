using System.Drawing.Drawing2D;

namespace LanBridge;

/// <summary>The LAN device picker. Discovery and connection ownership stay in MainForm.</summary>
public sealed class DevicesPage : UserControl
{
    static readonly Color Background = Color.FromArgb(16, 20, 29);
    static readonly Color Card = Color.FromArgb(25, 33, 46);
    static readonly Color Accent = Color.FromArgb(47, 208, 177);
    static readonly Color Muted = Color.FromArgb(151, 166, 187);
    static readonly Color Border = Color.FromArgb(43, 56, 74);
    readonly Label _localName = new(), _localAddress = new(), _localState = new(), _localDetail = new();
    readonly Label _status = new(), _sessionLabel = new(), _count = new();
    readonly Surface _sessionBar = new();
    readonly DeviceList _devices = new();
    readonly Surface _empty = new();
    readonly DeviceButton _refresh = new() { Text = "Làm mới" };
    readonly Dictionary<string, DeviceTile> _tiles = new(StringComparer.Ordinal);
    bool _connecting, _connected, _resizingTiles;
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
        BackColor = Background; ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        Padding = new Padding(22); DoubleBuffered = true;

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, BackColor = Background, Margin = Padding.Empty,
            ColumnCount = 1, RowCount = 8
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 151));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        Controls.Add(layout);

        var header = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty
        };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
        var title = CreateLabel("Thiết bị trong mạng", 22, Color.White, true);
        title.AutoEllipsis = true; title.Dock = DockStyle.Fill; title.Margin = Padding.Empty;
        header.Controls.Add(title, 0, 0);
        _refresh.Dock = DockStyle.Fill; _refresh.Margin = new Padding(0, 2, 0, 5);
        _refresh.BackColor = Card; _refresh.ForeColor = Color.White;
        _refresh.Click += (_, _) => RefreshRequested?.Invoke();
        header.Controls.Add(_refresh, 1, 0); layout.Controls.Add(header, 0, 0);

        var subtitle = CreateLabel("Một mạng LAN. Một bộ chuột phím. Kết nối liền mạch.", 10, Muted);
        subtitle.AutoEllipsis = true; subtitle.Dock = DockStyle.Fill;
        subtitle.Margin = new Padding(0, 3, 0, 15);
        layout.Controls.Add(subtitle, 0, 1);

        var local = new Surface
        {
            Dock = DockStyle.Fill, Padding = new Padding(18, 14, 18, 12), BackColor = Card,
            Margin = new Padding(0, 0, 0, 14)
        };
        var localContent = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 4, Margin = Padding.Empty,
            BackColor = Card
        };
        localContent.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
        localContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        localContent.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 170));
        foreach (var height in new[] { 22, 31, 23, 24 }) localContent.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        local.Controls.Add(localContent);
        var localIcon = new ComputerIcon { Dock = DockStyle.Fill, Margin = new Padding(0, 7, 14, 8), BackColor = Card };
        localContent.Controls.Add(localIcon, 0, 0); localContent.SetRowSpan(localIcon, 3);
        var localCaption = CreateLabel("MÁY CỦA BẠN", 8.5f, Muted, true);
        localCaption.Dock = DockStyle.Fill; localCaption.Margin = Padding.Empty;
        localContent.Controls.Add(localCaption, 1, 0);
        ConfigureLabel(_localState, 8.5f, Accent);
        _localState.Dock = DockStyle.Fill; _localState.AutoEllipsis = true;
        _localState.TextAlign = ContentAlignment.TopRight; _localState.Margin = Padding.Empty;
        localContent.Controls.Add(_localState, 2, 0);
        ConfigureLabel(_localName, 14, Color.White, true);
        _localName.Dock = DockStyle.Fill; _localName.AutoEllipsis = true; _localName.Margin = Padding.Empty;
        localContent.Controls.Add(_localName, 1, 1); localContent.SetColumnSpan(_localName, 2);
        ConfigureLabel(_localAddress, 9.5f, Muted);
        _localAddress.Dock = DockStyle.Fill; _localAddress.AutoEllipsis = true; _localAddress.Margin = Padding.Empty;
        localContent.Controls.Add(_localAddress, 1, 2); localContent.SetColumnSpan(_localAddress, 2);
        ConfigureLabel(_localDetail, 9, Muted);
        _localDetail.Dock = DockStyle.Fill; _localDetail.AutoEllipsis = true;
        _localDetail.Margin = new Padding(0, 4, 0, 0);
        localContent.Controls.Add(_localDetail, 0, 3); localContent.SetColumnSpan(_localDetail, 3);
        layout.Controls.Add(local, 0, 2);

        _sessionBar.Dock = DockStyle.Top; _sessionBar.Height = 47;
        _sessionBar.BackColor = Color.FromArgb(20, 57, 53);
        _sessionBar.BorderColor = Color.FromArgb(34, 101, 89);
        _sessionBar.Padding = new Padding(15, 0, 15, 0);
        _sessionBar.Margin = new Padding(0, 0, 0, 12); _sessionBar.Visible = false;
        ConfigureLabel(_sessionLabel, 10, Accent, true);
        _sessionLabel.AutoEllipsis = true; _sessionLabel.Dock = DockStyle.Fill;
        _sessionLabel.TextAlign = ContentAlignment.MiddleLeft;
        _sessionBar.Controls.Add(_sessionLabel); layout.Controls.Add(_sessionBar, 0, 3);

        var listHeading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 6, 0, 0)
        };
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 98));
        var peersCaption = CreateLabel("THIẾT BỊ KHÁC", 9, Color.White, true);
        peersCaption.Dock = DockStyle.Fill; peersCaption.Margin = Padding.Empty;
        listHeading.Controls.Add(peersCaption, 0, 0);
        ConfigureLabel(_count, 9, Muted); _count.Dock = DockStyle.Fill;
        _count.TextAlign = ContentAlignment.TopRight; _count.Margin = Padding.Empty;
        listHeading.Controls.Add(_count, 1, 0); layout.Controls.Add(listHeading, 0, 4);
        ConfigureLabel(_status, 9, Muted);
        _status.AutoEllipsis = true; _status.Dock = DockStyle.Fill; _status.Margin = new Padding(0, 0, 0, 7);
        layout.Controls.Add(_status, 0, 5);

        _devices.Dock = DockStyle.Fill; _devices.AutoScroll = true;
        _devices.FlowDirection = FlowDirection.TopDown; _devices.WrapContents = false;
        _devices.Padding = Padding.Empty; _devices.Margin = Padding.Empty;
        // A layout event may itself be caused by a card's width changing. Only react to
        // a real viewport resize and batch the updates, including while minimized.
        _devices.SizeChanged += (_, _) => ResizeTiles();
        layout.Controls.Add(_devices, 0, 6);
        _empty.Height = 185; _empty.Margin = Padding.Empty; _empty.BackColor = Card;
        _empty.Padding = new Padding(20, 21, 20, 18);
        var emptyIcon = new ComputerIcon
        {
            Dock = DockStyle.Left, Width = 63, Margin = Padding.Empty, BackColor = Card,
            IconColor = Muted
        };
        var emptyCopy = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
            Padding = new Padding(18, 0, 0, 0), Margin = Padding.Empty
        };
        emptyCopy.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        emptyCopy.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        emptyCopy.RowStyles.Add(new RowStyle(SizeType.Absolute, 29));
        var emptyTitle = CreateLabel("Sẵn sàng kết nối máy thứ hai", 12, Color.White, true);
        emptyTitle.Dock = DockStyle.Fill; emptyTitle.AutoEllipsis = true; emptyTitle.Margin = Padding.Empty;
        var emptyDetail = CreateLabel("Mở LanBridge trên máy còn lại và dùng cùng mạng LAN.\nTên máy sẽ tự xuất hiện tại đây để bạn chọn kết nối.", 9.5f, Muted);
        emptyDetail.Dock = DockStyle.Fill; emptyDetail.Margin = Padding.Empty;
        var emptyTip = CreateLabel("Chưa thấy máy? Bấm Làm mới và kiểm tra tường lửa mạng Riêng tư.", 8.5f, Muted);
        emptyTip.Dock = DockStyle.Fill; emptyTip.AutoEllipsis = true; emptyTip.Margin = Padding.Empty;
        emptyCopy.Controls.Add(emptyTitle, 0, 0); emptyCopy.Controls.Add(emptyDetail, 0, 1);
        emptyCopy.Controls.Add(emptyTip, 0, 2);
        _empty.Controls.Add(emptyCopy); _empty.Controls.Add(emptyIcon); _devices.Controls.Add(_empty);

        var footnote = CreateLabel("Mã hóa TLS  ·  Máy nhận xác nhận trước khi ghép nối", 8.5f, Muted);
        footnote.AutoEllipsis = true; footnote.Dock = DockStyle.Fill;
        footnote.Margin = new Padding(0, 11, 0, 0);
        layout.Controls.Add(footnote, 0, 7);

        SetLocalState(Environment.MachineName, "Đang xác định địa chỉ mạng…", false);
        SetStatus("Đang tìm thiết bị trong mạng…");
        _count.Text = "0 thiết bị";
    }

    public void SetLocalState(string name, string addresses, bool receiving, string? detail = null)
    {
        _localName.Text = name;
        _localAddress.Text = addresses;
        _localState.Text = receiving ? "●  Sẵn sàng nhận kết nối" : "●  Đang chuẩn bị";
        _localState.ForeColor = receiving ? Accent : Muted;
        _localDetail.Text = detail ?? (receiving
            ? "Máy khác có thể tìm thấy bạn. Mỗi lần kết nối đều cần bạn xác nhận."
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
    }

    public void SetDevices(IReadOnlyList<DiscoveredDevice> devices)
    {
        // Discovery heartbeats must preserve existing cards, focus and scroll position.
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
        if (_resizingTiles || _devices.ClientSize.Width <= 1) return;
        _resizingTiles = true;
        _devices.SuspendLayout();
        try
        {
            // Keep a scrollbar gutter even with a short list to prevent width oscillation.
            var width = Math.Max(180, _devices.ClientSize.Width - SystemInformation.VerticalScrollBarWidth - 2);
            foreach (Control control in _devices.Controls)
                if (control.Width != width) control.Width = width;
        }
        finally { _devices.ResumeLayout(true); _resizingTiles = false; }
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

    sealed class DeviceList : FlowLayoutPanel
    {
        public DeviceList() => DoubleBuffered = true;
    }

    class Surface : Panel
    {
        public Color BorderColor = Border;
        public Surface() => DoubleBuffered = true;
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(Parent?.BackColor ?? Background);
            if (Width < 2 || Height < 2) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var path = RoundedPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 12);
            using var fill = new SolidBrush(BackColor);
            using var edge = new Pen(BorderColor);
            e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(edge, path);
        }
    }

    sealed class ComputerIcon : Control
    {
        public Color IconColor = Accent;
        public ComputerIcon() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 8 || Height < 8) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var side = Math.Min(Width, Math.Min(Height, 58));
            var x = (Width - side) / 2f; var y = (Height - side) / 2f;
            using var backdrop = new SolidBrush(Color.FromArgb(24, 62, 63));
            using var tile = RoundedPath(new RectangleF(x, y, side, side), 11);
            e.Graphics.FillPath(backdrop, tile);
            using var line = new Pen(IconColor, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            var screen = new RectangleF(x + side * .22f, y + side * .25f, side * .56f, side * .38f);
            using var monitor = RoundedPath(screen, 3);
            e.Graphics.DrawPath(line, monitor);
            e.Graphics.DrawLine(line, x + side / 2, y + side * .64f, x + side / 2, y + side * .76f);
            e.Graphics.DrawLine(line, x + side * .36f, y + side * .77f, x + side * .64f, y + side * .77f);
        }
    }

    sealed class DeviceTile : Surface
    {
        readonly Label _name = new(), _address = new(), _availability = new();
        readonly DeviceButton _connect = new() { Text = "Kết nối" };
        DiscoveredDevice? _device;
        public event Action<DiscoveredDevice>? ConnectRequested;

        public DeviceTile()
        {
            Name = "DeviceCard";
            Height = 111; Margin = new Padding(0, 0, 0, 11);
            BackColor = Card; Padding = new Padding(16, 14, 16, 14);
            var content = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = Card
            };
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 65));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            content.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 124));
            Controls.Add(content);
            var icon = new ComputerIcon { Dock = DockStyle.Fill, Margin = new Padding(0, 8, 14, 8), BackColor = Card };
            content.Controls.Add(icon, 0, 0);
            var labels = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = new Padding(0, 1, 8, 0), BackColor = Card
            };
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            labels.RowStyles.Add(new RowStyle(SizeType.Absolute, 23));
            ConfigureLabel(_name, 12, Color.White, true);
            ConfigureLabel(_address, 9, Muted); ConfigureLabel(_availability, 9, Accent);
            foreach (var label in new[] { _name, _address, _availability })
            {
                label.AutoEllipsis = true; label.Dock = DockStyle.Fill; label.Margin = Padding.Empty;
                label.TextAlign = ContentAlignment.MiddleLeft;
            }
            labels.Controls.Add(_name, 0, 0); labels.Controls.Add(_address, 0, 1); labels.Controls.Add(_availability, 0, 2);
            content.Controls.Add(labels, 1, 0);
            _connect.Dock = DockStyle.Top; _connect.Height = 39;
            _connect.Margin = new Padding(3, 20, 0, 0); _connect.BackColor = Accent; _connect.ForeColor = Background;
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
        bool _hover, _pressed;
        public DeviceButton()
        {
            FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
            Font = new Font("Segoe UI", 9, FontStyle.Bold);
            Cursor = Cursors.Hand; UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        }
        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = e.Button == MouseButtons.Left; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            if (Width < 2 || Height < 2) return;
            e.Graphics.Clear(Parent?.BackColor ?? Background);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var color = Enabled ? (_pressed ? ControlPaint.Dark(BackColor, .08f) : _hover ? ControlPaint.Light(BackColor, .12f) : BackColor) : Color.FromArgb(38, 48, 64);
            using var path = RoundedPath(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8);
            using var brush = new SolidBrush(color); e.Graphics.FillPath(brush, path);
            using var border = new Pen(Enabled && Focused ? Accent : BackColor == Accent && Enabled ? color : Border);
            e.Graphics.DrawPath(border, path);
            TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }

    static GraphicsPath RoundedPath(RectangleF bounds, float radius)
    {
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
