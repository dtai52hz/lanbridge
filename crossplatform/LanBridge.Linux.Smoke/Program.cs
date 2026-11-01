using System.Runtime.InteropServices;
using LanBridge.CrossPlatform;

int assertions = 0;
void Check(bool value, string message)
{
    assertions++;
    if (!value) throw new InvalidOperationException(message);
}
for (int index = 0; index < 26; index++)
{
    Check(LinuxKeyMap.ToWindowsVirtualKey((ulong)('a' + index)) == 'A' + index, "Lowercase X11 letter becomes canonical VK");
    Check(LinuxKeyMap.ToWindowsVirtualKey((ulong)('A' + index)) == 'A' + index, "Uppercase X11 letter becomes canonical VK");
}
for (int index = 0; index < 24; index++)
    Check(LinuxKeyMap.ToWindowsVirtualKey((ulong)(0xFFBE + index)) == 0x70 + index, "F1–F24 mapping");
for (int index = 0; index < 10; index++)
{
    Check(LinuxKeyMap.ToWindowsVirtualKey((ulong)('0' + index)) == '0' + index, "Number row mapping");
    Check(LinuxKeyMap.ToWindowsVirtualKey((ulong)(0xFFB0 + index)) == 0x60 + index, "Numeric keypad mapping");
}
(ulong Symbol, int Key)[] special = [
    (0xFFE1, 0xA0), (0xFFE2, 0xA1), (0xFFE3, 0xA2), (0xFFE4, 0xA3),
    (0xFFE9, 0xA4), (0xFFEA, 0xA5), (0xFE03, 0xA5), (0xFFEB, 0x5B), (0xFFEC, 0x5C),
    (0xFF08, 8), (0xFF09, 9), (0xFF0D, 13), (0xFF8D, 13), (0xFF1B, 27),
    (0xFF50, 0x24), (0xFF51, 0x25), (0xFF52, 0x26), (0xFF53, 0x27), (0xFF54, 0x28),
    (0xFF55, 0x21), (0xFF56, 0x22), (0xFF57, 0x23), (0xFF63, 0x2D), (0xFFFF, 0x2E),
    (0xFF95, 0x67), (0xFF96, 0x64), (0xFF97, 0x68), (0xFF98, 0x66), (0xFF99, 0x62),
    (0xFF9A, 0x69), (0xFF9B, 0x63), (0xFF9C, 0x61), (0xFF9D, 0x65), (0xFF9E, 0x60), (0xFF9F, 0x6E),
    (';', 0xBA), ('=', 0xBB), (',', 0xBC), ('-', 0xBD), ('.', 0xBE), ('/', 0xBF),
    ('`', 0xC0), ('[', 0xDB), ('\\', 0xDC), (']', 0xDD), ('\'', 0xDE),
    (0x1008FF11, 0xAE), (0x1008FF12, 0xAD), (0x1008FF13, 0xAF), (0x1008FF14, 0xB3),
];
foreach (var item in special)
    Check(LinuxKeyMap.ToWindowsVirtualKey(item.Symbol) == item.Key, $"X11 special symbol {item.Symbol:X}");
Check(LinuxKeyMap.ToWindowsVirtualKey(0x06C6, 38) == 'A', "Non-Latin group retains Xorg evdev physical A fallback");
Check(LinuxKeyMap.ToWindowsVirtualKey(0, 94) == 0xE2, "ISO extra key fallback");
Check(LinuxKeyMap.ToWindowsVirtualKey(0xDEADBEEF) == 0, "Unknown symbol is not a native scan code");
Check(LinuxKeyMap.BasicUnicode('a') == 'a', "Latin Unicode symbol");
Check(LinuxKeyMap.BasicUnicode(0xE1) == 0xE1, "Latin-1 Unicode symbol");
Check(LinuxKeyMap.BasicUnicode(0x01001EC7) == 0x1EC7, "Vietnamese Unicode symbol");
Check(LinuxKeyMap.BasicUnicode(0x0101F600) == 0x1F600, "Supplementary Unicode symbol");
Check(LinuxKeyMap.BasicUnicode(0xFF0D) == 0, "X11 special Enter is never Unicode U+FF0D");
Check(LinuxKeyMap.UnicodeText(0x1EC7) == "ệ", "Vietnamese text");
Check(LinuxKeyMap.UnicodeText(0x1F600) == "😀", "Supplementary text");
foreach (uint invalid in new uint[] { 0, 9, 13, 0x1F, 0x7F, 0x80, 0x9F, 0xD800, 0xDFFF, 0x110000, uint.MaxValue })
    Check(LinuxKeyMap.UnicodeText(invalid) == null, "Controls and invalid scalars never become printable text");
