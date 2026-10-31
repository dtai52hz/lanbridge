using System.Collections;
using System.Collections.Concurrent;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckSwitchShortcutSessionAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        var originalBinding = Get<SwitchShortcut?>(a, "_configuredSwitchShortcut");
        var originalTyping = source.TypingMode;
        bool originalInputPermission = Checks(b)["Input"].Checked;
        bool originallyHiddenA = Get<bool>(a, "_hiddenInTray"), originallyHiddenB = Get<bool>(b, "_hiddenInTray");
        var originalTokenA = Get<CancellationTokenSource>(a, "_session");
        var originalTokenB = Get<CancellationTokenSource>(b, "_session");
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        object? originalObserver = observer.GetValue(receiver);
        var applied = new ConcurrentQueue<Packet>();
        observer.SetValue(receiver, (Action<Packet>)applied.Enqueue);

        async Task<bool> Key(int key, bool down) => await (Task<bool>)Call(source,
            "RoutePhysicalKeyForTestingAsync", key, down, 0, false)!;
        async Task Applied(int count, string message) => await WaitUntilAsync(() => applied.Count >= count, message);
        async Task Owner() => await (Task)Call(source, "InvokeForTestingAsync", (Action)(() => { }))!;
        try
        {
            // StartForTesting never installs hooks or injects input. Both the actual physical-key
            // decision path and A's existing authenticated sender are exercised without touching Windows.
            Call(source, "StartForTesting");
            source.TypingMode = TypingMode.Physical;
            Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x78)); // F9.
            Call(a, "HideToTray"); Call(b, "HideToTray");
            Check(!a.Visible && !b.Visible && Get<bool>(a, "_hiddenInTray") && Get<bool>(b, "_hiddenInTray"),
                "The shortcut integration must keep both preview windows hidden in tray state.");

            int tlsStart = session.AtB.Count;
            Check(await Key(0x78, true) && source.IsRemote, "The configured F9 must enter B through A's real sender while A is hidden.");
            for (int repeat = 0; repeat < 4; repeat++)
                Check(await Key(0x78, true) && source.IsRemote, "F9 autorepeat must stay consumed without toggling back.");
            Check(await Key(0x78, false), "F9 release must be consumed after entering B.");
            await Applied(1, "The hidden B input worker did not apply the shortcut's TLS enter packet.");
            var first = applied.ToArray();
            Check(first.Length == 1 && first[0].Type == "enter" && Math.Abs(first[0].X - 32768) <= 1
                && Math.Abs(first[0].Y - 32768) <= 1 && receiver.IsRemote,
                "The shortcut must enter B at its normalized center with no leaked F9 key event.");
            Check(await Key(0x78, true) && !source.IsRemote && await Key(0x78, false),
                "The next F9 press must return A while both main windows remain hidden.");
            await Applied(2, "The hidden B input worker did not apply the shortcut's TLS leave packet.");
            Check(!receiver.IsRemote && applied.Last().Type == "leave"
                && !session.AtB.Skip(tlsStart).Any(packet => packet.Type == "key" && packet.Key == 0x78),
                "A bare shortcut must emit only session boundaries and release B cleanly.");

            var chord = new SwitchShortcut(0x20, ShortcutModifiers.Control | ShortcutModifiers.Alt);
            Call(a, "ApplySwitchShortcut", chord);
            int chordStart = applied.Count;
            Check(!await Key(0xA3, true) && !await Key(0xA5, true),
                "Right Ctrl and Alt must stay local until the switching trigger arrives.");
            Check(await Key(0x20, true) && source.IsRemote && await Key(0x20, false),
                "Ctrl+Alt+Space must enter B using the configured chord.");
            Check(await Key(0xA5, false) && await Key(0xA3, false), "The chord's consumed modifiers must not leak their ups to B.");
            await Applied(chordStart + 1, "The chord enter did not cross TLS and finish on B.");
            Check(applied.Skip(chordStart).Count() == 1 && applied.Last().Type == "enter",
                "Entry must suppress Space and both shortcut modifiers from B's applied input.");

            // These modifiers begin after entry, so B legitimately sees their downs before the
            // trigger requests a return. The return must balance all keys before its leave.
            await Key(0x41, true);
            await Key(0xA2, true); await Key(0xA4, true);
            Check(await Key(0x20, true) && !source.IsRemote && await Key(0x20, false),
                "The same chord must return from B without forwarding its Space trigger.");
            Check(await Key(0xA4, false) && await Key(0xA2, false) && !await Key(0x41, false),
                "Return must consume chord modifier ups and let the unrelated release stay local on A.");
            await Applied(chordStart + 8, "The chord return did not release all held inputs and leave B.");
            var chordPackets = applied.Skip(chordStart).ToArray();
            Check(chordPackets.Length == 8 && chordPackets[0].Type == "enter" && chordPackets[^1].Type == "leave"
                && !chordPackets.Any(packet => packet.Type == "key" && packet.Key == 0x20),
                "Chord switching changed the number or order of TLS input boundaries.");
            foreach (int key in new[] { 0x41, 0xA2, 0xA4 })
                Check(chordPackets.Count(packet => packet.Type == "key" && packet.Key == key && packet.Down) == 1
                    && chordPackets.Count(packet => packet.Type == "key" && packet.Key == key && !packet.Down) == 1,
                    "Chord return must balance every prior key down on B exactly once.");
            Check(!receiver.IsRemote && Get<IDictionary>(receiver, "_receivedKeys").Count == 0,
                "The receiving router must not retain a held key after chord return.");

            Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x78));
            Checks(b)["Input"].Checked = false;
            await WaitUntilAsync(() => !source.CanControl && !receiver.CanControl && !Get<Permissions>(a, "_permissions").Input,
                "The host's local input revoke did not reach the shortcut admission gate.");
            int revokedStart = applied.Count;
            Check(!await Key(0x78, true) && !source.IsRemote && !await Key(0x78, false),
                "A revoked shortcut must keep the key local without entering B.");
            Check(applied.Count == revokedStart, "A denied shortcut unexpectedly emitted receiver input.");
            Checks(b)["Input"].Checked = true;
            await WaitUntilAsync(() => source.CanControl && receiver.CanControl, "Input regrant did not restore shortcut admission.");
            Check(await Key(0x78, true) && await Key(0x78, false), "A fresh shortcut press must work after input is granted again.");
            await Applied(revokedStart + 1, "The regranted shortcut did not enter B.");

            await source.SuspendSwitchShortcutAsync();
            await Applied(revokedStart + 2, "Preparing shortcut capture did not leave B over the live TLS session.");
            Check(!source.IsRemote && !receiver.IsRemote && source.SwitchShortcut is null,
                "Awaiting capture preparation must return to A and temporarily disable the input binding.");
            var saved = (UserPreferences)Call(a, "CapturePreferences")!;
            Check(saved.SwitchShortcut == new SwitchShortcut(0x78)
                && Get<SwitchShortcut?>(a, "_configuredSwitchShortcut") == saved.SwitchShortcut,
                "Temporary capture suspension must not erase the configured or persisted shortcut.");
            Check(!await Key(0x78, true) && !await Key(0x78, false),
                "A fresh key must reach local capture while the old binding is suspended.");
            Call(a, "ApplySwitchShortcut", saved.SwitchShortcut);
            await Owner();
            Check(source.SwitchShortcut == saved.SwitchShortcut,
                "Restoring a binding after capture must reactivate the configured shortcut.");

            Check(ReferenceEquals(Get<CancellationTokenSource>(a, "_session"), originalTokenA)
                && ReferenceEquals(Get<CancellationTokenSource>(b, "_session"), originalTokenB)
                && !originalTokenA.IsCancellationRequested && !originalTokenB.IsCancellationRequested
                && !session.A.IsClosed && !session.B.IsClosed,
                "Hidden shortcut switches, capture and permission changes must preserve the same authenticated TLS session.");
            Check(!a.Visible && !b.Visible && Get<NotifyIcon?>(a, "_trayIcon") is null && Get<NotifyIcon?>(b, "_trayIcon") is null,
                "Shortcut integration must not show windows or publish a real tray notification icon.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            source.ReleaseRemote();
            await Owner();
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => !receiver.IsRemote, "Shortcut regression cleanup did not release B.");
            Call(a, "ApplySwitchShortcut", originalBinding);
            source.TypingMode = originalTyping;
            Checks(b)["Input"].Checked = originalInputPermission;
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => source.CanControl == originalInputPermission && receiver.CanControl == originalInputPermission,
                    "Shortcut regression did not restore the original host input permission.");
            observer.SetValue(receiver, originalObserver);
            if (!originallyHiddenA) Call(a, "RestoreFromTray");
            if (!originallyHiddenB) Call(b, "RestoreFromTray");
            await Owner();
        }
        Check(!source.IsRemote && !receiver.IsRemote && !a.Visible && !b.Visible
            && Get<bool>(a, "_hiddenInTray") == originallyHiddenA && Get<bool>(b, "_hiddenInTray") == originallyHiddenB,
            "Shortcut integration must restore hidden-window state and leave input inactive for later tests.");
    }
}
