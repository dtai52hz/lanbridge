using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class SwitchShortcutChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        CheckAdmission(check);
        CheckSingleKeysAndRepeat(check);
        CheckChordRouting(check);
        CheckHeldInputsAcrossSwitch(check);
        CheckExactModifiersAndHeldRepeat(check);
        CheckSuspendAndDisconnect(check);
        CheckCaptureEdgeFence(check);
        CheckEmergency(check);
    }

    private static void CheckAdmission(Action<bool, string> check)
    {
        using (var beforeStart = new InputRouter())
        {
            check(beforeStart.SwitchShortcut is null, "The global switch shortcut must start disabled.");
            beforeStart.SwitchShortcut = new(0x77);
            beforeStart.ConfigureController(true, ScreenEdge.Right);
            beforeStart.Send = _ => { };
            check(!Press(beforeStart, 0x77, true) && !beforeStart.IsRemote,
                "Assigning a shortcut must not activate a router before Start.");
            Press(beforeStart, 0x77, false);
            beforeStart.SwitchShortcut = new(0x7B, ShortcutModifiers.Control | ShortcutModifiers.Alt);
            check(beforeStart.SwitchShortcut is null, "The input router must reject a reserved escape shortcut defensively.");
        }
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x77);
        route.Router.CanControl = false;
        check(!route.Key(0x77, true) && !route.Router.IsRemote, "A denied shortcut must keep the key local.");
        route.Key(0x77, false);
        route.Router.CanControl = true;
        var sender = route.Router.Send;
        route.Router.Send = null;
        check(!route.Key(0x77, true) && !route.Router.IsRemote, "An unconnected controller must not swallow its configured key.");
        route.Key(0x77, false);
        route.Router.Send = sender;
        route.Router.IsController = false;
        check(!route.Key(0x77, true) && !route.Router.IsRemote, "A receiver must never trigger the controller's switch shortcut.");
        route.Key(0x77, false);
        route.Router.ApplyRemoteAsync(new Packet { Type = "enter", X = 321, Y = 654 }).GetAwaiter().GetResult();
        check(!route.Key(0x77, true) && route.Router.IsRemote,
            "Physical keys on a receiving B must not return control through A's shortcut.");
        route.Key(0x77, false);
        check(route.Take().Length == 0, "Receiver shortcut checks must not emit controller packets.");
    }

    private static void CheckSingleKeysAndRepeat(Action<bool, string> check)
    {
        // Printable, navigation, function, OEM and media virtual keys share the same real hook route.
        foreach (int key in new[] { 0x41, 0x25, 0x77, 0xBA, 0xAF })
        {
            using var route = new Route();
            route.Router.SwitchShortcut = new(key);
            check(route.Key(key, true) && route.Router.IsRemote, "A configured bare key must enter B without moving to an edge.");
            var returnPoint = route.Field<Point?>("_shortcutReturnPoint");
            check(returnPoint.HasValue, "Shortcut entry must remember A's original pointer position.");
            check(Math.Abs(route.Field<double>("_remoteX") - 959.5) < .01
                && Math.Abs(route.Field<double>("_remoteY") - 539.5) < .01,
                "Shortcut entry must place B's pointer in its center, away from an immediate return edge.");
            for (int repeat = 0; repeat < 5; repeat++)
                check(route.Key(key, true) && route.Router.IsRemote, "A held shortcut must not toggle again on keyboard autorepeat.");
            check(route.Key(key, false), "The swallowed switch key down must have a swallowed key up.");
            var entry = route.Take();
            check(entry.Length == 1 && entry[0].Type == "enter" && Math.Abs(entry[0].X - 32768) <= 1
                && Math.Abs(entry[0].Y - 32768) <= 1, "The switch key and its repeats must never leak into B's input stream.");
            check(route.Key(key, true) && !route.Router.IsRemote, "Pressing the same shortcut again must return immediately to A.");
            check(route.Field<Point>("_lastLocalPoint") == returnPoint!.Value,
                "Returning from a shortcut-started session must restore A's original pointer position.");
            check(route.Key(key, true) && !route.Router.IsRemote && route.Key(key, false),
                "A return shortcut's repeat and release must stay consumed after A is active.");
            var leave = route.Take();
            check(leave.Length == 1 && leave[0].Type == "leave", "One return press must emit exactly one leave packet.");
            check(route.HeldRemoteKeys == 0, "A bare switch shortcut must not leave any held remote key.");
        }
    }

    private static void CheckChordRouting(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x4B, ShortcutModifiers.Control | ShortcutModifiers.Alt | ShortcutModifiers.Shift);
        foreach (int modifier in new[] { 0xA3, 0xA5, 0xA1 })
            check(!route.Key(modifier, true), "Chord modifiers must remain ordinary local keys before the trigger.");
        route.Router.TypingMode = TypingMode.VietnameseTelex;
        check(route.Key(0x4B, true) && route.Router.IsRemote, "Right-side Ctrl/Alt/Shift must match a configured generic chord.");
        check(route.NativeRequests == 4, "Shortcut entry must release three already-local modifiers and warp once, without injecting its consumed trigger.");
        bool hasShortcutModifiers = true;
        route.Router.InvokeForTestingAsync(() =>
        {
            hasShortcutModifiers = (bool)route.Call("HasShortcutModifier")!;
            route.Call("SendPhysicalShifts");
        }).GetAwaiter().GetResult();
        check(!hasShortcutModifiers && route.HeldRemoteKeys == 0,
            "Consumed chord modifiers must be absent from Unicode shortcut and deferred Shift routing.");
        check(route.Key(0x25, true) && route.Key(0x25, false), "Ordinary navigation must work while consumed shortcut modifiers are still held.");
        foreach (int modifier in new[] { 0xA3, 0xA5, 0xA1 })
            check(route.Key(modifier, true) && route.Key(modifier, false), "Consumed modifier repeats and ups must not leak to B.");
        route.Key(0x4B, false);
        var first = route.Take();
        check(first.Length == 3 && first[0].Type == "enter" && IsKey(first[1], 0x25, true) && IsKey(first[2], 0x25, false),
            "Entry must suppress the entire switching chord while preserving unrelated navigation order.");

        route.Router.TypingMode = TypingMode.Physical;
        route.Key(0x41, true); // An unrelated key is still held when the return chord starts.
        foreach (int modifier in new[] { 0xA2, 0xA4, 0xA0 }) route.Key(modifier, true);
        check(route.Key(0x4B, true) && !route.Router.IsRemote && route.HeldRemoteKeys == 0,
            "Returning with a chord must release every prior remote key, including its forwarded modifiers.");
        route.Key(0x4B, false);
        foreach (int modifier in new[] { 0xA2, 0xA4, 0xA0 }) check(route.Key(modifier, false), "Return chord modifiers must consume late key ups on A.");
        check(!route.Key(0x41, false), "An unrelated physical key release must remain local after returning to A.");
        var returned = route.Take();
        check(returned.All(packet => packet.Type != "key" || packet.Key != 0x4B), "The return chord trigger must never be sent to B.");
        foreach (int key in new[] { 0x41, 0xA2, 0xA4, 0xA0 })
            check(returned.Count(packet => IsKey(packet, key, true)) == 1 && returned.Count(packet => IsKey(packet, key, false)) == 1,
                "Every key sent to B before a shortcut return must have exactly one balancing release.");
        check(returned[^1].Type == "leave", "Remote releases must precede the leave packet.");
    }

    private static void CheckExactModifiersAndHeldRepeat(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x77, ShortcutModifiers.Control);
        route.Key(0x77, true); // The key was already down before Ctrl was added.
        route.Key(0xA2, true);
        check(!route.Key(0x77, true) && !route.Router.IsRemote,
            "Adding modifiers to a held ordinary key must not turn its autorepeat into a switch.");
        route.Key(0x77, false);
        route.Key(0xA0, true);
        check(!route.Key(0x77, true) && !route.Router.IsRemote, "Extra unassigned modifiers must not activate a different shortcut.");
        route.Key(0x77, false);
        route.Key(0xA0, false);
        check(route.Key(0x77, true) && route.Router.IsRemote, "The exact modifier set must activate after a fresh trigger down.");
        route.Key(0x77, false);
        check(route.Key(0x77, true) && !route.Router.IsRemote,
            "A second trigger press may return while its original consumed Ctrl remains physically held.");
        route.Key(0x77, false);
        route.Key(0xA2, false);
        var packets = route.Take();
        check(packets.Length == 2 && packets[0].Type == "enter" && packets[1].Type == "leave",
            "Modifier mismatch and held-key repeats must not leak extra session or key packets.");

        route.Router.SwitchShortcut = new(0x78, ShortcutModifiers.Windows);
        route.Key(0x5C, true);
        check(route.Key(0x78, true) && route.Router.IsRemote, "Right Windows must match a Windows-modified shortcut.");
        route.Key(0x78, false);
        check(route.Key(0x5C, false), "A consumed Windows modifier must release without reaching B.");
        check(route.Take().All(packet => packet.Type != "key"), "A Windows switch chord must not leave Windows held remotely.");
    }

    private static void CheckHeldInputsAcrossSwitch(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x77);
        check(!route.Key(0x51, true), "An unrelated held key must remain local before shortcut entry.");
        route.Router.InvokeForTestingAsync(() => route.Field<HashSet<string>>("_physicalButtons").Add("left")).GetAwaiter().GetResult();
        route.Key(0x77, true);
        route.Key(0x77, false);
        var entry = route.Take();
        check(entry.Length == 3 && entry[0].Type == "enter" && IsKey(entry[1], 0x51, true)
            && entry[2].Type == "button" && entry[2].Button == "left" && entry[2].Down,
            "Shortcut entry must transfer unrelated held keys and mouse buttons after enter, in order.");
        check(route.NativeRequests == 3, "Held key/button transfer must release each local input once and park A's cursor once.");
        route.Key(0x77, true);
        route.Key(0x77, false);
        var leave = route.Take();
        check(leave.Length == 3 && IsKey(leave[0], 0x51, false) && leave[1].Type == "button"
            && !leave[1].Down && leave[1].Button == "left" && leave[2].Type == "leave",
            "Shortcut return must release a held drag and keyboard key before leaving B.");
        check(route.HeldRemoteKeys == 0 && route.Field<HashSet<string>>("_sentButtons").Count == 0,
            "Switching back during a drag must leave no remote held input.");
        route.Key(0x51, false);
        route.Router.InvokeForTestingAsync(() => route.Field<HashSet<string>>("_physicalButtons").Clear()).GetAwaiter().GetResult();
    }

    private static void CheckSuspendAndDisconnect(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x77);
        route.Key(0x77, true);
        route.Router.SuspendSwitchShortcutAsync().GetAwaiter().GetResult();
        check(!route.Router.IsRemote && route.Router.SwitchShortcut is null,
            "Awaiting capture preparation must return to A and disable switching before a capture dialog opens.");
        check(route.Key(0x77, true) && route.Key(0x77, false), "Capture preparation must retain the prior consumed key's repeat/up latch.");
        check(!route.Key(0x77, true), "A fresh key press must reach shortcut capture while switching is suspended.");
        route.Key(0x77, false);
        route.Router.SwitchShortcut = new(0x78);
        check(route.Key(0x78, true) && route.Router.IsRemote, "Reassigning after capture must activate the new key.");
        route.Router.CanControl = false;
        route.Router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
        check(!route.Router.IsRemote && route.HeldRemoteKeys == 0, "Revoking permission during a held switch must immediately return and release input.");
        check(route.Key(0x78, true) && route.Key(0x78, false), "Permission revocation must preserve the consumed trigger until its physical up.");
        check(!route.Key(0x78, true), "The configured key must resume ordinary local behavior when permission is unavailable.");
        route.Key(0x78, false);
        route.Router.CanControl = true;
        check(route.Key(0x78, true) && route.Router.IsRemote, "A fresh press after regrant must switch normally.");
        route.Key(0x78, false);
        route.Router.SwitchShortcut = null;
        check(route.Key(0x78, true) && route.Router.IsRemote, "Disabling the shortcut must restore the key's ordinary remote-input behavior.");
        route.Key(0x78, false);
        check(route.Take().Count(packet => IsKey(packet, 0x78, true)) == 1,
            "Only the key pressed after shortcut disablement may be forwarded as ordinary input.");
    }

    private static void CheckEmergency(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x77);
        route.Key(0x77, true);
        route.Key(0x77, false);
        route.Key(0xA2, true);
        route.Key(0xA4, true);
        check(!route.Key(0x7B, true) && !route.Router.IsRemote,
            "The reserved Ctrl+Alt+F12 escape must still return A independently of the configured switch.");
        route.Key(0x7B, false);
        route.Key(0xA4, false);
        route.Key(0xA2, false);
        check(route.HeldRemoteKeys == 0 && route.Take().All(packet => packet.Type != "key" || packet.Key != 0x7B),
            "The escape must release prior remote modifiers without forwarding F12.");
        check(route.Key(0x77, true) && route.Router.IsRemote, "Completing the emergency chord must not disable the configured switch on A.");
        route.Key(0x77, false);
    }

    private static void CheckCaptureEdgeFence(Action<bool, string> check)
    {
        using var route = new Route();
        route.Router.SwitchShortcut = new(0x78);
        route.Router.SuspendSwitchShortcutAsync().GetAwaiter().GetResult();
        route.Router.InvokeForTestingAsync(() =>
        {
            var bounds = route.Field<Rectangle>("_bounds");
            var edge = new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2);
            check(!route.Router.TrackExitEdge(edge, Environment.TickCount64),
                "Pointer edge tracking must stay local while a shortcut recorder owns the keyboard.");
            route.Call("EnterRemote", edge);
            check(!route.Router.IsRemote, "A pending edge-entry request must not bypass shortcut-capture suspension.");
        }).GetAwaiter().GetResult();
        check(route.Take().Length == 0, "Edge movement during shortcut capture must emit no remote-input packet.");

        // Canceling an initially disabled binding restores null in MainForm's finally block.
        // That ordinary setter must lift the entry fence even though no hotkey is assigned.
        route.Router.SwitchShortcut = null;
        route.Router.InvokeForTestingAsync(() =>
        {
            var bounds = route.Field<Rectangle>("_bounds");
            var edge = new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2);
            check(route.Router.TrackExitEdge(edge, Environment.TickCount64),
                "Restoring a null shortcut after capture must re-enable normal immediate edge tracking.");
            route.Call("EnterRemote", edge);
            check(route.Router.IsRemote, "Restoring a null shortcut after capture must allow edge entry again.");
        }).GetAwaiter().GetResult();
        check(route.Take() is [{ Type: "enter" }], "Restored edge entry must emit one normal enter packet.");
    }

    private static bool Press(InputRouter input, int key, bool down) =>
        input.RoutePhysicalKeyForTestingAsync(key, down).GetAwaiter().GetResult();
    private static bool IsKey(Packet packet, int key, bool down) => packet.Type == "key" && packet.Key == key && packet.Down == down;

    private sealed class Route : IDisposable
    {
        private const string Barrier = "switch-shortcut-test-barrier";
        private readonly ConcurrentQueue<Packet> _packets = new();
        private readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal int NativeRequests;
        internal int HeldRemoteKeys => Field<IDictionary>("_sentKeys").Count;
        internal Route()
        {
            Router.StartForTesting();
            Router.ConfigureController(true, ScreenEdge.Right);
            Router.TypingMode = TypingMode.Physical;
            Router.RemoteWidth = 1920; Router.RemoteHeight = 1080;
            Router.NativeInputRequestedForTesting = _ => NativeRequests++;
            Router.Send = packet => { if (packet.Type == Barrier) _drained.Set(); else _packets.Enqueue(packet); };
        }
        internal bool Key(int key, bool down) => Press(Router, key, down);
        internal T Field<T>(string name) => (T)typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        internal object? Call(string name, params object[] args) => typeof(InputRouter).GetMethod(name, Private)!.Invoke(Router, args);
        internal Packet[] Take()
        {
            _drained.Reset();
            Router.InvokeForTestingAsync(() => Call("Queue", new Packet { Type = Barrier })).GetAwaiter().GetResult();
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Shortcut packets did not flush.");
            var output = new List<Packet>();
            while (_packets.TryDequeue(out var packet)) output.Add(packet);
            return output.ToArray();
        }
        public void Dispose()
        {
            Router.Dispose();
            _drained.Dispose();
        }
    }
}
