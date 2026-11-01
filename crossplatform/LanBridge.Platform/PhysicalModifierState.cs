using SharpHook.Data;

namespace LanBridge.CrossPlatform;

/// <summary>
/// Native OS state excludes suppressed keys. Keep physical modifier changes on the hook thread,
/// and freeze the real lock baseline while the router applies swallowed lock-key presses itself.
/// </summary>
internal sealed class PhysicalModifierState
{
    private EventMask _known, _held;
    private int _lastNative;
    private volatile bool _capturing, _captureCapsLock;

    public void ObservePointer(EventMask nativeMask) => Volatile.Write(ref _lastNative, (int)(nativeMask & EventMask.CapsLock));

    public void SetCapture(bool active)
    {
        if (active && !_capturing) _captureCapsLock = ((EventMask)Volatile.Read(ref _lastNative) & EventMask.CapsLock) != 0;
        _capturing = active;
    }

    public EventMask ObserveKey(int key, bool down, EventMask nativeMask)
    {
        ObservePointer(nativeMask);
        EventMask modifier = key switch
        {
            0xA0 => EventMask.LeftShift, 0xA1 => EventMask.RightShift,
            0xA2 => EventMask.LeftCtrl, 0xA3 => EventMask.RightCtrl,
            0xA4 => EventMask.LeftAlt, 0xA5 => EventMask.RightAlt,
            0x5B => EventMask.LeftMeta, 0x5C => EventMask.RightMeta, _ => EventMask.None,
        };
        _known |= modifier;
        if (down) _held |= modifier; else _held &= ~modifier;
        var effective = (nativeMask & ~_known) | _held;
        if (_capturing) effective = (effective & ~EventMask.CapsLock) | (_captureCapsLock ? EventMask.CapsLock : EventMask.None);
        return effective;
    }
}
