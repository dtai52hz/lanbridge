using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class CapturePathChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        using var router = new InputRouter();
        using var drained = new ManualResetEventSlim();
        router.StartForTesting();
        var output = new ConcurrentQueue<Packet>();
        router.Send = packet => { output.Enqueue(packet); if (packet.Type == "capture-barrier") drained.Set(); };
        int nativeRequests = 0, nativeInputs = 0;
        router.NativeInputRequestedForTesting = count => { nativeRequests++; nativeInputs += count; };
        object Get(string field) => typeof(InputRouter).GetField(field, Private)!.GetValue(router)!;
        void Set(string field, object value) => typeof(InputRouter).GetField(field, Private)!.SetValue(router, value);
        var move = typeof(InputRouter).GetMethod("ControllerMouseMoved", Private)!;
        var enter = typeof(InputRouter).GetMethod("EnterRemote", Private)!;
        var normalize = typeof(InputRouter).GetMethod("UpdateNormalizedRemote", Private)!;
        var queue = typeof(InputRouter).GetMethod("Queue", Private)!;
        var button = typeof(InputRouter).GetMethod("SendButton", Private)!;
        var wheel = typeof(InputRouter).GetMethod("SendWheel", Private)!;
        var key = typeof(InputRouter).GetMethod("RouteControllerKey", Private)!;
        var stroke = typeof(InputRouter).GetNestedType("KeyStroke", BindingFlags.NonPublic)!;
        var critical = new List<Packet>();
        var elapsed = new Stopwatch();
        router.InvokeForTestingAsync(() =>
        {
            router.ConfigureController(true, ScreenEdge.Right);
            router.TypingMode = TypingMode.Physical;
            router.RemoteWidth = router.RemoteHeight = 65536;
            var bounds = (Rectangle)Get("_bounds");
            enter.Invoke(router, [new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2)]);
            check(router.IsRemote && nativeRequests == 1 && nativeInputs == 1,
                "Entering control must still request exactly one local anchor warp.");
            nativeRequests = nativeInputs = 0;
            var anchor = (Point)Get("_anchor");
            Set("_remoteX", 16000d); Set("_remoteY", 16000d);
            normalize.Invoke(router, null);
            int expectedX = 16000, expectedY = 16000;

            void SendKey(int vk, int scan, bool down)
            {
                key.Invoke(router, [Activator.CreateInstance(stroke, vk, scan, false), down]);
                critical.Add(new Packet { Type = "key", Key = vk, Scan = scan, Down = down });
            }

            elapsed.Start();
            // Exercise the actual capture method, not just synthetic packets queued directly
            // to transport. Each physical report is suppressed by MouseHook, leaving A at
            // its anchor, so reported coordinates are anchor + this report's delta.
            for (int i = 1; i <= 8000; i++)
            {
                int dx = (i % 4) switch { 0 => -2, 1 => 1, 2 => 2, _ => -1 };
                int dy = i % 2 == 0 ? 1 : -1;
                expectedX += dx; expectedY += dy;
                move.Invoke(router, [new Point(anchor.X + dx, anchor.Y + dy)]);
                if (i == 1) check((double)Get("_remoteX") == 16001d && (double)Get("_remoteY") == 15999d,
                    "The first anchored source report must apply its signed hardware movement immediately.");
                if (i % 200 != 0) continue;
                bool down = i % 400 == 200;
                button.Invoke(router, ["left", down]);
                critical.Add(new Packet { Type = "button", Button = "left", Down = down, X = expectedX, Y = expectedY });
                SendKey(0xA2, 0x1D, true); SendKey(0x41, 30, true);
                SendKey(0x41, 30, false); SendKey(0xA2, 0x1D, false);
                int delta = down ? 120 : -30;
                wheel.Invoke(router, [0, delta]);
                critical.Add(new Packet { Type = "wheel", X = expectedX, Y = expectedY, Dy = delta });
            }
            elapsed.Stop();
            check(nativeRequests == 0 && nativeInputs == 0,
                $"Steady remote capture requested {nativeRequests} native injections/{nativeInputs} inputs per 8000 reports; avoid the extra global-hook round trips before clicks/keys can run.");
            check((double)Get("_remoteX") == expectedX && (double)Get("_remoteY") == expectedY &&
                (int)Get("_lastNormX") == expectedX && (int)Get("_lastNormY") == expectedY,
                "Removing repeated anchor injection must preserve every signed movement delta exactly once.");
            check(router.IsRemote && ((IDictionary)Get("_sentKeys")).Count == 0 && ((HashSet<string>)Get("_sentButtons")).Count == 0,
                "Moving while clicking/typing must preserve remote control and balance key/button state.");
            queue.Invoke(router, [new Packet { Type = "capture-barrier" }]);
        }).GetAwaiter().GetResult();
        check(drained.Wait(TimeSpan.FromSeconds(3)), "Interleaved capture failed to deliver its final marker.");
        var sent = output.ToArray();
        check(sent.Where(p => p.Type is "key" or "button" or "wheel").SequenceEqual(critical),
            "Native capture changed click positions, drag edges, Ctrl typing or fractional wheel ticks while moving.");
        var finalPointer = sent.Last(p => p.Type is "move" or "button" or "wheel");
        check(finalPointer.X == 16000 && finalPointer.Y == 16000,
            "The actual capture path lost its final stationary position.");
        Console.WriteLine($"Capture path: 8000 physical-report models + {critical.Count} click/key/wheel actions in {elapsed.ElapsedMilliseconds} ms; 0 native input requests during steady capture.");

        router.InvokeForTestingAsync(() =>
        {
            var anchor = (Point)Get("_anchor");
            Set("_remoteX", 1d);
            move.Invoke(router, [new Point(anchor.X - 2, anchor.Y)]);
            check(!router.IsRemote && nativeRequests == 1 && nativeInputs == 1,
                "The same immediate return-edge event must release control and retain its one local return warp.");
            router.NativeInputRequestedForTesting = null;
            router.Send = null;
        }).GetAwaiter().GetResult();
    }
}
