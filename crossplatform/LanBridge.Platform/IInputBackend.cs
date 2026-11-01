namespace LanBridge.CrossPlatform;

public readonly record struct DesktopBounds(int X, int Y, int Width, int Height);
public readonly record struct DesktopPoint(int X, int Y);
public enum PhysicalInputKind { Move, Button, Wheel, Key }

/// <summary>Canonical Windows virtual keys are retained on the wire for compatibility with LanBridge 0.7.x.</summary>
public sealed record PhysicalInput(PhysicalInputKind Kind, int X = 0, int Y = 0,
    int Key = 0, int Scan = 0, bool Extended = false, bool Down = false,
    string? Button = null, int WheelX = 0, int WheelY = 0, string? Text = null,
    bool Relative = false, double DeltaX = 0, double DeltaY = 0);

public sealed record BackendCapabilities(bool Capture, bool Inject, bool UnicodeText, string Description);

public interface IInputBackend : IDisposable
{
    string Name { get; }
    BackendCapabilities Capabilities { get; }
    DesktopBounds Bounds { get; }
    DesktopPoint Position { get; }
    /// <summary>Must run synchronously; true requests suppression on platforms that support capture.</summary>
    event Func<PhysicalInput, bool>? Input;
    event Action<string>? Error;
    void Start();
    void SetCapture(bool active);
    void Warp(DesktopPoint point);
    /// <summary>Called in FIFO order on a worker, never from the UI or global hook callback.</summary>
    void Inject(Packet packet);
    void ReleaseAll();
}
