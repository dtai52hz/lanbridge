using System.Collections;
using System.Collections.Concurrent;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckTypingRecoverySessionAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        var typingControl = Get<ComboBox>(a, "_typing");
        int originalSelection = typingControl.SelectedIndex;
        var originalTyping = source.TypingMode;
        var originalBinding = Get<SwitchShortcut?>(a, "_configuredSwitchShortcut");
        bool originalCapsLock = Get<bool>(source, "_textCapsLock");
        var originalTokenA = Get<CancellationTokenSource>(a, "_session");
        var originalTokenB = Get<CancellationTokenSource>(b, "_session");
        var translationProperty = typeof(InputRouter).GetProperty("PrintableTranslationForTesting", Private)!;
        var observerProperty = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        object? originalTranslation = translationProperty.GetValue(source);
        object? originalObserver = observerProperty.GetValue(receiver);
        var applied = new ConcurrentQueue<Packet>();
        observerProperty.SetValue(receiver, (Action<Packet>)applied.Enqueue);
        bool shifted = false;
        int toneAttempts = 0;

        async Task<bool> Key(int key, bool down) => await (Task<bool>)Call(source,
            "RoutePhysicalKeyForTestingAsync", key, down, 0, false)!;
        async Task Owner(Action action) => await (Task)Call(source, "InvokeForTestingAsync", action)!;
        async Task EnterAsync()
        {
            Check(await Key(0x78, true) && await Key(0x78, false) && source.IsRemote,
                "Typing recovery must enter B through the assigned F9 shortcut.");
            await Owner(() => Set(source, "_textCapsLock", false));
            await WaitUntilAsync(() => receiver.IsRemote, "Typing recovery enter was not applied on the TLS receiver.");
        }
        async Task TypeAsync(string keys)
        {
            bool consumed = true;
            foreach (char character in keys)
            {
                (int key, bool shift) = character switch
                {
                    '.' => (0xBE, false), ',' => (0xBC, false), '?' => (0xBF, true), '!' => (0x31, true),
                    _ => (char.ToUpperInvariant(character), char.IsAsciiLetterUpper(character)),
                };
                shifted = shift;
                if (shift) consumed &= await Key(0xA0, true);
                consumed &= await Key(key, true);
                consumed &= await Key(key, false);
                if (shift) consumed &= await Key(0xA0, false);
                shifted = false;
            }
            Check(consumed, "A printable typing probe unexpectedly left the active controller path.");
        }
        string Render(IEnumerable<Packet> packets)
        {
            string result = "";
            foreach (var packet in packets.Where(packet => packet.Type == "text"))
            {
                Check(packet.Dx >= 0 && packet.Dx <= result.Length,
                    "A Unicode correction reached B with an invalid or reordered suffix deletion.");
                result = result[..(result.Length - packet.Dx)] + packet.Text;
            }
            return result;
        }

        try
        {
            Check(Get<bool>(source, "_started") && !Get<bool>(source, "_nativeInputEnabled")
                && source.IsController && source.CanControl && receiver.CanControl,
                "Typing recovery requires the existing native-disabled source and approved TLS session.");
            typingControl.SelectedIndex = (int)TypingMode.VietnameseTelex;
            await Owner(() => { });
            Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x78));
            translationProperty.SetValue(source, (Func<int, int, (int Length, string Text)>)((key, _) =>
            {
                if (key == 0x53)
                {
                    int attempt = ++toneAttempts;
                    if (attempt == 1) return (0, "");
                    if (attempt == 2) return (1, "\u0001");
                }
                string text = key switch
                {
                    0xBE => ".", 0xBC => ",", 0xBF => "?", 0x31 when shifted => "!",
                    >= 0x41 and <= 0x5A => ((char)(shifted ? key : key + 32)).ToString(),
                    _ => ((char)key).ToString(),
                };
                return (text.Length, text);
            }));

            await EnterAsync();
            var beforeSource = source.GetTypingDiagnosticsSnapshot();
            var beforeReceiver = receiver.GetTypingDiagnosticsSnapshot();
            const string keys = "tieengs vieetj . , ? ! tieengs";
            const string expectedText = "tiếng việt . , ? ! tiếng";
            await TypeAsync(keys);
            await WaitUntilAsync(() => applied.Count(packet => packet.Type == "text") == keys.Length,
                "A single pass through the physical typing path did not deliver every Unicode edit to B.");
            var wordPackets = applied.ToArray();
            Check(Render(wordPackets) == expectedText && wordPackets.All(packet => packet.Type is "enter" or "text"),
                "Transient zero/control tone translations must produce correct Vietnamese and OEM punctuation on the first press over TLS.");
            var afterSource = source.GetTypingDiagnosticsSnapshot();
            var afterReceiver = receiver.GetTypingDiagnosticsSnapshot();
            Check(toneAttempts == 2 && afterSource.PrintableFallbacks - beforeSource.PrintableFallbacks == 2
                && afterSource.PrintableTranslationFailures - beforeSource.PrintableTranslationFailures == 2
                && afterSource.PrintableTranslationSuccesses > beforeSource.PrintableTranslationSuccesses,
                "Only the two deliberately failed tone translations should use Latin fallback.");
            Check(afterSource.EffectiveMode == TypingMode.VietnameseTelex
                && afterSource.OutgoingTextPackets - beforeSource.OutgoingTextPackets == keys.Length
                && afterSource.OutgoingNonAsciiCodeUnits > beforeSource.OutgoingNonAsciiCodeUnits
                && afterReceiver.IncomingTextPackets - beforeReceiver.IncomingTextPackets == keys.Length
                && afterReceiver.IncomingNonAsciiCodeUnits > beforeReceiver.IncomingNonAsciiCodeUnits,
                "A's outgoing Unicode counters and B's applied Unicode counters did not describe the same corrected text stream.");

            Check(Get<VietnameseComposer>(source, "_composer").CurrentWord == "tiếng",
                "The reset probe must start with active composition before a shortcut modifier ends that word.");
            int resetStart = applied.Count;
            Check(await Key(0xA2, true), "A held Ctrl must enter the normal remote shortcut route.");
            await (Task)Call(a, "ResetTypingFromUiAsync")!;
            await WaitUntilAsync(() => !source.IsRemote && !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "The UI typing reset did not return both input routers to A over the existing TLS session.");
            var resetPackets = applied.Skip(resetStart).ToArray();
            Check(resetPackets.Length == 3 && resetPackets[0].Type == "key" && resetPackets[0].Key == 0xA2 && resetPackets[0].Down
                && resetPackets[1].Type == "key" && resetPackets[1].Key == 0xA2 && !resetPackets[1].Down
                && resetPackets[2].Type == "leave" && Get<IDictionary>(receiver, "_receivedKeys").Count == 0,
                "Typing reset must balance held Ctrl before the leave packet, without retaining a key on B.");
            Check(Get<VietnameseComposer>(source, "_composer").CurrentWord == ""
                && source.TypingMode == TypingMode.VietnameseTelex && source.SwitchShortcut == new SwitchShortcut(0x78)
                && source.CanControl && receiver.CanControl,
                "Same-mode UI reset must clear composition while preserving the configured switch and session permission.");
            Check(!await Key(0xA2, false), "The real Ctrl release must remain local after the UI reset returns A.");

            int restartedAt = applied.Count;
            await EnterAsync();
            await TypeAsync("as");
            await WaitUntilAsync(() => applied.Skip(restartedAt).Count(packet => packet.Type == "text") == 2,
                "Fresh Vietnamese typing did not resume after same-mode reset and shortcut re-entry.");
            Check(Render(applied.Skip(restartedAt)) == "á" && toneAttempts == 3
                && source.GetTypingDiagnosticsSnapshot().PrintableFallbacks - beforeSource.PrintableFallbacks == 2,
                "Reset must start a new Vietnamese word and retain ordinary layout translation after transient failures end.");

            Check(typeof(TypingDiagnosticsSnapshot).GetProperties().All(property => property.PropertyType == typeof(long)
                || property.PropertyType == typeof(bool) || property.PropertyType == typeof(TypingMode)),
                "Typing diagnostics must contain only mode flags and numeric counters, without typed content.");
            foreach (var form in new[] { a, b })
            {
                string report = (string)Call(form, "BuildLatencyReport")!;
                Check(report.Contains("BỘ GÕ", StringComparison.Ordinal)
                    && !report.Contains(expectedText, StringComparison.Ordinal)
                    && !report.Contains(keys, StringComparison.Ordinal),
                    "Typing reports must describe counters without exposing typed text or the original key sequence.");
            }
            Check(ReferenceEquals(Get<CancellationTokenSource>(a, "_session"), originalTokenA)
                && ReferenceEquals(Get<CancellationTokenSource>(b, "_session"), originalTokenB)
                && !originalTokenA.IsCancellationRequested && !originalTokenB.IsCancellationRequested
                && !session.A.IsClosed && !session.B.IsClosed && !a.Visible && !b.Visible,
                "Typing fallback and reset must preserve the same hidden authenticated TLS session.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            foreach (int key in new[] { 0xA0, 0xA2, 0x78 }) await Key(key, false);
            source.ReleaseRemote();
            await Owner(() => { });
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => !receiver.IsRemote, "Typing recovery cleanup did not release B.");
            translationProperty.SetValue(source, originalTranslation);
            observerProperty.SetValue(receiver, originalObserver);
            typingControl.SelectedIndex = originalSelection;
            source.TypingMode = originalTyping;
            Call(a, "ApplySwitchShortcut", originalBinding);
            await Owner(() => Set(source, "_textCapsLock", originalCapsLock));
        }
    }
}
