using System.Reflection;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using LanBridge;

// Deliberately never call Start or hook callbacks. Real routing paths run only on the
// StartForTesting dispatcher, which disables all native injection and hook installation.
// Nothing may move the user's cursor, type into applications, or block physical input.
int assertions = 0;
void Check(bool result, string message)
{
    assertions++;
    if (!result) throw new InvalidOperationException(message);
}

foreach (var (origin, length) in new[] { (0, 1920), (-1920, 3840), (-4096, 8192), (-2400, 2400), (500, 1), (-32768, 65536) })
{
    Check(InputRouter.Normalize(origin, origin, length) == 0, "Virtual origin must normalize to zero.");
    Check(InputRouter.Denormalize(0, origin, length) == origin, "Normalized origin must preserve negative coordinates.");
    Check(InputRouter.Denormalize(65535, origin, length) == origin + length - 1, "Last desktop pixel must be reachable.");
    if (length > 1) Check(InputRouter.Normalize(origin + length - 1, origin, length) == 65535, "Last pixel must normalize to 65535.");
    for (int offset = 0; offset < length; offset += Math.Max(1, length / 97))
    {
        int original = origin + offset;
        int restored = InputRouter.Denormalize(InputRouter.Normalize(original, origin, length), origin, length);
        Check(restored == original, "Normalize/denormalize must round-trip all supported desktop pixels.");
    }
    Check(InputRouter.Normalize(origin - 100000, origin, length) == 0, "Values below the desktop must clamp.");
    Check(InputRouter.Normalize(origin + length + 100000, origin, length) == 65535, "Values above the desktop must clamp.");
    Check(InputRouter.Denormalize(-1, origin, length) == origin, "Malformed negative wire coordinates must clamp.");
    Check(InputRouter.Denormalize(100000, origin, length) == origin + length - 1, "Malformed wire coordinates must clamp.");
}

Type Nested(string name) => typeof(InputRouter).GetNestedType(name, BindingFlags.NonPublic) ?? throw new InvalidOperationException(name);
Check(Marshal.SizeOf(Nested("NativeInput")) == (IntPtr.Size == 8 ? 40 : 28), "INPUT native structure size is wrong.");
Check(Marshal.SizeOf(Nested("MouseInput")) == (IntPtr.Size == 8 ? 32 : 24), "MOUSEINPUT native structure size is wrong.");
Check(Marshal.SizeOf(Nested("KeyboardInput")) == (IntPtr.Size == 8 ? 24 : 16), "KEYBDINPUT native structure size is wrong.");
Check(Marshal.SizeOf(Nested("MouseHookData")) == (IntPtr.Size == 8 ? 32 : 24), "MSLLHOOKSTRUCT native structure size is wrong.");
Check(Marshal.SizeOf(Nested("KeyboardHookData")) == (IntPtr.Size == 8 ? 24 : 20), "KBDLLHOOKSTRUCT native structure size is wrong.");
Check(Marshal.OffsetOf(Nested("NativeInput"), "Union").ToInt32() == (IntPtr.Size == 8 ? 8 : 4), "INPUT union alignment is wrong.");
Check(Marshal.OffsetOf(Nested("MouseInput"), "ExtraInfo").ToInt32() == (IntPtr.Size == 8 ? 24 : 20), "MOUSEINPUT pointer alignment is wrong.");
Check(Marshal.OffsetOf(Nested("KeyboardInput"), "ExtraInfo").ToInt32() == (IntPtr.Size == 8 ? 16 : 12), "KEYBDINPUT pointer alignment is wrong.");

using (var router = new InputRouter())
{
    Check(!router.IsRemote && !router.CanControl && !router.IsController, "Router must be inert by default.");
    router.ConfigureController(true, ScreenEdge.Left);
    Check(router.IsController && router.CanControl && router.ExitEdge == ScreenEdge.Left, "Controller configuration must preserve the selected edge.");
    router.SetBlockLocalInput(true);
    var block = typeof(InputRouter).GetField("_blockLocal", BindingFlags.Instance | BindingFlags.NonPublic)!;
    Check(!(bool)block.GetValue(router)!, "Controller A must never block its own physical input.");
    router.ApplyRemote(new Packet { Type = "enter", X = 123, Y = 456 });
    Check(!router.IsRemote, "Incoming packets before Start must never activate input control.");
    router.IsController = false;
    router.SetBlockLocalInput(true);
    Check((bool)block.GetValue(router)!, "Host may remember a request to block input.");
    router.CanControl = false;
    Check(!(bool)block.GetValue(router)!, "Revoking input must clear local blocking.");
    router.ForceReleaseAll();
    router.ReleaseRemote();
    Check(!router.IsRemote, "Release must be idempotent before Start.");
}

// These private helpers only enqueue packets; testing them never invokes native input.
// Critical button/wheel packets must contain their own coordinates because move packets may drop.
using (var router = new InputRouter())
using (var delivered = new ManualResetEventSlim())
{
    var packets = new ConcurrentQueue<Packet>();
    int producerThread = Environment.CurrentManagedThreadId, consumerThread = producerThread;
    router.Send = packet =>
    {
        consumerThread = Environment.CurrentManagedThreadId;
        packets.Enqueue(packet);
        if (packets.Count == 3) delivered.Set();
    };
    typeof(InputRouter).GetField("_lastNormX", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(router, 43210);
    typeof(InputRouter).GetField("_lastNormY", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(router, 12345);
    typeof(InputRouter).GetMethod("SendButton", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(router, ["left", true]);
    typeof(InputRouter).GetMethod("SendWheel", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(router, [120, -240]);
    router.ForceReleaseAll();
    Check(delivered.Wait(TimeSpan.FromSeconds(3)), "Ordered output worker failed to deliver critical packets.");
    var output = packets.ToArray();
    Check(output.All(packet => packet.X == 43210 && packet.Y == 12345), "Button, wheel, and cleanup packets must retain the exact cursor position.");
    Check(output[0].Type == "button" && output[0].Down && output[1].Type == "wheel" && output[2].Type == "button" && !output[2].Down,
        "Critical input packet ordering was not preserved.");
    Check(output[1].Dx == 120 && output[1].Dy == -240, "Horizontal and vertical wheel deltas changed.");
    Check(consumerThread != producerThread, "Send callback must never run inline in the input hook producer.");
}
EdgeChecks.Run(Check);
UnicodeChecks.Run(Check);
KeyboardRouteChecks.Run(Check);
DispatcherChecks.Run(Check);
PointerPipelineChecks.Run(Check);
CapturePathChecks.Run(Check);
MouseSpeedChecks.Run(Check);
SwitchShortcutChecks.Run(Check);
InputDiagnosticsChecks.Run(Check);
NativeWorkerChecks.Run(Check);
Console.WriteLine($"Native input smoke passed: {assertions} assertions; no hooks installed and no input injected.");
