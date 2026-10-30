using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json;
using LanBridge;

internal static class HeartbeatLatencyChecks
{
    internal static async Task RunAsync(Func<string, Task<(TcpClient Tcp, SslStream Stream)>> rawConnect)
    {
        await CheckTrackedPongsAsync(rawConnect);
        await CheckLegacyPeerAsync(rawConnect);
        await CheckSlowRelayAsync();
    }

    private static async Task CheckTrackedPongsAsync(Func<string, Task<(TcpClient Tcp, SslStream Stream)>> rawConnect)
    {
        using var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = Signal<PeerConnection>();
        host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
        host.Start();
        var (tcp, stream) = await rawConnect(host.PairingCode("127.0.0.1"));
        using (tcp) using (stream)
        using (var peer = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3)))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            Check(peer.Latency is null, "A connection reported RTT before receiving a tracked pong.");
            var observed = Signal<ConnectionLatency>();
            peer.LatencyUpdated += _ => throw new InvalidOperationException("A diagnostic subscriber failed.");
            peer.LatencyUpdated += sample => observed.TrySetResult(sample);
            var fences = new Dictionary<string, TaskCompletionSource<bool>>();
            peer.Received += packet =>
            {
                if (packet.Type == "latency-fence" && packet.Id is { } id)
                    lock (fences) if (fences.TryGetValue(id, out var done)) done.TrySetResult(true);
            };
            async Task FenceAsync()
            {
                string id = Guid.NewGuid().ToString("N");
                var done = Signal<bool>();
                lock (fences) fences[id] = done;
                await WriteAsync(stream, new Packet { Type = "latency-fence", Id = id }, timeout.Token);
                await done.Task.WaitAsync(timeout.Token);
            }
            var ping = await ReadAsync(stream, timeout.Token);
            Check(ping.Type == "$ping" && ping.Id is { Length: 32 } && ping.Length > 0,
                "A heartbeat must include a unique opaque identifier and local monotonic timestamp.");
            await WriteAsync(stream, new Packet { Type = "$pong" }, timeout.Token);
            await WriteAsync(stream, new Packet { Type = "$pong", Id = new string('0', 32), Length = ping.Length }, timeout.Token);
            await WriteAsync(stream, new Packet { Type = "$pong", Id = ping.Id, Length = ping.Length + 1 }, timeout.Token);
            await FenceAsync();
            Check(peer.Latency is null, "Legacy, untracked or mismatched pongs must not fabricate RTT samples.");
            await WriteAsync(stream, new Packet { Type = "$pong", Id = ping.Id, Length = ping.Length }, timeout.Token);
            var sample = await observed.Task.WaitAsync(timeout.Token);
            Check(sample.Samples == 1 && sample.LastMilliseconds >= 0 && sample.LastMilliseconds < 2000
                && sample.SmoothedMilliseconds == sample.LastMilliseconds
                && sample.MinimumMilliseconds == sample.LastMilliseconds
                && sample.MaximumMilliseconds == sample.LastMilliseconds,
                "The first matched pong produced an invalid RTT snapshot.");
            Check(ReferenceEquals(sample, peer.Latency) && !peer.IsClosed,
                "RTT snapshot publication or diagnostic subscriber isolation failed.");
            await WriteAsync(stream, new Packet { Type = "$pong", Id = ping.Id, Length = ping.Length }, timeout.Token);
            await FenceAsync();
            Check(peer.Latency!.Samples == 1, "A replayed pong was counted twice.");
            // A peer's timestamp is opaque: echo it; never use its clock for local RTT.
            await WriteAsync(stream, new Packet { Type = "$ping", Id = "remote-clock", Length = 1234567 }, timeout.Token);
            Packet pong;
            do { pong = await ReadAsync(stream, timeout.Token); } while (pong.Type != "$pong");
            Check(pong.Id == "remote-clock" && pong.Length == 1234567,
                "The ping responder did not echo the originating peer's identifier/timestamp.");
        }
        Console.WriteLine("PASS: tracked heartbeat RTT snapshots, nonce/timestamp matching, replay rejection, opaque-clock echo and observer isolation.");
    }

    private static async Task CheckLegacyPeerAsync(Func<string, Task<(TcpClient Tcp, SslStream Stream)>> rawConnect)
    {
        using var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = Signal<PeerConnection>();
        host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
        host.Start();
        var (tcp, stream) = await rawConnect(host.PairingCode("127.0.0.1"));
        using (tcp) using (stream)
        using (var peer = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3)))
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6)))
        {
            await WriteAsync(stream, new Packet { Type = "$ping" }, timeout.Token);
            var elapsed = Stopwatch.StartNew();
            bool plainPong = false;
            int probes = 0;
            while (elapsed.Elapsed < TimeSpan.FromSeconds(4.1))
            {
                var packet = await ReadAsync(stream, timeout.Token);
                if (packet.Type == "$pong")
                    plainPong |= packet.Id is null && packet.Length == 0;
                if (packet.Type == "$ping")
                {
                    probes++;
                    await WriteAsync(stream, new Packet { Type = "$pong" }, timeout.Token);
                }
            }
            Check(plainPong && probes >= 6 && peer.Latency is null && !peer.IsClosed,
                "A legacy peer returning plain pongs must remain healthy without invented RTT values.");
        }
        Console.WriteLine("PASS: a legacy plain-ping/plain-pong peer remains connected beyond the silence threshold; RTT stays unavailable.");
    }

    private static async Task CheckSlowRelayAsync()
    {
        using var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = Signal<PeerConnection>();
        host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
        host.Start();
        await using var relay = new LatencyProbe.LimitedRelay(host.Port, delayMilliseconds: 300, bothDirections: true);
        string code = (PairingInfo.Parse(host.PairingCode("127.0.0.1")) with { Port = relay.Port }).ToString();
        using var controller = await PeerConnection.ConnectAsync(code);
        using var receiver = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var baseline = Signal<ConnectionLatency>();
        var slow = Signal<ConnectionLatency>();
        var recovered = Signal<ConnectionLatency>();
        int phase = 0, slowCount = 0;
        controller.LatencyUpdated += sample =>
        {
            if (Volatile.Read(ref phase) == 0) baseline.TrySetResult(sample);
            if (Volatile.Read(ref phase) == 1 && sample.LastMilliseconds >= 500
                && Interlocked.Increment(ref slowCount) >= 2) slow.TrySetResult(sample);
            if (Volatile.Read(ref phase) == 2 && sample.LastMilliseconds < 300) recovered.TrySetResult(sample);
        };
        controller.Start();
        var normal = await baseline.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Volatile.Write(ref phase, 1);
        Volatile.Write(ref relay.Limit, true);
        var delayed = await slow.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(delayed.Samples >= normal.Samples + 2 && delayed.MaximumMilliseconds >= 500
            && delayed.MinimumMilliseconds <= normal.LastMilliseconds
            && delayed.SmoothedMilliseconds > normal.SmoothedMilliseconds,
            "Slow heartbeat RTTs above the probe cadence were lost or did not update aggregates.");
        // Snapshot reads remain atomic and internally consistent outside the reader.
        await Task.Run(() =>
        {
            long previousSamples = 0;
            for (int i = 0; i < 10000; i++)
            {
                var snapshot = controller.Latency!;
                Check(snapshot.Samples >= previousSamples && snapshot.MinimumMilliseconds <= snapshot.LastMilliseconds
                    && snapshot.LastMilliseconds <= snapshot.MaximumMilliseconds
                    && snapshot.MinimumMilliseconds <= snapshot.SmoothedMilliseconds
                    && snapshot.SmoothedMilliseconds <= snapshot.MaximumMilliseconds,
                    "Concurrent readers observed a partial or inconsistent RTT snapshot.");
                previousSamples = snapshot.Samples;
            }
        });
        Volatile.Write(ref phase, 2);
        Volatile.Write(ref relay.Limit, false);
        var fast = await recovered.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Check(fast.MaximumMilliseconds >= delayed.MaximumMilliseconds && fast.SmoothedMilliseconds > fast.LastMilliseconds
            && !controller.IsClosed && !receiver.IsClosed,
            "Heartbeat RTT recovery lost the worst-case history or disconnected a responsive delayed peer.");
        Console.WriteLine($"PASS: real TLS heartbeat RTT baseline {normal.LastMilliseconds:F2} ms, delayed {delayed.LastMilliseconds:F1} ms (>500 ms cadence), recovered {fast.LastMilliseconds:F2} ms; thread-safe EWMA/min/max history.");
    }

    private static async Task<Packet> ReadAsync(SslStream stream, CancellationToken token)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, token);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        Check(length is > 0 and <= PeerConnection.MaximumPacketBytes, "Invalid frame in heartbeat fixture.");
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, token);
        return JsonSerializer.Deserialize<Packet>(payload, WireJson.Options)!;
    }

    private static async Task WriteAsync(SslStream stream, Packet packet, CancellationToken token)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(packet, WireJson.Options);
        var frame = new byte[payload.Length + 4];
        BinaryPrimitives.WriteInt32BigEndian(frame, payload.Length);
        payload.CopyTo(frame, 4);
        await stream.WriteAsync(frame, token);
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
