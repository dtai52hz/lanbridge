using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using LanBridge;

if (args.Contains("--latency-only"))
{
    await LatencyProbe.RunAsync();
    return;
}
if (args.Contains("--latency-shaped"))
{
    await LatencyProbe.RunShapedAsync();
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ThrowsAsync(Func<Task> action, string description)
{
    bool thrown = false;
    try { await action(); }
    catch { thrown = true; }
    Check(thrown, description);
}

static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static async Task<(TcpClient Tcp, SslStream Stream)> RawConnectAsync(string code)
{
    var info = PairingInfo.Parse(code);
    var tcp = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
    await tcp.ConnectAsync(info.Address, info.Port);
    var pin = Convert.FromHexString(info.Fingerprint);
    var ssl = new SslStream(tcp.GetStream(), false, (_, certificate, _, _) =>
        certificate is not null && CryptographicOperations.FixedTimeEquals(pin, SHA256.HashData(certificate.GetRawCertData())));
    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
    {
        TargetHost = "lanbridge",
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        ApplicationProtocols = [new SslApplicationProtocol("lanbridge/1")]
    });
    await ssl.WriteAsync(Convert.FromHexString(info.Secret));
    var ack = new byte[1];
    await ssl.ReadExactlyAsync(ack);
    Check(ack[0] == 1, "Raw client authentication failed.");
    return (tcp, ssl);
}

Check(!PairingInfo.TryParse("bad", out _), "Malformed pairing code was accepted.");
Check(!PairingInfo.TryParse("LB1|0.0.0.0|45831|" + new string('A', 64) + "|" + new string('B', 64), out _), "Wildcard pairing address was accepted.");
Check(LanAddresses.IsPrivate(IPAddress.Parse("192.168.1.2")), "Private IPv4 detection failed.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var accepted = Signal<PeerConnection>();
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    string code = host.PairingCode("127.0.0.1");
    Check(PairingInfo.Parse(code).ToString() == code, "Pairing code did not round-trip.");
    var wrongSecret = PairingInfo.Parse(code) with { Secret = new string('0', 64) };
    await ThrowsAsync(async () => { using var ignored = await PeerConnection.ConnectAsync(wrongSecret.ToString()); }, "Wrong pairing secret was accepted.");
    Check(!accepted.Task.IsCompleted, "Unauthenticated connection was published.");
    var wrongPin = PairingInfo.Parse(code) with { Fingerprint = new string('0', 64) };
    await ThrowsAsync(async () => { using var ignored = await PeerConnection.ConnectAsync(wrongPin.ToString()); }, "Wrong certificate fingerprint was accepted.");
    Check(!accepted.Task.IsCompleted, "Unpinned connection was published.");

    using var controller = await PeerConnection.ConnectAsync(code);
    using var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var received = Signal<Packet>();
    remote.Received += packet => { if (packet.Type == "test") received.TrySetResult(packet); };
    controller.Start();
    await controller.SendAsync(new Packet { Type = "test", Text = "Tiếng Việt, bàn phím và chuột", X = -7, Data = [1, 2, 3] });
    var packet = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(packet.Text == "Tiếng Việt, bàn phím và chuột" && packet.X == -7 && packet.Data!.SequenceEqual(new byte[] { 1, 2, 3 }), "TLS packet round-trip failed.");
    await ThrowsAsync(async () => { using var ignored = await PeerConnection.ConnectAsync(code); }, "Host accepted a second simultaneous controller.");

    int count = 0;
    var bulkDone = Signal<bool>();
    remote.Received += p => { if (p.Type == "chunk" && Interlocked.Increment(ref count) == 150) bulkDone.TrySetResult(true); };
    for (int i = 0; i < 150; i++)
        await controller.SendAsync(new Packet { Type = "chunk", X = i, Data = RandomNumberGenerator.GetBytes(4096) });
    await bulkDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Check(!controller.IsClosed && !remote.IsClosed, "Bulk writes disconnected the session.");
    // Healthy peers must remain connected beyond the silence threshold while idle.
    await Task.Delay(TimeSpan.FromSeconds(4.1));
    Check(!controller.IsClosed && !remote.IsClosed, "Healthy heartbeat disconnected the session.");
    var closed = Signal<bool>();
    var localReason = Signal<string?>();
    remote.Closed += _ => closed.TrySetResult(true);
    controller.Closed += reason => localReason.TrySetResult(reason);
    controller.Abort("Hàng đợi nhận quá tải; đã nhả chuột và bàn phím.");
    Check(await localReason.Task.WaitAsync(TimeSpan.FromSeconds(2)) == "Hàng đợi nhận quá tải; đã nhả chuột và bàn phím.",
        "An overloaded session must retain its actionable local disconnect reason.");
    await closed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    var reaccepted = Signal<PeerConnection>();
    host.Connected += peer => reaccepted.TrySetResult(peer);
    using var reconnected = await PeerConnection.ConnectAsync(code);
    using var reconnectedRemote = await reaccepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    reconnected.Start();
    Check(!reconnectedRemote.IsClosed, "Host did not release the active connection slot after disconnect.");
}
Console.WriteLine("PASS: pairing validation, TLS pin, secret authentication, authenticated packet round-trip, one controller, bulk backpressure, healthy heartbeat and close propagation.");

