using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using LanBridge;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static async Task WaitUntilAsync(Func<bool> condition, string message)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
    try { while (!condition()) await Task.Delay(20, timeout.Token); }
    catch (OperationCanceledException) { throw new InvalidOperationException(message); }
}
static async Task<Exception> RejectedAsync(Func<Task> action, string message)
{
    try { await action(); }
    catch (Exception exception) { return exception; }
    throw new InvalidOperationException(message);
}
static int UdpPort()
{
    using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
    return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
}
static DiscoveredDevice HostDevice(HostServer host) => new(Guid.NewGuid().ToString("N"), "Máy B", IPAddress.Loopback,
    host.Port, host.Fingerprint, host.IsBusy, DateTimeOffset.UtcNow);
static byte[] Beacon(string id, string name = "Máy B", int port = 45831, string? fingerprint = null,
    int version = 1, string kind = "announce", string protocol = "LanBridge") => JsonSerializer.SerializeToUtf8Bytes(new
    { protocol, version, kind, id, name, port, fingerprint = fingerprint ?? new string('A', 64), busy = false, address = "203.0.113.99" });
static async Task<(TcpClient Tcp, SslStream Stream)> RawApprovalAsync(HostServer host)
{
    var tcp = new TcpClient(AddressFamily.InterNetwork);
    await tcp.ConnectAsync(IPAddress.Loopback, host.Port);
    var pin = Convert.FromHexString(host.Fingerprint);
    var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) => certificate is not null &&
        CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(certificate.GetRawCertData())));
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "lanbridge",
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ApplicationProtocols = [new SslApplicationProtocol("lanbridge/1")]
    });
    await ApprovalProtocol.WriteRequestAsync(ssl, "Early sender", default);
    return (tcp, ssl);
}

var id = Guid.NewGuid().ToString("N");
var now = DateTimeOffset.UtcNow;
Check(LanDiscoveryService.TryDecode(Beacon(id), IPAddress.Loopback, now, out var decoded, out bool query) &&
    decoded!.Address.Equals(IPAddress.Loopback) && decoded.LastSeen == now && !query, "Valid beacon did not derive source IP from datagram.");
foreach (var bytes in new[] { Beacon("bad"), Beacon(Guid.Empty.ToString()), Beacon(id, "bad\nname"), Beacon(id, new string('A', 81)),
    Beacon(id, port: 0), Beacon(id, port: 65536), Beacon(id, fingerprint: "bad"), Beacon(id, version: 2), Beacon(id, kind: "pair"),
    Beacon(id, protocol: "OtherApp"), Encoding.UTF8.GetBytes("{bad"), new byte[2049] })
    Check(!LanDiscoveryService.TryDecode(bytes, IPAddress.Loopback, now, out _, out _), "Invalid beacon was accepted.");
Check(!LanDiscoveryService.TryDecode(Beacon(id), IPAddress.Broadcast, now, out _, out _), "Broadcast sender was accepted.");
Console.WriteLine("PASS: bounded discovery protocol, ID/name/port/pin validation, source address and version checks.");

int firstPort = UdpPort(), secondPort = UdpPort();
while (firstPort == secondPort) secondPort = UdpPort();
bool busy = false;
using (var first = new LanDiscoveryService(Guid.NewGuid().ToString("N"), "Máy A", 45831, new string('A', 64),
    discoveryPort: firstPort, destinations: [new(IPAddress.Loopback, secondPort)], bindAddress: IPAddress.Loopback))
