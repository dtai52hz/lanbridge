using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class NativeWorkerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly Packet CtrlDown = new() { Type = "key", Key = 0xA2, Scan = 29, Down = true };
    private static readonly Packet CtrlUp = CtrlDown with { Down = false };

    internal static void Run(Action<bool, string> check)
    {
        CheckAtomicNativeBatchAndFreeOwner(check);
        foreach (bool cancelToken in new[] { false, true }) CheckRevokeDuringNativeCall(check, cancelToken);
        CheckCancellationImmediatelyAfterAdmission(check);
        CheckSkippedPriorRelease(check);
        CheckRoleFence(check);
        foreach (int acceptedPrefix in new[] { 2, 6, 9 }) CheckPartialNativeFailure(check, acceptedPrefix);
        CheckWireBoundaries(check);
        CheckDisposeWithNativeCall(check);
    }

    private static void CheckAtomicNativeBatchAndFreeOwner(Action<bool, string> check)
    {
        using var router = Receiver();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        int owner = 0, nativeThread = 0, calls = 0;
        Frame[] actual = [];
        var applied = new ConcurrentQueue<Packet>();
        router.PacketAppliedForTesting = applied.Enqueue;
        router.InvokeForTestingAsync(() => owner = Environment.CurrentManagedThreadId).GetAwaiter().GetResult();
        router.NativeInputBatchForTesting = inputs => { nativeThread = Environment.CurrentManagedThreadId; actual = Frames(inputs); };
        router.NativeInputRequestedForTesting = _ => { Interlocked.Increment(ref calls); entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); };
        Packet[] batch = [new() { Type = "move", X = 10, Y = 20 }, new() { Type = "wheel", X = 11, Y = 21, Dx = 30, Dy = -120 },
            new() { Type = "button", X = 12, Y = 22, Button = "left", Down = true }, CtrlDown, CtrlUp,
            new() { Type = "text", Text = "ế" }, new() { Type = "button", X = 13, Y = 23, Button = "left", Down = false }];
        var pending = router.ApplyRemoteBatchAsync(batch);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The native worker did not start its combined receiver batch.");
        try
        {
            bool ownerResponsive = false;
            router.InvokeForTestingAsync(() => ownerResponsive = true).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(ownerResponsive && nativeThread != owner && !pending.IsCompleted && applied.IsEmpty,
                "Receiver SendInput must run off the hook owner and application completion must wait for the native call.");
        }
        finally { resume.Set(); }
        pending.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(calls == 1 && actual.Length == 12 && applied.SequenceEqual(batch), "One receiver batch must make one real native-array submission and preserve applied callbacks.");
        check(actual[0].X == 10 && actual[0].Y == 20 && actual[1].X == 11 && actual[2].Flags == 0x800 && actual[2].Data == -120 &&
            actual[3].Flags == 0x1000 && actual[3].Data == 30 && actual[4].X == 12 && actual[5].Flags == 2 &&
            actual[6].Scan == 29 && actual[6].Flags == 8 && actual[7].Flags == 10 && actual[8].Scan == 'ế' && actual[8].Flags == 4 &&
            actual[9].Flags == 6 && actual[10].X == 13 && actual[11].Flags == 4,
            "Native array batching changed pointer hit-test positions, wheel axes, Ctrl, Unicode or drag boundaries.");
        router.NativeInputRequestedForTesting = null;
        router.NativeInputBatchForTesting = null;
    }

    private static void CheckRevokeDuringNativeCall(Action<bool, string> check, bool cancelToken)
    {
        using var router = Receiver();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var state = new NativeState();
        int first = 1;
        router.NativeInputBatchForTesting = inputs =>
        {
            var frames = Frames(inputs);
            if (Interlocked.Exchange(ref first, 0) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); }
            state.Apply(frames);
            if (frames.Any(IsCtrlUp)) released.Set();
        };
        int applied = 0;
        router.PacketAppliedForTesting = _ => Interlocked.Increment(ref applied);
        var pending = router.ApplyRemoteAsync(CtrlDown, cancellation.Token);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The held native key-down did not start.");
        try
        {
            if (cancelToken) cancellation.Cancel(); else router.CanControl = false;
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(!router.IsRemote && HeldKeys(router) == 0 && (cancelToken || !router.CanControl),
                "Revoke/cancel must clear owner state immediately while native key-down is blocked.");
        }
        finally { resume.Set(); }
        check(IsCanceled(pending), "A revoked or canceled native application must never report successful completion.");
        check(released.Wait(TimeSpan.FromSeconds(2)) && state.IsEmpty && applied == 0,
            "Cleanup must follow an already admitted key-down on the native FIFO and suppress obsolete applied callbacks.");
        router.CanControl = true;
        router.ApplyRemoteAsync(new Packet { Type = "enter" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        router.ApplyRemoteAsync(new Packet { Type = "move", X = 321 }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(router.IsRemote && router.LastError is null, "A canceled input batch must not poison the next enabled control session.");
        router.NativeInputBatchForTesting = null;
    }

    private static void CheckCancellationImmediatelyAfterAdmission(Action<bool, string> check)
    {
        using var router = Receiver();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        using var canceledAtAdmission = new ManualResetEventSlim();
        int first = 1;
        router.NativeInputRequestedForTesting = _ => { if (Interlocked.Exchange(ref first, 0) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); } };
        router.NativeWorkQueuedForTesting = () =>
        {
            router.NativeWorkQueuedForTesting = null;
            // Test-only handshake: the fake worker never calls native input or needs the
            // owner. Force cancellation inside planning after admission has actually begun.
            if (!entered.Wait(TimeSpan.FromSeconds(2))) throw new InvalidOperationException("The admitted fake native call did not begin.");
            cancellation.Cancel();
            canceledAtAdmission.Set();
        };
        var canceled = router.ApplyRemoteAsync(CtrlDown, cancellation.Token);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "Admission-race native work did not start.");
        check(canceledAtAdmission.Wait(TimeSpan.FromSeconds(2)), "The admission callback did not finish canceling its input operation.");
        var next = router.ApplyRemoteAsync(new Packet { Type = "enter" });
        try
        {
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(!router.IsRemote && !next.IsCompleted && NativeOutstanding(router) == 2,
                "Cancellation between native admission and owner-await continuation must keep the serialized gate until cleanup is ordered.");
        }
        finally { resume.Set(); }
        check(IsCanceled(canceled), "Admission cancellation must release the public caller promptly.");
        next.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(router.IsRemote && HeldKeys(router) == 0, "A later session must enter only after the canceled batch and its release barrier.");
        router.NativeInputRequestedForTesting = null;
    }

    private static void CheckSkippedPriorRelease(Action<bool, string> check)
    {
        foreach (bool wireLeave in new[] { false, true })
        {
            using var router = Receiver();
            using var entered = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            using var released = new ManualResetEventSlim();
            var state = new NativeState();
            int hold = 0, newDowns = 0;
            router.NativeInputBatchForTesting = inputs =>
            {
                var frames = Frames(inputs);
                if (Interlocked.Exchange(ref hold, 0) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); }
                state.Apply(frames);
                if (frames.Any(IsCtrlUp)) released.Set();
                if (frames.Any(f => f.Type == 1 && f.Scan == 30 && (f.Flags & 2) == 0)) Interlocked.Increment(ref newDowns);
            };
            router.ApplyRemoteAsync(CtrlDown).GetAwaiter().GetResult();
            hold = 1;
            router.InvokeForTestingAsync(() => typeof(InputRouter).GetMethod("InjectAbsolute", Private)!.Invoke(router, [1, 2])).GetAwaiter().GetResult();
            check(entered.Wait(TimeSpan.FromSeconds(2)), "The prior native cleanup job did not pause the worker.");
            Packet[] packets = wireLeave ? [CtrlUp, new() { Type = "leave" }] : [CtrlUp,
                new() { Type = "key", Key = 65, Scan = 30, Down = true }, new() { Type = "key", Key = 65, Scan = 30, Down = false }];
            var pending = router.ApplyRemoteBatchAsync(packets);
            try
            {
                router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
                check(HeldKeys(router) == 0 && NativeOutstanding(router) == 2, "The stale-up regression requires an already planned up behind an earlier native job.");
                router.CanControl = false;
                router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
            }
            finally { resume.Set(); }
            check(IsCanceled(pending) && released.Wait(TimeSpan.FromSeconds(2)) && state.IsEmpty && newDowns == 0,
                "Skipping a revoked batch must retain its preexisting-key release, while discarding never-injected new downs.");
            router.NativeInputBatchForTesting = null;
        }
    }

    private static void CheckRoleFence(Action<bool, string> check)
    {
        using var router = Receiver();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var state = new NativeState();
        var calls = new ConcurrentQueue<Frame[]>();
        int first = 1;
        router.Send = _ => { };
        router.NativeInputBatchForTesting = inputs =>
        {
            var frames = Frames(inputs); calls.Enqueue(frames);
            if (Interlocked.Exchange(ref first, 0) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); }
            state.Apply(frames);
        };
        var old = router.ApplyRemoteAsync(CtrlDown);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The role-transition native work did not start.");
        var enter = typeof(InputRouter).GetMethod("EnterRemote", Private)!;
        void EnterA()
        {
            var bounds = (Rectangle)typeof(InputRouter).GetField("_bounds", Private)!.GetValue(router)!;
            enter.Invoke(router, [new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2)]);
        }
        try
        {
            router.InvokeForTestingAsync(() => { router.ConfigureController(true, ScreenEdge.Right); EnterA(); }).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(router.IsController && !router.IsRemote && HeldKeys(router) == 0 && calls.Count == 1,
                "A B-to-A role change must not synchronously warp or enter ahead of the old native cleanup fence.");
        }
        finally { resume.Set(); }
        check(IsCanceled(old), "The old receiver batch must become obsolete after the role change.");
        check(SpinWait.SpinUntil(() => NativeOutstanding(router) == 0, TimeSpan.FromSeconds(2)) && state.IsEmpty,
            "Old receiver key release must finish before the controller anchor can change.");
        router.InvokeForTestingAsync(EnterA).GetAwaiter().GetResult();
        check(router.IsRemote && calls.Count == 3 && calls.ToArray()[1].Any(IsCtrlUp) && calls.ToArray()[2].Single().Flags == 0xC001,
            "After the old worker drains, A must keep its one synchronous entry warp and exact FIFO release order.");
        router.NativeInputBatchForTesting = null;
        router.Send = null;
    }

    private static void CheckPartialNativeFailure(Action<bool, string> check, int acceptedPrefix)
    {
        using var router = Receiver();
        var state = new NativeState();
        var recovery = new ConcurrentQueue<Frame[]>();
        int first = 1;
        router.NativeInputBatchForTesting = inputs =>
        {
            var frames = Frames(inputs);
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                state.Apply(frames.Take(acceptedPrefix));
                throw new IOException("Simulated partially accepted native array.");
            }
            recovery.Enqueue(frames); state.Apply(frames);
        };
        Packet[] batch = [CtrlDown, new() { Type = "key", Key = 65, Scan = 30, Down = true },
            new() { Type = "key", Key = 65, Scan = 30, Down = false }, CtrlUp,
            new() { Type = "button", Button = "left", Down = true }, new() { Type = "button", Button = "left", Down = false },
            new() { Type = "text", Text = "ế" }];
        bool failed = false;
        try { router.ApplyRemoteBatchAsync(batch).GetAwaiter().GetResult(); } catch (IOException) { failed = true; }
        var releases = recovery.SelectMany(f => f).ToArray();
        check(failed && !router.IsRemote && HeldKeys(router) == 0 && state.IsEmpty && router.LastError is not null,
            "A partially accepted batch must recover potential held inputs and publish owner failure before its task faults.");
        check(releases.Any(IsCtrlUp) && releases.Any(f => f.Type == 1 && f.Scan == 30 && (f.Flags & 2) != 0) &&
            releases.Any(f => f.Type == 0 && f.Flags == 4) && releases.Any(f => f.Type == 1 && f.Scan == 'ế' && f.Flags == 6),
            "Failure recovery must cover paired Ctrl, normal keys, mouse buttons and Unicode downs even when planned state ended balanced.");
        router.NativeInputBatchForTesting = null;
    }

    private static void CheckWireBoundaries(Action<bool, string> check)
    {
        using var router = Receiver();
        var state = new NativeState();
        int calls = 0;
        router.NativeInputBatchForTesting = inputs => { Interlocked.Increment(ref calls); state.Apply(Frames(inputs)); };
        router.ApplyRemoteBatchAsync([new() { Type = "leave" }, new() { Type = "enter" }, CtrlDown, new() { Type = "leave" }]).GetAwaiter().GetResult();
        check(calls == 1 && !router.IsRemote && HeldKeys(router) == 0 && state.IsEmpty,
            "Valid leave/enter/down/leave boundaries within one batch must stay in native-array order without becoming falsely stale.");
        router.NativeInputBatchForTesting = null;
    }

    private static void CheckDisposeWithNativeCall(Action<bool, string> check)
    {
        using var router = Receiver();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var state = new NativeState();
        int first = 1;
        router.NativeInputBatchForTesting = inputs =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1) { entered.Set(); resume.Wait(TimeSpan.FromSeconds(4)); }
            state.Apply(Frames(inputs));
        };
        var pending = router.ApplyRemoteAsync(CtrlDown);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The dispose regression's native call did not start.");
        var dispose = Task.Run(router.Dispose);
        try
        {
            check(SpinWait.SpinUntil(() => (bool)typeof(InputRouter).GetField("_disposed", Private)!.GetValue(router)!, TimeSpan.FromSeconds(1)),
                "Disposal must stop hooks without waiting on the blocked native call from its own owner.");
        }
        finally { resume.Set(); }
        dispose.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        check(IsCanceled(pending) && state.IsEmpty && !((Thread)typeof(InputRouter).GetField("_nativeWorker", Private)!.GetValue(router)!).IsAlive,
            "Disposal must drain its final native release barrier, stop the worker and reject obsolete success completion.");
        router.NativeInputBatchForTesting = null;
    }

    private static InputRouter Receiver()
    {
        var router = new InputRouter();
        router.StartForTesting(); router.CanControl = true;
        router.ApplyRemoteAsync(new Packet { Type = "enter" }).GetAwaiter().GetResult();
        return router;
    }
    private static int HeldKeys(InputRouter router) => ((IDictionary)typeof(InputRouter).GetField("_receivedKeys", Private)!.GetValue(router)!).Count;
    private static int NativeOutstanding(InputRouter router) => (int)typeof(InputRouter).GetField("_nativeOutstanding", Private)!.GetValue(router)!;
    private static bool IsCanceled(Task task)
    {
        try { task.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult(); return false; }
        catch (OperationCanceledException) { return true; }
    }
    private static bool IsCtrlUp(Frame frame) => frame.Type == 1 && frame.Scan == 29 && (frame.Flags & 2) != 0;
    private static Frame[] Frames(Array inputs) => inputs.Cast<object>().Select(input =>
    {
        object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
        uint type = (uint)Field(input, "Type");
        object data = Field(Field(input, "Union"), type == 0 ? "Mouse" : "Keyboard");
        return type == 0 ? new Frame(type, (uint)Field(data, "Flags"), (int)Field(data, "Dx"), (int)Field(data, "Dy"), unchecked((int)(uint)Field(data, "MouseData")), 0, 0)
            : new Frame(type, (uint)Field(data, "Flags"), 0, 0, 0, (ushort)Field(data, "Vk"), (ushort)Field(data, "Scan"));
    }).ToArray();
    private readonly record struct Frame(uint Type, uint Flags, int X, int Y, int Data, ushort Vk, ushort Scan);
    private sealed class NativeState
    {
        private readonly object _sync = new();
        private readonly HashSet<(ushort Vk, ushort Scan, uint Flags)> _keys = [];
        private bool _left;
        internal bool IsEmpty { get { lock (_sync) return _keys.Count == 0 && !_left; } }
        internal void Apply(IEnumerable<Frame> frames)
        {
            lock (_sync) foreach (var frame in frames)
            {
                if (frame.Type == 1)
                {
                    var key = (frame.Vk, frame.Scan, frame.Flags & ~2u);
                    if ((frame.Flags & 2) == 0) _keys.Add(key); else _keys.Remove(key);
                }
                else if (frame.Flags == 2) _left = true;
                else if (frame.Flags == 4) _left = false;
            }
        }
    }
}
