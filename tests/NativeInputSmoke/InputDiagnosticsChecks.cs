using System.Reflection;
using LanBridge;

internal static class InputDiagnosticsChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        CheckReceiverWaitAndNativeTime(check);
        CheckOutgoingWait(check);
        CheckNativeFailure(check);
        CheckNestedCountersAndNativeClockWrap(check);
        CheckOwnInjectedEventFilter(check);
    }

    private static void CheckOwnInjectedEventFilter(Action<bool, string> check)
    {
        foreach (uint injectedMask in new[] { 1u, 0x10u })
        {
            var metrics = new InputDiagnostics();
            metrics.RecordHookEventArrival(0, injectedMask, 0, 100, 125);
            var physical = metrics.Snapshot(false);
            check(physical.HookEventAge.Count == 1 && Math.Abs(physical.HookEventAge.LastMilliseconds - 25) < 0.001 &&
                physical.OwnInjectedEventAge.Count == 0,
                "A physical mouse/keyboard event belongs only to physical hook-arrival timing.");
            metrics.RecordHookEventArrival(injectedMask, injectedMask, InputRouter.InjectionTag, 100, 160);
            var own = metrics.Snapshot(false);
            check(own.OwnInjectedEventAge.Count == 1 && Math.Abs(own.OwnInjectedEventAge.LastMilliseconds - 60) < 0.001 &&
                own.OwnInjectedEventAge.SlowCount == 1 && own.HookEventAge.Count == 1,
                "Our tagged injected event must record Windows input-path age before the hook ignores it.");
            metrics.RecordHookEventArrival(injectedMask, injectedMask, 0x12345678, 100, 250);
            var foreign = metrics.Snapshot(false);
            check(foreign.OwnInjectedEventAge.Count == 1 && foreign.HookEventAge.Count == 1,
                "Third-party injected input must not be mistaken for our own input-path delay or physical input.");
            metrics.RecordHookEventArrival(0, injectedMask, InputRouter.InjectionTag, uint.MaxValue - 10, 5);
            var wrapped = metrics.Snapshot(false);
            check(wrapped.OwnInjectedEventAge.Count == 2 && Math.Abs(wrapped.OwnInjectedEventAge.LastMilliseconds - 16) < 0.001 &&
                wrapped.HookEventAge.Count == 1,
                "Our tag must remain recognizable without the injected flag, across native DWORD uptime wrap.");
            metrics.RecordHookEventArrival(injectedMask, injectedMask, InputRouter.InjectionTag, 20, 10);
            check(metrics.Snapshot(false).OwnInjectedEventAge.Count == 2,
                "An invalid/future native timestamp must not fabricate an own-injection input-path stall.");
        }
    }

    private static void CheckReceiverWaitAndNativeTime(Action<bool, string> check)
    {
        using var router = new InputRouter();
        var inert = router.GetDiagnosticsSnapshot();
        check(inert.DispatcherQueue.Count == 0 && inert.NativeInjection.Count == 0 && !inert.NativeInputEnabled &&
            inert.DispatcherQueue.LastSampleAgeMilliseconds == -1 && inert.NativeInjection.LastSampleAgeMilliseconds == -1,
            "Unused diagnostics must have no input samples or fabricated native work.");
        router.StartForTesting();
        router.CanControl = true;
        router.ApplyRemoteAsync(new Packet { Type = "enter" }).GetAwaiter().GetResult();
        check(router.GetDiagnosticsSnapshot().DispatcherQueue.Count == 1 && router.GetDiagnosticsSnapshot().NativeInjection.Count == 0,
            "A disabled native dispatcher should measure input handoff but not pretend to call SendInput.");

        using var ownerEntered = new ManualResetEventSlim();
        using var ownerResume = new ManualResetEventSlim();
        var ownerBarrier = router.InvokeForTestingAsync(() => { ownerEntered.Set(); ownerResume.Wait(TimeSpan.FromSeconds(4)); });
        check(ownerEntered.Wait(TimeSpan.FromSeconds(2)), "The diagnostic owner-wait test failed to pause the hook-free dispatcher.");
        var queued = router.ApplyRemoteBatchAsync([new() { Type = "move", X = 2 }, new() { Type = "wheel", X = 2, Dy = 120 }]);
        InputDiagnosticsSnapshot waiting;
        try
        {
            Thread.Sleep(120);
            waiting = Task.Run(router.GetDiagnosticsSnapshot).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(waiting.PendingDispatcherWork == 1 && waiting.DispatcherQueue.Count == 1 && !queued.IsCompleted &&
                waiting.NativeInjection.Count == 0 && !waiting.NativeInjectionInProgress,
                "A busy input owner must be visible as pending dispatch work, independent of native SendInput.");
        }
        finally { ownerResume.Set(); }
        ownerBarrier.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        queued.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        var afterQueue = router.GetDiagnosticsSnapshot();
        check(afterQueue.DispatcherQueue.Count == 2 && afterQueue.DispatcherQueue.LastMilliseconds >= 80 &&
            afterQueue.DispatcherQueue.MaxMilliseconds >= afterQueue.DispatcherQueue.LastMilliseconds &&
            afterQueue.DispatcherQueue.SlowCount >= 1 && afterQueue.PendingDispatcherWork == 0 && afterQueue.NativeInjection.Count == 0,
            "Dispatcher queue timings must include the blocked-owner delay without counting it as native injection time.");

        using var nativeEntered = new ManualResetEventSlim();
        using var nativeResume = new ManualResetEventSlim();
        int blockOnce = 1;
        router.NativeInputRequestedForTesting = _ =>
        {
            if (Interlocked.Exchange(ref blockOnce, 0) == 0) return;
            nativeEntered.Set(); nativeResume.Wait(TimeSpan.FromSeconds(4));
        };
        var native = router.ApplyRemoteAsync(new Packet { Type = "move", X = 3 });
        check(nativeEntered.Wait(TimeSpan.FromSeconds(2)), "The native-disabled diagnostic timing observer did not start.");
        var behindNative = router.ApplyRemoteAsync(new Packet { Type = "move", X = 4 });
        try
        {
            Thread.Sleep(120);
            bool hookOwnerResponsive = false;
            router.InvokeForTestingAsync(() => hookOwnerResponsive = true).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            var active = Task.Run(router.GetDiagnosticsSnapshot).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(active.NativeInjectionInProgress && active.NativeInjectionInProgressMilliseconds >= 80 &&
                active.NativeInputs == 1 && active.NativeInjection.Count == 0 && active.PendingDispatcherWork == 0 &&
                !active.NativeInputEnabled && hookOwnerResponsive,
                "A stalled native worker must be visible while the hook owner remains free and later input waits at the serialized native boundary.");
        }
        finally { nativeResume.Set(); }
        native.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        behindNative.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        router.NativeInputRequestedForTesting = null;
        var afterNative = router.GetDiagnosticsSnapshot();
        check(afterNative.NativeInjection.Count == 2 && afterNative.NativeInputs == 2 && afterNative.NativeFailures == 0 &&
            afterNative.NativeInjection.MaxMilliseconds >= 80 && afterNative.NativeInjection.SlowCount >= 1 &&
            afterNative.NativeInjection.AverageMilliseconds >= 40 && !afterNative.NativeInjectionInProgress &&
            afterNative.PendingDispatcherWork == 0 && afterNative.NativeInjection.LastSampleAgeMilliseconds >= 0,
            "Native timing completion must retain total/slow/max samples, clear active state and keep the queue separate.");

        long beforeCancel = afterNative.DispatcherQueue.Count;
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        bool wasCanceled = false;
        try { router.ApplyRemoteAsync(new Packet { Type = "move" }, canceled.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { wasCanceled = true; }
        check(wasCanceled && router.GetDiagnosticsSnapshot().DispatcherQueue.Count == beforeCancel &&
            router.GetDiagnosticsSnapshot().PendingDispatcherWork == 0,
            "Already-canceled input must not leak a pending diagnostic counter.");
        router.ApplyRemote(new Packet { Type = "move", X = 5 });
        router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
        check(router.GetDiagnosticsSnapshot().DispatcherQueue.Count == beforeCancel + 1,
            "Fire-and-forget remote application must measure the same input-owner queue stage.");
        router.Dispose();
        check(!router.GetDiagnosticsSnapshot().NativeInputEnabled && router.GetDiagnosticsSnapshot().PendingDispatcherWork == 0,
            "Disposed input diagnostics must remain readable without claiming native input is enabled.");
    }

    private static void CheckOutgoingWait(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var senderEntered = new ManualResetEventSlim();
        using var senderResume = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        router.Send = packet =>
        {
            if (packet.Type == "enter") { senderEntered.Set(); senderResume.Wait(TimeSpan.FromSeconds(4)); }
            if (packet.Type == "wheel") finished.Set();
        };
        var queue = typeof(InputRouter).GetMethod("Queue", Private)!;
        queue.Invoke(router, [new Packet { Type = "enter" }]);
        check(senderEntered.Wait(TimeSpan.FromSeconds(2)), "The source diagnostic sender did not start.");
        for (int i = 0; i < 1000; i++) queue.Invoke(router, [new Packet { Type = "move", X = i }]);
        queue.Invoke(router, [new Packet { Type = "wheel", X = 999, Dy = -120 }]);
        try
        {
            Thread.Sleep(120);
            var pending = Task.Run(router.GetDiagnosticsSnapshot).WaitAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
            check(pending.PendingOutgoingWork == 2 && pending.OutgoingQueue.Count == 1 &&
                pending.DispatcherQueue.Count == 0 && pending.NativeInjection.Count == 0,
                "Source output waiting must be visible independently of receiver dispatch/native timing, after movement coalescing.");
        }
        finally { senderResume.Set(); }
        check(finished.Wait(TimeSpan.FromSeconds(2)), "Source diagnostic output did not finish after the sender resumed.");
        var done = router.GetDiagnosticsSnapshot();
        check(done.OutgoingQueue.Count == 3 && done.OutgoingQueue.MaxMilliseconds >= 80 &&
            done.OutgoingQueue.LastMilliseconds >= 80 && done.OutgoingQueue.SlowCount >= 2 && done.PendingOutgoingWork == 0,
            "Output wait must measure each actually dequeued packet, rather than all superseded mouse reports.");
        router.Send = null;
    }

    private static void CheckNativeFailure(Action<bool, string> check)
    {
        using var router = new InputRouter();
        router.StartForTesting();
        router.CanControl = true;
        router.ApplyRemoteAsync(new Packet { Type = "enter" }).GetAwaiter().GetResult();
        router.NativeInputRequestedForTesting = _ => throw new IOException("Simulated native call failure without injecting input.");
        bool failed = false;
        try { router.ApplyRemoteAsync(new Packet { Type = "move" }).GetAwaiter().GetResult(); }
        catch (IOException) { failed = true; }
        router.NativeInputRequestedForTesting = null;
        var sample = router.GetDiagnosticsSnapshot();
        check(failed && !router.IsRemote && sample.NativeInjection.Count == 1 && sample.NativeFailures == 1 &&
            !sample.NativeInjectionInProgress && sample.NativeInputs == 1,
            "A native-call exception must close its timing span, increment failure count and release remote input.");
    }

    private static void CheckNestedCountersAndNativeClockWrap(Action<bool, string> check)
    {
        var metrics = new InputDiagnostics();
        long outer = metrics.BeginNativeInput(2), inner = metrics.BeginNativeInput(1);
        metrics.EndNativeInput(inner, false);
        var nested = metrics.Snapshot(false);
        check(nested.NativeInjectionInProgress && nested.NativeInputs == 3 && nested.NativeInjection.Count == 1,
            "An inner native call must not hide a still-active outer call during hook reentry.");
        metrics.EndNativeInput(outer, true);
        check(!metrics.Snapshot(false).NativeInjectionInProgress && metrics.Snapshot(false).NativeInjection.Count == 2 &&
            metrics.Snapshot(false).NativeFailures == 1, "Nested native calls must balance active state and failure samples.");
        long outerHook = metrics.BeginHookCallback(), innerHook = metrics.BeginHookCallback();
        metrics.EndHookCallback(innerHook);
        check(metrics.Snapshot(false).HookCallbackInProgress && metrics.Snapshot(false).HookCallback.Count == 1,
            "Nested native hook callbacks must keep the outer hook visible to a diagnostic snapshot.");
        metrics.EndHookCallback(outerHook);
        check(!metrics.Snapshot(false).HookCallbackInProgress && metrics.Snapshot(false).HookCallback.Count == 2,
            "Native hook timing spans must balance after reentry.");
        metrics.RecordHookEventAge(uint.MaxValue - 10, 5);
        check(Math.Abs(metrics.Snapshot(false).HookEventAge.LastMilliseconds - 16) < 0.001,
            "Native hook event age must survive the DWORD uptime wrap at 49.7 days.");
        metrics.RecordHookEventAge(20, 10);
        check(metrics.Snapshot(false).HookEventAge.Count == 1, "A future/invalid native timestamp must not fabricate a multi-day hook lag.");
        metrics.RecordHookEventAge(800, 1000);
        var age = metrics.Snapshot(false).HookEventAge;
        check(age.Count == 2 && age.SlowCount == 1 && Math.Abs(age.MaxMilliseconds - 200) < 0.001 &&
            Math.Abs(age.AverageMilliseconds - 108) < 0.001,
            "Hook event age must aggregate only timestamp delays without retaining input payloads.");

        // Public diagnostic state is deliberately numeric-only, not a vehicle for keystroke,
        // mouse-coordinate or clipboard logging when shared with the support UI.
        check(typeof(InputDiagnosticsSnapshot).GetProperties().All(p => p.PropertyType == typeof(InputTimingSnapshot) ||
                p.PropertyType == typeof(long) || p.PropertyType == typeof(int) || p.PropertyType == typeof(bool) || p.PropertyType == typeof(double)) &&
            typeof(InputTimingSnapshot).GetProperties().All(p => p.PropertyType == typeof(long) || p.PropertyType == typeof(double)),
            "Diagnostic snapshots must contain numeric aggregates only, never input or clipboard payloads.");
    }
}
