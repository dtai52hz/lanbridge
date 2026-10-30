using System.Collections.Concurrent;
using System.Reflection;
using LanBridge;

internal static class DispatcherChecks
{
    public static void Run(Action<bool, string> check)
    {
        var callerContext = SynchronizationContext.Current;
        var blockedUi = new PausedUiContext();
        SynchronizationContext.SetSynchronizationContext(blockedUi);
        try
        {
            using var router = new InputRouter();
            router.StartForTesting();
            router.StartForTesting();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            int caller = Environment.CurrentManagedThreadId, inputThread = caller;
            var packets = new ConcurrentQueue<Packet>();
            router.PacketAppliedForTesting = packets.Enqueue;
            router.InvokeForTestingAsync(() => inputThread = Environment.CurrentManagedThreadId).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(inputThread != caller, "The input dispatcher must not share the caller's UI thread.");
            check(!(bool)typeof(InputRouter).GetField("_nativeInputEnabled", flags)!.GetValue(router)!, "The dispatcher test must disable native input injection.");
            check((IntPtr)typeof(InputRouter).GetField("_mouseHook", flags)!.GetValue(router)! == IntPtr.Zero &&
                (IntPtr)typeof(InputRouter).GetField("_keyboardHook", flags)!.GetValue(router)! == IntPtr.Zero,
                "The dispatcher test must not install global hooks.");

            router.CanControl = true;
            router.ApplyRemoteAsync(new Packet { Type = "enter", X = 1, Y = 2 }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            for (int i = 0; i < 1500; i++)
                router.ApplyRemoteAsync(new Packet { Type = "move", X = i, Y = 1500 - i }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.ApplyRemoteAsync(new Packet { Type = "button", Button = "left", Down = true, X = 1499, Y = 1 }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.ApplyRemoteAsync(new Packet { Type = "leave" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            var applied = packets.ToArray();
            check(applied.Length == 1503 && applied[0].Type == "enter" && applied[^2].Type == "button" && applied[^1].Type == "leave",
                "A paused UI must not block input or reorder enter/move/button/leave packets.");
            check(applied.Skip(1).Take(1500).Select(p => p.X).SequenceEqual(Enumerable.Range(0, 1500)),
                "The independent input dispatcher changed mouse packet order.");
            check(!router.IsRemote, "A leave packet must release input while the UI is paused.");
            check(blockedUi.Posts == 0, "Input processing and notifications must not post to the paused caller UI context.");

            // A disconnected session may already have queued input behind an operation. Cancellation
            // must prevent that old packet from activating control after cleanup or a new session.
            using var paused = new ManualResetEventSlim();
            using var resume = new ManualResetEventSlim();
            var barrier = router.InvokeForTestingAsync(() => { paused.Set(); resume.Wait(TimeSpan.FromSeconds(3)); });
            check(paused.Wait(TimeSpan.FromSeconds(2)), "Failed to pause the no-hook dispatcher for the cancellation regression.");
            using var canceledSession = new CancellationTokenSource();
            var staleEnter = router.ApplyRemoteAsync(new Packet { Type = "enter", X = 60000 }, canceledSession.Token);
            canceledSession.Cancel();
            resume.Set();
            barrier.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            bool canceled = false;
            try { staleEnter.GetAwaiter().GetResult(); } catch (OperationCanceledException) { canceled = true; }
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(canceled && !router.IsRemote && packets.Count == applied.Length, "A canceled, queued packet from an old session was applied.");

            router.ApplyRemoteAsync(new Packet { Type = "enter" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.ApplyRemoteAsync(new Packet { Type = "key", Key = 0xA2, Scan = 0x1D, Down = true }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            bool invalidText = false;
            try { router.ApplyRemoteAsync(new Packet { Type = "text", Dx = 1000, Text = "x" }).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { invalidText = true; }
            var held = (System.Collections.IDictionary)typeof(InputRouter).GetField("_receivedKeys", flags)!.GetValue(router)!;
            check(invalidText && !router.IsRemote && held.Count == 0,
                "A rejected input packet must release its held keys on the input thread without waiting for a paused UI.");

            Action<Packet> oldSender = _ => { }, newSender = _ => { };
            router.Send = oldSender;
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.Send = newSender;
            router.CanControl = true;
            router.ApplyRemoteAsync(new Packet { Type = "enter" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.ReleaseSession(oldSender);
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(router.CanControl && router.IsRemote, "An old session's delayed close must not revoke the new sender's input session.");
            router.ReleaseSession(newSender);
            router.InvokeForTestingAsync(() => { }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(!router.CanControl && !router.IsRemote, "Closing the current sender must release input without waiting for the UI.");

            router.CanControl = true;
            router.ApplyRemoteAsync(new Packet { Type = "enter" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.InvokeForTestingAsync(() => typeof(InputRouter).GetMethod("EmergencyReleaseCore", flags)!.Invoke(router, null)).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            router.ApplyRemoteAsync(new Packet { Type = "enter" }).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
            check(!router.CanControl && !router.IsRemote,
                "B's physical emergency escape must prevent a new enter before the paused UI publishes revoked permissions.");

            var worker = (Thread)typeof(InputRouter).GetField("_worker", flags)!.GetValue(router)!;
            router.Dispose();
            check(!worker.IsAlive, "Disposing input must stop its independent message loop.");
            check(blockedUi.Posts == 0, "Disposing input must not wait for the paused caller UI.");
        }
        finally { SynchronizationContext.SetSynchronizationContext(callerContext); }
    }

    private sealed class PausedUiContext : SynchronizationContext
    {
        public int Posts;
        public override void Post(SendOrPostCallback callback, object? state) => Interlocked.Increment(ref Posts);
        public override void Send(SendOrPostCallback callback, object? state) => throw new InvalidOperationException("Synchronous UI work would deadlock while minimized.");
    }
}
