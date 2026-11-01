using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using LanBridge;

// Synthetic JPEG markers/fake capture and real authenticated loopback transport.
// No input hooks, screen reads, clipboard, audio or physical input are used.
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
async Task Until(Func<bool> predicate, string message)
{
    var timer = Stopwatch.StartNew();
    while (!predicate())
    {
        if (timer.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException(message);
        await Task.Delay(5);
    }
    Check(true, message);
}
var frame = new ScreenFrame([0xff, 0xd8, 0xff, 0xd9], 32, 24);
using var server = new HostServer(IPAddress.Loopback, 0);
var accepted = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
server.Connected += peer => accepted.TrySetResult(peer);
server.Start();
using var controller = await PeerConnection.ConnectAsync(server.PairingCode("127.0.0.1"));
using var host = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
var received = new ConcurrentQueue<Packet>();
controller.Received += received.Enqueue;
host.Start(); controller.Start();

// A synchronously failing capture used to race ON behind OFF and leave the host
// falsely marked as sharing. Repeat across fresh instances to exercise scheduling.
for (int repeat = 0; repeat < 24; repeat++)
{
    var states = new ConcurrentQueue<bool>();
    int errors = 0;
    int packetStart = received.Count;
    using var session = new ScreenStreamingSession(host, _ => throw new InvalidOperationException("synthetic failure"));
    session.StateChanged += states.Enqueue;
    session.Error += _ => Interlocked.Increment(ref errors);
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Until(() => errors == 1 && states.Count == 2, "Synchronous capture failure did not settle.");
    Check(states.SequenceEqual(new[] { true, false }), "Capture failure reordered ON after OFF in the host state.");
    await Until(() => received.Skip(packetStart).Count(packet => packet.Type == "screen-state") == 2,
        "Capture failure state packets did not traverse TLS.");
    Check(received.Skip(packetStart).Where(packet => packet.Type == "screen-state").Select(packet => packet.Allow)
        .SequenceEqual(new[] { true, false }), "Capture failure reordered its network ON/OFF state.");
}

// A native capture may ignore cancellation. Rapid OFF/ON transitions must leave
// only one capture active and discard that old generation's completed image.
{
    int captures = 0, active = 0, maximumActive = 0, writes = 0;
    var firstCapture = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    async Task<ScreenFrame> Capture(CancellationToken token)
    {
        int running = Interlocked.Increment(ref active);
        int seen;
        do { seen = Volatile.Read(ref maximumActive); }
        while (running > seen && Interlocked.CompareExchange(ref maximumActive, running, seen) != seen);
        int number = Interlocked.Increment(ref captures);
        try { return number == 1 ? await firstCapture.Task : frame; }
        finally { Interlocked.Decrement(ref active); }
    }
    using var session = new ScreenStreamingSession(host, Capture, _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; });
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Until(() => captures == 1, "The pending native capture did not start.");
    for (int repeat = 0; repeat < 32; repeat++)
    {
        session.Handle(new Packet { Type = "screen-request", Allow = false });
        session.Handle(new Packet { Type = "screen-request", Allow = true });
    }
    await Task.Delay(80);
    Check(captures == 1 && active == 1 && writes == 0,
        "A restarted stream overlapped or transmitted an old noncancellable native capture.");
    firstCapture.SetResult(frame);
    await Until(() => writes == 1, "The fresh stream did not continue after the old native capture returned.");
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    Check(captures == 2 && maximumActive == 1, "Rapid restarts created concurrent captures or revived stale generations.");
    await Task.Delay(230);
    Check(writes == 1, "A stopped generation continued writing frames.");
}

// Stream cancellation cannot shorten the actual TLS write's lifetime: otherwise
// each OFF/ON could enqueue another frame behind that still-pending write.
{
    int captures = 0, writes = 0;
    var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var session = new ScreenStreamingSession(host, _ => { Interlocked.Increment(ref captures); return Task.FromResult(frame); },
        _ => Interlocked.Increment(ref writes) == 1 ? firstWrite.Task : Task.CompletedTask);
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Until(() => writes == 1, "The fake actual TLS write did not begin.");
    for (int repeat = 0; repeat < 16; repeat++)
    {
        session.Handle(new Packet { Type = "screen-request", Allow = false });
        session.Handle(new Packet { Type = "screen-request", Allow = true });
    }
    await Task.Delay(80);
    Check(captures == 1 && writes == 1, "Cancellation released a still-pending actual write and allowed queued screen backlog.");
    firstWrite.SetResult();
    await Until(() => writes == 2, "The latest generation did not resume after the real write completed.");
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    await Task.Delay(230);
    Check(captures == 2 && writes == 2, "An old restarted writer survived its generation's stop.");
}

// Disposal is nonblocking with a stuck native provider, is safe when called
// repeatedly/concurrently, and cannot dispose the owner's authenticated peer.
{
    int captures = 0, writes = 0;
    var pending = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    var states = new ConcurrentQueue<bool>();
    var session = new ScreenStreamingSession(host, _ => { Interlocked.Increment(ref captures); return pending.Task; },
        _ => { Interlocked.Increment(ref writes); return Task.CompletedTask; });
    session.StateChanged += states.Enqueue;
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Until(() => captures == 1, "Disposal test did not enter its pending native capture.");
    var timer = Stopwatch.StartNew();
    await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(session.Dispose)));
    Check(timer.Elapsed < TimeSpan.FromMilliseconds(500), "Dispose blocked on a native capture.");
    pending.SetResult(frame);
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Task.Delay(100);
    Check(states.SequenceEqual(new[] { true, false }) && captures == 1 && writes == 0,
        "A disposed session emitted new active state, capture or stale frame.");
    Check(!host.IsClosed && !controller.IsClosed, "Disposing screen viewing closed the shared input/TLS connection.");
    await host.SendAsync(new Packet { Type = "chat", Text = "after-screen-dispose" });
    await Until(() => received.Any(packet => packet.Text == "after-screen-dispose"), "The shared TLS peer stopped after disposing screen viewing.");
}
Console.WriteLine($"Screen streaming smoke passed: {assertions} assertions; deterministic failure/restart/write/dispose races, real loopback TLS, no native capture/input.");
