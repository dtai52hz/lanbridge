namespace LanBridge;

[Flags]
public enum ShortcutModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>A local Windows virtual-key shortcut. No connection or permission is implied by assigning it.</summary>
public sealed record SwitchShortcut(int Key, ShortcutModifiers Modifiers = ShortcutModifiers.None)
{
    private const ShortcutModifiers KnownModifiers = ShortcutModifiers.Control | ShortcutModifiers.Alt
        | ShortcutModifiers.Shift | ShortcutModifiers.Windows;

    public bool IsValid
    {
        get
        {
            if (Key is < 8 or > 254 || (Modifiers & ~KnownModifiers) != 0) return false;
            // A modifier must be held together with an ordinary key. The escape shortcut always remains available.
            if (Key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return false;
            bool controlAlt = (Modifiers & (ShortcutModifiers.Control | ShortcutModifiers.Alt))
                == (ShortcutModifiers.Control | ShortcutModifiers.Alt);
            if (controlAlt && Key is 0x7B or 0x2E) return false; // Ctrl+Alt+F12, Ctrl+Alt+Delete.
            if ((Modifiers & ShortcutModifiers.Windows) != 0 && Key == 0x4C) return false; // Win+L locks Windows.
            return true;
        }
    }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>(5);
            if ((Modifiers & ShortcutModifiers.Control) != 0) parts.Add("Ctrl");
            if ((Modifiers & ShortcutModifiers.Alt) != 0) parts.Add("Alt");
            if ((Modifiers & ShortcutModifiers.Shift) != 0) parts.Add("Shift");
            if ((Modifiers & ShortcutModifiers.Windows) != 0) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    public static bool TryCreate(int key, ShortcutModifiers modifiers, out SwitchShortcut? shortcut)
    {
        var candidate = new SwitchShortcut(key, modifiers);
        shortcut = candidate.IsValid ? candidate : null;
        return shortcut != null;
    }

    public override string ToString() => DisplayName;

    private static string KeyName(int key) => key switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)key).ToString(),
        >= 0x70 and <= 0x87 => $"F{key - 0x6F}",
        >= 0x60 and <= 0x69 => $"Num {key - 0x60}",
        0x08 => "Backspace",
        0x09 => "Tab",
        0x0C => "Clear",
        0x0D => "Enter",
        0x13 => "Pause",
        0x14 => "Caps Lock",
        0x1B => "Esc",
        0x20 => "Space",
        0x21 => "Page Up",
        0x22 => "Page Down",
        0x23 => "End",
        0x24 => "Home",
        0x25 => "←",
        0x26 => "↑",
        0x27 => "→",
        0x28 => "↓",
        0x2C => "Print Screen",
        0x2D => "Insert",
        0x2E => "Delete",
        0x5D => "Menu",
        0x6A => "Num *",
        0x6B => "Num +",
        0x6C => "Num Separator",
        0x6D => "Num -",
        0x6E => "Num .",
        0x6F => "Num /",
        0x90 => "Num Lock",
        0x91 => "Scroll Lock",
        0xAD => "Tắt tiếng",
        0xAE => "Giảm âm lượng",
        0xAF => "Tăng âm lượng",
        0xB0 => "Bài tiếp theo",
        0xB1 => "Bài trước",
        0xB2 => "Dừng phát",
        0xB3 => "Phát / tạm dừng",
        // OEM names identify virtual-key positions; their printed symbols depend on the user's keyboard layout.
        >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2 => $"OEM {key:X2}",
        _ => $"VK {key:X2}",
    };
}
