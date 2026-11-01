using LanBridge;
using LanBridge.CrossPlatform;
using SharpHook.Data;
using System.Diagnostics;
using System.Runtime.InteropServices;

int assertions = 0;
void Check(bool condition, string description)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(description);
}
void Map(KeyCode physical, int key, int scan, bool extended = false)
{
    Check(CanonicalKeyMap.ToWindowsVirtualKey(physical) == key, $"Canonical virtual key: {physical}");
    Check(CanonicalKeyMap.ToWindowsScan(physical) == scan, $"Windows set-1 scan: {physical}");
    Check(CanonicalKeyMap.IsExtended(physical) == extended, $"Extended flag: {physical}");
    Check(CanonicalKeyMap.ToSharpHookKeyCode(key, scan, extended) == physical, $"Round-trip physical key: {physical}");
}

// Screen geometry and native color masks are tested without opening a display or capturing pixels.
Check(DesktopScreenCapture.Fit(new(-1920, 0, 3840, 1080)) == (1280, 360), "Virtual desktop aspect ratio retained");
Check(DesktopScreenCapture.Fit(new(0, 0, 1080, 1920)) == (450, 800), "Portrait desktop fits phone image bounds");
Check(DesktopScreenCapture.Fit(new(0, 0, 800, 600)) == (800, 600), "Small desktop is not enlarged");
Check(DesktopScreenCapture.Component(0xf800, 0xf800) == 255, "RGB565 full red mask");
Check(DesktopScreenCapture.Component(0x07e0, 0x07e0) == 255, "RGB565 full green mask");
Check(DesktopScreenCapture.Component(0x001f, 0x001f) == 255, "RGB565 full blue mask");
Check(DesktopScreenCapture.Component(0x00800000, 0x00ff0000) == 128, "32-bit TrueColor red channel");
Check(DesktopScreenCapture.Component(0xffffffff, 0) == 0, "Missing X11 mask is harmless");
var inertCapture = DesktopScreenCapture.Create(() => throw new InvalidOperationException("Must remain inert"));
Check(inertCapture != null, "Constructing a capture provider does not inspect or request access to the desktop");
if (OperatingSystem.IsMacOS())
{
    int layoutStatus = MacScreenLayoutSmoke.Check();
    Check(layoutStatus == 0, $"ScreenCaptureKit helper preserves synthetic BGRA row order and negative monitor offsets without capturing a desktop (status {layoutStatus})");
}

int[] letterScans = [0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17, 0x24, 0x25, 0x26,
    0x32, 0x31, 0x18, 0x19, 0x10, 0x13, 0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C];
