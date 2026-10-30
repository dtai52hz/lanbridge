using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace LanBridge;

/// <summary>Discovery is a LAN hint, never authorization. The receiving PC still approves each connection.</summary>
public sealed record DiscoveredDevice(string Id, string Name, IPAddress Address, int Port, string Fingerprint, bool Busy, DateTimeOffset LastSeen);
public sealed record ConnectionRequest(string Name, IPAddress Address);

internal static class ApprovalProtocol
{
    public const int MaximumNameBytes = 256;
    public static readonly UTF8Encoding Utf8 = new(false, true);
    public static bool IsValidName(string? name) => name is { Length: > 0 and <= 80 } &&
        !name.Any(char.IsControl) && !string.IsNullOrWhiteSpace(name) && Utf8.GetByteCount(name) <= MaximumNameBytes;

    public static async Task WriteRequestAsync(Stream stream, string name, CancellationToken cancellationToken)
    {
        if (!IsValidName(name)) throw new ArgumentException("Tên máy phải có từ 1 đến 80 ký tự, không chứa ký tự điều khiển.", nameof(name));
        var nameBytes = Utf8.GetBytes(name);
        var header = new byte[] { (byte)'L', (byte)'B', (byte)'C', 1, 0, 0 };
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), checked((ushort)nameBytes.Length));
        await stream.WriteAsync(new byte[32], cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(nameBytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<string> ReadRequestAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[6];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        int length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (header[0] != 'L' || header[1] != 'B' || header[2] != 'C' || header[3] != 1 || length is < 1 or > MaximumNameBytes)
            throw new AuthenticationException("Yêu cầu kết nối không hợp lệ.");
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        string name = Utf8.GetString(bytes);
        if (!IsValidName(name)) throw new AuthenticationException("Tên máy không hợp lệ.");
        return name;
    }
}

/// <summary>The certificate pin and 256-bit secret are generated anew for each manually started listener.</summary>
public sealed record PairingInfo(IPAddress Address, int Port, string Fingerprint, string Secret)
{
    public override string ToString() => $"LB1|{Address}|{Port}|{Fingerprint}|{Secret}";

    public static PairingInfo Parse(string code)
    {
        if (!TryParse(code, out var pairing))
            throw new FormatException("Mã ghép nối không hợp lệ. Hãy sao chép lại mã trên máy B.");
        return pairing!;
    }

    public static bool TryParse(string? code, out PairingInfo? pairing)
    {
        pairing = null;
        if (code is null || code.Length > 256) return false;
        var parts = code.Trim().Split('|');
        if (parts.Length != 5 || parts[0] != "LB1" ||
            !IPAddress.TryParse(parts[1], out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast) ||
            !int.TryParse(parts[2], out var port) || port is < 1 or > 65535 ||
            !IsHex256(parts[3]) || !IsHex256(parts[4])) return false;
        pairing = new(address, port, parts[3].ToUpperInvariant(), parts[4].ToUpperInvariant());
        return true;
    }

    private static bool IsHex256(string text) => text.Length == 64 && text.All(Uri.IsHexDigit);
}

public static class LanAddresses
{
    public static IReadOnlyList<IPAddress> GetLocalIPv4()
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
            .Distinct()
            .OrderByDescending(IsPrivate)
            .ThenBy(a => a.ToString(), StringComparer.Ordinal)
            .ToList();
        if (addresses.Count == 0) addresses.Add(IPAddress.Loopback);
        return addresses;
    }

    public static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && (bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168) ||
            (bytes[0] == 169 && bytes[1] == 254) || bytes[0] == 127);
    }
}

public sealed class HostServer : IDisposable
{
    public const int DefaultPort = 45831;
    private readonly TcpListener _listener;
    private readonly IPAddress _bindAddress;
    private readonly RSA _certificateKey;
    private readonly X509Certificate2 _certificate;
    private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
    private readonly CancellationTokenSource _stop = new();
    private PeerConnection? _active;
    private int _started;
    private int _disposed;
    private int _pending;
    private int _rejecting;
    private Task _preambleComplete = Task.CompletedTask;

