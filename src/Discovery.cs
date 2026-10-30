using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace LanBridge;

/// <summary>Public LAN presence only. No pairing secret, input, or permissions are sent over UDP.</summary>
public sealed class LanDiscoveryService : IDisposable
{
    public const int DefaultPort = 45832;
    public const int MaximumDatagramBytes = 2048;
    public static readonly TimeSpan AnnounceInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DeviceLifetime = TimeSpan.FromSeconds(7);
    private static readonly IPAddress MulticastAddress = IPAddress.Parse("239.255.45.83");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, MaxDepth = 4 };
    private readonly string _deviceId;
    private readonly string _deviceName;
    private readonly int _hostPort;
    private readonly string _fingerprint;
    private readonly Func<bool> _isBusy;
    private readonly int _discoveryPort;
    private readonly IPAddress _bindAddress;
    private readonly IReadOnlyList<IPEndPoint>? _configuredDestinations;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DiscoveredDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastReply = new(StringComparer.Ordinal);
    private UdpClient? _udp;
    private IReadOnlyList<IPEndPoint> _destinations = [];
    private int _started;
    private int _disposed;

    private sealed record Beacon(string Protocol, int Version, string Kind, string Id, string Name, int Port, string Fingerprint, bool Busy);

    public event Action<IReadOnlyList<DiscoveredDevice>>? DevicesChanged;
    public event Action<string>? Error;
    public IReadOnlyList<DiscoveredDevice> Devices { get { lock (_gate) return Snapshot(); } }
    public int LocalPort => (_udp?.Client.LocalEndPoint as IPEndPoint)?.Port ?? _discoveryPort;

    /// <param name="destinations">When supplied, uses these destinations only and does not join multicast; useful for isolated loopback tests.</param>
    public LanDiscoveryService(string deviceId, string deviceName, int hostPort, string fingerprint, Func<bool>? isBusy = null,
        int discoveryPort = DefaultPort, IReadOnlyList<IPEndPoint>? destinations = null, IPAddress? bindAddress = null)
    {
        if (!Guid.TryParse(deviceId, out var id) || id == Guid.Empty) throw new ArgumentException("Mã thiết bị không hợp lệ.", nameof(deviceId));
        if (!ApprovalProtocol.IsValidName(deviceName)) throw new ArgumentException("Tên máy không hợp lệ.", nameof(deviceName));
        if (hostPort is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(hostPort));
        if (fingerprint is not { Length: 64 } || !fingerprint.All(Uri.IsHexDigit)) throw new ArgumentException("Chứng thư không hợp lệ.", nameof(fingerprint));
        if (discoveryPort is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(discoveryPort));
        _bindAddress = bindAddress ?? IPAddress.Any;
        if (_bindAddress.AddressFamily != AddressFamily.InterNetwork) throw new ArgumentException("Chỉ hỗ trợ mạng IPv4.", nameof(bindAddress));
        if (destinations?.Any(endpoint => endpoint.Address.AddressFamily != AddressFamily.InterNetwork || endpoint.Port is < 1 or > 65535) == true)
            throw new ArgumentException("Địa chỉ tìm máy không hợp lệ.", nameof(destinations));
        _deviceId = id.ToString("N");
        _deviceName = deviceName;
        _hostPort = hostPort;
        _fingerprint = fingerprint.ToUpperInvariant();
        _isBusy = isBusy ?? (() => false);
        _discoveryPort = discoveryPort;
        _configuredDestinations = destinations?.ToArray();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        UdpClient? udp = null;
        try
        {
            udp = new UdpClient(AddressFamily.InterNetwork);
            udp.Client.ExclusiveAddressUse = false;
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.EnableBroadcast = true;
            udp.Ttl = 1;
            udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
            udp.Client.Bind(new IPEndPoint(_bindAddress, _discoveryPort));
            _destinations = _configuredDestinations ?? DefaultDestinations(LocalPortOf(udp));
            if (_configuredDestinations is null)
            {
                foreach (var address in LanAddresses.GetLocalIPv4().Where(address => !IPAddress.IsLoopback(address)))
                    try { udp.JoinMulticastGroup(MulticastAddress, address); } catch (SocketException) { /* Broadcast still works on this interface. */ }
            }
            _udp = udp;
            _ = ReceiveLoopAsync(udp);
            _ = AnnounceLoopAsync();
        }
        catch
        {
            udp?.Dispose();
            Interlocked.Exchange(ref _started, 0);
            throw;
        }
    }

    private static int LocalPortOf(UdpClient udp) => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;

    private static IReadOnlyList<IPEndPoint> DefaultDestinations(int port)
    {
        var addresses = new HashSet<IPAddress> { IPAddress.Broadcast, MulticastAddress };
        foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(network =>
            network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            foreach (var local in network.GetIPProperties().UnicastAddresses.Where(local => local.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                var bytes = local.Address.GetAddressBytes();
                var mask = local.IPv4Mask.GetAddressBytes();
                if (mask.Length != 4) continue;
                for (int i = 0; i < 4; i++) bytes[i] |= (byte)~mask[i];
                addresses.Add(new IPAddress(bytes));
            }
        }
        return addresses.Select(address => new IPEndPoint(address, port)).ToArray();
    }

    public async Task RefreshAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_udp is null) throw new InvalidOperationException("Chưa bật tìm thiết bị.");
        await SendToDestinationsAsync("query").ConfigureAwait(false);
    }

    private byte[] Encode(string kind) => JsonSerializer.SerializeToUtf8Bytes(new Beacon("LanBridge", 1, kind, _deviceId,
        _deviceName, _hostPort, _fingerprint, _isBusy()), JsonOptions);

    private async Task SendToDestinationsAsync(string kind)
    {
        var bytes = Encode(kind);
        foreach (var endpoint in _destinations)
            await SendAsync(bytes, endpoint).ConfigureAwait(false);
    }

    private async Task SendAsync(byte[] bytes, IPEndPoint endpoint)
    {
        if (_udp is null || _stop.IsCancellationRequested) return;
        try { await _udp.SendAsync(bytes.AsMemory(), endpoint, _stop.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) { ReportError("Không thể gửi tìm thiết bị trên một card mạng. Kiểm tra mạng LAN và tường lửa UDP 45832."); }
    }

    private async Task AnnounceLoopAsync()
    {
        try
        {
            await SendToDestinationsAsync("query").ConfigureAwait(false);
            using var timer = new PeriodicTimer(AnnounceInterval);
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                PruneExpired(DateTimeOffset.UtcNow);
                await SendToDestinationsAsync("announce").ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch { ReportError("Tìm thiết bị đã dừng. Hãy mở lại LanBridge."); }
    }

    private async Task ReceiveLoopAsync(UdpClient udp)
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                UdpReceiveResult datagram;
                try { datagram = await udp.ReceiveAsync(_stop.Token).ConfigureAwait(false); }
                // Windows reports ICMP port-unreachable from an earlier UDP probe as a receive reset.
                // A PC which has not started LanBridge yet must not permanently stop our receive loop.
                catch (SocketException exception) when (exception.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
                { continue; }
                var now = DateTimeOffset.UtcNow;
                if (!TryDecode(datagram.Buffer, datagram.RemoteEndPoint.Address, now, out var device, out bool query) || device!.Id == _deviceId) continue;
                bool changed;
                bool reply = false;
                lock (_gate)
                {
                    if (!_devices.ContainsKey(device.Id) && _devices.Count >= 128) continue;
                    changed = !_devices.TryGetValue(device.Id, out var old) || old with { LastSeen = now } != device;
                    _devices[device.Id] = device;
                    if (query && (!_lastReply.TryGetValue(device.Id, out var previous) || now - previous > TimeSpan.FromMilliseconds(500)))
                    {
                        _lastReply[device.Id] = now;
                        reply = true;
                    }
                }
                if (changed) Publish();
                if (reply) await SendAsync(Encode("announce"), datagram.RemoteEndPoint).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch { ReportError("Không thể nhận danh sách thiết bị. Kiểm tra tường lửa UDP 45832 hoặc mở lại LanBridge."); }
    }

    internal static bool TryDecode(ReadOnlySpan<byte> bytes, IPAddress sender, DateTimeOffset seen,
        out DiscoveredDevice? device, out bool query)
    {
        device = null;
        query = false;
        if (bytes.Length is < 1 or > MaximumDatagramBytes || sender.AddressFamily != AddressFamily.InterNetwork ||
            sender.Equals(IPAddress.Any) || sender.Equals(IPAddress.Broadcast) || sender.GetAddressBytes()[0] >= 224) return false;
        try
        {
            var beacon = JsonSerializer.Deserialize<Beacon>(bytes, JsonOptions);
            if (beacon is null || beacon.Protocol != "LanBridge" || beacon.Version != 1 || beacon.Kind is not ("query" or "announce") ||
                !Guid.TryParse(beacon.Id, out var id) || id == Guid.Empty || !ApprovalProtocol.IsValidName(beacon.Name) ||
                beacon.Port is < 1 or > 65535 || beacon.Fingerprint is not { Length: 64 } || !beacon.Fingerprint.All(Uri.IsHexDigit)) return false;
            // Address comes exclusively from the received packet, never an advertised JSON field.
            device = new(id.ToString("N"), beacon.Name, sender, beacon.Port, beacon.Fingerprint.ToUpperInvariant(), beacon.Busy, seen);
            query = beacon.Kind == "query";
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException) { return false; }
    }

    internal void PruneExpired(DateTimeOffset now)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var id in _devices.Where(pair => now - pair.Value.LastSeen >= DeviceLifetime).Select(pair => pair.Key).ToArray())
            { _devices.Remove(id); _lastReply.Remove(id); changed = true; }
        }
        if (changed) Publish();
    }

    private IReadOnlyList<DiscoveredDevice> Snapshot() => _devices.Values.OrderBy(device => device.Name, StringComparer.OrdinalIgnoreCase)
        .ThenBy(device => device.Address.ToString(), StringComparer.Ordinal).ToArray();

    private void Publish()
    {
        IReadOnlyList<DiscoveredDevice> snapshot;
        lock (_gate) snapshot = Snapshot();
        if (DevicesChanged is { } handlers)
            foreach (Action<IReadOnlyList<DiscoveredDevice>> handler in handlers.GetInvocationList())
                try { handler(snapshot); } catch { /* UI errors must not stop discovery. */ }
    }

    private void ReportError(string message)
    {
        if (Error is { } handlers)
            foreach (Action<string> handler in handlers.GetInvocationList()) try { handler(message); } catch { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _udp?.Dispose();
        lock (_gate) { _devices.Clear(); _lastReply.Clear(); }
    }
}