Check(LinuxKeyMap.LookupModifierMask(0, [0xA0]) == 1, "Physical Shift survives local synthetic release");
Check(LinuxKeyMap.LookupModifierMask(0, [0xA3]) == 4, "Physical right Control survives local release");
Check(LinuxKeyMap.LookupModifierMask(0, [0xA4]) == 8, "Physical Alt mask");
Check(LinuxKeyMap.LookupModifierMask(0, [0xA5]) == 128, "Physical AltGr mask");
Check(LinuxKeyMap.LookupModifierMask(0xFF, []) == 0x72, "Server key-down modifiers cannot impersonate held physical modifiers");
Check(LinuxKeyMap.LookupModifierMask(0x12, [0xA0, 0xA2]) == 0x17, "CapsLock/NumLock state is retained");
Check(LinuxInputBackend.IsWaylandSession("wayland", null), "Wayland rejects even without display hint");
Check(LinuxInputBackend.IsWaylandSession("WAYLAND", null), "Wayland session matching ignores case");
Check(LinuxInputBackend.IsWaylandSession("x11", "wayland-0"), "XWayland with DISPLAY is not accepted as genuine Xorg");
Check(!LinuxInputBackend.IsWaylandSession("x11", null), "Genuine X11 session accepted");
Check(!LinuxInputBackend.IsWaylandSession(null, ""), "Missing Wayland hints alone are not proof of Wayland");

// ABI checks are pure marshaling metadata. No native library is loaded or called.
Type Native(string name) => typeof(LinuxInputBackend).Assembly.GetType("LanBridge.CrossPlatform.LinuxX11Native+" + name)!;
if (IntPtr.Size == 8)
{
    Check(Marshal.SizeOf(Native("Event")) == 192, "64-bit XEvent union size");
    Check(Marshal.SizeOf(Native("GenericCookie")) == 56, "64-bit XGenericEventCookie size");
    Check(Marshal.SizeOf(Native("MappingEvent")) == 56, "64-bit XMappingEvent size");
    Check(Marshal.OffsetOf(Native("GenericCookie"), "Data").ToInt32() == 48, "Cookie data pointer offset");
    Check(Marshal.SizeOf(Native("RawEvent")) == 96, "64-bit XIRawEvent size");
    Check(Marshal.OffsetOf(Native("RawEvent"), "SourceId").ToInt32() == 52, "XI2 source device offset");
    Check(Marshal.OffsetOf(Native("RawEvent"), "Valuators").ToInt32() == 64, "XI2 valuator offset");
    Check(Marshal.OffsetOf(Native("RawEvent"), "RawValues").ToInt32() == 88, "XI2 raw values offset");
    Check(Marshal.SizeOf(Native("DeviceInfo")) == 40, "64-bit XIDeviceInfo size");
    Check(Marshal.SizeOf(Native("WindowAttributes")) == 112, "64-bit XSetWindowAttributes size");
    Check(Marshal.SizeOf(Native("KeyboardState")) == 18, "XkbStateRec size");
}
using (var backend = new LinuxInputBackend())
{
    Check(backend.Name.Contains("X11"), "Constructing backend is inert");
    Check(backend.Bounds.Width == 1 && backend.Bounds.Height == 1, "Unstarted bounds do not read Xlib");
    backend.SetCapture(false);
    backend.Warp(new(1, 1));
    backend.ReleaseAll();
    bool rejected = false;
    try { backend.Inject(new LanBridge.Packet { Type = "key", Key = 0x41, Down = true }); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Unstarted Linux backend cannot inject native input");
}
Console.WriteLine($"Linux platform smoke passed: {assertions} assertions; no X11/Wayland connection or native input used.");
