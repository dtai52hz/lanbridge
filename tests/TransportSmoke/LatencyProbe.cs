using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using LanBridge;

internal static class LatencyProbe
{
    public static async Task RunAsync()
    {
        using var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = Signal<PeerConnection>();
        host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
        host.Start();
        using var controller = await PeerConnection.ConnectAsync(host.PairingCode("127.0.0.1"));
        using var receiver = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        controller.Start();
        var pending = new ConcurrentDictionary<int, (long Started, TaskCompletionSource<double> Done)>();
        var bulkPending = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
        receiver.Received += packet =>
        {
            if (packet.Type == "key" && pending.TryRemove(packet.X, out var item))
                item.Done.TrySetResult(Stopwatch.GetElapsedTime(item.Started).TotalMilliseconds);
            if (packet.Type == "latency-bulk")
                receiver.TrySend(new Packet { Type = "latency-bulk-ack", X = packet.X });
        };
        controller.Received += packet =>
        {
            if (packet.Type == "latency-bulk-ack" && bulkPending.TryRemove(packet.X, out var done))
                done.TrySetResult(true);
        };
        int sequence = 0;
        async Task<double> ProbeAsync()
        {
            int id = Interlocked.Increment(ref sequence);
            var done = Signal<double>();
            pending[id] = (Stopwatch.GetTimestamp(), done);
            if (!controller.TrySend(new Packet { Type = "key", X = id, Key = 0x41, Scan = 0x1E, Down = id % 2 == 0 }))
                throw new InvalidOperationException("Latency probe could not enqueue a critical input packet.");
            return await done.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        for (int i = 0; i < 32; i++) await ProbeAsync();
        foreach (var mode in new[] { "isolated", "moves", "moves+48KiB-bulk-ACK" })
        {
            using var stop = new CancellationTokenSource();
            var moving = mode == "isolated" ? Task.CompletedTask : Task.Run(async () =>
            {
                try
                {
                    int x = 0;
                    using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1));
                    while (await timer.WaitForNextTickAsync(stop.Token))
                        for (int sample = 0; sample < 8; sample++)
                            if (!controller.TrySend(new Packet { Type = "move", X = x++ % 65536, Y = 30000 }))
                                throw new InvalidOperationException("Latency probe congested the motion queue.");
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            long bulkBytes = 0;
            var bulk = mode != "moves+48KiB-bulk-ACK" ? Task.CompletedTask : Task.Run(async () =>
            {
                var data = new byte[48 * 1024];
                int id = 0;
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        var done = Signal<bool>();
                        bulkPending[++id] = done;
                        await controller.SendAsync(new Packet { Type = "latency-bulk", X = id, Data = data }, stop.Token);
                        await done.Task.WaitAsync(stop.Token);
                        Interlocked.Add(ref bulkBytes, data.Length);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            var samples = new double[200];
            using (var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(4)))
                for (int i = 0; i < samples.Length; i++)
                {
                    await timer.WaitForNextTickAsync();
                    samples[i] = await ProbeAsync();
                }
            stop.Cancel();
            await Task.WhenAll(moving, bulk).WaitAsync(TimeSpan.FromSeconds(2));
            Array.Sort(samples);
            double p50 = samples[(int)(samples.Length * .50) - 1];
            double p95 = samples[(int)(samples.Length * .95) - 1];
            double p99 = samples[(int)(samples.Length * .99) - 1];
            Console.WriteLine($"LATENCY real TLS loopback {mode}: p50={p50:F3} ms, p95={p95:F3} ms, p99={p99:F3} ms, max={samples[^1]:F3} ms, bulk={bulkBytes / 1024} KiB.");
            if (p99 > 250 || controller.IsClosed || receiver.IsClosed)
                throw new InvalidOperationException($"Input latency regressed or disconnected in {mode}: p99={p99:F1} ms.");
        }
    }

    // Audit-only: a transparent TCP relay limits downstream delivery after the
    // real pinned TLS handshake. It illustrates TCP head-of-line blocking under
    // a slow LAN; it does not run hooks, touch the desktop, or change OS settings.
    public static async Task RunShapedAsync()
    {
        foreach (bool acknowledge in new[] { true, false })
        {
            using var host = new HostServer(IPAddress.Loopback, 0);
            var accepted = Signal<PeerConnection>();
            host.Connected += peer => { accepted.TrySetResult(peer); peer.Start(); };
            host.Start();
            await using var relay = new LimitedRelay(host.Port);
            string code = (PairingInfo.Parse(host.PairingCode("127.0.0.1")) with { Port = relay.Port }).ToString();
            using var controller = await PeerConnection.ConnectAsync(code);
            using var receiver = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var delivered = Signal<long>();
            var acknowledgements = new ConcurrentDictionary<int, TaskCompletionSource<bool>>();
            receiver.Received += packet =>
            {
                if (packet.Type == "key") delivered.TrySetResult(Stopwatch.GetTimestamp());
                if (packet.Type == "latency-bulk" && acknowledge)
                    receiver.TrySend(new Packet { Type = "latency-bulk-ack", X = packet.X });
            };
            controller.Received += packet =>
            {
                if (packet.Type == "latency-bulk-ack" && acknowledgements.TryRemove(packet.X, out var done))
                    done.TrySetResult(true);
            };
            controller.Start();
            relay.Limit = true;
            using var stop = new CancellationTokenSource();
            var bulk = Task.Run(async () =>
            {
                int id = 0;
                var data = new byte[48 * 1024];
                try
                {
                    for (int i = 0; i < 24; i++)
                    {
                        var done = Signal<bool>();
                        acknowledgements[++id] = done;
                        await controller.SendAsync(new Packet { Type = "latency-bulk", X = id, Data = data }, stop.Token);
                        if (acknowledge) await done.Task.WaitAsync(stop.Token);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            });
            await Task.Delay(60);
            long started = Stopwatch.GetTimestamp();
            if (!controller.TrySend(new Packet { Type = "key", Key = 0x41, Down = true }))
                throw new InvalidOperationException("Shaped probe failed to enqueue key.");
            long ended = await delivered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Console.WriteLine($"AUDIT shaped TLS 16KiB/8ms, bulk ACK={acknowledge}: key delivery {Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds:F1} ms.");
            stop.Cancel();
            await bulk.WaitAsync(TimeSpan.FromSeconds(2));
        }
    }

    private sealed class LimitedRelay : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _bridge;
        public bool Limit;
        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public LimitedRelay(int targetPort)
        {
            _listener.Start();
            _bridge = BridgeAsync(targetPort);
        }

        private async Task BridgeAsync(int targetPort)
        {
            try
            {
                using var incoming = await _listener.AcceptTcpClientAsync(_stop.Token);
                incoming.NoDelay = true;
                using var outgoing = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                await outgoing.ConnectAsync(IPAddress.Loopback, targetPort, _stop.Token);
                await Task.WhenAll(CopyAsync(incoming.GetStream(), outgoing.GetStream(), true),
                    CopyAsync(outgoing.GetStream(), incoming.GetStream(), false));
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (IOException) { }
            catch (SocketException) { }
        }

        private async Task CopyAsync(Stream from, Stream to, bool limited)
        {
            var bytes = new byte[16 * 1024];
            int count;
            while ((count = await from.ReadAsync(bytes, _stop.Token)) > 0)
            {
                if (limited && Volatile.Read(ref Limit)) await Task.Delay(8, _stop.Token);
                await to.WriteAsync(bytes.AsMemory(0, count), _stop.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _bridge;
            _stop.Dispose();
        }
    }

    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
