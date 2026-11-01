using System.Runtime.InteropServices;
using System.Text;
using SharpHook;
using SharpHook.Data;

namespace LanBridge.CrossPlatform;

/// <summary>Read-only layout translation; never creates a second hook or types into the local application.</summary>
internal static class NativeTextTranslator
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private static readonly Lazy<nint> MacLayoutProperty = new(() =>
        Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Carbon), "kTISPropertyUnicodeKeyLayoutData")));

    public static string? Translate(KeyboardHookEventArgs args, int key, EventMask mask)
    {
        if ((mask & (EventMask.Ctrl | EventMask.Alt | EventMask.Meta)) != 0 ||
            key is 0x08 or 0x09 or 0x0D or 0x1B || key is >= 0x21 and <= 0x28 ||
            key is >= 0x70 and <= 0x87 || key is >= 0xA0 and <= 0xA5 || key is 0x5B or 0x5C)
            return null;
        string? text = null;
        if (OperatingSystem.IsWindows()) text = TranslateWindows(key, CanonicalKeyMap.ToWindowsScan(args.Data.KeyCode), mask);
        else if (OperatingSystem.IsMacOS()) text = TranslateMac(args.Data.RawCode, mask);
        return IsPrintable(text) ? text : null;
    }

    private static string? TranslateWindows(int key, int scan, EventMask mask)
    {
        var state = new byte[256];
        if ((mask & EventMask.Shift) != 0) state[0x10] = 0x80;
        if ((mask & EventMask.CapsLock) != 0) state[0x14] = 1;
        if ((mask & EventMask.NumLock) != 0) state[0x90] = 1;
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var text = new StringBuilder(16);
        // Flag 4 leaves Windows' dead-key state unchanged (Windows 10 1607+).
        int count = ToUnicodeEx((uint)key, (uint)scan, state, text, text.Capacity, 4, GetKeyboardLayout(thread));
        return count > 0 ? text.ToString(0, Math.Min(count, text.Length)) : null;
    }

    private static string? TranslateMac(ushort nativeKey, EventMask mask)
    {
        nint source = TISCopyCurrentKeyboardLayoutInputSource();
        if (source == 0) return null;
        try
        {
            nint data = TISGetInputSourceProperty(source, MacLayoutProperty.Value);
            if (data == 0) return null;
            nint layout = CFDataGetBytePtr(data);
            if (layout == 0) return null;
            // Carbon modifiers are the old EventRecord bits shifted right eight places.
            uint modifiers = ((mask & EventMask.Shift) != 0 ? 2u : 0u) | ((mask & EventMask.CapsLock) != 0 ? 4u : 0u);
            uint deadKey = 0;
            var chars = new char[16];
            int status = UCKeyTranslate(layout, nativeKey, 0, modifiers, LMGetKbdType(), 1,
                ref deadKey, chars.Length, out nint count, chars);
            return status == 0 && count > 0 && count <= chars.Length ? new(chars, 0, (int)count) : null;
        }
        finally { CFRelease(source); }
    }

    private static bool IsPrintable(string? text) => !string.IsNullOrEmpty(text) && text.All(c => !char.IsControl(c));

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int capacity, uint flags, nint layout);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("user32.dll")] private static extern nint GetKeyboardLayout(uint thread);
    [DllImport(Carbon)] private static extern nint TISCopyCurrentKeyboardLayoutInputSource();
    [DllImport(Carbon)] private static extern nint TISGetInputSourceProperty(nint source, nint property);
    [DllImport(Carbon)] private static extern byte LMGetKbdType();
    [DllImport(Carbon, CharSet = CharSet.Unicode, ExactSpelling = true)] private static extern int UCKeyTranslate(nint layout, ushort key, ushort action, uint modifiers,
        uint keyboardType, uint options, ref uint deadKey, nint capacity, out nint count, [Out] char[] text);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint instance);
}
