using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LanBridge;

internal static class PointerPipelineChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly MethodInfo Queue = typeof(InputRouter).GetMethod("Queue", Private)!;

    internal static void Run(Action<bool, string> check)
    {
        CheckPacing(check);
        CheckSlowSender(check);
        CheckSessionBoundary(check);
        CheckTimerTail(check);
        CheckOldSenderFailure(check);
        CheckNativeBatches(check);
        CheckDispatcherBatch(check);
    }

    private static void CheckPacing(Action<bool, string> check)
    {
        // Model eight seconds of a 1000 Hz gaming mouse without sleeping or native input.
        var pacer = new PointerMovePacer();
        var moves = new List<Packet>();
        for (int milliseconds = 0; milliseconds < 8000; milliseconds++)
        {
            var ready = pacer.Offer(new Packet { Type = "move", X = milliseconds, Y = 8000 - milliseconds }, milliseconds);
            if (ready is not null) moves.Add(ready);
        }
        var tail = pacer.Flush(8000, false);
        if (tail is not null) moves.Add(tail);
        check(moves.Count == 1001 && moves[0].X == 0 && moves[^1].X == 7999,
            "A 1000 Hz source must emit its first position immediately, pace at 125 Hz and flush its final stationary position.");
        check(moves.Take(1000).Select(p => p.X).SequenceEqual(Enumerable.Range(0, 1000).Select(i => i * 8)),
            "Pacing replayed stale positions instead of selecting the newest position on each cadence.");
        check(!pacer.HasPending, "An idle mouse must not leave an unsent final position behind.");

        var preciseClock = new PointerMovePacer();
        var coarseClock = new PointerMovePacer();
        int preciseMoves = 0, coarseMoves = 0;
        for (int report = 0; report < 1000; report++)
        {
            if (preciseClock.Offer(new Packet { Type = "move", X = report }, report) is not null) preciseMoves++;
            if (coarseClock.Offer(new Packet { Type = "move", X = report }, report / 16 * 16) is not null) coarseMoves++;
        }
        check(preciseMoves == 125 && coarseMoves == 63,
            "A 16 ms source clock halves nominal 125 Hz motion; pacing must use the high-resolution monotonic capture clock.");

        pacer.Clear();
        var mixed = new List<Packet>();
        var expectedWheels = new List<Packet>();
        for (int milliseconds = 0; milliseconds < 8000; milliseconds++)
        {
            var ready = pacer.Offer(new Packet { Type = "move", X = milliseconds, Y = 8000 - milliseconds }, milliseconds);
            if (ready is not null) mixed.Add(ready);
            if (milliseconds % 20 != 19) continue;
            // A physical scroll is a critical barrier: no cadence wait and no lost fractional
            // wheel delta, axis, reversal or cursor location while moving at the same time.
            var latest = pacer.Flush(milliseconds, true);
            if (latest is not null) mixed.Add(latest);
            var wheel = new Packet { Type = "wheel", X = milliseconds, Y = 8000 - milliseconds,
                Dx = milliseconds % 40 == 19 ? 17 : -23, Dy = milliseconds % 60 == 19 ? -120 : 30 };
            mixed.Add(wheel);
            expectedWheels.Add(wheel);
        }
        check(mixed.Count(p => p.Type == "move") <= 1201, "Simultaneous moving and scrolling restored a 1000 Hz move flood.");
        check(mixed.Where(p => p.Type == "wheel").SequenceEqual(expectedWheels), "Move pacing lost or rewrote a wheel tick.");
        foreach (var index in Enumerable.Range(0, mixed.Count).Where(i => mixed[i].Type == "wheel"))
            check(index > 0 && mixed[index - 1].Type == "move" && mixed[index - 1].X == mixed[index].X && mixed[index - 1].Y == mixed[index].Y,
                "A scroll barrier must flush the latest exact position without waiting for the next motion tick.");
        pacer.Clear();
        check(pacer.Offer(new Packet { Type = "move", X = 10 }, 10000)?.X == 10, "A new session's first move must be immediate.");
        check(pacer.Offer(new Packet { Type = "move", X = 11 }, 10001) is null && pacer.DelayMilliseconds(10001) == 7,
            "A recent move should retain just the next position until the remaining cadence.");
        check(pacer.Flush(10002, true)?.X == 11 && !pacer.HasPending,
            "A shortcut, drag edge or leave must be able to force the exact pending position immediately.");
    }

    private static void CheckSlowSender(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var delivered = new ConcurrentQueue<Packet>();
        router.Send = packet =>
        {
            if (packet.Type == "enter") { entered.Set(); resume.Wait(TimeSpan.FromSeconds(5)); }
            // A sender slower than 1000 reports/sec must not accumulate stale motion.
            Thread.Sleep(1);
            delivered.Enqueue(packet);
            if (packet.Type == "leave") finished.Set();
        };
        Queue.Invoke(router, [new Packet { Type = "enter" }]);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The deliberately blocked sender did not start.");
        var critical = new List<Packet>();
        for (int i = 0; i < 8000; i++)
        {
            Queue.Invoke(router, [new Packet { Type = "move", X = i, Y = 8000 - i }]);
            Packet? action = i switch
            {
                1000 => new Packet { Type = "key", Key = 0xA2, Scan = 0x1D, Down = true },
                2000 => new Packet { Type = "button", Button = "left", Down = true, X = i, Y = 8000 - i },
                3000 => new Packet { Type = "button", Button = "left", Down = false, X = i, Y = 8000 - i },
                4000 => new Packet { Type = "key", Key = 0xA2, Scan = 0x1D, Down = false },
                5000 => new Packet { Type = "text", Text = "Tiếng Việt" },
                _ when i % 200 == 199 => new Packet { Type = "wheel", X = i, Y = 8000 - i, Dx = i % 400 == 199 ? 30 : -120, Dy = i % 600 == 199 ? -120 : 15 },
                _ => null,
            };
            if (action is null) continue;
            critical.Add(action);
            Queue.Invoke(router, [action]);
        }
        Queue.Invoke(router, [new Packet { Type = "leave", X = 7999, Y = 1 }]);
        var pending = (ICollection)typeof(InputRouter).GetField("_outbound", Private)!.GetValue(router)!;
        check(pending.Count <= critical.Count * 2 + 2,
            "Slow output accumulated old 1000 Hz movement reports in front of wheel or key input.");
        var elapsed = Stopwatch.StartNew();
        resume.Set();
        check(finished.Wait(TimeSpan.FromSeconds(5)), "A slow sender failed to drain a compact mixed-input burst.");
        var output = delivered.ToArray();
        check(output.Length <= critical.Count * 2 + 3, "The worker replayed stale intermediate positions after the sender resumed.");
        check(output.Where(p => p.Type is not ("enter" or "move" or "leave")).SequenceEqual(critical),
            "Motion coalescing dropped or reordered wheel deltas, modifier keys, Unicode or drag boundaries.");
        var finalMove = output.Last(p => p.Type == "move");
        check(finalMove.X == 7999 && finalMove.Y == 1 && output[^1].Type == "leave",
            "The final cursor position must precede the session leave boundary.");
        foreach (int index in Enumerable.Range(0, output.Length).Where(i => output[i].Type is "wheel" or "button"))
            check(output[index - 1].Type == "move" && output[index - 1].X == output[index].X && output[index - 1].Y == output[index].Y,
                "A queued pointer action crossed a movement boundary or changed its exact hit-test position.");
        Console.WriteLine($"Pointer burst: 8000 source moves + {critical.Count} critical actions -> {output.Length} ordered output packets; slow sender drained in {elapsed.ElapsedMilliseconds} ms.");
    }

    private static void CheckTimerTail(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var finalMove = new ManualResetEventSlim();
        using var finalKey = new ManualResetEventSlim();
        var output = new ConcurrentQueue<Packet>();
        router.StartForTesting();
        router.Send = packet =>
        {
            output.Enqueue(packet);
            if (packet.Type == "move" && packet.X == 999) finalMove.Set();
            if (packet.Type == "key") finalKey.Set();
        };
        var captureMove = typeof(InputRouter).GetMethod("QueueControllerMove", Private)!;
        router.InvokeForTestingAsync(() =>
        {
            for (int i = 0; i < 1000; i++) captureMove.Invoke(router, [new Packet { Type = "move", X = i, Y = 1000 - i }]);
        }).GetAwaiter().GetResult();
        check(finalMove.Wait(TimeSpan.FromSeconds(2)), "The input owner's one-shot pacing timer failed to deliver the final stationary position.");
        check(output.Count < 100, "Rapid source mouse capture bypassed pacing and flooded the sender.");
        router.InvokeForTestingAsync(() =>
        {
            captureMove.Invoke(router, [new Packet { Type = "move", X = 1001, Y = 10 }]);
            captureMove.Invoke(router, [new Packet { Type = "move", X = 1002, Y = 11 }]);
            Queue.Invoke(router, [new Packet { Type = "key", Key = 65, Scan = 30, Down = true }]);
        }).GetAwaiter().GetResult();
        check(finalKey.Wait(TimeSpan.FromSeconds(2)), "A key barrier failed to flush the captured cursor position.");
        var sent = output.ToArray();
        int key = Array.FindIndex(sent, p => p.Type == "key");
        check(key > 0 && sent[key - 1].Type == "move" && sent[key - 1].X == 1002 && sent[key - 1].Y == 11,
            "The actual source pacer did not flush the latest exact position before the key shortcut.");

        foreach (string type in new[] { "wheel", "button" })
        {
            using var actionSent = new ManualResetEventSlim();
            int x = type == "wheel" ? 1004 : 1006;
            router.Send = packet => { output.Enqueue(packet); if (packet.Type == type && packet.X == x) actionSent.Set(); };
            router.InvokeForTestingAsync(() =>
            {
                captureMove.Invoke(router, [new Packet { Type = "move", X = x - 1, Y = 11 }]);
                var pacer = (PointerMovePacer)typeof(InputRouter).GetField("_movePacer", Private)!.GetValue(router)!;
                pacer.Clear();
                // Seed the pure clock ahead so the critical barrier is tested deterministically,
                // even if the test process is preempted between its two capture calls.
                pacer.Offer(new Packet { Type = "move", X = x - 1, Y = 11 }, InputRouter.PointerClockMilliseconds() + 1000);
                pacer.Offer(new Packet { Type = "move", X = x, Y = 12 }, InputRouter.PointerClockMilliseconds() + 1001);
                Queue.Invoke(router, [new Packet { Type = type, X = x, Y = 12, Dy = -120, Button = "left", Down = true }]);
                check(!pacer.HasPending, "A pointer action must consume the matching source position immediately.");
            }).GetAwaiter().GetResult();
            check(actionSent.Wait(TimeSpan.FromSeconds(2)), "A pointer action waited for the source movement cadence.");
            check(!output.Any(packet => packet.Type == "move" && packet.X == x),
                "A wheel or click already carrying the exact pending position redundantly sent a separate move.");
        }
        router.InvokeForTestingAsync(() =>
        {
            var pacer = (PointerMovePacer)typeof(InputRouter).GetField("_movePacer", Private)!.GetValue(router)!;
            pacer.Offer(new Packet { Type = "move", X = 2000 }, InputRouter.PointerClockMilliseconds() + 2000);
            router.ReleaseRemote(false);
            check(!pacer.HasPending, "A return or closed session must cancel its pending motion.");
            check(pacer.Offer(new Packet { Type = "move", X = 2001 }, InputRouter.PointerClockMilliseconds()) is not null,
                "Crossing again on the same connection must reset pacing and send the first position immediately.");
        }).GetAwaiter().GetResult();
    }

    private static void CheckOldSenderFailure(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var drained = new ManualResetEventSlim();
        router.StartForTesting();
        router.Send = _ =>
        {
            entered.Set();
            resume.Wait(TimeSpan.FromSeconds(3));
            throw new IOException("The old connection was closed.");
        };
        Queue.Invoke(router, [new Packet { Type = "enter" }]);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The old failing sender did not start.");
        router.Send = _ => drained.Set();
        router.CanControl = true;
        router.ApplyRemoteAsync(new Packet { Type = "enter" }).GetAwaiter().GetResult();
        Queue.Invoke(router, [new Packet { Type = "marker" }]);
        resume.Set();
        check(drained.Wait(TimeSpan.FromSeconds(2)), "The worker failed to continue with the new sender.");
        router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
        check(router.IsRemote && router.CanControl && router.LastError is null,
            "An old sender's delayed failure must not release or report an input error in the newer authenticated session.");
    }

    private static void CheckSessionBoundary(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var entered = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        var output = new ConcurrentQueue<(string Sender, Packet Packet)>();
        Action<Packet> first = packet =>
        {
            if (packet.Type == "enter") { entered.Set(); resume.Wait(TimeSpan.FromSeconds(3)); }
            output.Enqueue(("old", packet));
        };
        Action<Packet> next = packet => { output.Enqueue(("new", packet)); if (packet.Type == "leave") finished.Set(); };
        router.Send = first;
        Queue.Invoke(router, [new Packet { Type = "enter" }]);
        check(entered.Wait(TimeSpan.FromSeconds(2)), "The session boundary sender did not start.");
        Queue.Invoke(router, [new Packet { Type = "move", X = 5 }]);
        router.Send = next;
        Queue.Invoke(router, [new Packet { Type = "move", X = 6 }]);
        Queue.Invoke(router, [new Packet { Type = "move", X = 7 }]);
        Queue.Invoke(router, [new Packet { Type = "leave" }]);
        resume.Set();
        check(finished.Wait(TimeSpan.FromSeconds(2)), "Session-boundary output did not finish.");
        var sent = output.ToArray();
        check(sent.Length == 4 && sent[1].Sender == "old" && sent[1].Packet.X == 5 && sent[2].Sender == "new" && sent[2].Packet.X == 7,
            "Adjacent move coalescing mixed packets belonging to different authenticated sessions.");
    }

    private static void CheckNativeBatches(Action<bool, string> check)
    {
        var builder = typeof(InputRouter).GetMethod("BuildPointerInputs", BindingFlags.Static | BindingFlags.NonPublic)!;
        object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
        object Mouse(object input) => Field(Field(input, "Union"), "Mouse");
        foreach (int horizontal in new[] { 0, 1, -1, 120, -240, 40000, int.MinValue })
        foreach (int vertical in new[] { 0, 17, -30, 120, -240, 40000, int.MaxValue })
        {
            var native = (Array)builder.Invoke(null, [new Packet { Type = "wheel", X = 34567, Y = 12345, Dx = horizontal, Dy = vertical }])!;
            check(native.Length == 1 + (vertical != 0 ? 1 : 0) + (horizontal != 0 ? 1 : 0),
                "A wheel packet must build one atomic native batch containing position and every nonzero axis.");
            var position = Mouse(native.GetValue(0)!);
            check((int)Field(position, "Dx") == 34567 && (int)Field(position, "Dy") == 12345 &&
                (uint)Field(position, "Flags") == (0x0001 | 0x8000 | 0x4000), "Native wheel position changed or lost virtual desktop absolute flags.");
            int axis = 1;
            foreach (var (delta, flag) in new[] { (vertical, 0x0800u), (horizontal, 0x1000u) })
            {
                if (delta == 0) continue;
                var input = Mouse(native.GetValue(axis++)!);
                check((uint)Field(input, "Flags") == flag && unchecked((int)(uint)Field(input, "MouseData")) == delta,
                    "A native wheel axis changed signed direction, fractional ticks or a full 32-bit accumulated delta.");
                check(((UIntPtr)Field(input, "ExtraInfo")).ToUInt64() == 0x4C424447, "Native batches must retain the injection loop-prevention tag.");
            }
        }
        foreach (string button in new[] { "left", "right", "middle", "x1", "x2" })
        foreach (bool down in new[] { true, false })
        {
            var native = (Array)builder.Invoke(null, [new Packet { Type = "button", X = -1, Y = 70000, Button = button, Down = down }])!;
            var position = Mouse(native.GetValue(0)!);
            var action = Mouse(native.GetValue(1)!);
            check(native.Length == 2 && (int)Field(position, "Dx") == 0 && (int)Field(position, "Dy") == 65535,
                "A native button batch must clamp position before the action.");
            uint expected = button switch { "left" => down ? 2u : 4u, "right" => down ? 8u : 16u, "middle" => down ? 32u : 64u, _ => down ? 128u : 256u };
            check((uint)Field(action, "Flags") == expected && (uint)Field(action, "MouseData") == (button == "x1" ? 1u : button == "x2" ? 2u : 0u),
                "A batched button down/up changed drag semantics or X-button identity.");
        }
    }

    private static void CheckDispatcherBatch(Action<bool, string> check)
    {
        using var router = new InputRouter();
        router.StartForTesting();
        router.CanControl = true;
        var output = new List<Packet>();
        router.PacketAppliedForTesting = output.Add;
        var batch = new List<Packet> { new() { Type = "enter" } };
        batch.AddRange(Enumerable.Range(0, 28).Select(i => new Packet { Type = "move", X = i }));
        batch.Add(new Packet { Type = "wheel", X = 27, Dy = -120 });
        batch.Add(new Packet { Type = "button", Button = "left", Down = true, X = 27 });
        batch.Add(new Packet { Type = "key", Key = 0xA2, Scan = 0x1D, Down = true });
        router.ApplyRemoteBatchAsync(batch).WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        check(output.SequenceEqual(batch) && router.IsRemote, "One dispatcher handoff must preserve all 32 input packets and their exact order.");
        bool oversized = false;
        try { router.ApplyRemoteBatchAsync(Enumerable.Repeat(new Packet { Type = "move" }, 33).ToArray()); }
        catch (ArgumentOutOfRangeException) { oversized = true; }
        check(oversized, "Unbounded batches could starve the owner's physical emergency shortcut.");
        using var cancellation = new CancellationTokenSource();
        router.PacketAppliedForTesting = packet => { output.Add(packet); if (packet.Type == "wheel") cancellation.Cancel(); };
        bool canceled = false;
        try { router.ApplyRemoteBatchAsync([new() { Type = "wheel", Dy = 120 }, new() { Type = "key", Key = 65, Down = true }], cancellation.Token).GetAwaiter().GetResult(); }
        catch (OperationCanceledException) { canceled = true; }
        router.InvokeForTestingAsync(() => { }).GetAwaiter().GetResult();
        var held = (IDictionary)typeof(InputRouter).GetField("_receivedKeys", Private)!.GetValue(router)!;
        check(canceled && !router.IsRemote && held.Count == 0 && output[^1].Type == "wheel",
            "Cancellation between batch packets must immediately release held state and skip the canceled key.");
        router.PacketAppliedForTesting = output.Add;
        bool invalid = false;
        int prior = output.Count;
        try { router.ApplyRemoteBatchAsync([new() { Type = "enter" }, new() { Type = "text", Dx = 1000, Text = "invalid" }, new() { Type = "move", X = 123 }]).GetAwaiter().GetResult(); }
        catch (InvalidDataException) { invalid = true; }
        check(invalid && !router.IsRemote && output.Count == prior + 1,
            "An invalid Unicode edit must release control and stop the remainder of a dispatcher batch.");
    }
}
