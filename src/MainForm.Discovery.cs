using System.Net;
using System.Net.Sockets;

namespace LanBridge;

public sealed partial class MainForm
{
    readonly DevicesPage _devicesPage = new();
    readonly TabControl _tabs = new();
    readonly TabPage _deviceTab = new("Thiết bị"), _sessionTab = new("Phiên điều khiển"), _manualTab = new("Kết nối bằng mã");
    HostServer? _autoServer;
    LanDiscoveryService? _discovery;
    bool _servicesStarted, _approvalPending;
    ConnectionApprovalDialog? _approvalDialog;

    static string LoadDeviceId()
    {
        var id = Guid.NewGuid().ToString("N");
        try
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, "device-id.txt");
            if (File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var existing) && existing != Guid.Empty) return existing.ToString("N");
            File.WriteAllText(path, id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* An in-memory identity still permits discovery. */ }
        return id;
    }

    void StartNetworkServices()
    {
        if (_preview || _closing || _servicesStarted) return;
        try
        {
            var server = new HostServer(IPAddress.Any, HostServer.DefaultPort);
            try { server.Start(); }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            { server.Dispose(); server = new HostServer(IPAddress.Any, 0); server.Start(); }
            _autoServer = server;
            server.ApprovalRequested += RequestConnectionApproval;
            server.Connected += peer => Ui(() =>
            {
                if (_closing || _peer != null || _connecting) { peer.Dispose(); return; }
                _role.SelectedIndex = 1;
                AttachPeer(peer);
            });
            _discovery = new LanDiscoveryService(LoadDeviceId(), Environment.MachineName, server.Port, server.Fingerprint,
                () => _peer != null || _connecting || _approvalPending || server.IsBusy);
            _discovery.DevicesChanged += devices => Ui(() => _devicesPage.SetDevices(devices));
            _discovery.Error += message => Ui(() => { _devicesPage.SetStatus(message, true); Log(message); });
            _discovery.Start(); _servicesStarted = true;
            _devicesPage.SetLocalState(Environment.MachineName, string.Join(" · ", LanAddresses.GetLocalIPv4()), true);
            _devicesPage.SetStatus("Đang tìm máy đang mở LanBridge trong mạng LAN…");
            Log($"Đã bật tìm máy trong LAN. TCP {server.Port}, UDP 45832. Máy nhận sẽ xác nhận yêu cầu kết nối.");
        }
        catch (Exception ex)
        {
            StopNetworkServices();
            _devicesPage.SetLocalState(Environment.MachineName, string.Join(" · ", LanAddresses.GetLocalIPv4()), false);
            _devicesPage.SetStatus("Chưa bật được tìm máy: " + ex.Message + " Bạn vẫn có thể dùng Kết nối bằng mã.", true);
            Log("Tìm thiết bị: " + ex.Message);
        }
    }

    async Task RefreshDevicesAsync(bool showStatus = true)
    {
        if (_preview || _closing) return;
        if (_discovery == null) StartNetworkServices();
        try { if (_discovery != null) { if (showStatus) _devicesPage.SetStatus("Đang làm mới danh sách thiết bị…"); await _discovery.RefreshAsync(); } }
        catch (Exception ex) { _devicesPage.SetStatus("Không làm mới được: " + ex.Message, true); }
    }

    async Task ConnectDeviceAsync(DiscoveredDevice device)
    {
        if (_preview || _closing || _peer != null || _connecting || _approvalPending) return;
        if (device.Busy) { _devicesPage.SetStatus("Máy này đang bận. Hãy thử lại sau."); return; }
        _connecting = true; _devicesPage.Connecting = true;
        _role.SelectedIndex = 0; _role.Enabled = _network.Enabled = _connect.Enabled = false;
        _devicesPage.SetStatus($"Đang chờ {device.Name} ({device.Address}) đồng ý kết nối…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            var peer = await PeerConnection.ConnectDiscoveredAsync(device, Environment.MachineName, timeout.Token);
            if (_closing || _peer != null) { peer.Dispose(); return; }
            AttachPeer(peer);
        }
        catch (Exception ex)
        {
            if (!_closing) { _devicesPage.SetStatus("Chưa kết nối được: " + ex.Message, true); Log("Kết nối thiết bị: " + ex.Message); }
        }
        finally
        {
            _connecting = false; _devicesPage.Connecting = _approvalPending;
            if (_peer == null && !_closing) _role.Enabled = _network.Enabled = _connect.Enabled = true;
            await RefreshDevicesAsync(false);
        }
    }

    Task<bool> RequestConnectionApproval(ConnectionRequest request, CancellationToken cancellationToken)
    {
        if (_closing || IsDisposed) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                if (_closing || _peer != null || _connecting || _approvalPending || cancellationToken.IsCancellationRequested) { completion.TrySetResult(false); return; }
                _approvalPending = true; _devicesPage.Connecting = true; _connect.Enabled = false;
                try
                {
                    using var dialog = new ConnectionApprovalDialog(request);
                    _ = dialog.Handle;
                    _approvalDialog = dialog;
                    using var registration = cancellationToken.Register(() =>
                    {
                        try { dialog.BeginInvoke(() => { if (!dialog.IsDisposed) { dialog.DialogResult = DialogResult.Cancel; dialog.Close(); } }); }
                        catch (InvalidOperationException) { }
                    });
                    bool approved = !cancellationToken.IsCancellationRequested && dialog.ShowDialog(this) == DialogResult.Yes;
                    completion.TrySetResult(approved && !_closing && _peer == null && !_connecting && !cancellationToken.IsCancellationRequested);
                }
                catch (Exception ex) { Log("Yêu cầu kết nối: " + ex.Message); completion.TrySetResult(false); }
                finally { _approvalDialog = null; _approvalPending = false; if (!_closing) { _devicesPage.Connecting = _connecting; _connect.Enabled = _peer == null; } }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return completion.Task;
    }

    void StopNetworkServices()
    {
        _approvalDialog?.Close(); _discovery?.Dispose(); _discovery = null; _autoServer?.Dispose(); _autoServer = null; _servicesStarted = false;
        if (!_closing) _devicesPage.SetDevices([]);
    }
}
