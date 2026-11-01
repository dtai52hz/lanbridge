using SharpHook.Data;

namespace LanBridge.CrossPlatform;

/// <summary>
/// Converts SharpHook's platform-independent physical keys to the Windows virtual keys used by
/// the existing wire protocol. Native macOS key codes and X11 key codes must never be sent as scans.
/// </summary>
public static class CanonicalKeyMap
{
    private static readonly (KeyCode Physical, int Virtual, int Scan, bool Extended)[] Entries = BuildEntries();
    private static readonly Dictionary<KeyCode, (int Virtual, int Scan, bool Extended)> Forward =
        Entries.ToDictionary(e => e.Physical, e => (e.Virtual, e.Scan, e.Extended));
    private static readonly Dictionary<int, KeyCode> Reverse = Entries
        .GroupBy(e => e.Virtual).ToDictionary(e => e.Key, e => e.First().Physical);

    public static int ToWindowsVirtualKey(KeyCode key) => Forward.TryGetValue(key, out var value) ? value.Virtual : 0;
    public static int ToWindowsScan(KeyCode key) => Forward.TryGetValue(key, out var value) ? value.Scan : 0;
    public static bool IsExtended(KeyCode key) => Forward.TryGetValue(key, out var value) && value.Extended;

    public static KeyCode ToSharpHookKeyCode(int key, int scan = 0, bool extended = false)
    {
        // Legacy Windows peers may send generic modifier VKs and use the scan/extended bit for side.
        if (key == 0x10) return scan == 0x36 ? KeyCode.VcRightShift : KeyCode.VcLeftShift;
        if (key == 0x11) return extended ? KeyCode.VcRightControl : KeyCode.VcLeftControl;
        if (key == 0x12) return extended ? KeyCode.VcRightAlt : KeyCode.VcLeftAlt;
        if (key == 0x0D && extended) return KeyCode.VcNumPadEnter;
        return Reverse.GetValueOrDefault(key, KeyCode.VcUndefined);
    }

