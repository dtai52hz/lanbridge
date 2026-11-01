using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using LanBridge;
using LanBridge.CrossPlatform;
using SkiaSharp;

// This program deliberately installs native capture and emits input. It must never run on the
// user's desktop. The workflow creates a dedicated Xvfb :99 with no external client or user input.
if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("LANBRIDGE_ISOLATED_NATIVE_TEST") != "1"
    || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
    || Environment.GetEnvironmentVariable("DISPLAY") is not (":99" or ":99.0")
    || LinuxInputBackend.IsWaylandSession(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
{
    Console.Error.WriteLine("Refusing native input test: requires GitHub Actions, explicit isolated-test flag and dedicated Xvfb DISPLAY=:99.");
    return 2;
}

int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
void Wait(Func<bool> condition, string message)
{
    var deadline = DateTime.UtcNow.AddSeconds(8);
    while (!condition() && DateTime.UtcNow < deadline) Thread.Sleep(5);
    Check(condition(), message);
}

Native.XInitThreads(); // First native operation, before any Xlib connection or simulator exists.
using var window = new IsolatedWindow();
using var backend = new LinuxInputBackend();
var errors = new ConcurrentQueue<string>();
int physical = 0;
backend.Error += errors.Enqueue;
backend.Input += _ => { Interlocked.Increment(ref physical); return false; };
backend.Start();
Check(backend.Capabilities.Capture && backend.Capabilities.Inject, "X11 backend exposes native capture and injection after startup");
Check(backend.Bounds.Width > 1 && backend.Bounds.Height > 1, "X11 desktop bounds are read");

// Capture only this workflow's synthetic Xvfb screen, which the guard above isolates from user desktops.
var screen = DesktopScreenCapture.Create(() => backend.Bounds).CaptureAsync(CancellationToken.None).GetAwaiter().GetResult();
Check(screen.Width == 1280 && screen.Height == 720 && screen.Jpeg.Length <= 512 * 1024,
    "Native X11 screenshot has bounded dimensions and JPEG size");
using (var decoded = SKBitmap.Decode(screen.Jpeg))
{
    Check(decoded != null && decoded.Width == 1280 && decoded.Height == 720, "Native X11 JPEG decodes");
    var windowPixel = decoded!.GetPixel(400, 300);
    Check(windowPixel.Red > 230 && windowPixel.Green < 25 && windowPixel.Blue < 25,
        "XImage byte order, native masks and JPEG encoding preserve synthetic red window pixels");
    var desktopPixel = decoded.GetPixel(1200, 710);
    Check(desktopPixel.Red < 25 && desktopPixel.Green < 25 && desktopPixel.Blue < 25,
        "Screenshot matches the full input desktop rather than just the test window");
}

backend.Inject(new() { Type = "enter", X = 32768, Y = 32768 });
Wait(() => window.MotionCount > 0, "Normalized receiver movement reaches the isolated window");
backend.Inject(new() { Type = "key", Key = 0x41, Scan = 0x1E, Down = true });
backend.Inject(new() { Type = "key", Key = 0x41, Scan = 0x1E, Down = false });
Wait(() => window.Text == "a", "Canonical raw A key reaches X11 as a");
backend.Inject(new() { Type = "text", Dx = 1, Text = "" });
Wait(() => window.Text == "", "Text replacement backspace removes the prior character");
const string expected = "tiếng Việt . , ? !";
backend.Inject(new() { Type = "text", Text = expected });
Wait(() => window.Text == expected, "Vietnamese Unicode and punctuation reach the X11 receiver; actual: " + window.Text);

backend.Inject(new() { Type = "button", Button = "left", X = 32768, Y = 32768, Down = true });
backend.Inject(new() { Type = "button", Button = "left", X = 32768, Y = 32768, Down = false });
Wait(() => window.ButtonPresses.Count(button => button == 1) == 1 && window.ButtonReleases.Contains(1), "Receiver left click reaches X11");
backend.Inject(new() { Type = "wheel", X = 32768, Y = 32768, Dy = 120 });
Wait(() => window.ButtonPresses.Count(button => button == 4) >= 1, "Receiver wheel reaches X11");
Thread.Sleep(100);
Check(window.ButtonPresses.Count(button => button == 4) == 1, "One Windows wheel notch becomes one X11 wheel step");
backend.Inject(new() { Type = "wheel", X = 32768, Y = 32768, Dx = 120 });
Wait(() => window.ButtonPresses.Count(button => button == 7) >= 1, "Positive horizontal wheel moves right on X11");
Thread.Sleep(100);
Check(window.ButtonPresses.Count(button => button == 7) == 1, "One horizontal wheel notch becomes one X11 wheel step");
Check(Volatile.Read(ref physical) == 0, "XTEST-generated input is excluded from the physical event source");

backend.SetCapture(true);
Check(window.TryGrabKeyboard() != 0, "Active backend exclusively owns the keyboard grab");
Check(window.TryGrabPointer() != 0, "Active backend exclusively owns the pointer grab");
backend.SetCapture(false);
Check(window.TryGrabKeyboard() == 0, "Releasing backend capture restores keyboard ownership to other clients");
window.UngrabKeyboard();
Check(window.TryGrabPointer() == 0, "Releasing backend capture restores pointer ownership to other clients");
window.UngrabPointer();

// Force failure in the second half of capture acquisition. The successful keyboard grab must
// be rolled back even though a different client already owns the pointer.
Check(window.TryGrabPointer() == 0, "Test window owns pointer before rollback check");
bool rejected = false;
try { backend.SetCapture(true); }
catch (InvalidOperationException) { rejected = true; }
Check(rejected, "Capture refuses a pointer owned by another client");
Check(window.TryGrabKeyboard() == 0, "Failed pointer grab does not leak the backend's keyboard grab");
window.UngrabKeyboard();
window.UngrabPointer();

backend.Inject(new() { Type = "key", Key = 0xA2, Scan = 0x1D, Down = true });
Wait(() => window.KeyPressSymbols.Contains(0xFFE3UL), "Held left Control reaches X11 before release check");
int releases = window.KeyReleaseSymbols.Count(symbol => symbol == 0xFFE3);
backend.ReleaseAll();
Wait(() => window.KeyReleaseSymbols.Count(symbol => symbol == 0xFFE3) > releases, "ReleaseAll releases an injected held modifier");
backend.SetCapture(true);
backend.Dispose();
Check(window.TryGrabKeyboard() == 0, "Disposing active backend releases keyboard capture");
window.UngrabKeyboard();
Check(window.TryGrabPointer() == 0, "Disposing active backend releases pointer capture");
window.UngrabPointer();
Check(errors.IsEmpty, "No native backend error: " + string.Join("; ", errors));
Console.WriteLine($"Isolated Xvfb native smoke passed: {assertions} assertions. Injection, Unicode, XTEST filtering and grab cleanup tested; physical-device capture is not simulated.");
return 0;

sealed class IsolatedWindow : IDisposable
{
    private readonly ConcurrentQueue<Action> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;
    private readonly StringBuilder _text = new();
    private readonly object _textGate = new();
    private nint _display;
    private nuint _window;
    private volatile bool _disposed;
    private int _motion;
    public readonly ConcurrentQueue<int> ButtonPresses = new(), ButtonReleases = new(), KeyReleases = new();
    public readonly ConcurrentQueue<ulong> KeyPressSymbols = new(), KeyReleaseSymbols = new();
    public int MotionCount => Volatile.Read(ref _motion);
    public string Text { get { lock (_textGate) return _text.ToString(); } }

    public IsolatedWindow()
    {
        _thread = new Thread(Run) { Name = "Isolated Xvfb test window", IsBackground = true };
        _thread.Start();
        _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
    }
    public int TryGrabKeyboard() => Invoke(() => Native.XGrabKeyboard(_display, _window, 0, 1, 1, 0));
    public int TryGrabPointer() => Invoke(() => Native.XGrabPointer(_display, _window, 0, 0, 1, 1, 0, 0, 0));
    public void UngrabKeyboard() => Invoke(() => { Native.XUngrabKeyboard(_display, 0); return Native.XSync(_display, 0); });
    public void UngrabPointer() => Invoke(() => { Native.XUngrabPointer(_display, 0); return Native.XSync(_display, 0); });
    private T Invoke<T>(Func<T> callback)
    {
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue(() => { try { result.SetResult(callback()); } catch (Exception e) { result.SetException(e); } });
        _wake.Set();
        return result.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }
    private void Run()
    {
        try
        {
            _display = Native.XOpenDisplay(0);
            if (_display == 0) throw new InvalidOperationException("Could not open isolated Xvfb display.");
            nuint root = Native.XDefaultRootWindow(_display);
            _window = Native.XCreateSimpleWindow(_display, root, 0, 0, 1000, 700, 0, 0, 0x00ff0000);
            Native.XSelectInput(_display, _window, (nint)((1 << 0) | (1 << 1) | (1 << 2) | (1 << 3) | (1 << 6)));
            Native.XMapWindow(_display, _window);
            Native.XSetInputFocus(_display, _window, 0, 0);
            Native.XSync(_display, 0);
            _ready.SetResult();
            while (!_disposed)
            {
                while (_commands.TryDequeue(out var command)) command();
                while (Native.XPending(_display) > 0)
                {
                    Native.XNextEvent(_display, out var input);
                    if (input.Type == 34) Native.XRefreshKeyboardMapping(ref input.Mapping);
                    else if (input.Type == 2)
                    {
                        ulong symbol = Native.XLookupKeysym(ref input.Key, 0);
                        KeyPressSymbols.Enqueue(symbol);
                        lock (_textGate)
                        {
                            if (symbol == 0xFF08) { if (_text.Length > 0) _text.Length--; }
                            else
                            {
                                string? text = LinuxKeyMap.UnicodeText(Native.xkb_keysym_to_utf32((uint)symbol));
                                if (text != null) _text.Append(text);
                            }
                        }
                    }
                    else if (input.Type == 3)
                    {
                        KeyReleases.Enqueue((int)input.Key.KeyCode);
                        KeyReleaseSymbols.Enqueue(Native.XLookupKeysym(ref input.Key, 0));
                    }
                    else if (input.Type == 4) ButtonPresses.Enqueue((int)input.Key.KeyCode);
                    else if (input.Type == 5) ButtonReleases.Enqueue((int)input.Key.KeyCode);
                    else if (input.Type == 6) Interlocked.Increment(ref _motion);
                }
                _wake.WaitOne(1);
            }
        }
        catch (Exception error) { _ready.TrySetException(error); Console.Error.WriteLine(error); }
        finally
        {
            if (_display != 0)
            {
                Native.XUngrabPointer(_display, 0); Native.XUngrabKeyboard(_display, 0);
                if (_window != 0) Native.XDestroyWindow(_display, _window);
                Native.XCloseDisplay(_display);
            }
        }
    }
    public void Dispose()
    {
        _disposed = true;
        _wake.Set();
        if (_thread.Join(TimeSpan.FromSeconds(5))) _wake.Dispose();
    }
}

static class Native
{
    private const string X11 = "libX11.so.6";
    [StructLayout(LayoutKind.Sequential)] internal struct KeyEvent
    {
        public int Type; public nuint Serial; public int SendEvent; public nint Display;
        public nuint Window, Root, Subwindow, Time; public int X, Y, RootX, RootY;
        public uint State, KeyCode; public int SameScreen;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct MappingEvent
    {
        public int Type; public nuint Serial; public int SendEvent; public nint Display;
        public nuint Window; public int Request, FirstKeyCode, Count;
    }
    [StructLayout(LayoutKind.Explicit, Size = 192)] internal struct Event
    {
        [FieldOffset(0)] public int Type;
        [FieldOffset(0)] public KeyEvent Key;
        [FieldOffset(0)] public MappingEvent Mapping;
    }
    [DllImport(X11)] internal static extern int XInitThreads();
    [DllImport(X11)] internal static extern nint XOpenDisplay(nint name);
    [DllImport(X11)] internal static extern int XCloseDisplay(nint display);
    [DllImport(X11)] internal static extern nuint XDefaultRootWindow(nint display);
    [DllImport(X11)] internal static extern nuint XCreateSimpleWindow(nint display, nuint parent, int x, int y,
        uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport(X11)] internal static extern int XSelectInput(nint display, nuint window, nint mask);
    [DllImport(X11)] internal static extern int XMapWindow(nint display, nuint window);
    [DllImport(X11)] internal static extern int XDestroyWindow(nint display, nuint window);
    [DllImport(X11)] internal static extern int XSetInputFocus(nint display, nuint focus, int revert, nuint time);
    [DllImport(X11)] internal static extern int XPending(nint display);
    [DllImport(X11)] internal static extern int XNextEvent(nint display, out Event input);
    [DllImport(X11)] internal static extern int XSync(nint display, int discard);
    [DllImport(X11)] internal static extern nuint XLookupKeysym(ref KeyEvent input, int index);
    [DllImport(X11)] internal static extern int XRefreshKeyboardMapping(ref MappingEvent input);
    [DllImport(X11)] internal static extern int XGrabKeyboard(nint display, nuint window, int ownerEvents, int pointerMode, int keyboardMode, nuint time);
    [DllImport(X11)] internal static extern int XUngrabKeyboard(nint display, nuint time);
    [DllImport(X11)] internal static extern int XGrabPointer(nint display, nuint window, int ownerEvents, uint mask,
        int pointerMode, int keyboardMode, nuint confine, nuint cursor, nuint time);
    [DllImport(X11)] internal static extern int XUngrabPointer(nint display, nuint time);
    [DllImport("libxkbcommon.so.0")] internal static extern uint xkb_keysym_to_utf32(uint symbol);
}