    public event Action<PeerConnection>? Connected;
    public event Func<ConnectionRequest, CancellationToken, Task<bool>>? ApprovalRequested;
    public bool IsBusy => Volatile.Read(ref _pending) != 0 || Volatile.Read(ref _active) is { IsClosed: false };
    public int Port => _listener.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : DefaultPort;
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_certificate.RawData));

    public HostServer(IPAddress bindAddress, int port = DefaultPort)
    {
        if (bindAddress.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Chọn địa chỉ IPv4 của card mạng LAN.", nameof(bindAddress));
        if (port is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _bindAddress = bindAddress;
        _listener = new TcpListener(bindAddress, port);
        _certificateKey = RSA.Create(2048);
        var request = new CertificateRequest("CN=LanBridge", _certificateKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var pfx = generated.Export(X509ContentType.Pkcs12);
        try
        {
            // Windows SChannel needs a temporary user-profile key container, rather than an ephemeral RSA handle.
            // No PersistKeySet flag: disposing this certificate removes its temporary private-key container.
            _certificate = X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.UserKeySet);
        }
        finally { CryptographicOperations.ZeroMemory(pfx); }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        try { _listener.Start(4); }
        catch { Interlocked.Exchange(ref _started, 0); throw; }
        _ = AcceptLoopAsync();
    }

    public string PairingCode(string advertisedAddress)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0) throw new InvalidOperationException("Máy B chưa bật chờ kết nối.");
        if (!IPAddress.TryParse(advertisedAddress, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
            throw new ArgumentException("Địa chỉ chia sẻ phải là IPv4 của máy B.", nameof(advertisedAddress));
        return new PairingInfo(address, Port, Fingerprint, Convert.ToHexString(_secret)).ToString();
    }

    public string PairingCode() => PairingCode(_bindAddress.ToString());

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var tcp = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                // Preserve sequential validation for the short TLS/secret phase: a failed TLS pin on one
                // socket must not make an immediately following valid connection appear busy.
                try
                {
                    if (Volatile.Read(ref _pending) != 0) await _preambleComplete.WaitAsync(_stop.Token).ConfigureAwait(false);
                }
                catch { tcp.Dispose(); throw; }
                if (Volatile.Read(ref _active) is { IsClosed: false } || Interlocked.CompareExchange(ref _pending, 1, 0) != 0)
                {
                    // A separate, bounded TLS reply lets a discovered client distinguish a busy PC from rejection.
                    if (Interlocked.CompareExchange(ref _rejecting, 1, 0) == 0) _ = RejectBusyAsync(tcp);
                    else tcp.Dispose();
                    continue;
                }
                var preamble = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _preambleComplete = preamble.Task;
                _ = AuthenticateAndPublishAsync(tcp, preamble);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (ObjectDisposedException) when (_stop.IsCancellationRequested) { }
        catch (SocketException) when (_stop.IsCancellationRequested) { }
        catch { Dispose(); }
    }

    private Task AuthenticateTlsAsync(SslStream ssl, CancellationToken token) => ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
    {
        ServerCertificate = _certificate,
        ClientCertificateRequired = false,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ApplicationProtocols = [new SslApplicationProtocol("lanbridge/1")]
    }, token);

    private async Task RejectBusyAsync(TcpClient tcp)
    {
        try
        {
            using (tcp)
            using (var ssl = new SslStream(tcp.GetStream(), false))
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await AuthenticateTlsAsync(ssl, timeout.Token).ConfigureAwait(false);
                var request = new byte[32];
                try
                {
                    await ssl.ReadExactlyAsync(request, timeout.Token).ConfigureAwait(false);
                    if (request.All(value => value == 0)) await ApprovalProtocol.ReadRequestAsync(ssl, timeout.Token).ConfigureAwait(false);
                }
                finally { CryptographicOperations.ZeroMemory(request); }
                await ssl.WriteAsync(new byte[] { 2 }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch { tcp.Dispose(); }
        finally { Interlocked.Exchange(ref _rejecting, 0); }
    }

    private async Task<bool> RequestApprovalAsync(SslStream ssl, ConnectionRequest request, CancellationToken cancellationToken)
    {
        if (ApprovalRequested is not { } handlers) return false;
        using var watch = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var earlyRead = ssl.ReadAsync(new byte[1], watch.Token).AsTask();
        try
        {
            foreach (Func<ConnectionRequest, CancellationToken, Task<bool>> handler in handlers.GetInvocationList())
            {
                // The client must stay silent until approved. Closing or sending application data cancels the local prompt.
                var approval = handler(request, watch.Token).WaitAsync(watch.Token);
                if (await Task.WhenAny(approval, earlyRead).ConfigureAwait(false) == earlyRead)
                    throw new AuthenticationException("Máy yêu cầu đã ngắt hoặc gửi dữ liệu trước khi được chấp nhận.");
                if (!await approval.ConfigureAwait(false)) return false;
            }
            return true;
        }
        finally
        {
            watch.Cancel();
            bool receivedBeforeApproval = false;
            try { await earlyRead.ConfigureAwait(false); receivedBeforeApproval = true; }
            catch (OperationCanceledException) when (watch.IsCancellationRequested) { }
            catch (IOException) { receivedBeforeApproval = true; }
            if (receivedBeforeApproval) throw new AuthenticationException("Máy yêu cầu đã ngắt hoặc gửi dữ liệu trước khi được chấp nhận.");
        }
    }

    private async Task AuthenticateAndPublishAsync(TcpClient tcp, TaskCompletionSource preamble)
    {
        SslStream? ssl = null;
        try
        {
            tcp.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            ssl = new SslStream(tcp.GetStream(), false);
            await AuthenticateTlsAsync(ssl, timeout.Token).ConfigureAwait(false);
            var suppliedSecret = new byte[32];
            bool authenticated;
            try
            {
                await ssl.ReadExactlyAsync(suppliedSecret, timeout.Token).ConfigureAwait(false);
                bool asksApproval = suppliedSecret.All(value => value == 0);
                if (asksApproval)
                {
                    string name = await ApprovalProtocol.ReadRequestAsync(ssl, timeout.Token).ConfigureAwait(false);
                    var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
                    timeout.CancelAfter(TimeSpan.FromSeconds(30));
                    preamble.TrySetResult();
                    authenticated = await RequestApprovalAsync(ssl, new(name, address), timeout.Token).ConfigureAwait(false);
                }
                else authenticated = CryptographicOperations.FixedTimeEquals(suppliedSecret, _secret);
            }
            finally { CryptographicOperations.ZeroMemory(suppliedSecret); }
            await ssl.WriteAsync(new byte[] { authenticated ? (byte)1 : (byte)0 }, timeout.Token).ConfigureAwait(false);
            if (!authenticated) throw new AuthenticationException("Mã ghép nối không khớp.");
            _stop.Token.ThrowIfCancellationRequested();
            var peer = new PeerConnection(tcp, ssl);
            ssl = null; // PeerConnection now owns the socket and TLS stream.
            Interlocked.Exchange(ref _active, peer);
            peer.Closed += _ => Interlocked.CompareExchange(ref _active, null, peer);
            if (_stop.IsCancellationRequested || Connected is null) peer.Dispose();
            else
            {
                try { Connected.Invoke(peer); }
                catch { peer.Dispose(); }
            }
        }
        catch
        {
            if (ssl is not null) ssl.Dispose();
            tcp.Dispose();
        }
        finally { Interlocked.Exchange(ref _pending, 0); preamble.TrySetResult(); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _listener.Stop();
        Interlocked.Exchange(ref _active, null)?.Dispose();
        CryptographicOperations.ZeroMemory(_secret);
        _certificate.Dispose();
        _certificateKey.Dispose();
        // The in-flight accept/authentication uses this token until it observes cancellation.
    }
}

/// <summary>Heartbeat round-trip measurements, including queued writes and both peers' TLS readers.</summary>
public sealed record ConnectionLatency(double LastMilliseconds, double SmoothedMilliseconds,
    double MinimumMilliseconds, double MaximumMilliseconds, long Samples, DateTimeOffset SampledAt);

public sealed class PeerConnection : IDisposable
{
    public const int MaximumPacketBytes = 4 * 1024 * 1024;
    private const int QueueCapacity = 128;
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly OutboundQueue _outbound = new(QueueCapacity);
    private readonly object _latencyGate = new();
    private readonly Dictionary<string, long> _pendingHeartbeats = new(StringComparer.Ordinal);
    private ConnectionLatency? _latency;
    private long _lastReceive = Stopwatch.GetTimestamp();
    private int _started;
    private int _closed;

    private sealed record Outgoing(Packet? Packet, byte[]? Frame, TaskCompletionSource? Completion = null);
    private static readonly JsonSerializerOptions InputJson = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
    // Packet/Permissions have nonzero defaults. Restrict omission of defaults to
    // this input-only DTO so a denied permission is always transmitted as false.
    private readonly record struct InputMessage(string Type, string? Text, string? Name, string? Id, long Length,
        int X, int Y, int Dx, int Dy, int Key, int Scan, int Flags, bool Down, string? Button, bool Allow);

    // Keep only the newest unsent absolute position in each uninterrupted motion run.
    // Encoding happens after dequeue: a 1000/8000 Hz mouse cannot fill the queue with
    // obsolete JSON frames, and a wheel/key/button always follows its exact position.
    private sealed class OutboundQueue(int capacity)
    {
        private readonly object _gate = new();
        private readonly LinkedList<Outgoing> _packets = new();
        private readonly Channel<bool> _ready = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            SingleReader = true, SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            AllowSynchronousContinuations = false
        });
        private TaskCompletionSource _space = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private bool _completed;

        public bool TryWrite(Outgoing outgoing)
        {
            lock (_gate) return TryWriteLocked(outgoing);
        }

        private bool TryWriteLocked(Outgoing outgoing)
        {
            if (_completed) return false;
            if (outgoing is { Packet: { Type: "move" } replacement, Completion: null } &&
                _packets.Last is { Value: { Packet.Type: "move", Completion: null } } last)
            {
                var previous = last.Value.Packet!;
                // A sampled position represents this waiting motion cohort. Keep
                // its receipt token when newer unsampled positions replace it;
                // otherwise congestion would erase the very samples measuring it.
                if (replacement.Id is null && replacement.Length == 0
                    && previous.Id is { Length: 32 } id && previous.Length > 0
                    && Guid.TryParseExact(id, "N", out _))
                    outgoing = outgoing with { Packet = replacement with { Id = id, Length = previous.Length } };
                last.Value = outgoing;
                return true;
            }
            if (_packets.Count >= capacity) return false;
            _packets.AddLast(outgoing);
            _ready.Writer.TryWrite(true);
            return true;
        }

        public async ValueTask WriteAsync(Outgoing outgoing, CancellationToken cancellationToken)
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task available;
                lock (_gate)
                {
                    if (_completed) throw new OperationCanceledException(cancellationToken);
                    if (TryWriteLocked(outgoing)) return;
                    available = _space.Task;
                }
                await available.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public async IAsyncEnumerable<Outgoing> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            while (await _ready.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (_ready.Reader.TryRead(out _)) { }
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Outgoing outgoing;
                    lock (_gate)
                    {
                        if (_packets.First is not { } first) break;
                        outgoing = first.Value;
                        _packets.RemoveFirst();
                        if (_packets.Count == capacity - 1)
                        {
                            var available = _space;
                            _space = new(TaskCreationOptions.RunContinuationsAsynchronously);
                            available.TrySetResult();
                        }
                    }
                    yield return outgoing;
                }
            }
        }

        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                foreach (var outgoing in _packets) outgoing.Completion?.TrySetCanceled();
                _packets.Clear();
                _space.TrySetResult();
                _ready.Writer.TryComplete();
            }
        }
    }

    public event Action<Packet>? Received;
    public event Action<string?>? Closed;
    /// <summary>Raised on the reader thread. Subscribers must return promptly; marshal UI work without waiting.</summary>
    public event Action<ConnectionLatency>? LatencyUpdated;
    /// <summary>Null until a peer echoes a tracked heartbeat. Older peers remain connected without RTT samples.</summary>
    public ConnectionLatency? Latency => Volatile.Read(ref _latency);
    public bool IsClosed => Volatile.Read(ref _closed) != 0;
    public string RemoteAddress => (_tcp.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";

    internal PeerConnection(TcpClient tcp, SslStream stream)
    {
        _tcp = tcp;
        _stream = stream;
    }

    public static async Task<PeerConnection> ConnectAsync(string pairingCode, CancellationToken cancellationToken = default)
    {
        var pairing = PairingInfo.Parse(pairingCode);
        var pin = Convert.FromHexString(pairing.Fingerprint);
        var secret = Convert.FromHexString(pairing.Secret);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            await tcp.ConnectAsync(pairing.Address, pairing.Port, timeout.Token).ConfigureAwait(false);
            ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
                certificate is not null && CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(certificate.GetRawCertData())));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "lanbridge",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = [new SslApplicationProtocol("lanbridge/1")]
            }, timeout.Token).ConfigureAwait(false);
            // Never disclose the pairing secret until the TLS certificate pin has passed.
            await ssl.WriteAsync(secret, timeout.Token).ConfigureAwait(false);
            var ack = new byte[1];
            await ssl.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            if (ack[0] != 1) throw new AuthenticationException("Mã ghép nối đã sai hoặc hết hiệu lực. Lấy mã mới trên máy B.");
            return new PeerConnection(tcp, ssl);
        }
        catch
        {
            ssl?.Dispose();
            tcp.Dispose();
            throw;
        }
        finally { CryptographicOperations.ZeroMemory(secret); }
    }

    /// <summary>The discovered certificate is pinned, then the receiving PC must explicitly approve this request.</summary>
    public static async Task<PeerConnection> ConnectDiscoveredAsync(DiscoveredDevice device, string localName, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (device.Address.AddressFamily != AddressFamily.InterNetwork || device.Address.Equals(IPAddress.Any) ||
            device.Address.Equals(IPAddress.Broadcast) || device.Port is < 1 or > 65535 ||
            device.Fingerprint is not { Length: 64 } || !device.Fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Thông tin thiết bị không hợp lệ.", nameof(device));
        if (!ApprovalProtocol.IsValidName(localName)) throw new ArgumentException("Tên máy không hợp lệ.", nameof(localName));
        var pin = Convert.FromHexString(device.Fingerprint);
        var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        SslStream? ssl = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            await tcp.ConnectAsync(device.Address, device.Port, timeout.Token).ConfigureAwait(false);
            ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
                CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(certificate.GetRawCertData())));
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "lanbridge",
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                ApplicationProtocols = [new SslApplicationProtocol("lanbridge/1")]
            }, timeout.Token).ConfigureAwait(false);
            await ApprovalProtocol.WriteRequestAsync(ssl, localName, timeout.Token).ConfigureAwait(false);
            var ack = new byte[1];
            await ssl.ReadExactlyAsync(ack, timeout.Token).ConfigureAwait(false);
            if (ack[0] == 2) throw new AuthenticationException("Máy này đang có phiên hoặc yêu cầu kết nối khác. Hãy thử lại sau.");
            if (ack[0] != 1) throw new AuthenticationException("Máy bên kia đã từ chối yêu cầu kết nối.");
            return new PeerConnection(tcp, ssl);
        }
        catch
        {
            ssl?.Dispose();
            tcp.Dispose();
            throw;
        }
    }

    public void Start()
    {
        if (IsClosed) return;
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
        _ = ReadLoopAsync();
        _ = WriteLoopAsync();
        _ = HeartbeatLoopAsync();
    }

    /// <summary>Input producers must never wait inside a Windows hook. A congested critical queue closes the session.</summary>
    public bool TrySend(Packet packet)
    {
        if (IsClosed) return false;
        Packet snapshot;
        try
        {
            Validate(packet);
            snapshot = packet with
            {
                Data = packet.Data?.ToArray(),
                Permissions = packet.Permissions is { } permissions ? permissions with { } : null,
                ClipboardEntries = packet.ClipboardEntries?.Select(entry => entry is null ? null! : entry with { }).ToArray()
            };
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            Close("Gói dữ liệu gửi không hợp lệ.");
            return false;
        }
        if (_outbound.TryWrite(new(snapshot, null))) return true;
        if (!CanDrop(packet.Type)) Close("Kết nối quá chậm; phiên điều khiển đã dừng để nhả phím và chuột.");
        return false;
    }

    /// <summary>For files and other bulk producers: waits for queue capacity and the actual TLS write.</summary>
    public async Task SendAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _outbound.WriteAsync(new(null, Encode(packet), completion), linked.Token).ConfigureAwait(false);
        await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private static bool CanDrop(string type) => type is "move" or "mouseMove" or "mouse-move" or "audio" or "frame";

    private static byte[] Encode(Packet packet)
    {
        Validate(packet);
        var bytes = packet.Type is "move" or "wheel" or "button" or "key" or "enter" or "leave" or "text"
            ? JsonSerializer.SerializeToUtf8Bytes(new InputMessage(packet.Type, packet.Text, packet.Name, packet.Id, packet.Length,
                packet.X, packet.Y, packet.Dx, packet.Dy, packet.Key, packet.Scan, packet.Flags,
                packet.Down, packet.Button, packet.Allow), InputJson)
            : JsonSerializer.SerializeToUtf8Bytes(packet, WireJson.Options);
        if (bytes.Length is < 1 or > MaximumPacketBytes) throw new InvalidDataException("Gói dữ liệu vượt quá giới hạn 4 MiB.");
        // One application frame in one TLS write avoids an extra record/packet and
        // header-only reads for every movement and wheel notch.
        var frame = new byte[bytes.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, bytes.Length);
        bytes.CopyTo(frame, 4);
        return frame;
    }

    private static void Validate(Packet packet)
    {
        if (packet.Type is not { Length: > 0 and <= 64 } || packet.Type.Any(char.IsControl) || packet.Length < 0)
            throw new InvalidDataException("Gói dữ liệu không hợp lệ.");
        if (packet.Data?.Length > MaximumPacketBytes) throw new InvalidDataException("Gói dữ liệu quá lớn.");
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _stream.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                int length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is < 1 or > MaximumPacketBytes) throw new InvalidDataException("Kích thước gói dữ liệu không hợp lệ.");
                var bytes = new byte[length];
                await _stream.ReadExactlyAsync(bytes, _stop.Token).ConfigureAwait(false);
                var packet = JsonSerializer.Deserialize<Packet>(bytes, WireJson.Options) ?? throw new InvalidDataException("Gói dữ liệu rỗng.");
                Validate(packet);
                Interlocked.Exchange(ref _lastReceive, Stopwatch.GetTimestamp());
                if (packet.Type == "$ping")
                {
                    // Echo an opaque identifier and the originating peer's monotonic timestamp.
                    // Never subtract a timestamp produced by the other PC's clock.
                    TrySend(new Packet { Type = "$pong", Id = packet.Id, Length = packet.Length });
                    continue;
                }
                if (packet.Type == "$pong") { RecordHeartbeat(packet); continue; }
                if (Received is { } handlers)
                    foreach (Action<Packet> handler in handlers.GetInvocationList()) handler(packet);
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (EndOfStreamException) { Close("Máy bên kia đã ngắt kết nối."); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        { Close("Đã ngắt kết nối vì nhận dữ liệu không hợp lệ."); }
        catch { Close("Mất kết nối với máy bên kia."); }
    }

    private async Task WriteLoopAsync()
    {
        Outgoing? current = null;
        try
        {
            await foreach (var outgoing in _outbound.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                current = outgoing;
                var frame = outgoing.Frame ?? Encode(outgoing.Packet!);
                await _stream.WriteAsync(frame, _stop.Token).ConfigureAwait(false);
                outgoing.Completion?.TrySetResult();
                current = null;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        { Close("Gói dữ liệu gửi không hợp lệ."); }
        catch { Close("Không thể gửi dữ liệu đến máy bên kia."); }
        finally
        {
            current?.Completion?.TrySetCanceled();
            _outbound.Complete();
        }
    }

    private async Task HeartbeatLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500));
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastReceive)) >= TimeSpan.FromSeconds(3.5))
                {
                    Close("Mất phản hồi từ máy bên kia; đã dừng điều khiển.");
                    return;
                }
                if (!SendHeartbeat()) return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Close("Đã dừng phiên điều khiển."); }
    }

    private bool SendHeartbeat()
    {
        string id = Guid.NewGuid().ToString("N");
        long started = Stopwatch.GetTimestamp();
        lock (_latencyGate)
        {
            // Keep more than one in flight: an RTT above the 500 ms cadence must
            // still be measurable. Legacy peers return plain pongs, so bound storage.
            if (_pendingHeartbeats.Count >= 64)
            {
                string oldest = _pendingHeartbeats.MinBy(pair => pair.Value).Key;
                _pendingHeartbeats.Remove(oldest);
            }
            _pendingHeartbeats[id] = started;
        }
        if (TrySend(new Packet { Type = "$ping", Id = id, Length = started })) return true;
        lock (_latencyGate) _pendingHeartbeats.Remove(id);
        return false;
    }

    private void RecordHeartbeat(Packet packet)
    {
        ConnectionLatency sample;
        lock (_latencyGate)
        {
            if (packet.Id is null || !_pendingHeartbeats.TryGetValue(packet.Id, out long started)
                || packet.Length != started) return;
            _pendingHeartbeats.Remove(packet.Id);
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var previous = _latency;
            sample = new(elapsed, previous is null ? elapsed : previous.SmoothedMilliseconds * .875 + elapsed * .125,
                previous is null ? elapsed : Math.Min(previous.MinimumMilliseconds, elapsed),
                previous is null ? elapsed : Math.Max(previous.MaximumMilliseconds, elapsed),
                (previous?.Samples ?? 0) + 1, DateTimeOffset.UtcNow);
            Volatile.Write(ref _latency, sample);
        }
        if (LatencyUpdated is { } handlers)
            foreach (Action<ConnectionLatency> handler in handlers.GetInvocationList())
                try { handler(sample); } catch { /* Diagnostic observers must not close an otherwise healthy session. */ }
    }

    internal void Abort(string reason) => Close(reason);

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        lock (_latencyGate) _pendingHeartbeats.Clear();
        _outbound.Complete();
        _tcp.Dispose();
        _stream.Dispose();
        if (Closed is { } handlers)
            foreach (Action<string?> handler in handlers.GetInvocationList())
                try { handler(reason); } catch { /* Cleanup must reach every subscriber, including input release. */ }
    }

    public void Dispose() => Close(null);
}