using (var second = new LanDiscoveryService(id, "Máy B", 45831, new string('B', 64), () => busy,
    secondPort, [new(IPAddress.Loopback, firstPort)], IPAddress.Loopback))
{
    first.Error += message => Console.WriteLine("first: " + message);
    second.Error += message => Console.WriteLine("second: " + message);
    first.Start(); second.Start();
    await first.RefreshAsync(); await second.RefreshAsync();
    await WaitUntilAsync(() => first.Devices.Count == 1 && second.Devices.Count == 1, "Loopback devices did not discover each other.");
    Check(first.Devices[0].Id == id && first.Devices[0].Address.Equals(IPAddress.Loopback), "Discovery showed the wrong device or own device.");
    busy = true;
    await second.RefreshAsync();
    await WaitUntilAsync(() => first.Devices[0].Busy, "Busy presence did not update.");
    using var sender = new UdpClient(AddressFamily.InterNetwork);
    await sender.SendAsync(Beacon(id, "Changed name"), new IPEndPoint(IPAddress.Loopback, firstPort));
    await WaitUntilAsync(() => first.Devices[0].Name == "Changed name", "Same device was not updated in place.");
    Check(first.Devices.Count == 1, "An updated device was duplicated.");
    first.PruneExpired(DateTimeOffset.UtcNow.Add(LanDiscoveryService.DeviceLifetime));
    Check(first.Devices.Count == 0, "Stale device did not expire.");
}
using (var capture = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
using (var presence = new LanDiscoveryService(id, "Public name", 45831, new string('C', 64), discoveryPort: 0,
    destinations: [(IPEndPoint)capture.Client.LocalEndPoint!], bindAddress: IPAddress.Loopback))
{
    presence.Start();
    var datagram = await capture.ReceiveAsync().WaitAsync(TimeSpan.FromSeconds(3));
    Check(datagram.Buffer.Length <= LanDiscoveryService.MaximumDatagramBytes, "Public beacon exceeded bound.");
    using var json = JsonDocument.Parse(datagram.Buffer);
    var allowed = new HashSet<string> { "protocol", "version", "kind", "id", "name", "port", "fingerprint", "busy" };
    Check(json.RootElement.EnumerateObject().All(property => allowed.Contains(property.Name)), "Beacon exposed non-public fields.");
    Check(!Encoding.UTF8.GetString(datagram.Buffer).Contains("secret", StringComparison.OrdinalIgnoreCase), "UDP beacon included a pairing secret.");
}
Console.WriteLine("PASS: isolated loopback discovery, query/reply, updates, busy state, deduplication, expiry and public-only beacons.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    bool published = false;
    host.Connected += peer => { published = true; peer.Dispose(); };
    host.Start();
    await RejectedAsync(async () => { using var ignored = await PeerConnection.ConnectDiscoveredAsync(HostDevice(host), "Máy A"); }, "Missing consent handler allowed connection.");
    await WaitUntilAsync(() => !host.IsBusy, "Rejected request did not release pending slot.");
    Check(!published, "Unapproved peer was published.");
}

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var prompted = Signal<ConnectionRequest>();
    var approved = Signal<bool>();
    var accepted = Signal<PeerConnection>();
    host.ApprovalRequested += async (request, token) => { prompted.TrySetResult(request); return await approved.Task.WaitAsync(token); };
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    var device = HostDevice(host);
    var wrongPin = device with { Fingerprint = new string('0', 64) };
    await RejectedAsync(async () => { using var ignored = await PeerConnection.ConnectDiscoveredAsync(wrongPin, "Máy A"); }, "Discovered client accepted a different certificate.");
    Check(!prompted.Task.IsCompleted, "Certificate failure reached local consent.");
    await WaitUntilAsync(() => !host.IsBusy, "Pin failure retained pending slot.");
    var connecting = PeerConnection.ConnectDiscoveredAsync(device, "Máy A tiếng Việt");
    var request = await prompted.Task.WaitAsync(TimeSpan.FromSeconds(4));
    Check(request.Name == "Máy A tiếng Việt" && request.Address.Equals(IPAddress.Loopback), "Consent did not show actual source address and name.");
    Check(host.IsBusy && !connecting.IsCompleted && !accepted.Task.IsCompleted, "Peer became usable before local approval.");
    var second = await RejectedAsync(async () => { using var ignored = await PeerConnection.ConnectDiscoveredAsync(device, "Second requester"); }, "Concurrent pending request was allowed.");
    Check(second is AuthenticationException && second.Message.Contains("đang có phiên"), "Pending slot did not return explicit busy status.");
    approved.TrySetResult(true);
    using var controller = await connecting.WaitAsync(TimeSpan.FromSeconds(4));
    using var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(4));
    controller.Start();
    var received = Signal<Packet>();
    remote.Received += packet => received.TrySetResult(packet);
    await controller.SendAsync(new Packet { Type = "test", Text = "Chỉ sau khi cho phép" });
    Check((await received.Task.WaitAsync(TimeSpan.FromSeconds(3))).Text == "Chỉ sau khi cho phép", "Approved TLS packet failed.");
    controller.Dispose();
    await WaitUntilAsync(() => !host.IsBusy, "Disconnect did not clear active slot.");
}
Console.WriteLine("PASS: no-handler denial, TLS pinning, local consent required, Unicode requester name, actual source IP, busy rejection and approved packets.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var prompted = Signal<bool>();
    var promptCancelled = Signal<bool>();
    bool published = false;
    host.ApprovalRequested += async (_, token) =>
    {
        prompted.TrySetResult(true);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
        finally { promptCancelled.TrySetResult(true); }
    };
    host.Connected += peer => { published = true; peer.Dispose(); };
    host.Start();
    using var cancel = new CancellationTokenSource();
    var connecting = PeerConnection.ConnectDiscoveredAsync(HostDevice(host), "Máy A", cancel.Token);
    await prompted.Task.WaitAsync(TimeSpan.FromSeconds(4));
    cancel.Cancel();
    await RejectedAsync(async () => { using var ignored = await connecting; }, "Cancelled request succeeded.");
    await promptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(4));
    await WaitUntilAsync(() => !host.IsBusy, "Client cancellation retained pending slot.");
    Check(!published, "Cancelled peer was published.");
}
Console.WriteLine("PASS: caller cancellation closes consent and releases the pending slot without publishing a peer.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    bool published = false;
    host.ApprovalRequested += (_, _) => Task.FromResult(false);
    host.Connected += peer => { published = true; peer.Dispose(); };
    host.Start();
    var denied = await RejectedAsync(async () => { using var ignored = await PeerConnection.ConnectDiscoveredAsync(HostDevice(host), "Máy A"); }, "Explicit denial allowed a connection.");
    Check(denied is AuthenticationException && denied.Message.Contains("từ chối"), "Explicit denial did not report rejection.");
    await WaitUntilAsync(() => !host.IsBusy, "Explicit denial retained pending slot.");
    Check(!published, "Denied peer was published.");
}
using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var prompted = Signal<bool>();
    var promptCancelled = Signal<bool>();
    bool published = false;
    host.ApprovalRequested += async (_, token) =>
    {
        prompted.TrySetResult(true);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
        finally { promptCancelled.TrySetResult(true); }
    };
    host.Connected += peer => { published = true; peer.Dispose(); };
    host.Start();
    var (tcp, ssl) = await RawApprovalAsync(host);
    using (tcp) using (ssl)
    {
        await prompted.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await ssl.WriteAsync(new byte[] { 123 });
        await promptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(4));
        await WaitUntilAsync(() => !host.IsBusy, "Early application data retained pending slot.");
        Check(!published, "Early application data reached an approved peer.");
    }
}
Console.WriteLine("PASS: explicit denial and premature application data reject without publishing a peer.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var prompted = Signal<bool>();
    var promptCancelled = Signal<bool>();
    bool published = false;
    host.ApprovalRequested += async (_, token) =>
    {
        prompted.TrySetResult(true);
        try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return true; }
        finally { promptCancelled.TrySetResult(true); }
    };
    host.Connected += peer => { published = true; peer.Dispose(); };
    host.Start();
    var connecting = PeerConnection.ConnectDiscoveredAsync(HostDevice(host), "Máy A");
    await prompted.Task.WaitAsync(TimeSpan.FromSeconds(4));
    await promptCancelled.Task.WaitAsync(TimeSpan.FromSeconds(34));
    await RejectedAsync(async () => { using var ignored = await connecting; }, "Expired consent allowed a connection.");
    await WaitUntilAsync(() => !host.IsBusy, "Expired consent retained pending slot.");
    Check(!published, "Expired request was published.");
}
Console.WriteLine("PASS: unanswered consent expires after 30 seconds, cancels the prompt and releases pending state.");
