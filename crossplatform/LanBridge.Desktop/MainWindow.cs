using System.Net;
using System.Net.Sockets;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using LanBridge.CrossPlatform;

namespace LanBridge.Desktop;

public sealed class MainWindow : Window
{
    readonly TextBlock _status = Label("Sẵn sàng", 15), _platform = Label("", 12), _role = Label("Chưa kết nối", 14);
    readonly ListBox _devices = new() { MinHeight = 130 };
    readonly TextBox _code = new() { PlaceholderText = "Dán mã LB1 từ máy nhận", MinWidth = 250 };
    readonly TextBox _chat = new() { PlaceholderText = "Tin nhắn trong phiên LAN…" };
    readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 130 };
    readonly ComboBox _edge = new() { ItemsSource = new[] { "B ở bên phải A", "B ở bên trái A", "B ở phía trên A", "B ở phía dưới A" }, SelectedIndex = 0 };
    readonly ComboBox _typing = new() { ItemsSource = new[] { "Tiếng Việt Telex", "Tiếng Việt VNI", "Phím gốc" }, SelectedIndex = 0 };
    readonly ComboBox _shortcut = new() { ItemsSource = new[] { "F9", "F10", "F11", "Ctrl + Alt + Space", "Tắt phím chuyển" }, SelectedIndex = 0 };
    readonly CheckBox _inputGrant = new() { Content = "Chuột / bàn phím", IsChecked = true };
    readonly CheckBox _clipboardGrant = new() { Content = "Chia sẻ clipboard chữ", IsChecked = false };
    readonly CheckBox _filesGrant = new() { Content = "Truyền file", IsChecked = true };
    readonly CheckBox _screenGrant = new() { Content = "Cho xem màn hình", IsChecked = false };
    readonly TextBlock _screenStatus = Label("Xem màn hình: đang tắt. Chỉ bật khi bạn muốn thiết bị điều khiển thấy màn hình này.", 12);
    readonly DispatcherTimer _clipboardTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly SemaphoreSlim _clipboardAccess = new(1, 1);
    readonly bool _existingInstance;
    readonly bool _preview;
    IInputBackend? _backend;
    PortableInputRouter? _input;
    HostServer? _server;
    LanDiscoveryService? _discovery;
    PortablePeerSession? _session;
    bool _closing, _connecting, _approvalPending;
    string _lastClipboard = "";
    readonly string _dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge", "Desktop");

    public MainWindow(bool existingInstance = false, bool preview = false)
    {
        _existingInstance = existingInstance;
        _preview = preview;
        Title = "LanBridge Desktop · Windows / macOS / Linux";
        Width = 1080; Height = 840; MinWidth = 850; MinHeight = 660;
        Background = new SolidColorBrush(Color.Parse("#10141d"));
        var root = new Grid { RowDefinitions = new("Auto,Auto,*,Auto"), Margin = new Thickness(24), RowSpacing = 14 };
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(Label("LanBridge", 32));
        title.Children.Add(Label("Một bộ chuột phím. Windows · macOS · Linux.", 16));
        title.Children.Add(_platform); root.Children.Add(title);
        var status = Card(new StackPanel { Spacing = 4, Children = { _status, _role } });
        Grid.SetRow(status, 1); root.Children.Add(status);

        var tabs = new TabControl(); Grid.SetRow(tabs, 2); root.Children.Add(tabs);
        var devicesPanel = new StackPanel { Spacing = 12 };
        devicesPanel.Children.Add(Label("Thiết bị trong mạng LAN", 20));
        devicesPanel.Children.Add(Label("Chọn máy muốn điều khiển; máy nhận phải bấm Đồng ý.", 13));
        devicesPanel.Children.Add(_devices);
        var deviceActions = Row(Button("Kết nối máy đã chọn", ConnectSelectedAsync), Button("Làm mới", RefreshAsync));
        devicesPanel.Children.Add(deviceActions);
        devicesPanel.Children.Add(Label("Kết nối bằng mã", 17)); devicesPanel.Children.Add(_code);
        devicesPanel.Children.Add(Row(Button("Kết nối bằng mã", ConnectCodeAsync), Button("Chép mã máy này", CopyPairingAsync)));
        devicesPanel.Children.Add(Label("Chỉ chia sẻ mã LB1 cho máy bạn muốn cấp quyền. Danh sách LAN luôn hỏi xác nhận.", 12));

        var sessionPanel = new StackPanel { Spacing = 12 };
        sessionPanel.Children.Add(Label("Chuyển chuột và bàn phím", 20));
        var choices = new Grid { ColumnDefinitions = new("*,*,*"), ColumnSpacing = 14 };
        choices.Children.Add(Field("Vị trí máy B", _edge));
        var typingField = Field("Bộ gõ trên A", _typing); Grid.SetColumn(typingField, 1); choices.Children.Add(typingField);
        var shortcutField = Field("Phím chuyển A ⇄ B", _shortcut); Grid.SetColumn(shortcutField, 2); choices.Children.Add(shortcutField);
        sessionPanel.Children.Add(choices);
        sessionPanel.Children.Add(Label("Bấm F9 để sang B, bấm lại để về A. Hoặc đưa chuột qua mép đã chọn. Ctrl + Alt + F12 luôn thoát khẩn cấp.", 13));
        sessionPanel.Children.Add(Label("Trên Mac, Command được chuyển thành phím Win/Super; Ctrl và Command giữ đúng phím vật lý.", 12));
        sessionPanel.Children.Add(Row(Button("Trả điều khiển về A", () => { _input?.ReleaseRemote(); return Task.CompletedTask; }),
            Button("Đặt lại bộ gõ", async () => { if (_input is { IsController: true }) await _input.ResetTypingAsync((TypingMode)_typing.SelectedIndex); }),
            Button("Ngắt kết nối", () => { _session?.Dispose(); return Task.CompletedTask; })));
        sessionPanel.Children.Add(Label("Quyền do máy nhận B cấp", 17));
        sessionPanel.Children.Add(Row(_inputGrant, _clipboardGrant, _filesGrant));
        sessionPanel.Children.Add(_screenGrant);
        sessionPanel.Children.Add(_screenStatus);
        sessionPanel.Children.Add(Label("Clipboard chữ cần được bật trên cả hai máy. Bản desktop preview có chuột/phím, Telex/VNI, chat, clipboard chữ và gửi file thủ công.", 12));
        sessionPanel.Children.Add(Button("Gửi file…", SendFileAsync));
        sessionPanel.Children.Add(Label("Nhật ký và tin nhắn", 17)); sessionPanel.Children.Add(_log);
        var chatRow = new Grid { ColumnDefinitions = new("*,Auto"), ColumnSpacing = 8 };
        chatRow.Children.Add(_chat); var send = Button("Gửi", SendChatAsync); Grid.SetColumn(send, 1); chatRow.Children.Add(send);
        sessionPanel.Children.Add(chatRow);
        tabs.ItemsSource = new[]
        {
            new TabItem { Header = "Thiết bị", Content = new ScrollViewer { Content = devicesPanel, Margin = new Thickness(0, 14) } },
            new TabItem { Header = "Phiên điều khiển", Content = new ScrollViewer { Content = sessionPanel, Margin = new Thickness(0, 14) } },
        };
        var footer = Label("0.8.0-preview.2 · TLS · X11 trên Linux; Wayland chưa hỗ trợ · Ctrl + Alt + F12: thoát khẩn cấp", 12);
        Grid.SetRow(footer, 3); root.Children.Add(footer); Content = root;
        _edge.SelectionChanged += (_, _) => { if (_input is { IsController: true }) _input.ExitEdge = (ScreenEdge)Math.Max(0, _edge.SelectedIndex); };
        _typing.SelectionChanged += (_, _) => { if (_input is { IsController: true }) _input.TypingMode = (TypingMode)Math.Max(0, _typing.SelectedIndex); };
        _shortcut.SelectionChanged += (_, _) => { if (_input != null) _input.SwitchShortcut = SelectedShortcut(); };
        foreach (var grant in new[] { _inputGrant, _clipboardGrant, _filesGrant, _screenGrant })
            grant.IsCheckedChanged += (_, _) => { _session?.SetLocalPermissions(LocalPermissions()); RefreshStatus(); };
        _clipboardTimer.Tick += async (_, _) => await SyncClipboardAsync();
        Opened += (_, _) => Initialize();
        Closing += (_, _) => Shutdown();
        _chat.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendChatAsync(); } };
        _devices.DoubleTapped += async (_, _) => await ConnectSelectedAsync();
        _sessionTabs = tabs;
    }

    readonly TabControl _sessionTabs;
    void Initialize()
    {
        if (_preview) { _platform.Text = "Xem giao diện · không mở hook, mạng hoặc clipboard"; return; }
        if (_existingInstance) { _status.Text = "LanBridge đang mở. Đóng bản đang chạy trước khi mở bản Desktop preview."; return; }
        try
        {
            _backend = BackendFactory.Create();
            _input = new PortableInputRouter(_backend);
            _input.Error += message => Ui(() => { Log(message); _session?.Dispose(); });
            _input.RemoteChanged += _ => Ui(RefreshStatus);
            _input.EmergencyReleased += () => Ui(() =>
            {
                if (_session?.IsHost == true)
                {
                    _inputGrant.IsChecked = false;
                    _screenGrant.IsChecked = false;
                    _session.RevokeInput();
                }
                RefreshStatus();
            });
            _platform.Text = _backend.Name + " · " + _backend.Capabilities.Description;
            _inputGrant.IsChecked = _backend.Capabilities.Inject;
            Directory.CreateDirectory(_dataDirectory);
            _server = new HostServer(IPAddress.Any);
            try { _server.Start(); }
            catch (SocketException error) when (error.SocketErrorCode == SocketError.AddressAlreadyInUse)
            { _server.Dispose(); _server = new HostServer(IPAddress.Any, 0); _server.Start(); }
            _server.ApprovalRequested += ApproveAsync;
            _server.Connected += peer => Ui(() => Attach(peer, true));
            var idPath = Path.Combine(_dataDirectory, "device-id.txt");
            string deviceId = File.Exists(idPath) && Guid.TryParse(File.ReadAllText(idPath), out var id) && id != Guid.Empty ? id.ToString("N") : Guid.NewGuid().ToString("N");
            File.WriteAllText(idPath, deviceId);
            _discovery = new LanDiscoveryService(deviceId, LocalName(), _server.Port, _server.Fingerprint,
                () => _session != null || _connecting || _approvalPending || _server.IsBusy);
            _discovery.DevicesChanged += devices => Ui(() =>
            {
                string? selection = (_devices.SelectedItem as DeviceItem)?.Device.Id;
                _devices.ItemsSource = devices.Select(device => new DeviceItem(device)).ToArray();
                _devices.SelectedItem = (_devices.ItemsSource as DeviceItem[])?.FirstOrDefault(item => item.Device.Id == selection);
            });
            _discovery.Error += message => Ui(() => Log(message));
            _discovery.Start(); _clipboardTimer.Start();
            _status.Text = "Đang tìm thiết bị · " + string.Join(" · ", LanAddresses.GetLocalIPv4());
            Log("Đã mở tìm thiết bị. Các yêu cầu kết nối trong danh sách cần được đồng ý tại máy nhận.");
        }
        catch (Exception error) { _status.Text = error.Message; Log(error.Message); }
    }

    async Task<bool> ApproveAsync(ConnectionRequest request, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(async () =>
        {
            if (_closing || _session != null || _connecting || _approvalPending || token.IsCancellationRequested) { completion.TrySetResult(false); return; }
            _approvalPending = true;
            var dialog = new Window { Title = "Yêu cầu kết nối LanBridge", Width = 480, Height = 255, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var panel = new StackPanel { Margin = new Thickness(22), Spacing = 15 };
            panel.Children.Add(Label(request.Name + " muốn điều khiển máy này", 18));
            panel.Children.Add(Label("Địa chỉ: " + request.Address + "\nChỉ đồng ý nếu đây là máy bạn muốn cho kết nối.", 13));
            panel.Children.Add(Row(Button("Từ chối", () => { dialog.Close(false); return Task.CompletedTask; }), Button("Đồng ý", () => { dialog.Close(true); return Task.CompletedTask; })));
            dialog.Content = panel;
            using var registration = token.Register(() => Ui(() => dialog.Close(false)));
            try { completion.TrySetResult(!token.IsCancellationRequested && await dialog.ShowDialog<bool>(this) && !_closing && !token.IsCancellationRequested); }
            catch { completion.TrySetResult(false); }
            finally { _approvalPending = false; }
        });
        return await completion.Task.WaitAsync(token);
    }

    async Task ConnectSelectedAsync()
    {
        if (_devices.SelectedItem is not DeviceItem item) { Log("Chọn một máy trong danh sách trước."); return; }
        if (item.Device.Busy) { Log("Máy đã chọn đang bận."); return; }
        await ConnectAsync(() => PeerConnection.ConnectDiscoveredAsync(item.Device, LocalName()));
    }
    Task ConnectCodeAsync() => ConnectAsync(() => PeerConnection.ConnectAsync(_code.Text ?? ""));
    async Task ConnectAsync(Func<Task<PeerConnection>> connect)
    {
        if (_session != null || _connecting || _approvalPending || _input == null) return;
        if (!_backend!.Capabilities.Capture) { Log(_backend.Capabilities.Description); return; }
        _connecting = true; _status.Text = "Đang chờ máy bên kia đồng ý…";
        try { var peer = await connect(); if (_closing) peer.Dispose(); else Attach(peer, false); }
        catch (Exception error) { Log(error.Message); _status.Text = "Chưa kết nối được"; }
        finally { _connecting = false; }
    }

    void Attach(PeerConnection peer, bool host)
    {
        if (_closing || _session != null || host && _connecting || _input == null || _backend == null) { peer.Dispose(); return; }
        try
        {
            _input.Start();
            _input.ExitEdge = (ScreenEdge)Math.Max(0, _edge.SelectedIndex);
            _input.TypingMode = (TypingMode)Math.Max(0, _typing.SelectedIndex);
            _input.SwitchShortcut = SelectedShortcut();
            var receive = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanBridge");
            var bounds = _backend.Bounds;
            DesktopBounds CaptureBounds()
            {
                if (_backend.Bounds != bounds)
                    throw new IOException("Bố trí màn hình đã thay đổi; kết nối lại để đồng bộ ảnh và tọa độ điều khiển.");
                return bounds;
            }
            var session = new PortablePeerSession(peer, _input, host, LocalPermissions(), receive,
                host ? DesktopScreenCapture.Create(CaptureBounds) : null);
            _session = session;
            session.Log += message => Ui(() => { if (_session == session) Log(message); });
            session.Changed += () => Ui(() => { if (_session == session) RefreshStatus(); });
            session.Closed += () => Ui(() =>
            {
                if (_session != session) return;
                _session = null; RefreshStatus(); Log("Đã ngắt phiên; chuột/phím đã được nhả.");
            });
            session.SetClipboardText = text => PublishClipboardAsync(session, text);
            session.Start(bounds, LocalName());
            _sessionTabs.SelectedIndex = 1; RefreshStatus();
            Log(host ? "Đã nhận kết nối; bạn có thể thu hồi quyền Chuột / bàn phím." : "Đã kết nối. F9 để chuyển; Ctrl + Alt + F12 để thoát khẩn cấp.");
        }
        catch (Exception error) { peer.Dispose(); _session?.Dispose(); _session = null; Log(error.Message); RefreshStatus(); }
    }

    void RefreshStatus()
    {
        _status.Text = _session?.Connected == true ? "Đã kết nối · TLS" : "Sẵn sàng · Thiết bị trong mạng LAN";
        _role.Text = _session == null ? "Chưa có phiên điều khiển" : _session.IsHost ? "Máy B · Nhận điều khiển"
            : !_session.InputAllowed ? "Máy A · B chưa cấp quyền chuột/phím"
            : _input?.IsRemote == true ? "ĐANG ĐIỀU KHIỂN MÁY B" : "Máy A · Dùng phím đã chọn để chuyển sang B";
        bool host = _session?.IsHost == true;
        _edge.IsEnabled = _typing.IsEnabled = _shortcut.IsEnabled = !host;
        _inputGrant.IsEnabled = _filesGrant.IsEnabled = _screenGrant.IsEnabled = _session == null || host;
        _screenStatus.Text = _session?.ScreenViewingActive == true
            ? "ĐANG CHIA SẺ MÀN HÌNH với thiết bị điều khiển · bỏ chọn Cho xem màn hình để dừng ngay."
            : _session?.ScreenViewingError is { } screenError ? screenError
            : _screenGrant.IsChecked == true
                ? "Xem màn hình: đã cấp quyền, đang chờ thiết bị điều khiển yêu cầu. macOS có thể hỏi thêm quyền Screen Recording."
                : "Xem màn hình: đang tắt. Thiết bị điều khiển vẫn có thể dùng chuột/phím nếu được cấp quyền.";
    }

    Permissions LocalPermissions() => new()
    {
        Input = _inputGrant.IsChecked == true && _backend?.Capabilities.Inject == true,
        Clipboard = _clipboardGrant.IsChecked == true, Files = _filesGrant.IsChecked == true,
        ScreenViewing = _screenGrant.IsChecked == true,
    };
    SwitchShortcut? SelectedShortcut() => _shortcut.SelectedIndex switch
    {
        0 => new(0x78), 1 => new(0x79), 2 => new(0x7A),
        3 => new(0x20, ShortcutModifiers.Control | ShortcutModifiers.Alt), _ => null,
    };
    async Task RefreshAsync() { if (_discovery != null) await _discovery.RefreshAsync(); }
    async Task CopyPairingAsync()
    {
        if (_server == null || Clipboard == null) return;
        await Clipboard.SetTextAsync(_server.PairingCode(LanAddresses.GetLocalIPv4()[0].ToString()));
        Log("Đã chép mã máy này. Mã là quyền kết nối trực tiếp, chỉ gửi cho máy bạn tin cậy.");
    }
    Task SendChatAsync()
    {
        if (_session?.Connected == true && !string.IsNullOrWhiteSpace(_chat.Text))
        { _session.SendChat(_chat.Text); Log("Bạn: " + _chat.Text); _chat.Text = ""; }
        return Task.CompletedTask;
    }
    async Task SendFileAsync()
    {
        var session = _session; if (session?.FilesAllowed != true) { Log("Chưa được cấp quyền truyền file."); return; }
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Gửi file qua LAN", AllowMultiple = false });
        if (files.Count != 0 && files[0].TryGetLocalPath() is { } path) await session.SendFileAsync(path);
    }
    async Task SyncClipboardAsync()
    {
        var session = _session;
        if (session?.ClipboardAllowed != true || Clipboard == null || !_clipboardAccess.Wait(0)) return;
        try
        {
            var text = await Clipboard.TryGetTextAsync();
            if (_session == session && session.ClipboardAllowed && text is { Length: <= 262144 } && text != _lastClipboard)
            { _lastClipboard = text; session.SendClipboard(text); }
        }
        catch { /* Clipboard contention is retried on the next tick. */ }
        finally { _clipboardAccess.Release(); }
    }
    Task PublishClipboardAsync(PortablePeerSession session, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Ui(async () =>
        {
            await _clipboardAccess.WaitAsync();
            try
            {
                if (!_closing && _session == session && session.ClipboardAllowed && Clipboard != null)
                { _lastClipboard = text; await Clipboard.SetTextAsync(text); }
                completion.TrySetResult();
            }
            catch (Exception error) { completion.TrySetException(error); }
            finally { _clipboardAccess.Release(); }
        });
        return completion.Task;
    }
    void Shutdown()
    {
        if (_closing) return; _closing = true; _clipboardTimer.Stop();
        _discovery?.Dispose(); _server?.Dispose(); _session?.Dispose(); _input?.Dispose();
    }
    void Ui(Action action) { if (!_closing) Dispatcher.UIThread.Post(action); }
    void Log(string message)
    {
        _log.Text += $"{DateTime.Now:HH:mm:ss}  {message}\n";
        if (_log.Text.Length > 30000) _log.Text = _log.Text[^20000..];
        _log.CaretIndex = _log.Text.Length;
    }
    static string LocalName() => (Environment.MachineName.Length <= 60 ? Environment.MachineName : Environment.MachineName[..60])
        + (OperatingSystem.IsMacOS() ? " · macOS" : OperatingSystem.IsLinux() ? " · Linux" : " · Windows");
    static TextBlock Label(string text, double size) => new() { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.White };
    static Border Card(Control content) => new() { Background = new SolidColorBrush(Color.Parse("#1a2230")), CornerRadius = new CornerRadius(12), Padding = new Thickness(16), Child = content };
    static StackPanel Row(params Control[] controls)
    { var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 }; foreach (var control in controls) panel.Children.Add(control); return panel; }
    static StackPanel Field(string label, Control control) => new() { Spacing = 6, Children = { Label(label, 13), control } };
    Button Button(string text, Func<Task> action)
    {
        var button = new Button { Content = text, Padding = new Thickness(14, 9), MinHeight = 36 };
        button.Click += async (_, _) => { try { await action(); } catch (Exception error) { Log(error.Message); } };
        return button;
    }
    sealed record DeviceItem(DiscoveredDevice Device)
    { public override string ToString() => $"{Device.Name}   ·   {Device.Address}   ·   {(Device.Busy ? "Đang bận" : "Sẵn sàng")}"; }
}