    private static (KeyCode, int, int, bool)[] BuildEntries()
    {
        var entries = new List<(KeyCode, int, int, bool)>();
        void Add(KeyCode physical, int key, int scan = 0, bool extended = false) => entries.Add((physical, key, scan, extended));
        // Explicit set-1 scans preserve interoperability with the original Windows injector.
        int[] letterScans = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26,
            0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
        for (int i = 0; i < 26; i++) Add(Enum.Parse<KeyCode>("Vc" + (char)('A' + i)), 0x41 + i, letterScans[i]);
        for (int i = 0; i < 9; i++) Add(Enum.Parse<KeyCode>("Vc" + (i + 1)), 0x31 + i, 0x02 + i);
        Add(KeyCode.Vc0, 0x30, 0x0B);
        int[] functionScans = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
        for (int i = 0; i < 24; i++) Add(Enum.Parse<KeyCode>("VcF" + (i + 1)), 0x70 + i, i < 12 ? functionScans[i] : 0);
        Add(KeyCode.VcEscape, 0x1B, 0x01); Add(KeyCode.VcBackspace, 0x08, 0x0E);
        Add(KeyCode.VcTab, 0x09, 0x0F); Add(KeyCode.VcEnter, 0x0D, 0x1C);
        Add(KeyCode.VcSpace, 0x20, 0x39); Add(KeyCode.VcCapsLock, 0x14, 0x3A);
        Add(KeyCode.VcMinus, 0xBD, 0x0C); Add(KeyCode.VcEquals, 0xBB, 0x0D);
        Add(KeyCode.VcOpenBracket, 0xDB, 0x1A); Add(KeyCode.VcCloseBracket, 0xDD, 0x1B);
        Add(KeyCode.VcBackslash, 0xDC, 0x2B); Add(KeyCode.VcSemicolon, 0xBA, 0x27);
        Add(KeyCode.VcQuote, 0xDE, 0x28); Add(KeyCode.VcBackQuote, 0xC0, 0x29);
        Add(KeyCode.VcComma, 0xBC, 0x33); Add(KeyCode.VcPeriod, 0xBE, 0x34); Add(KeyCode.VcSlash, 0xBF, 0x35);
        Add(KeyCode.VcMisc, 0xE2, 0x56);
        Add(KeyCode.VcLeftShift, 0xA0, 0x2A); Add(KeyCode.VcRightShift, 0xA1, 0x36);
        Add(KeyCode.VcLeftControl, 0xA2, 0x1D); Add(KeyCode.VcRightControl, 0xA3, 0x1D, true);
        Add(KeyCode.VcLeftAlt, 0xA4, 0x38); Add(KeyCode.VcRightAlt, 0xA5, 0x38, true);
        Add(KeyCode.VcLeftMeta, 0x5B, 0x5B, true); Add(KeyCode.VcRightMeta, 0x5C, 0x5C, true);
        Add(KeyCode.VcContextMenu, 0x5D, 0x5D, true);
        Add(KeyCode.VcPrintScreen, 0x2C, 0x37, true); Add(KeyCode.VcScrollLock, 0x91, 0x46);
        Add(KeyCode.VcPause, 0x13); Add(KeyCode.VcCancel, 0x03); Add(KeyCode.VcHelp, 0x2F);
        Add(KeyCode.VcInsert, 0x2D, 0x52, true); Add(KeyCode.VcDelete, 0x2E, 0x53, true);
        Add(KeyCode.VcHome, 0x24, 0x47, true); Add(KeyCode.VcEnd, 0x23, 0x4F, true);
        Add(KeyCode.VcPageUp, 0x21, 0x49, true); Add(KeyCode.VcPageDown, 0x22, 0x51, true);
        Add(KeyCode.VcUp, 0x26, 0x48, true); Add(KeyCode.VcLeft, 0x25, 0x4B, true);
        Add(KeyCode.VcRight, 0x27, 0x4D, true); Add(KeyCode.VcDown, 0x28, 0x50, true);
        Add(KeyCode.VcNumLock, 0x90, 0x45, true);
        int[] numberScans = [0x4F, 0x50, 0x51, 0x4B, 0x4C, 0x4D, 0x47, 0x48, 0x49];
        for (int i = 0; i < 9; i++) Add(Enum.Parse<KeyCode>("VcNumPad" + (i + 1)), 0x61 + i, numberScans[i]);
        Add(KeyCode.VcNumPad0, 0x60, 0x52); Add(KeyCode.VcNumPadClear, 0x0C, 0x4C);
        Add(KeyCode.VcNumPadDivide, 0x6F, 0x35, true); Add(KeyCode.VcNumPadMultiply, 0x6A, 0x37);
        Add(KeyCode.VcNumPadSubtract, 0x6D, 0x4A); Add(KeyCode.VcNumPadAdd, 0x6B, 0x4E);
        Add(KeyCode.VcNumPadEnter, 0x0D, 0x1C, true); Add(KeyCode.VcNumPadDecimal, 0x6E, 0x53);
        Add(KeyCode.VcNumPadSeparator, 0x6C);
        Add(KeyCode.VcSleep, 0x5F); Add(KeyCode.VcMediaPlay, 0xB3); Add(KeyCode.VcMediaStop, 0xB2);
        Add(KeyCode.VcMediaPrevious, 0xB1); Add(KeyCode.VcMediaNext, 0xB0); Add(KeyCode.VcMediaSelect, 0xB5);
        Add(KeyCode.VcVolumeMute, 0xAD); Add(KeyCode.VcVolumeDown, 0xAE); Add(KeyCode.VcVolumeUp, 0xAF);
        Add(KeyCode.VcAppMail, 0xB4); Add(KeyCode.VcApp1, 0xB6); Add(KeyCode.VcApp2, 0xB7);
        Add(KeyCode.VcBrowserSearch, 0xAA); Add(KeyCode.VcBrowserHome, 0xAC);
        Add(KeyCode.VcBrowserBack, 0xA6); Add(KeyCode.VcBrowserForward, 0xA7);
        Add(KeyCode.VcBrowserStop, 0xA9); Add(KeyCode.VcBrowserRefresh, 0xA8); Add(KeyCode.VcBrowserFavorites, 0xAB);
        Add(KeyCode.VcKana, 0x15); Add(KeyCode.VcJunja, 0x17); Add(KeyCode.VcFinal, 0x18);
        Add(KeyCode.VcHanja, 0x19); Add(KeyCode.VcAccept, 0x1E); Add(KeyCode.VcConvert, 0x1C);
        Add(KeyCode.VcNonConvert, 0x1D); Add(KeyCode.VcImeOn, 0x16); Add(KeyCode.VcImeOff, 0x1A);
        Add(KeyCode.VcModeChange, 0x1F); Add(KeyCode.VcProcess, 0xE5);
        return entries.ToArray();
    }
}