for (int i = 0; i < 26; i++) Map((KeyCode)((int)KeyCode.VcA + i), 0x41 + i, letterScans[i]);
for (int i = 0; i < 9; i++) Map((KeyCode)((int)KeyCode.Vc1 + i), 0x31 + i, 0x02 + i);
Map(KeyCode.Vc0, 0x30, 0x0B);
int[] functionScans = [0x3B, 0x3C, 0x3D, 0x3E, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44, 0x57, 0x58];
for (int i = 0; i < 24; i++) Map((KeyCode)((int)KeyCode.VcF1 + i), 0x70 + i, i < 12 ? functionScans[i] : 0);
Map(KeyCode.VcLeftShift, 0xA0, 0x2A); Map(KeyCode.VcRightShift, 0xA1, 0x36);
Map(KeyCode.VcLeftControl, 0xA2, 0x1D); Map(KeyCode.VcRightControl, 0xA3, 0x1D, true);
Map(KeyCode.VcLeftAlt, 0xA4, 0x38); Map(KeyCode.VcRightAlt, 0xA5, 0x38, true);
Map(KeyCode.VcLeftMeta, 0x5B, 0x5B, true); Map(KeyCode.VcRightMeta, 0x5C, 0x5C, true);
Map(KeyCode.VcContextMenu, 0x5D, 0x5D, true);
Map(KeyCode.VcEnter, 0x0D, 0x1C); Map(KeyCode.VcNumPadEnter, 0x0D, 0x1C, true);
Map(KeyCode.VcUp, 0x26, 0x48, true); Map(KeyCode.VcDown, 0x28, 0x50, true);
Map(KeyCode.VcLeft, 0x25, 0x4B, true); Map(KeyCode.VcRight, 0x27, 0x4D, true);
Map(KeyCode.VcDelete, 0x2E, 0x53, true); Map(KeyCode.VcInsert, 0x2D, 0x52, true);
Map(KeyCode.VcBackspace, 0x08, 0x0E); Map(KeyCode.VcEscape, 0x1B, 0x01); Map(KeyCode.VcTab, 0x09, 0x0F);
Map(KeyCode.VcSemicolon, 0xBA, 0x27); Map(KeyCode.VcEquals, 0xBB, 0x0D);
Map(KeyCode.VcComma, 0xBC, 0x33); Map(KeyCode.VcMinus, 0xBD, 0x0C); Map(KeyCode.VcPeriod, 0xBE, 0x34);
Map(KeyCode.VcSlash, 0xBF, 0x35); Map(KeyCode.VcBackQuote, 0xC0, 0x29);
Map(KeyCode.VcOpenBracket, 0xDB, 0x1A); Map(KeyCode.VcBackslash, 0xDC, 0x2B);
Map(KeyCode.VcCloseBracket, 0xDD, 0x1B); Map(KeyCode.VcQuote, 0xDE, 0x28); Map(KeyCode.VcMisc, 0xE2, 0x56);
Map(KeyCode.VcNumPadDivide, 0x6F, 0x35, true); Map(KeyCode.VcNumPadMultiply, 0x6A, 0x37);
Map(KeyCode.VcMediaPlay, 0xB3, 0); Map(KeyCode.VcVolumeMute, 0xAD, 0);
Check(CanonicalKeyMap.ToSharpHookKeyCode(0x10, 0x2A) == KeyCode.VcLeftShift, "Legacy generic left Shift");
Check(CanonicalKeyMap.ToSharpHookKeyCode(0x10, 0x36) == KeyCode.VcRightShift, "Legacy generic right Shift");
Check(CanonicalKeyMap.ToSharpHookKeyCode(0x11, 0x1D, true) == KeyCode.VcRightControl, "Legacy generic right Control");
Check(CanonicalKeyMap.ToSharpHookKeyCode(0x12, 0x38, true) == KeyCode.VcRightAlt, "Legacy generic right Alt");
Check(CanonicalKeyMap.ToWindowsVirtualKey(KeyCode.VcUndefined) == 0, "Undefined key never becomes a native keycode");
Check(CanonicalKeyMap.ToWindowsVirtualKey((KeyCode)65535) == 0, "Unknown physical key is rejected");
Check(CanonicalKeyMap.ToSharpHookKeyCode(-1) == KeyCode.VcUndefined, "Negative key rejected");
Check(CanonicalKeyMap.ToSharpHookKeyCode(256) == KeyCode.VcUndefined, "Outside wire virtual key range rejected");
Check(CanonicalKeyMap.ToSharpHookKeyCode(0) == KeyCode.VcUndefined, "Undefined wire key rejected");

// Injection-only X11 uses the owning backend's root bounds, independently of libuiohook's
// XRandR cache. Validate metadata without opening a display or installing an extra hook.
foreach (var bounds in new[] { new DesktopBounds(0, 0, 1280, 720), new(-1920, -1080, 3840, 2160),
    new(short.MinValue, short.MinValue, 65536, 65536) })
    Check(SharpHookInputBackend.ValidateInjectionBounds(bounds) == bounds, "Valid native screen metadata is retained");
foreach (var bounds in new[] { new DesktopBounds(0, 0, 0, 720), new(0, 0, 1280, 0), new(0, 0, -1, 720) })
{
    bool rejected = false;
    try { SharpHookInputBackend.ValidateInjectionBounds(bounds); }
    catch (ArgumentOutOfRangeException) { rejected = true; }
    Check(rejected, "Empty/negative screen metadata cannot initialize a simulator");
}
foreach (var bounds in new[] { new DesktopBounds(short.MinValue - 1, 0, 1, 1), new(0, short.MinValue - 1, 1, 1),
    new(0, 0, 32769, 1), new(0, 0, 1, 32769), new(int.MaxValue, 0, int.MaxValue, 1) })
{
    bool rejected = false;
    try { SharpHookInputBackend.ValidateInjectionBounds(bounds); }
    catch (NotSupportedException) { rejected = true; }
    Check(rejected, "Out-of-range screen metadata cannot overflow SharpHook coordinates");
}

