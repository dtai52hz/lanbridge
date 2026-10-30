using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckMoveAndScrollLatencyAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        // Model one second of a 1,000/8,000 Hz mouse, compressed into a deterministic
        // producer burst. Realistic scroll detents remain separate, including reversals,
        // horizontal scroll, Ctrl/Shift, a drag and Unicode edits. Neither test invokes
        // a hook callback or injects input; the production output worker, TLS frames,
        // session inbox and independent receiver pump all run normally.
        foreach (int samples in new[] { 1000, 8000 })
            await CheckMoveAndScrollBurstAsync(a, b, session, source, receiver, samples);
    }

    private static async Task CheckMoveAndScrollBurstAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver, int samples)
    {
        const int wheelCount = 32;
        string marker = "MOVE_SCROLL_" + samples + "_" + Guid.NewGuid().ToString("N");
        var packets = new List<Packet> { new() { Type = "enter", Name = "Right", X = 1000, Y = 2000, Text = marker } };
        for (int group = 0; group < wheelCount; group++)
        {
            if (group == 4) packets.Add(new Packet { Type = "key", Key = 0xA0, Scan = 0x2a, Down = true });
            if (group == 8) packets.Add(new Packet { Type = "key", Key = 0xA2, Scan = 0x1d, Down = true });
            if (group == 12) packets.Add(new Packet { Type = "key", Key = 0xA0, Scan = 0x2a, Down = false });
            if (group == 16)
            {
                packets.Add(new Packet { Type = "key", Key = 0xA2, Scan = 0x1d, Down = false });
                packets.Add(new Packet { Type = "text", Text = "cuộn chuột tiếng Việt" });
            }
            int first = samples * group / wheelCount, last = samples * (group + 1) / wheelCount - 1;
            for (int sample = first; sample <= last; sample++)
                packets.Add(new Packet { Type = "move", X = 1000 + sample * 7 % 60000, Y = 2000 + sample * 3 % 50000 });
            int x = 1000 + last * 7 % 60000, y = 2000 + last * 3 % 50000;
            if (group == 24) packets.Add(new Packet { Type = "button", Button = "left", Down = true, X = x, Y = y });
            packets.Add(new Packet
            {
                Type = "wheel", X = x, Y = y,
                Dx = group % 4 == 0 ? (group < 16 ? 60 : -60) : 0,
                Dy = (group / 8) switch { 0 => 120, 1 => -120, 2 => 60, _ => -60 }
            });
            if (group == 30) packets.Add(new Packet { Type = "button", Button = "left", Down = false, X = x, Y = y });
        }
        var finalPosition = packets.Last(packet => packet.Type == "move");
        packets.Add(new Packet { Type = "leave", Name = "Right", Text = marker, X = finalPosition.X, Y = finalPosition.Y });

        var applied = new ConcurrentQueue<Packet>();
        using var senderPaused = new ManualResetEventSlim();
        using var senderResume = new ManualResetEventSlim();
        using var receiverPaused = new ManualResetEventSlim();
        using var receiverResume = new ManualResetEventSlim();
        using var reachedLeave = new ManualResetEventSlim();
        Action<Packet> originalSender = source.Send ?? throw new InvalidOperationException("The input worker has no live TLS sender.");
        var originalContext = Get<SynchronizationContext>(receiver, "_context");
        var countingContext = new CountingInputContext(originalContext);
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        var queue = typeof(InputRouter).GetMethod("Queue", Private)!;
        Task? pausedInput = null;
        int queued = 0;
        long pausedAt = 0;
        try
        {
            await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => Set(receiver, "_context", countingContext)))!);
            observer.SetValue(receiver, (Action<Packet>)(packet =>
            {
                applied.Enqueue(packet);
                if (packet.Type == "leave" && packet.Text == marker) reachedLeave.Set();
            }));
            // Holding the first Send callback emulates a short network scheduler stall.
            // Adjacent absolute moves queued behind it must collapse before JSON/TLS,
            // while each wheel and every critical boundary retains its exact position.
            source.Send = packet =>
            {
                if (packet.Type == "enter" && packet.Text == marker)
                {
                    senderPaused.Set();
                    if (!senderResume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test's source pause was not released.");
                }
                originalSender(packet);
                Interlocked.Increment(ref queued);
            };
            queue.Invoke(source, new object[] { packets[0] });
            Check(await Task.Run(() => senderPaused.Wait(TimeSpan.FromSeconds(3))), "Could not pause the real input output worker for the mixed pointer burst.");
            pausedInput = (Task)Call(receiver, "InvokeForTestingAsync", (Action)(() =>
            {
                receiverPaused.Set();
                if (!receiverResume.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("The test's input-pump pause was not released.");
            }))!;
            Check(await Task.Run(() => receiverPaused.Wait(TimeSpan.FromSeconds(3))), "Could not pause the safe independent receiver pump.");
            foreach (var packet in packets.Skip(1)) queue.Invoke(source, new object[] { packet });
            await Task.Delay(100);
            pausedAt = Stopwatch.GetTimestamp();
            senderResume.Set();
            await WaitUntilAsync(() => session.AtB.Any(packet => packet.Type == "leave" && packet.Text == marker),
                "Mixed move/wheel packets did not traverse the real output worker and authenticated TLS session");
            Check(!session.A.IsClosed && !session.B.IsClosed && !reachedLeave.IsSet,
                "A short receiver stall must buffer mixed pointer input without disconnecting or bypassing its pump.");
            int criticalCount = packets.Count(packet => packet.Type != "move");
            int compactBound = criticalCount + wheelCount + 4;
            Check(Volatile.Read(ref queued) <= compactBound,
                $"A {samples} Hz movement burst retained {queued} output packets; expected at most {compactBound} after preserving all wheel and critical boundaries.");
            // Leave the receiver stopped for at least a typical 120ms render/scheduler
            // interruption. This controlled pause is solely a regression-test stimulus.
            double elapsed = Stopwatch.GetElapsedTime(pausedAt).TotalMilliseconds;
            if (elapsed < 120) await Task.Delay(TimeSpan.FromMilliseconds(120 - elapsed));
            int ownerPostsBeforeResume = countingContext.Posts;
            var catchUp = Stopwatch.StartNew();
            receiverResume.Set();
            await pausedInput.WaitAsync(TimeSpan.FromSeconds(3));
            Check(await Task.Run(() => reachedLeave.Wait(TimeSpan.FromSeconds(3))), "A resumed input pump kept replaying stale movement instead of reaching the final leave.");
            await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => { }))!);
            catchUp.Stop();
            Packet[] actual = applied.ToArray();
            Packet[] expectedCritical = packets.Where(packet => packet.Type != "move").ToArray();
            Check(actual.Where(packet => packet.Type != "move").Select(InputBoundary).SequenceEqual(expectedCritical.Select(InputBoundary)),
                "Mixed pointer compaction changed a wheel position/delta, reversal, modifier, drag, Unicode edit or enter/leave boundary.");
            Check(actual.Length <= compactBound && actual.Count(packet => packet.Type == "wheel") == wheelCount,
                "The receiver replayed obsolete high-poll positions or lost a wheel event during the input-pump stall.");
            var expectedWheels = expectedCritical.Where(packet => packet.Type == "wheel").ToArray();
            var actualWheels = actual.Where(packet => packet.Type == "wheel").ToArray();
            Check(actualWheels.Sum(packet => (long)packet.Dx) == expectedWheels.Sum(packet => (long)packet.Dx)
                && actualWheels.Sum(packet => (long)packet.Dy) == expectedWheels.Sum(packet => (long)packet.Dy),
                "The mixed pointer session changed the total vertical or horizontal scroll distance.");
            foreach (int sign in new[] { -1, 1 })
                Check(actualWheels.Where(packet => Math.Sign(packet.Dy) == sign).Sum(packet => Math.Abs((long)packet.Dy))
                    == expectedWheels.Where(packet => Math.Sign(packet.Dy) == sign).Sum(packet => Math.Abs((long)packet.Dy)),
                    "Opposing wheel directions must never cancel each other while compacting movement.");
            var lastMove = actual.LastOrDefault(packet => packet.Type == "move");
            Check(lastMove?.X == finalPosition.X && lastMove.Y == finalPosition.Y
                && actual[^1].Type == "leave" && !receiver.IsRemote,
                "The mixed burst lost its latest cursor position or failed to release the remote session.");
            int ownerPosts = countingContext.Posts;
            int postBound = (compactBound + 31) / 32 + 10;
            Check(ownerPosts <= postBound,
                $"The receiver posted {ownerPosts} separate owner callbacks for {actual.Length} packets; the ordered batch dispatcher should require at most {postBound}.");
            Check(Get<System.Collections.IDictionary>(receiver, "_receivedKeys").Count == 0
                && Get<ISet<string>>(receiver, "_receivedButtons").Count == 0,
                "Mixed scrolling/dragging left a held modifier or mouse button after leave.");
            Check(source.EdgeDwellMilliseconds == 0 && !a.Visible && !b.Visible,
                "Pointer throughput fixes must preserve immediate edge switching and keep the regression windows hidden.");
            AssertNoHooks(source, receiver);
            Console.WriteLine($"Move+scroll {samples} samples: {queued} TLS input packets, {actual.Length} applied, {ownerPosts} input-owner posts ({ownerPostsBeforeResume} before resume), {catchUp.ElapsedMilliseconds}ms catch-up after a 120ms pump pause; 32 wheels preserved.");
        }
        catch
        {
            Console.Error.WriteLine($"Move+scroll {samples} samples failed: {queued} worker packets, {applied.Count} applied, TLS closed A={session.A.IsClosed}, B={session.B.IsClosed}, input-owner posts={countingContext.Posts}.");
            throw;
        }
        finally
        {
            senderResume.Set(); receiverResume.Set();
            if (pausedInput != null) await pausedInput.WaitAsync(TimeSpan.FromSeconds(3));
            source.Send = originalSender;
            observer.SetValue(receiver, null);
            await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => Set(receiver, "_context", originalContext)))!);
        }
    }

    private sealed class CountingInputContext(SynchronizationContext inner) : SynchronizationContext
    {
        private int _posts;
        public int Posts => Volatile.Read(ref _posts);
        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _posts);
            inner.Post(callback, state);
        }
        public override void Send(SendOrPostCallback callback, object? state) => inner.Send(callback, state);
        public override SynchronizationContext CreateCopy() => this;
    }
}
