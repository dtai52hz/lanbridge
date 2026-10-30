using System.Drawing;
using System.Reflection;
using LanBridge;

internal static class EdgeChecks
{
    internal static void Run(Action<bool, string> check)
    {
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(InputRouter router, string name, object value) =>
            typeof(InputRouter).GetField(name, fields)!.SetValue(router, value);
        var bounds = new Rectangle(-1920, -200, 3840, 1280);
        var edges = new[]
        {
            (ScreenEdge.Right, new Point(1909, 500), new Point(1919, 500), new Point(1929, 500)),
            (ScreenEdge.Left, new Point(-1910, 500), new Point(-1920, 500), new Point(-1930, 500)),
            (ScreenEdge.Top, new Point(0, -190), new Point(0, -200), new Point(0, -210)),
            (ScreenEdge.Bottom, new Point(0, 1069), new Point(0, 1079), new Point(0, 1089)),
        };
        foreach (var (edge, interior, exit, outside) in edges)
        {
            using var router = new InputRouter();
            check(router.EdgeDwellMilliseconds == 0, "Edge switching must have no dwell by default.");
            router.ConfigureController(true, edge);
            Set(router, "_bounds", bounds);
            Set(router, "_lastLocalPoint", interior);
            check(!router.TrackExitEdge(interior, 100), $"{edge}: interior movement must stay on A.");
            check(router.TrackExitEdge(exit, 100), $"{edge}: the first outward edge event must cross at the same timestamp.");

            // Return warps ten pixels inward; reaching the edge again re-arms immediately.
            router.ReleaseRemote();
            check(!router.TrackExitEdge(interior, 100), $"{edge}: the return margin must remain on A.");
            check(router.TrackExitEdge(exit, 100), $"{edge}: returning and crossing again must not impose a time cooldown.");
            Set(router, "_lastLocalPoint", outside);
            check(!router.TrackExitEdge(exit, 100), $"{edge}: an inward movement must not start a crossing.");

            router.CanControl = false;
            Set(router, "_lastLocalPoint", interior);
            check(!router.TrackExitEdge(exit, 100), $"{edge}: revoked input permission must prevent crossing.");
            router.CanControl = true;
            Set(router, "_remote", true);
            Set(router, "_lastLocalPoint", interior);
            check(!router.TrackExitEdge(exit, 100), $"{edge}: an active remote session must not start a duplicate crossing.");
            Set(router, "_remote", false);
            router.IsController = false;
            Set(router, "_lastLocalPoint", interior);
            check(!router.TrackExitEdge(exit, 100), $"{edge}: host B must never initiate controller edge switching.");
        }

        using (var router = new InputRouter { EdgeDwellMilliseconds = 180 })
        {
            router.ConfigureController(true, ScreenEdge.Right);
            Set(router, "_bounds", bounds);
            Set(router, "_lastLocalPoint", new Point(1909, 500));
            check(!router.TrackExitEdge(new Point(1919, 500), 100), "An explicitly configured dwell must wait.");
            check(!router.TrackExitEdge(new Point(1919, 500), 279), "A positive dwell must not complete early.");
            check(router.TrackExitEdge(new Point(1919, 500), 280), "A positive dwell must complete at the configured duration.");
            check(!router.TrackExitEdge(new Point(1909, 500), 281), "Moving away must cancel a pending edge dwell.");
            check(!router.TrackExitEdge(new Point(1919, 500), 282), "Returning after cancellation must start a fresh dwell.");
            router.EdgeDwellMilliseconds = -1;
            check(router.TrackExitEdge(new Point(1919, 500), 282), "Negative dwell must clamp to immediate switching.");
        }
    }
}
