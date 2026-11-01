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
    var freshWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var freshWriteCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
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
    using var session = new ScreenStreamingSession(host, Capture, _ =>
    {
        Interlocked.Increment(ref writes);
        freshWriteEntered.TrySetResult();
        return freshWriteCompleted.Task;
    });
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
    await freshWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    Check(captures == 2 && writes == 1 && maximumActive == 1,
        "Rapid restarts created concurrent captures/writes or revived stale generations.");
    freshWriteCompleted.SetResult();
    await Task.Delay(230);
    Check(writes == 1, "A stopped generation continued writing frames.");
}

// Stream cancellation cannot shorten the actual TLS write's lifetime: otherwise
// each OFF/ON could enqueue another frame behind that still-pending write.
{
    int captures = 0, writes = 0;
    var firstWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondWriteEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondWriteCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var session = new ScreenStreamingSession(host, _ => { Interlocked.Increment(ref captures); return Task.FromResult(frame); },
        _ =>
        {
            if (Interlocked.Increment(ref writes) == 1) return firstWrite.Task;
            secondWriteEntered.TrySetResult();
            return secondWriteCompleted.Task;
        });
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
    await secondWriteEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    Check(captures == 2 && writes == 2, "The latest generation did not resume after the actual write completed.");
    secondWriteCompleted.SetResult();
    await Task.Delay(230);
    Check(captures == 2 && writes == 2, "An old restarted writer survived its generation's stop.");
}

// The old generation holds the shared slot for 500 ms of fake time. After it
// returns, the fresh generation must still wait its full 200 ms between frames;
// time spent waiting for that slot must not cause a catch-up burst.
{
    int captures = 0, writes = 0;
    var time = new ManualScreenTime();
    var oldCapture = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
    var firstFreshWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondFreshWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var finishSecondWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using var session = new ScreenStreamingSession(host,
        _ => Interlocked.Increment(ref captures) == 1 ? oldCapture.Task : Task.FromResult(frame),
        _ =>
        {
            if (Interlocked.Increment(ref writes) == 1) { firstFreshWrite.TrySetResult(); return Task.CompletedTask; }
            secondFreshWrite.TrySetResult(); return finishSecondWrite.Task;
        }, time);
    session.SetPermission(true);
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    await Until(() => captures == 1, "The cadence regression did not enter its old pending capture.");
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    session.Handle(new Packet { Type = "screen-request", Allow = true });
    // The queue diagnostic is a synchronization barrier: the new generation is
    // actually blocked by the old capture before its fake clock advances.
    await Until(() => session.WaitingCaptureCount == 1, "The fresh cadence generation did not wait behind the old capture.");
    time.Advance(TimeSpan.FromMilliseconds(500));
    oldCapture.SetResult(frame);
    await firstFreshWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await Until(() => time.PendingTimers == 1 || secondFreshWrite.Task.IsCompleted,
        "The fresh frame did not reach its post-write cadence boundary.");
    Check(!secondFreshWrite.Task.IsCompleted && time.PendingTimers == 1 && writes == 1,
        "Waiting behind an old capture caused an immediate second-frame burst.");
    time.Advance(TimeSpan.FromMilliseconds(199));
    Check(!secondFreshWrite.Task.IsCompleted && captures == 2 && writes == 1,
        "A new frame started before the full 200 ms cadence elapsed.");
    time.Advance(TimeSpan.FromMilliseconds(1));
    await secondFreshWrite.Task.WaitAsync(TimeSpan.FromSeconds(5));
    session.Handle(new Packet { Type = "screen-request", Allow = false });
    Check(captures == 3 && writes == 2, "The fake-clock cadence did not release exactly one fresh frame at 200 ms.");
    finishSecondWrite.SetResult();
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

sealed class ManualScreenTime : TimeProvider
{
    readonly object _gate = new();
    readonly List<ScreenTimer> _timers = [];
    long _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() { lock (_gate) return _ticks; }
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public int PendingTimers { get { lock (_gate) return _timers.Count(timer => !timer.Disposed && timer.Due != long.MaxValue); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            var timer = new ScreenTimer(this, callback, state);
            _timers.Add(timer);
            timer.Change(dueTime, period);
            return timer;
        }
    }
    public void Advance(TimeSpan elapsed)
    {
        List<ScreenTimer> due;
        lock (_gate)
        {
            _ticks += elapsed.Ticks;
            due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToList();
            foreach (var timer in due) timer.Due = timer.Period > 0 ? _ticks + timer.Period : long.MaxValue;
        }
        foreach (var timer in due) timer.Callback(timer.State);
    }
    sealed class ScreenTimer(ManualScreenTime owner, TimerCallback callback, object? state) : ITimer
    {
        public readonly TimerCallback Callback = callback;
        public readonly object? State = state;
        public long Due = long.MaxValue, Period;
        public bool Disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                if (Disposed) return false;
                Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                Period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
                return true;
            }
        }
        public void Dispose() { lock (owner._gate) { Disposed = true; Due = long.MaxValue; } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
