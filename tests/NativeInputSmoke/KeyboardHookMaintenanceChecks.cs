using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LanBridge;

internal static class KeyboardHookMaintenanceChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        CheckLeaseOverlapAndFailures(check);
        CheckCallbackRetirement(check);
        CheckFailedDisposeRooting(check);
        CheckFirstWordAndIdlePreservation(check);
        CheckHeldInputs(check);
        CheckExplicitRecovery(check);
    }

    private static void CheckLeaseOverlapAndFailures(Action<bool, string> check)
    {
        var chain = new FakeChain(); int processed = 0;
        using var lease = new KeyboardHookLease(callback => chain.Install(callback.Invoke), chain.Remove,
            (code, message, data) => { processed++; return chain.Next(code, message, data); }, chain.Next, () => chain.Clock);
        check(lease.TryRenew(), "A fake initial keyboard hook must install without calling any native hook API.");
        chain.OnInstall = () => chain.Emit(0x41, true);
        chain.OnRemove = () => chain.Emit(0x41, false);
        int before = processed;
        check(lease.TryRenew() && processed == before + 2,
            "Reentrant events during new-before-old installation must pass the active registration exactly once.");
        chain.OnInstall = chain.OnRemove = null;
        var old = lease.ActiveHandle;
        chain.FailInstall = true;
        check(!lease.TryRenew() && lease.ActiveHandle == old && chain.Count == 1,
            "A failed replacement must preserve the old active registration without an input gap.");
        chain.FailInstall = false;
        chain.FailRemove = true;
        check(lease.TryRenew() && chain.Count == 2 && lease.Snapshot(false).PendingRetired == 1,
            "A failed old unhook must retain one inactive callback beside the active replacement.");
        before = processed; chain.Emit(0x42, true); chain.Emit(0x42, false);
        check(processed == before + 2, "A retained inactive old callback must not duplicate pass-through key events.");
        for (int retry = 0; retry < 20; retry++) check(!lease.TryRenew() && chain.Count == 2,
            "Persistent unhook failure must not accumulate native registrations.");
        chain.FailRemove = false;
        check(lease.TryRenew() && chain.Count == 1 && lease.Snapshot(false).PendingRetired == 1,
            "Renewal must recover once the retained registration can be removed.");
        lease.CollectCompletedCallbacks();
        check(lease.Snapshot(false).PendingRetired == 0,
            "Even an idle just-unhooked generation stays rooted until a later owner maintenance pass.");
        // Simulate Windows silently removing the active hook; removing its now-absent
        // handle is treated as already done by the fake/native adapters.
        chain.RemoveExternally(lease.ActiveHandle);
        check(lease.TryRenew() && chain.Count == 1, "An already-removed old hook must not permanently prevent later renewal.");
        var snapshot = lease.Snapshot(false);
        check(snapshot.SuccessfulRenewals == 5 && snapshot.FailedRenewals == 21,
            "Maintenance diagnostics must count initial registration, successful replacements and failed attempts separately.");
        check(typeof(KeyboardHookDiagnosticsSnapshot).GetProperties().All(property => property.PropertyType == typeof(long) ||
            property.PropertyType == typeof(int) || property.PropertyType == typeof(bool) || property.PropertyType == typeof(double)),
            "Hook diagnostics must contain counters and flags only, never typed keys or text.");
    }

    private static void CheckCallbackRetirement(Action<bool, string> check)
    {
        var chain = new FakeChain(); KeyboardHookLease? lease = null; bool replaced = false;
        lease = new(callback => chain.Install(callback.Invoke), chain.Remove, (code, message, data) =>
        {
            if (!replaced)
            {
                replaced = true;
                check(lease!.TryRenew() && lease.Snapshot(false).PendingRetired == 1,
                    "Renewing from an active callback must retain that callback until it returns.");
                GC.Collect(); GC.WaitForPendingFinalizers();
                check(lease.Snapshot(false).PendingRetired == 1,
                    "GC during an old in-flight callback must not retire its registration.");
            }
            return new IntPtr(1);
        }, chain.Next, () => chain.Clock);
        using (lease)
        {
            check(lease.TryRenew(), "Callback-retirement fake registration must install.");
            chain.Emit(0x41, true);
            check(lease.Snapshot(false).PendingRetired == 1,
                "The completing callback must stay retained until a later owner maintenance pass.");
            lease.CollectCompletedCallbacks();
            check(lease.Snapshot(false).PendingRetired == 0 && chain.Count == 1,
                "Later owner maintenance must collect successfully unhooked callbacks after they returned.");
        }
    }

    private static void CheckFailedDisposeRooting(Action<bool, string> check)
    {
        var (failed, retiredCallback) = CreateDisposedFailure();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(failed.IsAlive && retiredCallback.IsAlive,
            "A callback whose native unhook failed must stay rooted after the owner is disposed and collected.");
        var successful = CreateDisposedSuccess();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        check(!successful.IsAlive, "Successfully disposed hook leases must remain collectable.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Lease, WeakReference Callback) CreateDisposedFailure()
    {
        WeakReference? callbackReference = null;
        var lease = new KeyboardHookLease(callback => { callbackReference = new(callback); return new IntPtr(23); }, _ => false,
            (_, _, _) => IntPtr.Zero, (_, _, _) => IntPtr.Zero, () => 0);
        lease.TryRenew(); lease.Dispose();
        return (new(lease), callbackReference!);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedSuccess()
    {
        var lease = new KeyboardHookLease(_ => new IntPtr(24), _ => true,
            (_, _, _) => IntPtr.Zero, (_, _, _) => IntPtr.Zero, () => 0);
        lease.TryRenew(); lease.Dispose(); return new(lease);
    }

    private static void CheckFirstWordAndIdlePreservation(Action<bool, string> check)
    {
        using var source = new Source();
        int swallowed = 0;
        source.Chain.AddForeign((code, message, data) =>
        {
            int key = Marshal.PtrToStructure<KeyData>(data).Vk;
            if (key is 0x4F or 0x4A) { swallowed++; return new IntPtr(1); }
            return source.Chain.Next(code, message, data);
        });
        source.Enter();
        check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == 2,
            "An explicit F9 entry must renew immediately after its final key-up, without a 150 ms wait.");
        source.Type("mootj");
        check(Render(source.Take()) == "một" && swallowed == 0,
            "The first fast Vietnamese word after entry must reach LanBridge before a preceding consuming IME model.");
        source.Type(" "); source.Take(); source.Type("moot");
        string pending = Render(source.Take());
        int native = 0; source.Router.NativeInputRequestedForTesting = count => native += count;
        long renewed = source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
        source.Chain.Clock += 10_001;
        source.Maintain();
        check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == renewed + 1 &&
            source.Take().Length == 0 && native == 0 && source.Router.IsRemote && source.Composer.CurrentWord == "môt",
            "An idle renewal must preserve the unfinished word, active session and native FIFO without injecting or sending releases.");
        source.Type("j");
        check(Render(source.Take(), pending) == "một", "The first tone after a maintenance renewal must apply to the existing word.");
        source.Router.ReleaseRemote(); source.Take();
        renewed = source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
        source.Chain.Clock += 20_000; source.Maintain();
        check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == renewed,
            "Automatic hook renewal must stop while control is local on A.");
    }

    private static void CheckHeldInputs(Action<bool, string> check)
    {
        foreach (int modifier in new[] { 0xA2, 0xA0 })
        {
            using var source = new Source(); source.Enter(); source.Take();
            source.Key(modifier, true); source.Take();
            long before = source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
            source.Chain.Clock += 20_000; source.Maintain();
            check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == before && source.Physical.Contains(modifier),
                "A long-held genuine Ctrl/Shift must defer automatic renewal and retain physical state.");
            source.Key(modifier, false); source.Take(); source.Chain.Clock += 149; source.Maintain();
            check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == before,
                "An ordinary periodic renewal must wait for at least 150 ms of keyboard/button quiet.");
            source.Chain.Clock++; source.Maintain();
            check(source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == before + 1 && source.Take().Length == 0,
                "Renewal after a genuine modifier release must not add any key release or change the session.");
        }
        using var drag = new Source(); drag.Enter(); drag.Take(); drag.Drag(true); drag.Take();
        long dragBefore = drag.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
        drag.Chain.Clock += 20_000; drag.Maintain();
        check(drag.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == dragBefore && drag.PhysicalButtons.Contains("left"),
            "A long mouse drag must defer renewal without releasing the physical button.");
        drag.Drag(false); drag.Take(); drag.Chain.Clock += 150; drag.Maintain();
        check(drag.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == dragBefore + 1,
            "Periodic renewal may resume after the drag ends.");
    }

    private static void CheckExplicitRecovery(Action<bool, string> check)
    {
        using var source = new Source(); source.Enter(); source.Take();
        source.Key(0xA2, true); source.Take();
        source.Router.ResetTypingAsync(TypingMode.VietnameseTelex).GetAwaiter().GetResult(); source.Take();
        check(source.Physical.Contains(0xA2), "Ordinary reset must continue preserving genuinely held modifiers.");
        source.Chain.RemoveExternally(source.Chain.CaptureHandle);
        long before = source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
        source.Router.RecoverTypingAsync(TypingMode.VietnameseTelex).GetAwaiter().GetResult(); source.Take();
        check(source.Physical.Count == 0 && !source.Router.IsRemote && source.Router.IsController && source.Router.CanControl &&
            source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == before + 1,
            "Explicit recovery must clear stale physical tracking and repair a removed hook immediately while local.");
        source.Enter(); source.Take(); source.Type("mootj");
        check(source.Router.IsRemote && Render(source.Take()) == "một",
            "F9 and first-try Vietnamese must work after explicit recovery without restarting the router/session.");

        // A held switch is still owned until its real up, even though strong recovery
        // abandons physical-state caches. Its repeat must never leak into local apps.
        source.Key(0x78, true); source.Take();
        source.Router.RecoverTypingAsync(TypingMode.VietnameseTelex).GetAwaiter().GetResult(); source.Take();
        long afterRecovery = source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals;
        check(source.Key(0x78, true) != IntPtr.Zero && !source.Router.IsRemote,
            "Explicit recovery must quarantine a previously consumed switch's auto-repeat.");
        check(source.Key(0x78, false) != IntPtr.Zero && !source.Router.IsRemote,
            "Explicit recovery must retain ownership of the consumed switch's trailing key-up.");
        source.Enter(); source.Take();
        check(source.Router.IsRemote && source.Router.GetKeyboardHookDiagnosticsSnapshot().SuccessfulRenewals == afterRecovery + 1,
            "A fresh switch press after its actual release must enter normally.");
    }

    private static string Render(IEnumerable<Packet> packets, string value = "")
    { foreach (var packet in packets.Where(packet => packet.Type == "text")) value = value[..(value.Length - packet.Dx)] + packet.Text; return value; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyData { internal int Vk; internal uint Scan, Flags, Time; internal UIntPtr ExtraInfo; }

    private sealed class FakeChain
    {
        private sealed record Node(IntPtr Id, Func<int, IntPtr, IntPtr, IntPtr> Callback);
        private sealed class Frame(Node[] nodes) { internal Node[] Nodes = nodes; internal int Index = -1; }
        private readonly List<Node> _nodes = [];
        private readonly Stack<Frame> _frames = new();
        private long _serial;
        internal long Clock;
        internal bool FailInstall, FailRemove;
        internal Action? OnInstall, OnRemove;
        internal int Count => _nodes.Count;
        internal IntPtr CaptureHandle;
        internal IntPtr Install(Func<int, IntPtr, IntPtr, IntPtr> callback)
        {
            if (FailInstall) return IntPtr.Zero;
            var id = new IntPtr(++_serial); _nodes.Insert(0, new(id, callback)); CaptureHandle = id;
            OnInstall?.Invoke(); return id;
        }
        internal void AddForeign(Func<int, IntPtr, IntPtr, IntPtr> callback) => _nodes.Insert(0, new(new IntPtr(++_serial), callback));
        internal bool Remove(IntPtr handle)
        { OnRemove?.Invoke(); if (FailRemove) return false; _nodes.RemoveAll(node => node.Id == handle); return true; }
        internal void RemoveExternally(IntPtr handle) => _nodes.RemoveAll(node => node.Id == handle);
        internal IntPtr Next(int code, IntPtr message, IntPtr data)
        {
            var frame = _frames.Peek(); int previous = frame.Index; int index = previous + 1;
            if (index >= frame.Nodes.Length) return IntPtr.Zero;
            frame.Index = index;
            try { return frame.Nodes[index].Callback(code, message, data); }
            finally { frame.Index = previous; }
        }
        internal IntPtr Emit(int key, bool down)
        {
            var data = Marshal.AllocHGlobal(Marshal.SizeOf<KeyData>());
            try
            {
                Marshal.StructureToPtr(new KeyData { Vk = key }, data, false);
                _frames.Push(new(_nodes.ToArray()));
                try { return Next(0, new IntPtr(down ? 0x100 : 0x101), data); }
                finally { _frames.Pop(); }
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }

    private sealed class Source : IDisposable
    {
        private readonly ConcurrentQueue<Packet> _packets = new();
        private readonly ManualResetEventSlim _drained = new();
        internal readonly FakeChain Chain = new();
        internal InputRouter Router { get; } = new();
        internal IDictionary Physical => (IDictionary)Field("_physicalKeys");
        internal HashSet<string> PhysicalButtons => (HashSet<string>)Field("_physicalButtons");
        internal VietnameseComposer Composer => (VietnameseComposer)Field("_composer");
        private object Field(string name) => typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        internal Source()
        {
            Router.KeyboardHooksForTesting = new(Chain.Install, Chain.Remove, Chain.Next, () => Chain.Clock);
            Router.StartForTesting(); Router.ConfigureController(true, ScreenEdge.Right); Router.SwitchShortcut = new(0x78);
            Router.PrintableTranslationForTesting = (key, _) => (1, ((char)(key is >= 0x41 and <= 0x5A ? key + 32 : key)).ToString());
            Router.Send = packet => { if (packet.Type == "hook-maintenance-barrier") _drained.Set(); else _packets.Enqueue(packet); };
        }
        internal IntPtr Key(int key, bool down)
        { IntPtr result = IntPtr.Zero; Router.InvokeForTestingAsync(() => result = Chain.Emit(key, down)).GetAwaiter().GetResult(); return result; }
        internal void Enter() { Key(0x78, true); Key(0x78, false); }
        internal void Type(string value) { foreach (char key in value) { Key(char.ToUpperInvariant(key), true); Key(char.ToUpperInvariant(key), false); } }
        internal void Maintain() => Router.MaintainKeyboardHookForTestingAsync().GetAwaiter().GetResult();
        internal void Drag(bool down) => Router.InvokeForTestingAsync(() =>
        {
            if (down) PhysicalButtons.Add("left"); else PhysicalButtons.Remove("left");
            typeof(InputRouter).GetMethod("SendButton", Private)!.Invoke(Router, ["left", down]);
            typeof(InputRouter).GetField("_lastKeyboardActivityAt", Private)!.SetValue(Router, Chain.Clock);
        }).GetAwaiter().GetResult();
        internal Packet[] Take()
        {
            _drained.Reset();
            Router.InvokeForTestingAsync(() => typeof(InputRouter).GetMethod("Queue", Private)!.Invoke(Router, [new Packet { Type = "hook-maintenance-barrier" }])).GetAwaiter().GetResult();
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Hook-maintenance packets did not drain.");
            var value = _packets.ToArray(); while (_packets.TryDequeue(out _)) { } return value;
        }
        public void Dispose() { Router.Send = null; Router.Dispose(); _drained.Dispose(); }
    }
}
