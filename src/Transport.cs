using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
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

public sealed class PeerConnection : IDisposable
{
    public const int MaximumPacketBytes = 4 * 1024 * 1024;
    private const int QueueCapacity = 128;
    private readonly TcpClient _tcp;
    private readonly SslStream _stream;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<Outgoing> _outbound = Channel.CreateBounded<Outgoing>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = false,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private long _lastReceive = Stopwatch.GetTimestamp();
    private int _started;
    private int _closed;

    private sealed record Outgoing(byte[] Bytes, TaskCompletionSource? Completion = null);

    public event Action<Packet>? Received;
    public event Action<string?>? Closed;
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
        byte[] bytes;
        try { bytes = Encode(packet); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidDataException)
        {
            Close("Gói dữ liệu gửi không hợp lệ.");
            return false;
        }
        if (_outbound.Writer.TryWrite(new(bytes))) return true;
        if (!CanDrop(packet.Type)) Close("Kết nối quá chậm; phiên điều khiển đã dừng để nhả phím và chuột.");
        return false;
    }

    /// <summary>For files and other bulk producers: waits for queue capacity and the actual TLS write.</summary>
    public async Task SendAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(IsClosed, this);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stop.Token);
        await _outbound.Writer.WriteAsync(new(Encode(packet), completion), linked.Token).ConfigureAwait(false);
        await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
    }

    private static bool CanDrop(string type) => type is "move" or "mouseMove" or "mouse-move" or "audio" or "frame";

    private static byte[] Encode(Packet packet)
    {
        Validate(packet);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(packet, WireJson.Options);
        if (bytes.Length is < 1 or > MaximumPacketBytes) throw new InvalidDataException("Gói dữ liệu vượt quá giới hạn 4 MiB.");
        return bytes;
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
                if (packet.Type == "$ping") { TrySend(new Packet { Type = "$pong" }); continue; }
                if (packet.Type == "$pong") continue;
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
        var header = new byte[4];
        try
        {
            await foreach (var outgoing in _outbound.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
            {
                current = outgoing;
                BinaryPrimitives.WriteInt32BigEndian(header, outgoing.Bytes.Length);
                await _stream.WriteAsync(header, _stop.Token).ConfigureAwait(false);
                await _stream.WriteAsync(outgoing.Bytes, _stop.Token).ConfigureAwait(false);
                outgoing.Completion?.TrySetResult();
                current = null;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Close("Không thể gửi dữ liệu đến máy bên kia."); }
        finally
        {
            current?.Completion?.TrySetCanceled();
            while (_outbound.Reader.TryRead(out var outgoing)) outgoing.Completion?.TrySetCanceled();
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
                if (!TrySend(new Packet { Type = "$ping" })) return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch { Close("Đã dừng phiên điều khiển."); }
    }

    internal void Abort(string reason) => Close(reason);

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _outbound.Writer.TryComplete();
        _tcp.Dispose();
        _stream.Dispose();
        while (_outbound.Reader.TryRead(out var outgoing)) outgoing.Completion?.TrySetCanceled();
        if (Closed is { } handlers)
            foreach (Action<string?> handler in handlers.GetInvocationList())
                try { handler(reason); } catch { /* Cleanup must reach every subscriber, including input release. */ }
    }

    public void Dispose() => Close(null);
}
