using System.Diagnostics;
using SharpHook.Data;

namespace LanBridge.CrossPlatform;

/// <summary>Pure receiver-side click state for Quartz, which does not infer double clicks from posted down/up events.</summary>
internal sealed class MouseClickSequence(TimeSpan interval, int distance = 4)
{
    private readonly long _intervalTicks = interval > TimeSpan.Zero && interval <= TimeSpan.FromSeconds(60)
        ? (long)(interval.TotalSeconds * Stopwatch.Frequency) : throw new ArgumentOutOfRangeException(nameof(interval));
    private readonly int _distance = distance is >= 0 and <= 1000 ? distance : throw new ArgumentOutOfRangeException(nameof(distance));
    private readonly Dictionary<MouseButton, ushort> _pressed = [];
    private MouseButton _lastButton;
    private DesktopPoint _lastPoint;
    private long _lastDown;
    private ushort _lastCount;
    private bool _eligible, _movedTooFar;

    public ushort Press(MouseButton button, DesktopPoint point, long timestamp)
    {
        if (button == MouseButton.NoButton) throw new ArgumentException("A mouse button is required.", nameof(button));
        if (_pressed.TryGetValue(button, out ushort heldCount)) return heldCount;
        bool next = _pressed.Count == 0 && _eligible && button == _lastButton && timestamp >= _lastDown
            && timestamp - _lastDown <= _intervalTicks && Nearby(point);
        // Double/triple clicks cover selection gestures; repeated or hostile packets cannot grow native counts without bound.
        ushort count = next ? (ushort)Math.Min(3, _lastCount + 1) : (ushort)1;
        _lastButton = button; _lastPoint = point; _lastDown = timestamp; _lastCount = count;
        _eligible = _movedTooFar = false; _pressed[button] = count;
        return count;
    }

    public ushort Release(MouseButton button, DesktopPoint point)
    {
        if (!_pressed.Remove(button, out ushort count)) return 1;
        if (button == _lastButton) _eligible = !_movedTooFar && Nearby(point);
        return count;
    }

    public void Move(DesktopPoint point)
    {
        if (Nearby(point)) return;
        _eligible = false;
        if (_pressed.ContainsKey(_lastButton)) _movedTooFar = true;
    }

    public void Clear()
    {
        _pressed.Clear(); _lastButton = MouseButton.NoButton; _lastCount = 0; _eligible = _movedTooFar = false;
    }

    private bool Nearby(DesktopPoint point) => Math.Abs((long)point.X - _lastPoint.X) <= _distance
        && Math.Abs((long)point.Y - _lastPoint.Y) <= _distance;
}
