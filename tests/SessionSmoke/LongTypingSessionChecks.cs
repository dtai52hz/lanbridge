using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckLongTypingSessionAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        const int rounds = 200; // 600 words without making the suite wait for wall-clock aging.
        var originalTyping = source.TypingMode;
        var originalBinding = Get<SwitchShortcut?>(a, "_configuredSwitchShortcut");
        bool originalCaps = Get<bool>(source, "_textCapsLock");
        var tokenA = Get<CancellationTokenSource>(a, "_session");
        var tokenB = Get<CancellationTokenSource>(b, "_session");
        var translation = typeof(InputRouter).GetProperty("PrintableTranslationForTesting", Private)!;
        var native = typeof(InputRouter).GetProperty("NativeInputBatchForTesting", Private)!;
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        object? originalTranslation = translation.GetValue(source), originalNative = native.GetValue(receiver),
            originalObserver = observer.GetValue(receiver);
        var document = new StringBuilder();
        var expected = new StringBuilder();
        var documentGate = new object();
        var applied = new ConcurrentQueue<Packet>();
        int nativeBatches = 0, unicodeUnits = 0, backspaces = 0;
        bool consumed = true;
        string Document() { lock (documentGate) return document.ToString(); }
        object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
        async Task Key(int key, bool down) => consumed &= await (Task<bool>)Call(source,
            "RoutePhysicalKeyForTestingAsync", key, down, 0, false)!;
        async Task Owner(Action action) => await (Task)Call(source, "InvokeForTestingAsync", action)!;
        async Task Type(string text)
        {
            foreach (char character in text)
            {
                int key = char.ToUpperInvariant(character);
                await Key(key, true); await Key(key, false);
            }
        }

        try
        {
            Check(Get<bool>(source, "_started") && !Get<bool>(source, "_nativeInputEnabled")
                && !Get<bool>(receiver, "_nativeInputEnabled") && source.CanControl && receiver.CanControl,
                "Long typing requires the existing approved native-disabled TLS session.");
            source.TypingMode = TypingMode.VietnameseTelex;
            Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x78));
            translation.SetValue(source, (Func<int, int, (int Length, string Text)>)((key, _) =>
                (1, ((char)(key is >= 0x41 and <= 0x5A ? key + 32 : key)).ToString())));
            observer.SetValue(receiver, (Action<Packet>)applied.Enqueue);
            // Observe exactly the production native arrays. StartForTesting makes it impossible
            // for this observer to install hooks or type into the user's real desktop.
            native.SetValue(receiver, (Action<Array>)(entries =>
            {
                lock (documentGate)
                {
                    nativeBatches++;
                    foreach (object entry in entries)
                    {
                        if ((uint)Field(entry, "Type") != 1) continue;
                        object keyboard = Field(Field(entry, "Union"), "Keyboard");
                        uint flags = (uint)Field(keyboard, "Flags");
                        if ((flags & 2) != 0) continue;
                        if ((flags & 4) != 0)
                        {
                            document.Append((char)(ushort)Field(keyboard, "Scan"));
                            unicodeUnits++;
                        }
                        else if ((ushort)Field(keyboard, "Vk") == 8)
                        {
                            if (document.Length == 0) throw new InvalidDataException("A correction erased before the test document.");
                            document.Length--; backspaces++;
                        }
                    }
                }
            }));
            var beforeA = source.GetTypingDiagnosticsSnapshot();
            var beforeB = receiver.GetTypingDiagnosticsSnapshot();
            await Key(0x78, true); await Key(0x78, false);
            await Owner(() => Set(source, "_textCapsLock", false));
            await WaitUntilAsync(() => receiver.IsRemote, "Long typing did not enter B over TLS.");
            var watch = Stopwatch.StartNew();
            for (int round = 0; round < rounds; round++)
            {
                await Type("mootj tieengs vieetj ");
                expected.Append("một tiếng việt ");
                if (round % 10 == 0)
                {
                    await Key(0xA2, true); await Key(0x43, true); await Key(0x43, false); await Key(0xA2, false);
                    Call(source, "QueueControllerMove", new Packet { Type = "move", X = round * 100, Y = 30000 });
                    Call(source, "SendWheel", 0, 120);
                }
                if (round % 20 == 19)
                {
                    string checkpoint = expected.ToString();
                    await WaitUntilAsync(() => Document() == checkpoint,
                        "Long-session Unicode correction arrays changed, dropped or reordered a word.");
                    Check(Document() == checkpoint, $"The native-array document diverged after {(round + 1) * 3} words.");
                }
            }
            await Key(0x78, true); await Key(0x78, false);
            await WaitUntilAsync(() => !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "Long typing did not finish B's native cleanup at leave.");
            var afterA = source.GetTypingDiagnosticsSnapshot();
            var afterB = receiver.GetTypingDiagnosticsSnapshot();
            Check(consumed && Document() == expected.ToString() && Get<IDictionary>(receiver, "_receivedKeys").Count == 0,
                "Mixed shortcuts, mouse traffic and repeated Telex edits must retain text and release all remote keys.");
            Check(unicodeUnits == 25 * rounds && backspaces == 10 * rounds && nativeBatches > 0,
                "Long typing must exercise actual non-ASCII native arrays and correction Backspaces rather than literal final words.");
            Check(afterA.OutgoingTextPackets - beforeA.OutgoingTextPackets == 21 * rounds
                && afterB.IncomingTextPackets - beforeB.IncomingTextPackets == 21 * rounds
                && afterA.OutgoingNonAsciiCodeUnits - beforeA.OutgoingNonAsciiCodeUnits
                    == afterB.IncomingNonAsciiCodeUnits - beforeB.IncomingNonAsciiCodeUnits,
                $"Long-session source/receiver counters must cover the same ordered Unicode edits (A text={afterA.OutgoingTextPackets - beforeA.OutgoingTextPackets}, B text={afterB.IncomingTextPackets - beforeB.IncomingTextPackets}, A accents={afterA.OutgoingNonAsciiCodeUnits - beforeA.OutgoingNonAsciiCodeUnits}, B accents={afterB.IncomingNonAsciiCodeUnits - beforeB.IncomingNonAsciiCodeUnits}).");
            Check(ReferenceEquals(Get<CancellationTokenSource>(a, "_session"), tokenA)
                && ReferenceEquals(Get<CancellationTokenSource>(b, "_session"), tokenB)
                && !tokenA.IsCancellationRequested && !tokenB.IsCancellationRequested
                && !session.A.IsClosed && !session.B.IsClosed && !a.Visible && !b.Visible,
                "A long sequence of words must retain the same hidden authenticated TLS session.");
            Console.WriteLine($"Long typing: {rounds * 3} words, {unicodeUnits} Unicode units, {backspaces} correction Backspaces, {nativeBatches} native arrays, {watch.Elapsed.TotalSeconds:F1}s; exact document preserved.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            foreach (int key in new[] { 0x43, 0xA2, 0x78 }) await Key(key, false);
            source.ReleaseRemote();
            await Owner(() => { });
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => !receiver.IsRemote, "Long typing cleanup did not release B.");
            translation.SetValue(source, originalTranslation);
            native.SetValue(receiver, originalNative);
            observer.SetValue(receiver, originalObserver);
            source.TypingMode = originalTyping;
            Call(a, "ApplySwitchShortcut", originalBinding);
            await Owner(() => Set(source, "_textCapsLock", originalCaps));
        }
    }
}