var modifiers = new PhysicalModifierState();
Check(modifiers.ObserveKey(0x41, true, EventMask.CapsLock).HasFlag(EventMask.CapsLock), "Preexisting Caps Lock state must be read before capture");
modifiers.SetCapture(true);
modifiers.ObserveKey(0xA0, true, EventMask.CapsLock | EventMask.LeftShift);
Check(modifiers.ObserveKey(0x41, true, EventMask.None).HasFlag(EventMask.LeftShift), "Suppressed Shift down remains physically held when OS state omits it");
Check(modifiers.ObserveKey(0x41, true, EventMask.None).HasFlag(EventMask.CapsLock), "Capture freezes the preexisting native Caps Lock baseline");
modifiers.ObserveKey(0xA1, true, EventMask.None);
modifiers.ObserveKey(0xA0, false, EventMask.LeftShift);
var rightHeld = modifiers.ObserveKey(0x42, true, EventMask.LeftShift);
Check(!rightHeld.HasFlag(EventMask.LeftShift) && rightHeld.HasFlag(EventMask.RightShift), "Released left Shift cannot cancel held right Shift or inherit stale OS state");
modifiers.ObserveKey(0xA1, false, EventMask.RightShift);
Check((modifiers.ObserveKey(0x42, true, EventMask.LeftShift | EventMask.RightShift) & EventMask.Shift) == 0, "Physical release overrides stale native Shift state");
foreach (var pair in new[] { (0xA2, EventMask.LeftCtrl), (0xA3, EventMask.RightCtrl), (0xA4, EventMask.LeftAlt),
    (0xA5, EventMask.RightAlt), (0x5B, EventMask.LeftMeta), (0x5C, EventMask.RightMeta) })
{
    modifiers.ObserveKey(pair.Item1, true, EventMask.None);
    Check(modifiers.ObserveKey(0x41, true, EventMask.None).HasFlag(pair.Item2), "Suppressed shortcut modifier remains physically held");
    modifiers.ObserveKey(pair.Item1, false, pair.Item2);
    Check(!modifiers.ObserveKey(0x41, true, pair.Item2).HasFlag(pair.Item2), "Modifier up cannot retain stale OS state");
}
modifiers.SetCapture(false);
Check(!modifiers.ObserveKey(0x41, true, EventMask.None).HasFlag(EventMask.CapsLock), "Returning local resumes the actual native Caps Lock state");
modifiers.ObservePointer(EventMask.None); modifiers.SetCapture(true);
Check(!modifiers.ObserveKey(0x14, true, EventMask.CapsLock).HasFlag(EventMask.CapsLock), "Swallowed Caps Lock must not alter the router's baseline twice");
modifiers.SetCapture(false); modifiers.ObservePointer(EventMask.CapsLock); modifiers.SetCapture(true);
Check(modifiers.ObserveKey(0x41, true, EventMask.None).HasFlag(EventMask.CapsLock), "Edge entry uses latest pointer-event native lock baseline");

