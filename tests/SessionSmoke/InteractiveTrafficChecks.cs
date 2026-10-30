using System.Collections.Concurrent;
using System.Diagnostics;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckInteractiveTrafficAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        Checks(b)["Clipboard"].Checked = true;
        await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").Clipboard, "Interactive test did not enable the fake clipboard permission.");
        try
        {
            await CheckContinuousInputAsync(a, b, session, source, receiver, false);
            await CheckContinuousInputAsync(a, b, session, source, receiver, true);
            await CheckInputDuringFileIoAsync(a, b, session, source, receiver);
            await CheckImmediateClipboardGrantAsync(a, b, session);
            await CheckOrderedInputFeaturesAsync(a, b, session, source, receiver);
        }
        finally
        {
            Checks(b)["Clipboard"].Checked = false;
            await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Clipboard, "Interactive test did not restore clipboard permission.");
        }
    }

    private static async Task CheckContinuousInputAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver, bool clipboardWhileUiPaused, string? scenario = null)
    {
        string marker = "INTERACTIVE_" + Guid.NewGuid().ToString("N") + "_";
        var sendTimes = new ConcurrentDictionary<string, long>();
        var applied = new ConcurrentQueue<(Packet Packet, double Milliseconds)>();
        var expected = new List<Packet>();
        using var begin = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)(packet =>
        {
            if (packet.Text is not { } id || !sendTimes.TryGetValue(id, out long sentAt)) return;
            applied.Enqueue((packet, Stopwatch.GetElapsedTime(sentAt).TotalMilliseconds));
            if (packet.Type == "leave") completed.Set();
        }));
        Exception? failure = null;
        var sender = Task.Run(async () =>
        {
            begin.Wait();
            int sequence = 0;
            async Task SendInputAsync(Packet packet)
            {
                packet.Text = marker + sequence++;
                expected.Add(packet);
                sendTimes[packet.Text] = Stopwatch.GetTimestamp();
                await session.A.SendAsync(packet);
            }
            try
            {
                if (clipboardWhileUiPaused) await session.A.SendAsync(new Packet { Type = "clipboard", Text = marker + "CLIPBOARD" });
                await SendInputAsync(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
                for (int index = 0; index < 40; index++)
                {
                    int x = 1000 + index * 31, y = 2000 + index * 17;
                    await SendInputAsync(new Packet { Type = "move", X = x, Y = y });
                    if (index % 4 == 0)
                    {
                        await SendInputAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true });
                        await SendInputAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
                    }
                    if (index % 5 == 0)
                    {
                        await SendInputAsync(new Packet { Type = "button", Button = "left", Down = true, X = x, Y = y });
                        await SendInputAsync(new Packet { Type = "button", Button = "left", Down = false, X = x, Y = y });
                    }
                    if (index % 8 == 0) await SendInputAsync(new Packet { Type = "wheel", X = x, Y = y, Dy = index % 16 == 0 ? 120 : -120 });
                    await Task.Delay(8);
                }
                await SendInputAsync(new Packet { Type = "leave", Name = "Right", X = 2209, Y = 2663 });
            }
            catch (Exception error) { failure = error; completed.Set(); }
        });
        bool completedWhileUiPaused = true;
        try
        {
            begin.Set();
            if (clipboardWhileUiPaused) completedWhileUiPaused = completed.Wait(TimeSpan.FromMilliseconds(900));
            await sender.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => completed.IsSet, "Continuous move/click/key input failed to reach the receiver.");
            await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => { }))!);
            var actual = applied.ToArray();
            double[] latency = actual.Select(item => item.Milliseconds).Order().ToArray();
            double median = latency.Length == 0 ? double.PositiveInfinity : latency[latency.Length / 2];
            double p95 = latency.Length == 0 ? double.PositiveInfinity : latency[(latency.Length - 1) * 95 / 100];
            Console.WriteLine($"Continuous move/click/key {scenario ?? (clipboardWhileUiPaused ? "with clipboard/UI stalled" : "normal")}: {actual.Length} applied; median={median:F1}ms, p95={p95:F1}ms; completed while UI paused={completedWhileUiPaused}.");
            Check(failure is null && !session.A.IsClosed && !session.B.IsClosed,
                "Interactive traffic unexpectedly closed the TLS session: " + failure?.Message);
            Check(actual.Where(item => item.Packet.Type != "move").Select(item => InputBoundary(item.Packet))
                .SequenceEqual(expected.Where(packet => packet.Type != "move").Select(InputBoundary)),
                "Continuous movement dropped or reordered a click, wheel, keyboard or session boundary.");
            Check(completedWhileUiPaused,
                "A clipboard packet held subsequent movement, clicks and typing behind the paused main window.");
            Check(p95 < 250, "Continuous input accumulated more than 250ms of avoidable session-dispatch latency.");
            Check(!receiver.IsRemote && !a.Visible && !b.Visible, "Interactive regression left input active or exposed a test window.");
            AssertNoHooks(source, receiver);
        }
        finally { begin.Set(); await sender.WaitAsync(TimeSpan.FromSeconds(5)); observer.SetValue(receiver, null); }
    }

    private static async Task CheckInputDuringFileIoAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        var files = Get<FileTransfer>(b, "_files");
        var receiveGate = Get<SemaphoreSlim>(files, "_receiveGate");
        string transfer = Guid.NewGuid().ToString("N"), staleText = "STALE_CLIPBOARD_" + transfer;
        await receiveGate.WaitAsync();
        try
        {
            await session.A.SendAsync(new Packet { Type = "file-offer", Id = transfer, Name = "paused-io.bin", Length = 0 });
            await session.A.SendAsync(new Packet { Type = "clipboard", Text = staleText });
            // Only the logical feature flag is set: no recorder or capture device
            // is ever started. Its queued OFF must survive an unrelated revocation.
            Checks(b)["Recording"].Checked = true;
            Set(b, "_recordingOn", true);
            await session.A.SendAsync(new Packet { Type = "feature", Name = "recording", Allow = false, Id = transfer });
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "clipboard" && packet.Text == staleText),
                "Could not queue a clipboard update behind a paused file receive operation.");
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "feature" && packet.Id == transfer),
                "Could not queue a recording OFF request behind a paused file receive operation.");
            // These local revocations run while the old work is already on the wire.
            // Re-enabling a permission must not give that queued clipboard a new lease.
            Checks(b)["Clipboard"].Checked = false;
            Checks(b)["Clipboard"].Checked = true;
            Checks(b)["Privacy"].Checked = true;
            Checks(b)["Privacy"].Checked = false;
            await CheckContinuousInputAsync(a, b, session, source, receiver, false, "with file I/O stalled");
        }
        finally { receiveGate.Release(); }
        await WaitUntilAsync(() => session.AtA.Any(packet => packet.Type == "file-accept" && packet.Id == transfer),
            "The resumed file worker did not process its queued file offer.");
        await session.A.SendAsync(new Packet { Type = "file-cancel", Id = transfer });
        await WaitUntilAsync(() => Get<FileStream?>(files, "_incoming") is null,
            "The file I/O regression left a temporary receive stream open.");
        string drained = "BACKGROUND_DRAIN_" + transfer;
        await session.A.SendAsync(new Packet { Type = "chat", Text = drained });
        await WaitUntilAsync(() => Log(b).Contains(drained, StringComparison.Ordinal), "The background FIFO failed to drain after file cancellation.");
        Check(Get<IClipboardAccess>(b, "_clipboard").Read().Text != staleText,
            "A clipboard update queued before local revoke/re-enable was published after the new grant.");
        Check(!Get<bool>(b, "_recordingOn"),
            "An unrelated permission revocation discarded a queued feature OFF request and left its logical recording state active.");
        Checks(b)["Recording"].Checked = false;
    }

    private static async Task CheckImmediateClipboardGrantAsync(MainForm a, MainForm b, Session session)
    {
        Checks(b)["Clipboard"].Checked = false;
        await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Clipboard, "Could not prepare the remote clipboard-grant barrier.");
        string text = "IMMEDIATE_CLIPBOARD_GRANT_" + Guid.NewGuid().ToString("N");
        using var begin = new ManualResetEventSlim();
        using var arrived = new ManualResetEventSlim();
        var send = Task.Run(async () =>
        {
            begin.Wait();
            await session.B.SendAsync(new Packet { Type = "clipboard", Text = text });
            var timeout = Stopwatch.StartNew();
            while (!session.AtA.Any(packet => packet.Type == "clipboard" && packet.Text == text))
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Immediate clipboard data never reached the controller's TLS reader.");
                await Task.Delay(2);
            }
            arrived.Set();
        });
        Checks(b)["Clipboard"].Checked = true;
        begin.Set();
        // Both the authority's grant and its data arrive while the shared test UI is
        // deliberately paused, so A must snapshot permission after the ordered grant.
        Check(arrived.Wait(TimeSpan.FromSeconds(2)), "Could not receive a remote permission grant and its following data before the UI resumed.");
        await send.WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => Get<IClipboardAccess>(a, "_clipboard").Read().Text == text,
            "The controller rejected clipboard data that immediately followed the authority's new permission grant.");
    }

    private static async Task CheckOrderedInputFeaturesAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        var route = typeof(MainForm).GetMethod("IsInputBarrier", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        bool IsBarrier(Packet packet) => (bool)route.Invoke(null, new object[] { packet })!;
        foreach (bool enabled in new[] { false, true })
        {
            Check(IsBarrier(new Packet { Type = "feature", Name = "privacy", Allow = enabled }), "Both privacy toggles must be barriers before subsequent input.");
            Check(IsBarrier(new Packet { Type = "feature", Name = "block", Allow = enabled }), "Both local-input lock toggles must remain ordered before subsequent input.");
            Check(!IsBarrier(new Packet { Type = "feature", Name = "audio", Allow = enabled })
                && !IsBarrier(new Packet { Type = "feature", Name = "recording", Allow = enabled }),
                "Audio and recording controls must not hold input behind background device or disk work.");
        }
        Checks(b)["BlockInput"].Checked = true;
        await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").BlockInput, "Could not grant the no-native local-input lock regression permission.");
        string marker = "ORDERED_BLOCK_" + Guid.NewGuid().ToString("N");
        using var begin = new ManualResetEventSlim();
        using var arrived = new ManualResetEventSlim();
        using var keyApplied = new ManualResetEventSlim();
        bool lockedWhenKeyApplied = false;
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)(packet =>
        {
            if (packet.Type == "key" && packet.Down && packet.Text == marker)
            {
                lockedWhenKeyApplied = Get<bool>(receiver, "_blockLocal");
                keyApplied.Set();
            }
        }));
        var sender = Task.Run(async () =>
        {
            begin.Wait();
            await session.A.SendAsync(new Packet { Type = "feature", Name = "block", Allow = true });
            await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
            await session.A.SendAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true, Text = marker });
            await session.A.SendAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            var timeout = Stopwatch.StartNew();
            while (!session.AtB.Any(packet => packet.Type == "key" && packet.Text == marker))
            {
                if (timeout.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("The ordered-lock probe did not reach the receiver's TLS reader.");
                await Task.Delay(2);
            }
            arrived.Set();
        });
        try
        {
            begin.Set();
            Check(arrived.Wait(TimeSpan.FromSeconds(2)), "Could not receive an input-lock toggle and its following key while the test UI was paused.");
            Check(!keyApplied.Wait(TimeSpan.FromMilliseconds(100)), "A remote action overtook its preceding input-lock barrier while the UI was paused.");
            await sender.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => keyApplied.IsSet && !receiver.IsRemote, "The input-lock barrier did not resume and release after the UI became available.");
            Check(lockedWhenKeyApplied, "The key was applied before its ordered local-input lock took effect on the no-native input owner.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            begin.Set();
            await sender.WaitAsync(TimeSpan.FromSeconds(3));
            observer.SetValue(receiver, null);
            Checks(b)["BlockInput"].Checked = false;
        }
    }

    private sealed class MemoryClipboard : IClipboardAccess
    {
        private uint _sequence;
        private ClipboardContent _content = new();
        public uint SequenceNumber => _sequence;
        public ClipboardContent Read() => _content;
        public void SetText(string text) { _content = new(Text: text); _sequence++; }
        public void SetFiles(string[] paths) { _content = new(Files: paths.ToArray()); _sequence++; }
        public void SetImage(byte[] png) { _content = new(ImagePng: png.ToArray()); _sequence++; }
        public void Clear() { _content = new(); _sequence++; }
    }
}
