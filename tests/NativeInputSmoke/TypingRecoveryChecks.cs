using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using LanBridge;

internal static class TypingRecoveryChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        CheckFailedLatinTranslation(check);
        CheckIntermittentToneTranslation(check);
        CheckCaseAndVni(check);
        CheckPreservedLayoutAndBoundaries(check);
        CheckSameModeReset(check);
        CheckCounters(check);
    }

    private static void CheckFailedLatinTranslation(Action<bool, string> check)
    {
        foreach (var failed in new[] { (0, ""), (1, "\u0001") })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            route.Router.PrintableTranslationForTesting = (_, _) => failed;
            route.Type("tieengs Vieetj");
            var output = route.Take();
            check(Render(output) == "tiếng Việt" && !output.Any(packet => packet.Type == "key"),
                "Missing/control Windows translations must preserve the Telex word using Latin fallback, without raw key resets.");
            var stats = route.Router.GetTypingDiagnosticsSnapshot();
            check(stats.PrintableTranslationFailures == "tieengs Vieetj".Length && stats.PrintableFallbacks == "tieengs Vieetj".Length
                && stats.PrintableTranslationSuccesses == 0 && stats.OutgoingNonAsciiCodeUnits > 0,
                "Failed translations, successful Latin fallbacks and outgoing accents must have distinct numeric counters.");
        }
    }

    private static void CheckIntermittentToneTranslation(Action<bool, string> check)
    {
        using var route = new Route(TypingMode.VietnameseTelex);
        int tones = 0;
        route.Router.PrintableTranslationForTesting = (key, _) => key == 0x53 ? ++tones switch
        {
            1 => (0, ""),
            2 => (1, "\u0001"),
            _ => (1, "s"),
        } : Latin(key);
        route.Type("tieengs tieengs tieengs");
        var output = route.Take();
        check(Render(output) == "tiếng tiếng tiếng" && !output.Any(packet => packet.Type == "key"),
            "A transient failure on each of the first two tone-key presses must not require retrying the tone or reset the word.");
        var stats = route.Router.GetTypingDiagnosticsSnapshot();
        check(stats.PrintableTranslationFailures == 2 && stats.PrintableFallbacks == 2 && stats.PrintableTranslationSuccesses > 0,
            "Intermittent tone translation failures must be visible independently from normal layout translations.");

        using var escaped = new Route(TypingMode.VietnameseTelex);
        escaped.Router.PrintableTranslationForTesting = (_, _) => (0, "");
        escaped.Type("ass");
        check(Render(escaped.Take()) == "as", "Latin fallback must retain Telex's repeated-tone literal escape convention.");
    }

    private static void CheckCaseAndVni(Action<bool, string> check)
    {
        foreach (bool caps in new[] { false, true })
        foreach (bool shift in new[] { false, true })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            route.Router.PrintableTranslationForTesting = (_, _) => (0, "");
            route.Router.InvokeForTestingAsync(() => typeof(InputRouter).GetField("_textCapsLock", Private)!.SetValue(route.Router, caps))
                .GetAwaiter().GetResult();
            if (shift) route.Key(0xA0, true);
            route.Key(0x41, true); route.Key(0x41, false);
            if (shift) route.Key(0xA0, false);
            check(Render(route.Take()) == (shift ^ caps ? "A" : "a"), "Fallback letter casing must follow Shift XOR the remote typing Caps Lock state.");
        }
        using var vni = new Route(TypingMode.VietnameseVni);
        vni.Router.PrintableTranslationForTesting = (_, _) => (0, "");
        vni.Type("tie6ng1 Vie6t5");
        check(Render(vni.Take()) == "tiếng Việt", "Unshifted digit fallback must preserve built-in VNI commands.");
    }

    private static void CheckPreservedLayoutAndBoundaries(Action<bool, string> check)
    {
        using (var route = new Route(TypingMode.VietnameseTelex))
        {
            route.Router.PrintableTranslationForTesting = (_, _) => (1, "q");
            route.Key(0x41, true); route.Key(0x41, false);
            check(Render(route.Take()) == "q" && route.Router.GetTypingDiagnosticsSnapshot().PrintableFallbacks == 0,
                "A valid layout-dependent character must win over a virtual-key Latin fallback.");
            route.Router.PrintableTranslationForTesting = (_, _) => (1, "é");
            route.Key(0x41, true); route.Key(0x41, false);
            check(Render(route.Take()) == "é", "Valid non-ASCII layout output must stay unchanged.");
        }
        foreach (var (key, text, shift) in new[] { (0xBE, ".", false), (0xBC, ",", false), (0xBF, "?", true), (0x31, "!", true) })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            route.Router.PrintableTranslationForTesting = (_, _) => (1, text);
            if (shift) route.Key(0xA0, true);
            route.Key(key, true); route.Key(key, false);
            if (shift) route.Key(0xA0, false);
            var output = route.Take();
            check(Render(output) == text && output.All(packet => packet.Type == "text"),
                "Valid OEM/shifted punctuation must retain its layout translation without falling back to raw keys.");
        }
        foreach (int key in new[] { 0x41, 0xBE, 0xBC, 0xBF })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            route.Router.PrintableTranslationForTesting = (_, _) => (-1, "´");
            route.Key(key, true); route.Key(key, false);
            var packets = route.Take();
            check(packets.Length == 2 && packets.All(packet => packet.Type == "key")
                && route.Router.GetTypingDiagnosticsSnapshot().PrintableFallbacks == 0,
                "A Windows dead key must retain its existing physical path and never be replaced by ASCII.");
        }
        foreach (int key in new[] { 0xBE, 0xBC, 0xBF, 0xBA })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            route.Router.PrintableTranslationForTesting = (_, _) => (0, "");
            route.Key(key, true); route.Key(key, false);
            check(route.Take().All(packet => packet.Type == "key") && route.Router.GetTypingDiagnosticsSnapshot().PrintableFallbacks == 0,
                "Failed layout-dependent OEM punctuation must not acquire a hard-coded QWERTY interpretation.");
        }
        using (var route = new Route(TypingMode.VietnameseVni))
        {
            route.Router.PrintableTranslationForTesting = (_, _) => (0, "");
            route.Key(0xA0, true); route.Key(0x31, true); route.Key(0x31, false); route.Key(0xA0, false);
            check(route.Take().All(packet => packet.Type == "key") && route.Router.GetTypingDiagnosticsSnapshot().PrintableFallbacks == 0,
                "A shifted top-row digit must not become an unshifted VNI tone command.");
        }
        foreach (int modifier in new[] { 0xA2, 0xA4, 0x5B })
        {
            using var route = new Route(TypingMode.VietnameseTelex);
            int translations = 0;
            route.Router.PrintableTranslationForTesting = (_, _) => { translations++; return (0, ""); };
            route.Key(modifier, true); route.Key(0x41, true); route.Key(0x41, false); route.Key(modifier, false);
            check(route.Take().All(packet => packet.Type == "key") && translations == 0
                && route.Router.GetTypingDiagnosticsSnapshot().ShortcutBypassKeyDowns == 2,
                "Ctrl/Alt/Windows shortcuts must bypass both translation and ASCII fallback.");
        }
        using (var route = new Route(TypingMode.Physical))
        {
            int translations = 0;
            route.Router.PrintableTranslationForTesting = (_, _) => { translations++; return (0, ""); };
            route.Type("as");
            var stats = route.Router.GetTypingDiagnosticsSnapshot();
            check(route.Take().All(packet => packet.Type == "key") && translations == 0
                && stats.PhysicalModeKeyDowns == 2 && stats.PrintableFallbacks == 0,
                "Physical mode must retain raw keys and never enter built-in Latin fallback.");
        }
    }

    private static void CheckSameModeReset(Action<bool, string> check)
    {
        using var route = new Route(TypingMode.VietnameseTelex);
        route.Router.PrintableTranslationForTesting = (key, _) => Latin(key);
        route.Type("tieeng");
        route.Take();
        check(route.Field<VietnameseComposer>("_composer").CurrentWord == "tiêng", "The same-mode reset probe must begin with live composition.");
        route.Router.ResetTypingAsync(TypingMode.VietnameseTelex).GetAwaiter().GetResult();
        check(!route.Router.IsRemote && route.Field<VietnameseComposer>("_composer").CurrentWord == ""
            && route.Take() is [{ Type: "leave" }], "Same-mode reset must discard an active word and return to A even without a modifier transition.");
        route.Key(0x78, true); route.Key(0x78, false); route.Take();
        route.Key(0xA2, true);
        route.Router.ResetTypingAsync(TypingMode.VietnameseTelex).GetAwaiter().GetResult();
        var release = route.Take();
        check(!route.Router.IsRemote && route.Router.IsController && route.Router.CanControl
            && route.Router.SwitchShortcut == new SwitchShortcut(0x78) && route.Router.TypingMode == TypingMode.VietnameseTelex,
            "Resetting an unchanged typing mode must return to A while preserving the shortcut, role and permission.");
        check(release.Length == 3 && release[0].Type == "key" && release[0].Key == 0xA2 && release[0].Down
            && release[1].Type == "key" && !release[1].Down && release[2].Type == "leave",
            "Explicit typing reset must balance prior remote modifiers before the leave packet.");
        check(route.Field<VietnameseComposer>("_composer").CurrentWord == "" && route.Field<HashSet<int>>("_textKeys").Count == 0
            && route.Field<IDictionary>("_physicalKeys").Contains(0xA2),
            "Typing reset must clear composition while preserving real physical state instead of guessing from injected key ups.");
        route.Key(0xA2, false);
        route.Key(0x78, true); route.Key(0x78, false);
        route.Type("as");
        check(Render(route.Take()) == "á" && route.Router.IsRemote, "The configured switch and a fresh Vietnamese word must work after same-mode reset.");
    }

    private static void CheckCounters(Action<bool, string> check)
    {
        using var route = new Route(TypingMode.VietnameseTelex);
        route.Router.PrintableTranslationForTesting = (key, _) => Latin(key);
        route.Type("as");
        var sent = route.Take();
        var senderStats = route.Router.GetTypingDiagnosticsSnapshot();
        check(senderStats.OutgoingTextPackets == 2 && senderStats.OutgoingNonAsciiCodeUnits == 1
            && senderStats.OutgoingBackspaces == 1 && senderStats.IncomingTextPackets == 0,
            "Sender typing counters must describe accepted text edits without recording their content.");
        using var receiver = new InputRouter();
        receiver.StartForTesting(); receiver.CanControl = true;
        receiver.ApplyRemoteBatchAsync([new Packet { Type = "enter", X = 1, Y = 2 }, .. sent.Where(packet => packet.Type == "text")])
            .GetAwaiter().GetResult();
        var received = receiver.GetTypingDiagnosticsSnapshot();
        check(received.IncomingTextPackets == 2 && received.IncomingNonAsciiCodeUnits == 1 && received.OutgoingTextPackets == 0
            && !received.IsController && received.IsRemote,
            "Receiver counters must record validated Unicode input independently from outgoing typing.");
        check(typeof(TypingDiagnosticsSnapshot).GetProperties().All(property => property.PropertyType == typeof(long)
            || property.PropertyType == typeof(bool) || property.PropertyType == typeof(TypingMode)),
            "Typing diagnostics must contain only counters, role flags and the selected mode, never payload strings or key codes.");
    }

    private static (int Length, string Text) Latin(int key) => (1, ((char)(key is >= 0x41 and <= 0x5A ? key + 32 : key)).ToString());
    private static string Render(IEnumerable<Packet> packets)
    {
        string text = "";
        foreach (var packet in packets.Where(packet => packet.Type == "text")) text = text[..(text.Length - packet.Dx)] + packet.Text;
        return text;
    }

    private sealed class Route : IDisposable
    {
        private readonly ConcurrentQueue<Packet> _packets = new();
        private readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal Route(TypingMode mode)
        {
            Router.StartForTesting(); Router.ConfigureController(true, ScreenEdge.Right); Router.TypingMode = mode;
            Router.SwitchShortcut = new(0x78);
            Router.Send = packet => { if (packet.Type == "typing-recovery-barrier") _drained.Set(); else _packets.Enqueue(packet); };
            Key(0x78, true); Key(0x78, false); Take();
            Router.InvokeForTestingAsync(() => typeof(InputRouter).GetField("_textCapsLock", Private)!.SetValue(Router, false))
                .GetAwaiter().GetResult();
        }
        internal void Key(int key, bool down) => Router.RoutePhysicalKeyForTestingAsync(key, down).GetAwaiter().GetResult();
        internal T Field<T>(string name) => (T)typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        internal void Type(string keys)
        {
            foreach (char character in keys)
            {
                bool shift = char.IsAsciiLetterUpper(character);
                int key = char.ToUpperInvariant(character);
                if (shift) Key(0xA0, true);
                Key(key, true); Key(key, false);
                if (shift) Key(0xA0, false);
            }
        }
        internal Packet[] Take()
        {
            _drained.Reset();
            Router.InvokeForTestingAsync(() => typeof(InputRouter).GetMethod("Queue", Private)!.Invoke(Router,
                [new Packet { Type = "typing-recovery-barrier" }])).GetAwaiter().GetResult();
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Typing recovery output did not flush.");
            var output = new List<Packet>();
            while (_packets.TryDequeue(out var packet)) output.Add(packet);
            return output.ToArray();
        }
        public void Dispose() { Router.Dispose(); _drained.Dispose(); }
    }
}
