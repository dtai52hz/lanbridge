using System.Diagnostics;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckInputAcknowledgementAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        // These diagnostics acknowledge the real receiving input pump, not target-app
        // rendering. The two forms still use no hooks, no physical input and no real clipboard.
        await Task.Delay(550); // A fresh slot in the production 2 Hz sampler.
        string marker = "INPUT_ACK_TEST_" + Guid.NewGuid().ToString("N");
        await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
        await WaitUntilAsync(() => receiver.IsRemote, "The input-ACK regression did not enter the receiver's no-native session.");
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var acknowledged = new ManualResetEventSlim();
        int applied = 0;
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)(packet =>
        {
            if (packet.Type == "key" && packet.Text == marker) Interlocked.Exchange(ref applied, 1);
        }));
        var pause = (Task)Call(receiver, "InvokeForTestingAsync", (Action)(() =>
        {
            paused.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The input-ACK test did not release its safe dispatcher pause.");
        }))!;
        Action<Packet>? acknowledgementObserver = null;
        try
        {
            Check(await Task.Run(() => paused.Wait(TimeSpan.FromSeconds(2))), "Could not pause the input dispatcher before its acknowledgement probe.");
            (source.Send ?? throw new InvalidOperationException("The session input sender is missing."))(
                new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true, Text = marker });
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "key" && packet.Text == marker),
                "The sampled key did not traverse the production sender wrapper and authenticated TLS connection.");
            var sample = session.AtB.Single(packet => packet.Type == "key" && packet.Text == marker);
            Check(Guid.TryParseExact(sample.Id, "N", out _) && sample.Length > 0,
                "Negotiated input diagnostics did not attach an opaque nonce and A's local stopwatch token.");
            await Task.Delay(120);
            Check(Volatile.Read(ref applied) == 0 && !session.AtA.Any(packet => packet.Type == "input-ack" && packet.Id == sample.Id),
                "B acknowledged an input sample before its receiving input pump had applied it.");
            bool appliedBeforeAcknowledgement = false;
            acknowledgementObserver = packet =>
            {
                if (packet.Type != "input-ack" || packet.Id != sample.Id || packet.Length != sample.Length) return;
                appliedBeforeAcknowledgement = Volatile.Read(ref applied) != 0;
                acknowledged.Set();
            };
            session.A.Received += acknowledgementObserver;
            resume.Set();
            // Keep the shared main UI stopped. Neither the receiver's acknowledgement
            // nor A's raw ACK handler may wait behind clipboard/OLE or UI diagnostics.
            Check(acknowledged.Wait(TimeSpan.FromSeconds(2)), "Input ACK delivery waited for the main window instead of the independent input pump and TLS reader.");
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            Check(appliedBeforeAcknowledgement, "The input ACK reached A before the no-native receiver finished its sampled action.");
            Check(Stopwatch.GetElapsedTime(sample.Length).TotalMilliseconds >= 100,
                "The source stopwatch token did not include the deliberately paused receiving input pump.");
            var latency = ReadInputLatency(a);
            Check(latency is { LastMilliseconds: >= 100, Samples: > 0 },
                "A's fast TLS receipt handler did not correlate its input-pump acknowledgement while the UI was paused.");
            long samplesBeforeInvalidReceipts = latency!.Samples;
            await session.B.SendAsync(new Packet { Type = "input-ack", Id = sample.Id, Length = sample.Length }); // duplicate
            await session.B.SendAsync(new Packet { Type = "input-ack", Id = Guid.NewGuid().ToString("N"), Length = sample.Length });
            await session.B.SendAsync(new Packet { Type = "input-ack", Id = "not-a-nonce", Length = sample.Length });
            await session.B.SendAsync(new Packet { Type = "input-ack", Id = sample.Id, Length = sample.Length + 1 });
            string drain = "INVALID_INPUT_ACK_DRAIN_" + marker;
            await session.B.SendAsync(new Packet { Type = "chat", Text = drain });
            await WaitUntilAsync(() => Log(a).Contains(drain, StringComparison.Ordinal), "Malformed diagnostic receipts held the session reader.");
            Check(ReadInputLatency(a)?.Samples == samplesBeforeInvalidReceipts,
                "An unknown, duplicate, malformed or mismatched input nonce altered the measured latency.");
            string report = (string)Call(a, "BuildLatencyReport")!;
            Check(!report.Contains(marker, StringComparison.Ordinal), "The latency report leaked the sampled input packet's content.");
            Check(!session.A.IsClosed && !session.B.IsClosed && !a.Visible && !b.Visible,
                "Input diagnostics closed a healthy TLS session or exposed a test window.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            resume.Set();
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            if (acknowledgementObserver != null) session.A.Received -= acknowledgementObserver;
            observer.SetValue(receiver, null);
            await session.A.SendAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            await WaitUntilAsync(() => !receiver.IsRemote, "The input-ACK regression failed to release its test key/session.");
        }
        await CheckCoalescedMoveAcknowledgementAsync(a, b, session, source, receiver);
        CheckStoppedInputSampler();
    }

    private static ConnectionLatency? ReadInputLatency(MainForm form)
    {
        var sampler = Get<object>(form, "_inputLatency");
        return (ConnectionLatency?)sampler.GetType().GetProperty("Latency", Private)!.GetValue(sampler);
    }

    private static void CheckStoppedInputSampler()
    {
        var type = typeof(MainForm).Assembly.GetType("LanBridge.InputLatencySampler", throwOnError: true)!;
        var sampler = Activator.CreateInstance(type, nonPublic: true)!;
        var sample = (Packet)Call(sampler, "Stamp", new Packet { Type = "key", Key = 0x41, Down = true })!;
        Check(sample.Id is { Length: 32 } && sample.Length > 0, "An active diagnostic sampler did not create its opaque correlation token.");
        Call(sampler, "Stop");
        Check(!(bool)Call(sampler, "Accept", new Packet { Type = "input-ack", Id = sample.Id, Length = sample.Length })!,
            "A stopped session's delayed acknowledgement was accepted after its pending input tokens were cleared.");
        Check(type.GetProperty("Latency", Private)!.GetValue(sampler) is null,
            "A delayed old-session receipt created latency measurements in a stopped sampler.");
    }

    private static async Task CheckCoalescedMoveAcknowledgementAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        await Task.Delay(550);
        using var paused = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var acknowledged = new ManualResetEventSlim();
        var pause = (Task)Call(receiver, "InvokeForTestingAsync", (Action)(() =>
        {
            paused.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The coalesced-input probe did not release its receiver.");
        }))!;
        string marker = "COALESCED_ACK_" + Guid.NewGuid().ToString("N");
        Packet? latestApplied = null;
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)(packet =>
        {
            if (packet.Type == "move" && packet.Text == marker + "LATEST") latestApplied = packet;
        }));
        Action<Packet>? onAcknowledged = null;
        try
        {
            Check(await Task.Run(() => paused.Wait(TimeSpan.FromSeconds(2))), "Could not pause the coalesced-input acknowledgement test.");
            // The ordered reader takes enter out of its inbox and waits for the input
            // owner, forcing the following adjacent moves to coalesce in SessionInbox.
            await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1, Y = 2 });
            await WaitUntilAsync(() => receiver.GetDiagnosticsSnapshot().PendingDispatcherWork > 0,
                "The input reader did not queue its held enter before the sampled movement.");
            source.Send!(new Packet { Type = "move", X = 100, Y = 200, Text = marker + "SAMPLED" });
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "move" && packet.Text == marker + "SAMPLED"),
                "The sampled movement did not reach the actual TLS receiver.");
            var sample = session.AtB.Single(packet => packet.Type == "move" && packet.Text == marker + "SAMPLED");
            Check(Guid.TryParseExact(sample.Id, "N", out _) && sample.Length > 0, "The coalesced movement lacked its negotiated input diagnostic nonce.");
            await session.A.SendAsync(new Packet { Type = "move", X = 54321, Y = 12345, Text = marker + "LATEST" });
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "move" && packet.Text == marker + "LATEST"),
                "The movement replacing a sampled position did not reach the receive inbox.");
            onAcknowledged = packet =>
            {
                if (packet.Type == "input-ack" && packet.Id == sample.Id && packet.Length == sample.Length) acknowledged.Set();
            };
            session.A.Received += onAcknowledged;
            resume.Set();
            Check(acknowledged.Wait(TimeSpan.FromSeconds(2)), "Replacing an obsolete movement discarded its useful latency receipt token.");
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            Check(latestApplied is { X: 54321, Y: 12345 } && latestApplied.Id == sample.Id && latestApplied.Length == sample.Length,
                "The input inbox retained an obsolete pointer position or copied old session metadata while carrying a diagnostic token.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            resume.Set();
            await pause.WaitAsync(TimeSpan.FromSeconds(2));
            if (onAcknowledged != null) session.A.Received -= onAcknowledged;
            observer.SetValue(receiver, null);
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            await WaitUntilAsync(() => !receiver.IsRemote, "The coalesced-input acknowledgement probe did not release its session.");
        }
    }
}
