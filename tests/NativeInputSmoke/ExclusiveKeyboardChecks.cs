using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class ExclusiveKeyboardChecks
{
    const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    internal static void Run(Action<bool, string> check)
    {
        CheckCallbackProvenance(check);
        CheckPhysicalAndUnicode(check);
        CheckReturnOwnership(check);
        CheckReentryAndEmergency(check);
        CheckRoutingFailure(check);
    }

    static void CheckCallbackProvenance(Action<bool, string> check)
    {
        foreach (bool controller in new[] { false, true })
        foreach (bool remote in new[] { false, true })
        {
            using var source = new Source();
            source.Router.IsController = controller;
            if (remote) { if (controller) source.Enter(); else source.Router.ApplyRemoteAsync(new Packet { Type = "enter" }).GetAwaiter().GetResult(); }
            source.Take(); int before = source.Next;
            bool exclusive = controller && remote;
            foreach (uint flags in new uint[] { 0x10, 0x12, 0x30 })
            {
                check(source.Key(0xE7, true, flags, scan: 0x1ED9) == exclusive && source.Key(0xE7, false, flags, scan: 0x1ED9) == exclusive,
                    "Foreign injected Unicode replacements must be swallowed only on exclusive controller A.");
            }
            check(source.Next == before + (exclusive ? 0 : 6) && source.Take().Length == 0,
                "Foreign synthetic text must never be forwarded again as physical input or routed through Telex.");
            check(source.Field<IDictionary>("_physicalKeys").Count == 0,
                "Foreign injection must not become a held physical key or interfere with emergency detection.");
            before = source.Next;
            foreach (uint flags in new uint[] { 0, 0x10, 0x12 })
                check(!source.Key(0xA2, false, flags, InputRouter.InjectionTag),
                    "LanBridge-tagged cleanup must reach Windows even while controller A is exclusive.");
            check(source.Next == before + 3, "Every tagged cleanup must pass to the next hook exactly once.");
            before = source.Next;
            check(!source.Key(0x41, true, code: -1) && source.Next == before + 1,
                "Negative hook codes must pass through without interpreting or consuming their event.");
            var snapshot = source.Router.GetExclusiveInputDiagnosticsSnapshot();
            check(snapshot.IsExclusiveController == exclusive && snapshot.ForeignKeyboardSuppressed == (exclusive ? 6 : 0),
                "Payload-free exclusivity counters must distinguish active A from a receiver or local session.");
        }
        check(typeof(ExclusiveInputDiagnosticsSnapshot).GetProperties().All(property => property.PropertyType == typeof(bool)
            || property.PropertyType == typeof(long)), "Exclusive diagnostics must contain flags/counts only.");
        using var flood = new Source(); flood.Enter(); flood.Take();
        int native = flood.Native, next = flood.Next;
        for (int i = 0; i < 1000; i++) check(flood.Key(0xE7, i % 2 == 0, 0x10, scan: 0x1ED9),
            "A foreign injection flood must never bypass exclusive keyboard capture.");
        check(flood.Native == native && flood.Next == next && flood.Take().Length == 0
            && flood.Router.GetExclusiveInputDiagnosticsSnapshot().ForeignKeyboardSuppressed == 1000,
            "Suppressing foreign input must neither inject native work nor flood network/hook renewal work.");
    }

    static void CheckPhysicalAndUnicode(Action<bool, string> check)
    {
        using var source = new Source();
        check(!source.Key(0x41, true) && !source.Key(0x41, false) && source.Next == 2 && source.Take().Length == 0,
            "Local A must receive fresh ordinary physical keys before entering B.");
        source.Enter(); source.Take(); int next = source.Next;
        check(source.Key(0x41, true) && source.Key(0x41, false) && source.Next == next,
            "Physical A key down/up must be sent to B without reaching the local next hook.");
        check(source.Take() is [{ Type: "key", Key: 0x41, Down: true }, { Type: "key", Key: 0x41, Down: false }],
            "Exclusive physical typing must retain one balanced remote down/up sequence.");
        source.Router.TypingMode = TypingMode.VietnameseTelex;
        foreach (char c in "mootj . , ? !")
        {
            int key = c switch { '.' => 0xBE, ',' => 0xBC, '?' => 0xBF, '!' => 0x31, _ => char.ToUpperInvariant(c) };
            source.Key(key, true); source.Key(key, false);
        }
        string text = "";
        foreach (var packet in source.Take())
        {
            check(packet.Type == "text", "Built-in Vietnamese text must remain a Unicode edit while exclusive.");
            text = text[..(text.Length - packet.Dx)] + packet.Text;
        }
        check(text == "một . , ? !" && source.Next == next,
            "Exclusivity must preserve first-press Vietnamese and punctuation without local keystroke leakage.");
    }

    static void CheckReturnOwnership(Action<bool, string> check)
    {
        foreach (string end in new[] { "return", "End", "permission", "disconnect", "suspend", "role" })
        foreach (int key in new[] { 0x41, 0xA0, 0xA2, 0xA4, 0x5B, 0x25, 0x08, 0x70 })
        {
            using var source = new Source(); source.Enter(); source.Take(); source.Key(key, true); source.Take();
            if (end == "return") source.Router.ReleaseRemote();
            else if (end == "End")
            {
                // Exact modifier matching is intentional: Ctrl+End remains a normal B shortcut
                // when only bare End is assigned. Bind the held modifier for this return case.
                var modifiers = key switch { 0xA0 => ShortcutModifiers.Shift, 0xA2 => ShortcutModifiers.Control,
                    0xA4 => ShortcutModifiers.Alt, 0x5B => ShortcutModifiers.Windows, _ => ShortcutModifiers.None };
                source.Router.SwitchShortcut = new(0x23, modifiers);
                check(source.Key(0x23, true), "The assigned End shortcut must return immediately from B."); source.Key(0x23, false);
            }
            else if (end == "permission") source.Router.CanControl = false;
            else if (end == "disconnect") source.Router.ReleaseSession(source.Router.Send!);
            else if (end == "suspend") source.Router.SuspendSwitchShortcutAsync().GetAwaiter().GetResult();
            else source.Router.IsController = false;
            source.Barrier(); var releases = source.Take(); int next = source.Next;
            check(!source.Router.IsRemote && releases.Count(packet => packet.Type == "key" && packet.Key == key && !packet.Down) == 1,
                "Returning/disconnecting must release every held remote key exactly once.");
            check(source.Key(key, true) && source.Key(key, true) && source.Key(key, false) && source.Next == next,
                "Remote-owned repeats and trailing key-up must remain swallowed after return, permission loss or role change.");
            check(source.Field<IDictionary>("_physicalKeys").Count == 0 && source.Take().Length == 0,
                "Actual quarantined key-up must clear physical ownership without sending new remote input.");
            check(!source.Key(key, true) && !source.Key(key, false) && source.Next == next + 2,
                "The fresh complete gesture after physical release must work normally on A.");
        }
        using var imported = new Source();
        check(!imported.Key(0x51, true), "The initial held key must first belong to local A.");
        imported.Enter(); imported.Take(); imported.Router.ReleaseRemote(); imported.Barrier(); imported.Take();
        check(imported.Key(0x51, false), "A key transferred from A to B must keep its trailing up quarantined after returning.");
    }

    static void CheckReentryAndEmergency(Action<bool, string> check)
    {
        foreach (int key in new[] { 0x41, 0xA0, 0xA2, 0xA4, 0x5B })
        {
            using var source = new Source(); source.Enter(); source.Take(); source.Key(key, true); source.Take();
            source.Router.ReleaseRemote(); source.Barrier(); source.Take();
            check(source.Key(0x23, true) && source.Router.IsRemote && source.Key(0x23, false),
                "A fresh End switch must work even while an older B-owned key is quarantined.");
            check(source.Take() is [{ Type: "enter" }], "Reentry must not re-arm a still-held quarantined key on B.");
            source.Router.TypingMode = TypingMode.VietnameseTelex;
            source.Key(0x51, true); source.Key(0x51, false);
            check(source.Take() is [{ Type: "text", Text: "q" }],
                "Quarantined modifiers must not influence fresh text translation or shortcut bypass after reentry.");
            check(source.Key(key, false) && source.Take().Length == 0,
                "The quarantined up must remain swallowed when the pointer reentered B before physical release.");
        }
        using var escape = new Source(); escape.Enter(); escape.Take();
        escape.Key(0xA2, true); escape.Key(0xA4, true); escape.Take(); int before = escape.Next;
        check(escape.Key(0x7B, true) && !escape.Router.IsRemote,
            "Ctrl+Alt+F12 must exit exclusive A capture while consuming its F12 trigger.");
        check(escape.Key(0x7B, true) && escape.Key(0x7B, false) && escape.Key(0xA4, false) && escape.Key(0xA2, false)
            && escape.Next == before, "Emergency repeats and trailing chord ups must not trigger F12 or modifier actions on A.");
        escape.Take(); check(!escape.Key(0x41, true) && !escape.Key(0x41, false), "Fresh local typing must resume after emergency release.");
        escape.Enter(); escape.Take(); escape.Key(0xA2, true); escape.Key(0xA4, true);
        escape.Router.ReleaseRemote(); escape.Barrier(); escape.Take();
        check(escape.Key(0x7B, true), "Emergency detection must still see physically held modifiers quarantined by an earlier return.");
        escape.Key(0x7B, false); escape.Key(0xA2, false); escape.Key(0xA4, false);
        check(escape.Key(0x23, true) && escape.Router.IsRemote && escape.Key(0x23, false),
            "Completing the emergency chord must restore fresh End switching without a stuck latch.");
    }

    static void CheckRoutingFailure(Action<bool, string> check)
    {
        using var source = new Source(); source.Enter(); source.Take();
        source.Router.TypingMode = TypingMode.VietnameseTelex;
        source.Router.PrintableTranslationForTesting = (_, _) => throw new InvalidOperationException("synthetic translation failure");
        int before = source.Next;
        check(source.Key(0x41, true), "A failing remote-owned physical event must be consumed rather than fall through to local A.");
        source.Barrier();
        check(!source.Router.IsRemote && source.Next == before, "A routing error must release capture without leaking the original event to A.");
        check(source.Key(0x41, false), "The failed remote-owned press must keep ownership until its physical up.");
        check(!source.Key(0x51, true) && !source.Key(0x51, false), "Fresh local gestures must work after failure recovery.");
    }

    sealed class Source : IDisposable
    {
        const string Marker = "exclusive-keyboard-barrier";
        readonly ConcurrentQueue<Packet> _packets = new();
        readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal int Next, Native;
        internal Source()
        {
            Router.KeyboardHookNextForTesting = (_, _, _) => { Next++; return IntPtr.Zero; };
            Router.NativeInputRequestedForTesting = count => Native += count;
            Router.StartForTesting(); Router.ConfigureController(true, ScreenEdge.Right); Router.SwitchShortcut = new(0x23);
            Router.TypingMode = TypingMode.Physical;
            Router.PrintableTranslationForTesting = (key, _) =>
            {
                if (key is 0xBE or 0xBC or 0xBF or 0x31)
                    return (1, key switch { 0xBE => ".", 0xBC => ",", 0xBF => "?", _ => "!" });
                var state = Field<byte[]>("_translationState");
                char value = (char)key;
                if (value is >= 'A' and <= 'Z' && (state[0x10] & 0x80) == 0) value = char.ToLowerInvariant(value);
                return (1, value.ToString());
            };
            Router.Send = packet => { if (packet.Type == Marker) _drained.Set(); else _packets.Enqueue(packet); };
        }
        internal bool Key(int key, bool down, uint flags = 0, ulong extraInfo = 0, int scan = 0, int code = 0) =>
            Router.RouteKeyboardHookForTestingAsync(key, down, flags, extraInfo, scan, code).GetAwaiter().GetResult();
        internal T Field<T>(string name) => (T)typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        internal void Enter() => Router.InvokeForTestingAsync(() =>
        {
            var bounds = Field<Rectangle>("_bounds");
            typeof(InputRouter).GetMethod("EnterRemote", Private)!.Invoke(Router, [new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2)]);
        }).GetAwaiter().GetResult();
        internal void Barrier() => Router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
        internal Packet[] Take()
        {
            _drained.Reset(); Router.InvokeForTestingAsync(() => typeof(InputRouter).GetMethod("Queue", Private)!.Invoke(Router, [new Packet { Type = Marker }])).GetAwaiter().GetResult();
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Exclusive keyboard packets did not drain.");
            var result = new List<Packet>(); while (_packets.TryDequeue(out var packet)) result.Add(packet); return result.ToArray();
        }
        public void Dispose() { Router.Send = null; Router.Dispose(); _drained.Dispose(); }
    }
}