long Tick(int milliseconds) => (long)(milliseconds / 1000d * Stopwatch.Frequency);
void Click(MouseClickSequence sequence, MouseButton button, DesktopPoint point, int milliseconds, ushort expected)
{
    Check(sequence.Press(button, point, Tick(milliseconds)) == expected, "Quartz press must carry the inferred click count");
    Check(sequence.Release(button, point) == expected, "Quartz release must retain the matching press click count");
}
var clicks = new MouseClickSequence(TimeSpan.FromMilliseconds(500));
var location = new DesktopPoint(100, 100);
Click(clicks, MouseButton.Button1, location, 0, 1);
Click(clicks, MouseButton.Button1, location, 100, 2);
Click(clicks, MouseButton.Button1, location, 200, 3);
Click(clicks, MouseButton.Button1, location, 300, 3);
Click(clicks, MouseButton.Button1, location, 1000, 1);
Click(clicks, MouseButton.Button1, new(104, 104), 1100, 2);
Click(clicks, MouseButton.Button1, new(109, 104), 1200, 1);
Click(clicks, MouseButton.Button2, new(109, 104), 1300, 1);
Click(clicks, MouseButton.Button1, new(109, 104), 1400, 1);
clicks.Clear();
Click(clicks, MouseButton.Button1, location, 1450, 1);
Click(clicks, MouseButton.Button1, location, 1400, 1); // Backwards/faulty clocks never create extra clicks.
clicks.Clear();
Check(clicks.Press(MouseButton.Button1, location, Tick(0)) == 1, "Initial press count");
Check(clicks.Press(MouseButton.Button1, location, Tick(10)) == 1, "Repeated down without up is not a double click");
Check(clicks.Release(MouseButton.Button1, location) == 1, "Repeated down preserves release count");
Click(clicks, MouseButton.Button1, location, 100, 2);
clicks.Clear();
Check(clicks.Press(MouseButton.Button1, location, Tick(0)) == 1, "Drag starts with single count");
clicks.Move(new(150, 150)); clicks.Move(location);
Check(clicks.Release(MouseButton.Button1, location) == 1, "Drag release keeps its original count");
Click(clicks, MouseButton.Button1, location, 100, 1);
clicks.Move(new(200, 200)); clicks.Move(location);
Click(clicks, MouseButton.Button1, location, 200, 1);
Check(clicks.Release(MouseButton.Button5, location) == 1, "Unpaired cleanup up uses a safe single count");
var slowClicks = new MouseClickSequence(TimeSpan.FromMilliseconds(1000));
Click(slowClicks, MouseButton.Button1, location, 0, 1); Click(slowClicks, MouseButton.Button1, location, 800, 2);
var fastClicks = new MouseClickSequence(TimeSpan.FromMilliseconds(200));
Click(fastClicks, MouseButton.Button1, location, 0, 1); Click(fastClicks, MouseButton.Button1, location, 300, 1);
clicks.Clear();
Click(clicks, MouseButton.Button1, new(int.MinValue, int.MinValue), 0, 1);
Click(clicks, MouseButton.Button1, new(int.MaxValue, int.MaxValue), 100, 1);
bool invalidInterval = false, invalidDistance = false, invalidButton = false;
try { _ = new MouseClickSequence(TimeSpan.Zero); } catch (ArgumentOutOfRangeException) { invalidInterval = true; }
try { _ = new MouseClickSequence(TimeSpan.FromMilliseconds(500), -1); } catch (ArgumentOutOfRangeException) { invalidDistance = true; }
try { clicks.Press(MouseButton.NoButton, location, 0); } catch (ArgumentException) { invalidButton = true; }
Check(invalidInterval && invalidDistance && invalidButton, "Invalid native click configuration must be rejected without injection");

// Do not start a hook, access the user's clipboard, or inject any native input in smoke tests.
using (var backend = new SharpHookInputBackend())
{
    Check(backend.Name.Length > 0, "Inert backend exposes its name");
    Check(backend.Capabilities.Inject, "Desktop backend declares injection support");
    bool rejected = false;
    try { backend.Inject(new Packet { Type = "key", Key = 0x41, Down = true }); }
    catch (InvalidOperationException) { rejected = true; }
    Check(rejected, "Unstarted backend cannot touch native input");
    backend.ReleaseAll(); // No simulator, so this is inert too.
    backend.Dispose();
    bool disposed = false;
    try { backend.Start(); }
    catch (ObjectDisposedException) { disposed = true; }
    Check(disposed, "Disposed backend cannot install hooks");
}
Console.WriteLine($"Platform smoke passed: {assertions} assertions; no native hooks or input installed.");

static class MacScreenLayoutSmoke
{
    [DllImport("LanBridge.ScreenCapture", EntryPoint = "lanbridge_capture_layout_smoke")]
    internal static extern int Check();
}
