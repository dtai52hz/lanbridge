using System.Collections;
using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class MouseSpeedChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static void Run(Action<bool, string> check)
    {
        CheckBounds(check);
        CheckScaledMovement(check);
        CheckActiveChange(check);
        foreach (var edge in Enum.GetValues<ScreenEdge>()) CheckReturn(check, edge);
    }

    private static void CheckBounds(Action<bool, string> check)
    {
        using var router = new InputRouter();
        check(router.MouseSpeedPercent == 100, "Remote pointer speed must default to unchanged movement.");
        foreach (var (requested, expected) in new[] { (int.MinValue, 50), (49, 50), (50, 50), (125, 125), (200, 200), (201, 200), (int.MaxValue, 200) })
        {
            router.MouseSpeedPercent = requested;
            check(router.MouseSpeedPercent == expected, $"Pointer speed {requested} did not stay within 50–200 percent.");
        }
        check(!router.IsRemote && !router.CanControl && !router.IsController,
            "Changing pointer speed before startup must not enable control or install hooks.");
    }

    private static void CheckScaledMovement(Action<bool, string> check)
    {
        using var source = new Source();
        source.OnOwner(() =>
        {
            source.Position(100, 200);
            source.Move(7, -3);
            check(source.X == 107 && source.Y == 197,
                "Default pointer speed must preserve each signed source delta unchanged.");
            source.Router.MouseSpeedPercent = 50;
            source.Move(1, -1);
            check(source.X == 107.5 && source.Y == 196.5,
                "Half-speed movement must retain fractional pixels after a single hardware report.");
            source.Move(1, -1);
            check(source.X == 108 && source.Y == 196,
                "Repeated half-speed movement must accumulate rather than lose subpixel deltas.");
            source.Router.MouseSpeedPercent = 200;
            source.Move(-4, -7);
            check(source.X == 100 && source.Y == 182,
                "Double-speed movement must scale negative X and Y deltas exactly once.");
            source.Position(65534, 100);
            source.Move(4, -100);
            check(source.X == 65535 && source.Y == 0 && source.Router.IsRemote,
                "Scaled movement must clamp to the remote desktop without crossing an unrelated return edge.");
            check(source.NativeCalls == 0,
                "Changing speed and steady pointer capture must not add local native input calls.");
        });
        var output = source.Drain();
        var final = output.Last(packet => packet.Type == "move");
        check(final.X == 65535 && final.Y == 0,
            "Pointer pacing must retain the final scaled and clamped remote position.");
    }

    private static void CheckActiveChange(Action<bool, string> check)
    {
        using var source = new Source();
        source.OnOwner(() =>
        {
            source.Position(321, 654);
            source.Call("SendKey", Stroke(0xA2, 0x1d), true);
            source.Call("SendButton", "left", true);
        });
        // Exercise the same cross-thread property setter used by the settings UI.
        source.Router.MouseSpeedPercent = 175;
        source.OnOwner(() =>
        {
            check(source.Router.MouseSpeedPercent == 175 && source.X == 321 && source.Y == 654
                && source.Anchor == new Point(100, 100) && source.Router.IsRemote,
                "Updating speed during control must preserve the current position, anchor and active session.");
            check(((IDictionary)source.Get("_sentKeys")).Count == 1
                && ((HashSet<string>)source.Get("_sentButtons")).SetEquals(["left"]),
                "Updating speed must preserve held remote modifiers and drag buttons.");
            source.Move(4, -4);
            source.Call("SendWheel", 15, -30);
            source.Call("SendButton", "left", false);
            source.Call("SendKey", Stroke(0xA2, 0x1d), false);
            check(source.X == 328 && source.Y == 647 && source.NativeCalls == 0,
                "The next active report must use the new speed without a reset or native warp.");
        });
        var critical = source.Drain().Where(packet => packet.Type is "key" or "button" or "wheel").ToArray();
        check(critical.Length == 5 && critical[0].Type == "key" && critical[0].Down
            && critical[1].Type == "button" && critical[1].Down && critical[1].X == 321 && critical[1].Y == 654
            && critical[2].Type == "wheel" && critical[2].X == 328 && critical[2].Y == 647
            && critical[2].Dx == 15 && critical[2].Dy == -30
            && critical[3].Type == "button" && !critical[3].Down && critical[3].X == 328 && critical[3].Y == 647
            && critical[4].Type == "key" && !critical[4].Down,
            "An active speed change must preserve drag/key ordering, action positions and exact wheel deltas.");
    }

    private static void CheckReturn(Action<bool, string> check, ScreenEdge edge)
    {
        using var source = new Source(edge);
        source.OnOwner(() =>
        {
            source.Router.MouseSpeedPercent = 200;
            source.Position(edge == ScreenEdge.Left ? 65534 : edge == ScreenEdge.Right ? 1 : 300,
                edge == ScreenEdge.Top ? 65534 : edge == ScreenEdge.Bottom ? 1 : 400);
            source.Move(edge == ScreenEdge.Right ? -1 : edge == ScreenEdge.Left ? 1 : 0,
                edge == ScreenEdge.Bottom ? -1 : edge == ScreenEdge.Top ? 1 : 0);
            check(!source.Router.IsRemote && source.NativeCalls == 1,
                $"{edge}: a scaled crossing must return immediately with exactly one existing local return warp.");
        });
        var leaves = source.Drain().Where(packet => packet.Type == "leave").ToArray();
        check(leaves.Length == 1 && leaves[0].Name == edge.ToString() && !leaves[0].Allow
            && (edge switch { ScreenEdge.Right => leaves[0].X == 0, ScreenEdge.Left => leaves[0].X == 65535,
                ScreenEdge.Top => leaves[0].Y == 65535, _ => leaves[0].Y == 0 }),
            $"{edge}: scaled return must keep its one ordered leave packet and exact boundary position.");
    }

    private static object Stroke(int key, int scan) => Activator.CreateInstance(
        typeof(InputRouter).GetNestedType("KeyStroke", BindingFlags.NonPublic)!, key, scan, false)!;

    // Models an already-entered A session. The real dispatcher has all hooks and input
    // injection disabled; even the return warp reaches only the fake native observer.
    private sealed class Source : IDisposable
    {
        private readonly ConcurrentQueue<Packet> _output = new();
        private readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal int NativeCalls { get; private set; }
        internal double X => (double)Get("_remoteX");
        internal double Y => (double)Get("_remoteY");
        internal Point Anchor => (Point)Get("_anchor");

        internal Source(ScreenEdge edge = ScreenEdge.Right)
        {
            Router.StartForTesting();
            Router.Send = packet => { _output.Enqueue(packet); if (packet.Type == "speed-barrier") _drained.Set(); };
            Router.NativeInputRequestedForTesting = _ => NativeCalls++;
            OnOwner(() =>
            {
                Router.ConfigureController(true, edge);
                Router.RemoteWidth = Router.RemoteHeight = 65536;
                Set("_anchor", new Point(100, 100));
                Set("_remote", true);
            });
        }

        internal void OnOwner(Action action) => Router.InvokeForTestingAsync(action).GetAwaiter().GetResult();
        internal object Get(string name) => typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        private void Set(string name, object value) => typeof(InputRouter).GetField(name, Private)!.SetValue(Router, value);
        internal void Call(string name, params object[] args) => typeof(InputRouter).GetMethod(name, Private)!.Invoke(Router, args);
        internal void Position(double x, double y) { Set("_remoteX", x); Set("_remoteY", y); Call("UpdateNormalizedRemote"); }
        internal void Move(int x, int y) => Call("ControllerMouseMoved", new Point(Anchor.X + x, Anchor.Y + y));
        internal Packet[] Drain()
        {
            _drained.Reset();
            OnOwner(() => Call("Queue", new Packet { Type = "speed-barrier" }));
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Scaled pointer output did not drain.");
            var packets = new List<Packet>();
            while (_output.TryDequeue(out var packet)) if (packet.Type != "speed-barrier") packets.Add(packet);
            return packets.ToArray();
        }
        public void Dispose() { Router.Dispose(); _drained.Dispose(); }
    }
}
