using System.Collections.Concurrent;
using System.Drawing;
using System.Reflection;
using LanBridge;

/// <summary>Runs the real low-level mouse callback through an inert native-disabled dispatcher.</summary>
internal static class ExclusivePointerChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private const int Move = 0x0200, Wheel = 0x020A, HorizontalWheel = 0x020E;
    private static readonly (string Name, int Down, int Up, uint Data)[] Buttons =
    [
        ("left", 0x0201, 0x0202, 0), ("right", 0x0204, 0x0205, 0),
        ("middle", 0x0207, 0x0208, 0), ("x1", 0x020B, 0x020C, 1u << 16),
        ("x2", 0x020B, 0x020C, 2u << 16),
    ];

    internal static void Run(Action<bool, string> check)
    {
        CheckPhysicalCapture(check);
        CheckInjectedDisposition(check);
        CheckReturnQuarantine(check, importedLocalDown: false);
        CheckReturnQuarantine(check, importedLocalDown: true);
        CheckRapidReentryQuarantine(check);
        CheckReturnCrossing(check);
    }

    private static void CheckPhysicalCapture(Action<bool, string> check)
    {
        using var route = new Route();
        var point = route.Field<Rectangle>("_bounds");
        check(!route.Mouse(Move, point.Left + point.Width / 2, point.Top + point.Height / 2),
            "Local A must pass physical pointer input through before entering B.");
        int next = route.NextCalls;
        route.Enter(); route.Take();
        var anchor = route.Field<Point>("_anchor");
        check(route.Mouse(Move, anchor.X + 2, anchor.Y + 1),
            "The actual mouse callback must consume remote A's physical movement.");
        foreach (var button in Buttons)
        {
            check(route.Mouse(button.Down, anchor.X, anchor.Y, button.Data)
                    && route.Mouse(button.Up, anchor.X, anchor.Y, button.Data),
                $"Remote A's {button.Name} down/up must both be consumed at the callback boundary.");
        }
        check(route.Mouse(Wheel, anchor.X, anchor.Y, 120u << 16)
                && route.Mouse(HorizontalWheel, anchor.X, anchor.Y, unchecked((uint)(ushort)-120) << 16),
            "Remote A must consume both physical wheel axes.");
        var packets = route.Take();
        check(route.NextCalls == next, "No consumed remote physical pointer action may reach A's next hook.");
        var actions = packets.Where(p => p.Type is "button" or "wheel").ToArray();
        check(actions.Length == Buttons.Length * 2 + 2,
            "Every physical button/wheel callback must be routed to B exactly once.");
        for (int i = 0; i < Buttons.Length; i++)
            check(actions[i * 2].Button == Buttons[i].Name && actions[i * 2].Down
                    && actions[i * 2 + 1].Button == Buttons[i].Name && !actions[i * 2 + 1].Down,
                "Physical capture must preserve each button identity and balanced down/up order.");
        check(actions[^2].Type == "wheel" && actions[^2].Dy == 120 && actions[^2].Dx == 0
                && actions[^1].Type == "wheel" && actions[^1].Dx == -120 && actions[^1].Dy == 0,
            "Physical wheel capture must preserve signed horizontal/vertical Windows detents.");
        check(route.Mouse(0x02FF, anchor.X, anchor.Y) && route.Take().Length == 0 && route.NextCalls == next,
            "An unsupported physical pointer message must not escape to A during remote capture.");
    }

    private static void CheckInjectedDisposition(Action<bool, string> check)
    {
        foreach (bool controller in new[] { true, false })
        foreach (bool remote in new[] { false, true })
        {
            using var route = new Route();
            route.Router.IsController = controller;
            if (remote)
            {
                if (controller) route.Enter();
                else route.Router.ApplyRemoteAsync(new Packet { Type = "enter", X = 32768, Y = 32768 }).GetAwaiter().GetResult();
            }
            route.Take();
            bool suppressForeign = controller && remote;
            var anchor = route.Field<Point>("_anchor");
            foreach (uint flags in new[] { 1u, 3u })
            foreach (int message in new[] { Move, Buttons[0].Down, Buttons[0].Up, Wheel, HorizontalWheel })
            {
                int before = route.NextCalls;
                check(route.Mouse(message, anchor.X + 5, anchor.Y + 5, 120u << 16, flags, 0x12345678) == suppressForeign,
                    "Foreign simulated pointer input must be blocked only while A is the remote controller.");
                check(route.NextCalls == before + (suppressForeign ? 0 : 1),
                    "Foreign injected input suppression must occur before the local next-hook boundary.");
            }
            check(route.Take().Length == 0,
                "Foreign simulated pointer input must never be forwarded as duplicate physical input to B.");
            foreach (uint flags in new[] { 0u, 1u, 3u })
            foreach (int message in new[] { Move, Buttons[0].Up, Wheel })
            {
                int before = route.NextCalls;
                check(!route.Mouse(message, anchor.X, anchor.Y, 0, flags, InputRouter.InjectionTag)
                        && route.NextCalls == before + 1,
                    "LanBridge's tagged anchor/cleanup input must pass even during exclusive capture.");
            }
            int next = route.NextCalls;
            check(!route.Mouse(Move, anchor.X, anchor.Y, 0, 1, 0x12345678, code: -1) && route.NextCalls == next + 1,
                "A negative hook code must pass through before exclusive-capture filtering.");
            check(route.Take().Length == 0, "Own-tagged or negative-code pointer input must never create remote packets.");
        }
    }

    private static void CheckReturnQuarantine(Action<bool, string> check, bool importedLocalDown)
    {
        foreach (var button in Buttons)
        {
            using var route = new Route();
            var point = route.Field<Rectangle>("_bounds");
            int x = point.Left + point.Width / 2, y = point.Top + point.Height / 2;
            if (importedLocalDown)
                check(!route.Mouse(button.Down, x, y, button.Data), "A button already held on local A starts as ordinary local input.");
            route.Enter(); route.Take();
            if (!importedLocalDown)
            {
                check(route.Mouse(button.Down, x, y, button.Data), "A button pressed on B must be owned by remote capture.");
                route.Take();
            }
            route.Router.ReleaseRemote();
            var released = route.Take();
            check(!route.Router.IsRemote && released.Count(p => p.Type == "button" && p.Button == button.Name && !p.Down) == 1
                    && released.Last().Type == "leave",
                "Returning to A must release B's held button exactly once before leave.");
            int next = route.NextCalls;
            check(route.Mouse(button.Up, x, y, button.Data) && route.NextCalls == next && route.Take().Length == 0,
                "The trailing up of a button held on B must not click or open a menu on local A.");
            check(!route.Field<HashSet<string>>("_physicalButtons").Contains(button.Name),
                "A quarantined real button-up must clear physical tracking so idle maintenance can resume.");
            check(!route.Mouse(button.Down, x, y, button.Data) && !route.Mouse(button.Up, x, y, button.Data)
                    && route.NextCalls == next + 2 && route.Take().Length == 0,
                "After the real release, a fresh local click must work normally on A.");
        }
    }

    private static void CheckReturnCrossing(Action<bool, string> check)
    {
        using var route = new Route();
        route.Enter(); route.Take();
        var anchor = route.Field<Point>("_anchor");
        int next = route.NextCalls;
        check(route.Mouse(Move, anchor.X - 8, anchor.Y) && !route.Router.IsRemote && route.NextCalls == next,
            "The move that crosses back to A must be consumed while restoring the local pointer once.");
        var packets = route.Take();
        check(packets is [{ Type: "leave" }], "A return-edge movement must not become a click or a final remote move.");
        check(!route.Mouse(Move, anchor.X, anchor.Y) && route.NextCalls == next + 1,
            "The next fresh local movement must be usable immediately after the return crossing.");
    }

    private static void CheckRapidReentryQuarantine(Action<bool, string> check)
    {
        foreach (var button in Buttons)
        {
            using var route = new Route();
            route.Enter(); route.Take();
            var anchor = route.Field<Point>("_anchor");
            check(route.Mouse(button.Down, anchor.X, anchor.Y, button.Data), "A remote button begins owned by B.");
            route.Take(); route.Router.ReleaseRemote(); route.Take();
            route.Enter();
            check(route.Take() is [{ Type: "enter" }],
                "Reentering B while an old button remains held must not revive its quarantined down.");
            check(route.Mouse(button.Up, anchor.X, anchor.Y, button.Data) && route.Take().Length == 0,
                "An old held button's eventual up remains consumed across a rapid return and reentry.");
            check(route.Mouse(button.Down, anchor.X, anchor.Y, button.Data)
                    && route.Mouse(button.Up, anchor.X, anchor.Y, button.Data),
                "After that release, a fresh click must work immediately on B.");
            check(route.Take() is [{ Type: "button", Down: true }, { Type: "button", Down: false }],
                "A fresh post-quarantine remote click must route its balanced down/up exactly once.");
        }
    }

    private sealed class Route : IDisposable
    {
        private const string Barrier = "exclusive-pointer-test-barrier";
        private readonly ConcurrentQueue<Packet> _packets = new();
        private readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal int NextCalls;
        internal Route()
        {
            Router.StartForTesting();
            Router.ConfigureController(true, ScreenEdge.Right);
            Router.TypingMode = TypingMode.Physical;
            Router.MouseHookNextForTesting = (_, _, _) => { NextCalls++; return IntPtr.Zero; };
            Router.NativeInputRequestedForTesting = _ => { };
            Router.Send = packet => { if (packet.Type == Barrier) _drained.Set(); else _packets.Enqueue(packet); };
        }
        internal bool Mouse(int message, int x, int y, uint data = 0, uint flags = 0, ulong extraInfo = 0, int code = 0) =>
            Router.RouteMouseHookForTestingAsync(message, x, y, data, flags, extraInfo, code).GetAwaiter().GetResult();
        internal T Field<T>(string name) => (T)typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        private object? Call(string name, params object[] arguments) => typeof(InputRouter).GetMethod(name, Private)!.Invoke(Router, arguments);
        internal void Enter() => Router.InvokeForTestingAsync(() =>
        {
            var bounds = Field<Rectangle>("_bounds");
            Call("EnterRemote", new Point(bounds.Right - 1, bounds.Top + bounds.Height / 2));
        }).GetAwaiter().GetResult();
        internal Packet[] Take()
        {
            _drained.Reset();
            Router.InvokeForTestingAsync(() => Call("Queue", new Packet { Type = Barrier })).GetAwaiter().GetResult();
            if (!_drained.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Exclusive pointer packets did not drain.");
            var result = new List<Packet>();
            while (_packets.TryDequeue(out var packet)) result.Add(packet);
            return result.ToArray();
        }
        public void Dispose() { Router.Dispose(); _drained.Dispose(); }
    }
}
