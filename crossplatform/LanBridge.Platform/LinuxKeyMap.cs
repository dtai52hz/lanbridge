using System.Text;

namespace LanBridge.CrossPlatform;

/// <summary>X11 key symbols are not Windows virtual keys or Windows scan codes.</summary>
public static class LinuxKeyMap
{
    public static int ToWindowsVirtualKey(ulong symbol, int xorgKeyCode = 0)
    {
        if (symbol is >= 'a' and <= 'z') return (int)symbol - 32;
        if (symbol is >= 'A' and <= 'Z' or >= '0' and <= '9') return (int)symbol;
        if (symbol is >= 0xFFBE and <= 0xFFD5) return 0x70 + (int)(symbol - 0xFFBE);
        if (symbol is >= 0xFFB0 and <= 0xFFB9) return 0x60 + (int)(symbol - 0xFFB0);
        int key = symbol switch
        {
            0xFF08 => 0x08, 0xFF09 or 0xFE20 => 0x09, 0xFF0D or 0xFF8D => 0x0D,
            0xFF13 => 0x13, 0xFF14 => 0x91, 0xFF1B => 0x1B,
            0xFF50 => 0x24, 0xFF51 => 0x25, 0xFF52 => 0x26, 0xFF53 => 0x27,
            0xFF54 => 0x28, 0xFF55 => 0x21, 0xFF56 => 0x22, 0xFF57 => 0x23,
            0xFF63 => 0x2D, 0xFFFF => 0x2E,
            // Preserve keypad identity even when NumLock maps the symbol to navigation.
            0xFF95 => 0x67, 0xFF96 => 0x64, 0xFF97 => 0x68, 0xFF98 => 0x66,
            0xFF99 => 0x62, 0xFF9A => 0x69, 0xFF9B => 0x63, 0xFF9C => 0x61,
            0xFF9D => 0x65, 0xFF9E => 0x60, 0xFF9F => 0x6E,
            0xFF61 => 0x2C, 0xFF67 => 0x5D, 0xFF7F => 0x90,
            0xFFE1 => 0xA0, 0xFFE2 => 0xA1, 0xFFE3 => 0xA2, 0xFFE4 => 0xA3,
            0xFFE5 or 0xFFE6 => 0x14, 0xFFE9 => 0xA4, 0xFFEA or 0xFE03 => 0xA5,
            0xFFEB or 0xFFE7 => 0x5B, 0xFFEC or 0xFFE8 => 0x5C,
            0xFFAA => 0x6A, 0xFFAB => 0x6B, 0xFFAC => 0x6C, 0xFFAD => 0x6D,
            0xFFAE => 0x6E, 0xFFAF => 0x6F,
            ' ' => 0x20, ';' or ':' => 0xBA, '=' or '+' => 0xBB,
            ',' or '<' => 0xBC, '-' or '_' => 0xBD, '.' or '>' => 0xBE,
            '/' or '?' => 0xBF, '`' or '~' => 0xC0, '[' or '{' => 0xDB,
            '\\' or '|' => 0xDC, ']' or '}' => 0xDD, '\'' or '"' => 0xDE,
            0x1008FF11 => 0xAE, 0x1008FF12 => 0xAD, 0x1008FF13 => 0xAF,
            0x1008FF14 => 0xB3, 0x1008FF15 => 0xB2, 0x1008FF16 => 0xB1,
            0x1008FF17 => 0xB0, 0x1008FF18 => 0xAC, 0x1008FF19 => 0xB4,
            0x1008FF26 => 0xA6, 0x1008FF27 => 0xA7, 0x1008FF29 => 0xA8,
            _ => 0
        };
        // Xorg's evdev keyboard uses Linux input codes plus eight. This fallback is only used
        // for non-Latin symbol groups, retaining the physical position for raw-key mode.
        return key != 0 ? key : EvdevFallback(xorgKeyCode - 8);
    }

    public static string? UnicodeText(uint unicode)
        => Rune.IsValid((int)unicode) && !Rune.IsControl(new Rune((int)unicode))
            ? char.ConvertFromUtf32((int)unicode) : null;

    public static uint BasicUnicode(ulong symbol)
        => symbol is >= 0x20 and <= 0x7E or >= 0xA0 and <= 0xFF ? (uint)symbol
            : symbol is >= 0x01000100 and <= 0x0110FFFF ? (uint)(symbol & 0x00FFFFFF) : 0;

    public static uint LookupModifierMask(byte serverModifiers, IEnumerable<int> physicalKeys)
    {
        // Local release synthesis prevents stuck modifiers in A's application, so XKB's
        // logical state can no longer be used alone while the physical key remains held.
        uint state = (uint)(serverModifiers & ~(1 | 4 | 8 | 128));
        foreach (int key in physicalKeys)
            state |= key switch { 0x10 or 0xA0 or 0xA1 => 1u, 0x11 or 0xA2 or 0xA3 => 4u,
                0xA4 => 8u, 0xA5 => 128u, _ => 0u };
        return state;
    }

    private static int EvdevFallback(int code)
    {
        if (code is >= 2 and <= 10) return '1' + code - 2;
        if (code is >= 16 and <= 25) return "QWERTYUIOP"[code - 16];
        if (code is >= 30 and <= 38) return "ASDFGHJKL"[code - 30];
        if (code is >= 44 and <= 50) return "ZXCVBNM"[code - 44];
        return code switch { 11 => '0', 86 => 0xE2, _ => 0 };
    }
}