// A paused writer models a short Wi-Fi/scheduler stall. Thousands of absolute
// samples must collapse before encoding, while every wheel/key/button boundary
// retains its exact place, coordinates, direction and release state over real TLS.
using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var accepted = Signal<PeerConnection>();
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    using var controller = await PeerConnection.ConnectAsync(host.PairingCode("127.0.0.1"));
    using var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var actual = new List<Packet>();
    var expected = new List<Packet>();
    var done = Signal<bool>();
    remote.Received += packet =>
    {
        actual.Add(packet);
        if (packet.Type == "burst-end") done.TrySetResult(true);
    };
    void SendBarrier(Packet packet)
    {
        Check(controller.TrySend(packet), $"Critical {packet.Type} was congested behind stale pointer positions.");
        expected.Add(packet);
    }
    SendBarrier(new Packet { Type = "enter", X = 0, Y = 0, Name = "Right" });
    SendBarrier(new Packet { Type = "key", Key = 0x11, Scan = 0x1D, Down = true });
    for (int run = 0; run < 16; run++)
    {
        var movement = new Packet { Type = "move" };
        for (int sample = 0; sample < 5000; sample++)
        {
            movement.X = run * 3000 + sample % 3000;
            movement.Y = 60000 - movement.X;
            Check(controller.TrySend(movement), "An uninterrupted pointer burst filled the outbound queue.");
        }
        expected.Add(movement with { });
        // Reusing a mutable producer object cannot change a deferred packet.
        movement.X = movement.Y = -1;
        SendBarrier(new Packet { Type = "wheel", X = run * 3000 + 1999, Y = 60000 - (run * 3000 + 1999),
            Dx = run % 2 == 0 ? 120 : -120, Dy = run % 3 == 0 ? -240 : 120 });
        if (run == 7) SendBarrier(new Packet { Type = "button", X = 22999, Y = 37001, Button = "left", Down = true });
        if (run == 8) SendBarrier(new Packet { Type = "button", X = 25999, Y = 34001, Button = "left", Down = false });
    }
    SendBarrier(new Packet { Type = "text", Text = "chuột và cuộn", Dx = 2 });
    SendBarrier(new Packet { Type = "key", Key = 0x11, Scan = 0x1D, Down = false });
    SendBarrier(new Packet { Type = "leave", X = 65535, Y = 0, Name = "Right", Allow = false });
    var permissions = new Permissions { Input = false, Clipboard = false, Files = false };
    Check(controller.TrySend(new Packet { Type = "permissions", Permissions = permissions }), "Permissions did not queue.");
    expected.Add(new Packet { Type = "permissions", Permissions = permissions with { } });
    permissions.Input = permissions.Clipboard = permissions.Files = true;
    var end = new Packet { Type = "burst-end" };
    expected.Add(end);
    var written = controller.SendAsync(end);
    Check(!written.IsCompleted, "SendAsync completed before its actual TLS write.");
    var elapsed = Stopwatch.StartNew();
    controller.Start();
    await written.WaitAsync(TimeSpan.FromSeconds(2));
    await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
    Check(actual.Count == expected.Count, $"80,000 moves produced {actual.Count} frames; expected {expected.Count} ordered coalesced frames.");
    for (int i = 0; i < expected.Count; i++)
        Check(JsonSerializer.Serialize(actual[i], WireJson.Options) == JsonSerializer.Serialize(expected[i], WireJson.Options),
            $"Pointer/wheel/key/button/text/permission ordering or deferred snapshot changed at frame {i} ({expected[i].Type}).");
    Check(elapsed.Elapsed < TimeSpan.FromSeconds(2), "Coalesced pointer and wheel burst left a stale input backlog.");
    Check(!controller.IsClosed && !remote.IsClosed, "A high polling-rate pointer burst disconnected the session.");
    Console.WriteLine($"PASS: 80,000 pointer samples became {actual.Count} ordered TLS frames in {elapsed.ElapsedMilliseconds} ms, preserving all wheel/key/button/text/permission boundaries.");
}

