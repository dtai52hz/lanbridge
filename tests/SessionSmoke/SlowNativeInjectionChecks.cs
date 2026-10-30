using System.Collections.Concurrent;
using System.Diagnostics;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckSlowNativeInjectionAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        await CheckContinuousSlowNativeInputAsync(a, b, session, source, receiver);
        await CheckHookOwnerDuringNativeWaitAsync(session, source, receiver);
        await CheckHeldNativeInputAcknowledgementAsync(a, b, session, source, receiver, revokeInput: false);
        await CheckHeldNativeInputAcknowledgementAsync(a, b, session, source, receiver, revokeInput: true);
    }

    private static async Task CheckHookOwnerDuringNativeWaitAsync(Session session, InputRouter source, InputRouter receiver)
    {
        // This callback replaces SendInput only when StartForTesting explicitly disabled
        // native injection. It models a stalled Windows/global-hook path without touching
        // the user's desktop, and must not occupy the hook/state-owner message loop.
        var nativeObserver = typeof(InputRouter).GetProperty("NativeInputRequestedForTesting", Private)!;
        using var nativeStarted = new ManualResetEventSlim();
        using var nativeResume = new ManualResetEventSlim();
        using var ownerResponded = new ManualResetEventSlim();
        int nativeThread = 0, ownerThread = 0;
        await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => ownerThread = Environment.CurrentManagedThreadId))!);
        nativeObserver.SetValue(receiver, (Action<int>)(_ =>
        {
            nativeThread = Environment.CurrentManagedThreadId;
            nativeStarted.Set();
            if (!nativeResume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The slow-native-input regression failed to release its fake Windows call.");
        }));
        Task? ownerProbe = null;
        bool ownerRespondedDuringNativeCall = false;
        try
        {
            await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
            Check(await Task.Run(() => nativeStarted.Wait(TimeSpan.FromSeconds(2))), "The real receive pipeline did not reach its safe native-injection observer.");
            ownerProbe = (Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => ownerResponded.Set()))!;
            ownerRespondedDuringNativeCall = await Task.Run(() => ownerResponded.Wait(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            nativeResume.Set();
            if (ownerProbe != null) await ownerProbe.WaitAsync(TimeSpan.FromSeconds(2));
            nativeObserver.SetValue(receiver, null);
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            await WaitUntilAsync(() => !receiver.IsRemote, "The native-wait probe did not release its no-native input session.");
        }
        Console.WriteLine($"Held native-injection call: hook owner responsive={ownerRespondedDuringNativeCall}; separate native worker={nativeThread != ownerThread}.");
        Check(ownerRespondedDuringNativeCall && nativeThread != ownerThread,
            "A slow Windows input call still blocks the same STA that must process local hooks, emergency release and incoming state.");
        AssertNoHooks(source, receiver);
    }

    private static async Task CheckContinuousSlowNativeInputAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        const int fakeNativeDelayMilliseconds = 35;
        string marker = "SLOW_NATIVE_" + Guid.NewGuid().ToString("N") + "_";
        var expected = new List<Packet>();
        var sentAt = new ConcurrentDictionary<string, long>();
        var applied = new ConcurrentQueue<(Packet Packet, double Delay)>();
        using var left = new ManualResetEventSlim();
        var nativeObserver = typeof(InputRouter).GetProperty("NativeInputRequestedForTesting", Private)!;
        var packetObserver = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        int nativeCalls = 0, nativeInputsRequested = 0;
        nativeObserver.SetValue(receiver, (Action<int>)(count =>
        {
            Interlocked.Increment(ref nativeCalls);
            Interlocked.Add(ref nativeInputsRequested, count);
            Thread.Sleep(fakeNativeDelayMilliseconds);
        }));
        packetObserver.SetValue(receiver, (Action<Packet>)(packet =>
        {
            if (packet.Text is not { } id || !sentAt.TryGetValue(id, out long timestamp)) return;
            applied.Enqueue((packet, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds));
            if (packet.Type == "leave") left.Set();
        }));
        var productionTime = Stopwatch.StartNew();
        var sender = Task.Run(async () =>
        {
            int sequence = 0;
            void Send(Packet packet)
            {
                packet.Text = marker + sequence++;
                expected.Add(packet);
                sentAt[packet.Text] = Stopwatch.GetTimestamp();
                source.Send!(packet);
            }
            Send(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
            for (int group = 0; group < 48; group++)
            {
                int x = 1000 + group * 73, y = 2000 + group * 29;
                Send(new Packet { Type = "move", X = x, Y = y });
                if (group % 8 == 0)
                {
                    Send(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true });
                    Send(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
                }
                if (group % 8 == 4)
                {
                    Send(new Packet { Type = "button", Button = "left", Down = true, X = x, Y = y });
                    Send(new Packet { Type = "button", Button = "left", Down = false, X = x, Y = y });
                }
                if (group % 12 == 0) Send(new Packet { Type = "wheel", X = x, Y = y, Dy = group % 24 == 0 ? 120 : -120 });
                if (group % 16 == 0) Send(new Packet { Type = "text", Dx = 0 });
                await Task.Delay(8);
            }
            Send(new Packet { Type = "leave", Name = "Right", X = 4431, Y = 3363 });
        });
        try
        {
            await sender.WaitAsync(TimeSpan.FromSeconds(4));
            productionTime.Stop();
            Check(await Task.Run(() => left.Wait(TimeSpan.FromSeconds(5))), "Slow native calls left the receive pipeline replaying stale input for more than five seconds.");
            string drained = marker + "DRAINED";
            await session.A.SendAsync(new Packet { Type = "chat", Text = drained });
            await WaitUntilAsync(() => Log(b).Contains(drained, StringComparison.Ordinal),
                "The input reader advanced past leave before completing its ordered native-injection job.");
            var actual = applied.ToArray();
            var lastMove = actual.LastOrDefault(item => item.Packet.Type == "move");
            int calls = Volatile.Read(ref nativeCalls);
            Console.WriteLine($"Continuous input with fake {fakeNativeDelayMilliseconds}ms Windows calls: {calls} native jobs, {actual.Length} applied packets, {productionTime.ElapsedMilliseconds}ms producer, latest-move age={lastMove.Delay:F1}ms.");
            Check(!session.A.IsClosed && !session.B.IsClosed, "Sustained slow native input closed an otherwise healthy authenticated TLS session.");
            Check(actual.Where(item => item.Packet.Type != "move").Select(item => InputBoundary(item.Packet))
                .SequenceEqual(expected.Where(packet => packet.Type != "move").Select(InputBoundary)),
                "Batching slow native input lost or reordered a key, click, wheel, Unicode edit or enter/leave boundary.");
            Check(lastMove.Packet is { X: 4431, Y: 3363 } && lastMove.Delay < 350,
                "A slow native call caused old pointer positions to accumulate instead of applying the latest position promptly.");
            int callBudget = (int)Math.Ceiling(productionTime.Elapsed.TotalMilliseconds / fakeNativeDelayMilliseconds) + 8;
            Check(calls > 0 && calls <= callBudget,
                "The receiver still makes an individual slow Windows call for every packet instead of batching its ordered native input.");
            int minimumCriticalInputs = expected.Sum(packet => packet.Type switch
            {
                "enter" or "key" => 1, "button" => 2, "wheel" => 2,
                "text" => (packet.Dx + packet.Text!.Normalize().Length) * 2, _ => 0
            });
            Check(Volatile.Read(ref nativeInputsRequested) >= minimumCriticalInputs,
                "The slow-injection test completed logical packets without requesting all their native critical events.");
            Check(!receiver.IsRemote && !a.Visible && !b.Visible, "The slow-native regression left control active or showed a test window.");
            Check(Get<System.Collections.IDictionary>(receiver, "_receivedKeys").Count == 0
                && Get<ISet<string>>(receiver, "_receivedButtons").Count == 0,
                "Slow native batching left a held test key or mouse button after leave.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            await sender.WaitAsync(TimeSpan.FromSeconds(4));
            nativeObserver.SetValue(receiver, null);
            packetObserver.SetValue(receiver, null);
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            await WaitUntilAsync(() => !receiver.IsRemote, "The slow native-input regression failed to release its session.");
        }
    }

    private static async Task CheckHeldNativeInputAcknowledgementAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver, bool revokeInput)
    {
        await Task.Delay(550); // The sampler's next production slot, without changing its clock.
        string marker = (revokeInput ? "REVOKED_NATIVE_ACK_" : "HELD_NATIVE_ACK_") + Guid.NewGuid().ToString("N");
        await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1000, Y = 2000 });
        string enterDrained = marker + "ENTER_DRAINED";
        await session.A.SendAsync(new Packet { Type = "chat", Text = enterDrained });
        await WaitUntilAsync(() => receiver.IsRemote && Log(b).Contains(enterDrained, StringComparison.Ordinal),
            "The held-native acknowledgement test did not finish its initial safe enter.");

        using var nativeStarted = new ManualResetEventSlim();
        using var nativeResume = new ManualResetEventSlim();
        var ownerResponded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nativeObserver = typeof(InputRouter).GetProperty("NativeInputRequestedForTesting", Private)!;
        var packetObserver = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        int nativeJobs = 0, freshKeys = 0;
        nativeObserver.SetValue(receiver, (Action<int>)(_ =>
        {
            if (Interlocked.Increment(ref nativeJobs) != 1) return;
            nativeStarted.Set();
            if (!nativeResume.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("The held-native ACK test did not release its fake Windows input call.");
        }));
        Packet? sample = null;
        try
        {
            source.Send!(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true, Text = marker });
            Check(await Task.Run(() => nativeStarted.Wait(TimeSpan.FromSeconds(2))), "The sampled action did not reach its deliberately held native job.");
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "key" && packet.Text == marker),
                "The held native job's sampled action did not reach the authenticated session.");
            sample = session.AtB.Single(packet => packet.Type == "key" && packet.Text == marker);
            Check(Guid.TryParseExact(sample.Id, "N", out _) && sample.Length > 0, "The held-native regression lacked a valid source correlation token.");

            Task probe = (Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => ownerResponded.TrySetResult()))!;
            await ownerResponded.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await probe.WaitAsync(TimeSpan.FromSeconds(2));
            Check(!nativeResume.IsSet, "The hook-owner probe ran only after its slow native input call was released.");
            if (revokeInput)
            {
                Checks(b)["Input"].Checked = false;
                await WaitUntilAsync(() => !receiver.CanControl && !receiver.IsRemote,
                    "Local permission revocation waited behind a native call instead of releasing the free hook/state owner.");
            }

            // Advance an independent roundtrip while the native job stays held. This
            // proves the network is live and gives any incorrectly early ACK time to
            // arrive, without relying on a fragile short sleep or blocking the UI.
            long heartbeatSamples = session.A.Latency?.Samples ?? 0;
            await WaitUntilAsync(() => (session.A.Latency?.Samples ?? 0) > heartbeatSamples,
                "A held native input job blocked the independent TLS heartbeat path.");
            Check(!session.AtA.Any(packet => packet.Type == "input-ack" && packet.Id == sample.Id),
                "The receiver acknowledged a queued/built action before its actual native job completed.");
            nativeResume.Set();

            string drained = marker + "NATIVE_DRAINED";
            await session.A.SendAsync(new Packet { Type = "chat", Text = drained });
            await WaitUntilAsync(() => Log(b).Contains(drained, StringComparison.Ordinal),
                "A stale native-generation cancellation terminated the ordered session reader instead of skipping just that batch.");
            if (revokeInput)
            {
                string sourceDrain = marker + "SOURCE_DRAINED";
                await session.B.SendAsync(new Packet { Type = "chat", Text = sourceDrain });
                await WaitUntilAsync(() => Log(a).Contains(sourceDrain, StringComparison.Ordinal), "The source reader did not drain after input revocation.");
                Check(!session.AtA.Any(packet => packet.Type == "input-ack" && packet.Id == sample.Id),
                    "A sampled action revoked during its native call received a misleading success acknowledgement.");
                Check(Get<System.Collections.IDictionary>(receiver, "_receivedKeys").Count == 0
                    && Get<ISet<string>>(receiver, "_receivedButtons").Count == 0,
                    "Revocation during a slow native call left a held logical key or mouse button.");
                await WaitUntilAsync(() => Volatile.Read(ref nativeJobs) >= 2,
                    "Revoking an already started native key job did not run its ordered native release cleanup after that job returned.");

                Checks(b)["Input"].Checked = true;
                await WaitUntilAsync(() => source.CanControl && receiver.CanControl,
                    "The session did not accept a fresh input grant after canceling the old native job.");
                packetObserver.SetValue(receiver, (Action<Packet>)(packet =>
                {
                    if (packet.Type == "key" && packet.Down && packet.Text == marker + "FRESH") Interlocked.Increment(ref freshKeys);
                }));
                await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 1100, Y = 2200 });
                await session.A.SendAsync(new Packet { Type = "key", Key = 0x42, Scan = 0x30, Down = true, Text = marker + "FRESH" });
                await session.A.SendAsync(new Packet { Type = "key", Key = 0x42, Scan = 0x30, Down = false });
                await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
                await WaitUntilAsync(() => Volatile.Read(ref freshKeys) == 1 && !receiver.IsRemote,
                    "Fresh input stopped after re-enable because an old native-job cancellation killed its session consumer.");
            }
            else
            {
                await WaitUntilAsync(() => session.AtA.Any(packet => packet.Type == "input-ack" && packet.Id == sample.Id && packet.Length == sample.Length),
                    "A completed native action did not receive its deferred input acknowledgement.");
            }
            Check(!session.A.IsClosed && !session.B.IsClosed, "A held/revoked native call unnecessarily closed the authenticated TLS session.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            nativeResume.Set();
            nativeObserver.SetValue(receiver, null);
            packetObserver.SetValue(receiver, null);
            if (!Checks(b)["Input"].Checked) Checks(b)["Input"].Checked = true;
            await session.A.SendAsync(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
            await session.A.SendAsync(new Packet { Type = "leave", Name = "Right" });
            await WaitUntilAsync(() => !receiver.IsRemote, "The held-native ACK test did not restore its no-native input state.");
        }
    }
}