// Compact input JSON stays compatible with the original Packet reader. Global
// permission defaults must not participate in omission of input-only defaults.
using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var accepted = Signal<PeerConnection>();
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    var (tcp, ssl) = await RawConnectAsync(host.PairingCode("127.0.0.1"));
    using (tcp) using (ssl)
    using (var sender = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5)))
    {
        Check(sender.TrySend(new Packet { Type = "wheel", X = 65535, Y = 0, Dx = -120, Dy = 240 }), "Compact wheel did not queue.");
        var header = new byte[4];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await ssl.ReadExactlyAsync(header, timeout.Token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        Check(length is > 0 and < 80, $"An input frame still carries unrelated packet fields ({length} bytes).");
        var bytes = new byte[length];
        await ssl.ReadExactlyAsync(bytes, timeout.Token);
        var wheel = JsonSerializer.Deserialize<Packet>(bytes, WireJson.Options)!;
        Check(wheel.Type == "wheel" && wheel.X == 65535 && wheel.Y == 0 && wheel.Dx == -120 && wheel.Dy == 240,
            "Compact input JSON changed coordinates or wheel direction for the existing Packet reader.");
    }
}
Console.WriteLine("PASS: compact input frames retain existing JSON protocol compatibility.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var accepted = Signal<PeerConnection>();
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    using var controller = await PeerConnection.ConnectAsync(host.PairingCode("127.0.0.1"));
    using var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var closed = Signal<string?>();
    controller.Closed += reason => closed.TrySetResult(reason);
    for (int i = 0; i < 128; i++)
        Check(controller.TrySend(new Packet { Type = "key", Key = 0x41, Down = i % 2 == 0 }), "Critical queue capacity changed unexpectedly.");
    using var cancel = new CancellationTokenSource();
    var waiting = controller.SendAsync(new Packet { Type = "file-chunk", Data = [1, 2, 3] }, cancel.Token);
    Check(!waiting.IsCompleted, "A full bulk queue did not apply backpressure.");
    cancel.Cancel();
    await ThrowsAsync(async () => await waiting.WaitAsync(TimeSpan.FromSeconds(2)), "Cancelling a bulk capacity wait did not unblock it.");
    Check(!controller.IsClosed, "Cancelling a queued bulk producer disconnected the session.");
    Check(!controller.TrySend(new Packet { Type = "key", Key = 0x41, Down = false }), "A critical queue overflow was silently accepted.");
    Check(await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)) is { Length: > 0 }, "Critical overflow did not close with an actionable reason.");
}
Console.WriteLine("PASS: bulk backpressure cancellation and safe critical-overflow shutdown remain bounded.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var closed = Signal<string?>();
    host.Connected += peer => { peer.Closed += reason => closed.TrySetResult(reason); peer.Start(); };
    host.Start();
    var (tcp, ssl) = await RawConnectAsync(host.PairingCode("127.0.0.1"));
    using (tcp) using (ssl)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, PeerConnection.MaximumPacketBytes + 1);
        await ssl.WriteAsync(header);
        Check(await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)) is not null, "Oversized frame did not disconnect.");
    }
}
Console.WriteLine("PASS: oversized wire frame disconnects before allocation.");

foreach (bool truncate in new[] { false, true })
{
    using var host = new HostServer(IPAddress.Loopback, 0);
    var closed = Signal<string?>();
    host.Connected += peer => { peer.Closed += reason => closed.TrySetResult(reason); peer.Start(); };
    host.Start();
    var (tcp, ssl) = await RawConnectAsync(host.PairingCode("127.0.0.1"));
    using (tcp) using (ssl)
    {
        byte[] payload = System.Text.Encoding.UTF8.GetBytes(truncate ? "{}" : "{\"type\":null}");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, truncate ? 1024 : payload.Length);
        await ssl.WriteAsync(header);
        await ssl.WriteAsync(payload);
        if (truncate) tcp.Dispose();
        Check(await closed.Task.WaitAsync(TimeSpan.FromSeconds(2)) is not null,
            truncate ? "Truncated payload did not disconnect." : "Malformed packet JSON did not disconnect.");
    }
}
Console.WriteLine("PASS: malformed packet JSON and truncated payload disconnect cleanly.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var closed = Signal<string?>();
    host.Connected += peer => { peer.Closed += reason => closed.TrySetResult(reason); peer.Start(); };
    host.Start();
    var (tcp, ssl) = await RawConnectAsync(host.PairingCode("127.0.0.1"));
    using (tcp) using (ssl)
    {
        var elapsed = Stopwatch.StartNew();
        Check(await closed.Task.WaitAsync(TimeSpan.FromSeconds(4.5)) is not null, "Silent peer did not time out.");
        Check(elapsed.Elapsed < TimeSpan.FromSeconds(4.25), "Silent peer timeout exceeded four seconds plus scheduler tolerance.");
    }
}
Console.WriteLine("PASS: a silent authenticated peer closes within four seconds.");

using (var host = new HostServer(IPAddress.Loopback, 0))
{
    var accepted = Signal<PeerConnection>();
    host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
    host.Start();
    using var unauthenticated = new TcpClient(AddressFamily.InterNetwork);
    await unauthenticated.ConnectAsync(IPAddress.Loopback, host.Port);
    await Task.Delay(TimeSpan.FromSeconds(5.4));
    Check(!accepted.Task.IsCompleted, "An unauthenticated TCP socket was published.");
    using var valid = await PeerConnection.ConnectAsync(host.PairingCode("127.0.0.1"));
    using var remote = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(2));
    valid.Start();
}
Console.WriteLine("PASS: an idle unauthenticated socket times out and the listener accepts the next valid controller.");
