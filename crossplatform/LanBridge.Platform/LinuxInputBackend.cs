using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using SharpHook;
using SharpHook.Providers;
using LinuxMode = SharpHook.Data.LinuxMode;
using static LanBridge.CrossPlatform.LinuxX11Native;

namespace LanBridge.CrossPlatform;

/// <summary>
/// Native X11 input source. XI2 supplies one physical event stream before and during capture;
/// active Xlib grabs suppress delivery to local applications. SharpHook is only an injector.
/// Wayland/XWayland are deliberately rejected rather than silently monitoring only X11 windows.
/// </summary>
public sealed class LinuxInputBackend : IInputBackend
{
    private readonly SharpHookInputBackend _injector = new();
    private readonly object _commandGate = new(), _snapshotGate = new();
    private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Completion)> _commands = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<int> _syntheticDevices = [];
    private readonly Dictionary<int, (int Key, int Scan, bool Extended)> _downKeys = [];
    private readonly Channel<PhysicalInput> _physical = Channel.CreateBounded<PhysicalInput>(new BoundedChannelOptions(4096)
    {
        SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait,
    });
    private Task? _physicalWorker;
    private Thread? _thread;
    private nint _display;
    private nuint _root, _confineWindow, _blankCursor;
    private int _screen, _xiOpcode, _started, _disposed, _startAttempt;
    private bool _capture, _hasXkbCommon = true, _ownerRunning;
    private DesktopBounds _bounds = new(0, 0, 1, 1);
    private DesktopPoint _position;
    private BackendCapabilities? _failure;

    public LinuxInputBackend() => _injector.Error += message => Error?.Invoke(message);
    public string Name => "Linux X11 (XInput2 + XGrab / XTest)";
    public static bool IsWaylandSession(string? sessionType, string? waylandDisplay)
        => string.Equals(sessionType, "wayland", StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(waylandDisplay);
    private static bool Wayland => IsWaylandSession(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"),
        Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    public BackendCapabilities Capabilities => _failure ?? (Wayland
        ? new(false, false, false, "Wayland chưa được hỗ trợ. Cần InputCapture/RemoteDesktop portal + libei; " +
            "XWayland không cho phép điều khiển toàn bộ desktop. Đăng nhập phiên X11/Xorg để dùng bản này.")
        : new(true, true, true, "Cần phiên X11/Xorg 64-bit với XInput 2.1, XTEST, libX11 và libXi. " +
            "Không cần root hay quyền /dev/input. Gõ Unicode X11 dùng ánh xạ XKB tạm thời, có thể chậm tùy ứng dụng."));
    public DesktopBounds Bounds { get { lock (_snapshotGate) return _bounds; } }
    public DesktopPoint Position { get { lock (_snapshotGate) return _position; } }
    public event Func<PhysicalInput, bool>? Input;
    public event Action<string>? Error;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _startAttempt, 1, 0) != 0)
        {
            if (Volatile.Read(ref _started) != 0) return;
            throw new InvalidOperationException("Linux input backend đã dừng hoặc không khởi động được.");
        }
        if (!OperatingSystem.IsLinux() || IntPtr.Size != 8)
            throw new PlatformNotSupportedException("Backend Linux X11 cần Linux 64-bit.");
        if (Wayland) throw new PlatformNotSupportedException(Capabilities.Description);
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new PlatformNotSupportedException("Không có DISPLAY của phiên X11. Không dùng được trong phiên SSH/headless.");
        try
        {
            X11Threading.Initialize(); // Before the first display: input and screen-view workers use separate Xlib connections.
            _thread = new Thread(Run) { IsBackground = true, Name = "LanBridge X11 input" };
            _thread.Start();
            _ready.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            // Explicitly avoid SharpHook 8's /dev/input + /dev/uinput backend and its broad permissions.
            UioHookProvider.Instance.SetLinuxMode(LinuxMode.XRecord);
            _injector.StartInjectionOnly(Bounds);
            _physicalWorker = Task.Run(DeliverPhysicalAsync);
            Volatile.Write(ref _started, 1);
        }
        catch (Exception error)
        {
            _failure = new(false, false, false, "Không mở được đầu vào X11: " + error.Message);
            Dispose();
            throw;
        }
    }

    public void SetCapture(bool active)
    {
        if (Volatile.Read(ref _disposed) != 0) { if (!active) return; ObjectDisposedException.ThrowIf(true, this); }
        if (Volatile.Read(ref _started) == 0) { if (!active) return; throw new InvalidOperationException("X11 chưa sẵn sàng."); }
        OnOwner(() => SetCaptureCore(active));
    }

    public void Warp(DesktopPoint point)
    {
        if (Volatile.Read(ref _started) == 0 || Volatile.Read(ref _disposed) != 0) return;
        OnOwner(() =>
        {
            var bounded = new DesktopPoint(Math.Clamp(point.X, 0, _bounds.Width - 1),
                Math.Clamp(point.Y, 0, _bounds.Height - 1));
            if (_capture) XMoveWindow(_display, _confineWindow, bounded.X, bounded.Y);
            XWarpPointer(_display, 0, _root, 0, 0, 0, 0, bounded.X, bounded.Y);
            XFlush(_display);
            lock (_snapshotGate) _position = bounded;
        });
    }

    public void Inject(Packet packet)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Volatile.Read(ref _started) == 0) throw new InvalidOperationException("X11 chưa sẵn sàng.");
        _injector.Inject(packet);
    }
    public void ReleaseAll() => _injector.ReleaseAll();

    private void OnOwner(Action action)
    {
        if (Thread.CurrentThread == _thread) { action(); return; }
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_commandGate)
        {
            if (!_ownerRunning) throw new InvalidOperationException("Luồng đầu vào X11 đã dừng.");
            _commands.Enqueue((action, completed));
        }
        _wake.Set();
        // Only commands are synchronous. The owning event thread never waits for UI work.
        completed.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
    }

    private void Run()
    {
        try
        {
            InitializeX11();
            lock (_commandGate) _ownerRunning = true;
            _ready.TrySetResult();
            long refreshed = Stopwatch.GetTimestamp();
            while (Volatile.Read(ref _disposed) == 0)
            {
                DrainCommands();
                int count = 0;
                while (count++ < 128 && XPending(_display) != 0 && Volatile.Read(ref _disposed) == 0)
                {
                    XNextEvent(_display, out var input);
                    if (input.Type == 34)
                    {
                        XRefreshKeyboardMapping(ref input.Mapping);
                        continue;
                    }
                    if (input.Type != 35 || input.Cookie.Extension != _xiOpcode) continue;
                    var cookie = input.Cookie;
                    if (XGetEventData(_display, ref cookie) == 0) continue;
                    try
                    {
                        if (cookie.EventType == 11) RefreshSyntheticDevices();
                        else if (cookie.EventType is >= 13 and <= 17 && Volatile.Read(ref _started) != 0)
                            HandleRaw(Marshal.PtrToStructure<RawEvent>(cookie.Data));
                    }
                    finally { XFreeEventData(_display, ref cookie); }
                    DrainCommands();
                }
                if (Stopwatch.GetElapsedTime(refreshed).TotalSeconds >= 2)
                {
                    RefreshGeometry();
                    refreshed = Stopwatch.GetTimestamp();
                }
                if (count == 1) _wake.WaitOne(1);
            }
        }
        catch (Exception error)
        {
            _failure = new(false, false, false, "Đầu vào X11 đã dừng: " + error.Message);
            _ready.TrySetException(error);
            ReportError(_failure.Description);
        }
        finally
        {
            Volatile.Write(ref _started, 0);
            lock (_commandGate)
            {
                _ownerRunning = false;
                while (_commands.TryDequeue(out var command))
                    command.Completion.TrySetException(new InvalidOperationException("Luồng đầu vào X11 đã dừng."));
            }
            if (_display != 0)
            {
                SetCaptureCore(false);
                if (_blankCursor != 0) XFreeCursor(_display, _blankCursor);
                if (_confineWindow != 0) XDestroyWindow(_display, _confineWindow);
                XCloseDisplay(_display);
                _display = 0;
            }
            _physical.Writer.TryComplete();
        }
    }

    private void DrainCommands()
    {
        while (_commands.TryDequeue(out var command))
        {
            try { command.Action(); command.Completion.TrySetResult(); }
            catch (Exception error) { command.Completion.TrySetException(error); }
        }
    }

    private void InitializeX11()
    {
        _display = XOpenDisplay(0);
        if (_display == 0) throw new InvalidOperationException("Không mở được DISPLAY. Kiểm tra phiên X11 và Xauthority của người dùng.");
        _screen = XDefaultScreen(_display);
        _root = XRootWindow(_display, _screen);
        if (XQueryExtension(_display, "XInputExtension", out _xiOpcode, out _, out _) == 0)
            throw new PlatformNotSupportedException("X server không có XInput2.");
        int major = 2, minor = 1;
        if (XIQueryVersion(_display, ref major, ref minor) != 0 || major < 2 || major == 2 && minor < 1)
            throw new PlatformNotSupportedException("Cần XInput 2.1 trở lên để nhận input khi đang grab.");
        if (XQueryExtension(_display, "XTEST", out _, out _, out _) == 0)
            throw new PlatformNotSupportedException("X server không có XTEST để nhận điều khiển.");
        RefreshGeometry();
        RefreshSyntheticDevices();
        SelectRawEvents();
        var attributes = new WindowAttributes { OverrideRedirect = 1 };
        _confineWindow = XCreateWindow(_display, _root, _bounds.Width / 2, _bounds.Height / 2,
            1, 1, 0, 0, 2 /* InputOnly */, 0, 1u << 9, ref attributes);
        if (_confineWindow == 0) throw new InvalidOperationException("Không tạo được vùng giữ chuột X11.");
        nuint bitmap = XCreateBitmapFromData(_display, _root, [0], 1, 1);
        if (bitmap != 0)
        {
            var black = new Color();
            _blankCursor = XCreatePixmapCursor(_display, bitmap, bitmap, ref black, ref black, 0, 0);
            XFreePixmap(_display, bitmap);
        }
        XSync(_display, 0);
    }

    private unsafe void SelectRawEvents()
    {
        byte* raw = stackalloc byte[3] { 0, 0, 0 };
        for (int index = 13; index <= 17; index++) raw[index / 8] |= (byte)(1 << (index % 8));
        var mask = new EventMask { DeviceId = 1 /* XIAllMasterDevices */, MaskLength = 3, Mask = (nint)raw };
        if (XISelectEvents(_display, _root, ref mask, 1) != 0)
            throw new InvalidOperationException("Không đăng ký được XInput raw events.");
        byte* hierarchy = stackalloc byte[2] { 0, 0 };
        hierarchy[11 / 8] = (byte)(1 << (11 % 8));
        mask = new EventMask { DeviceId = 0 /* XIAllDevices */, MaskLength = 2, Mask = (nint)hierarchy };
        XISelectEvents(_display, _root, ref mask, 1);
        XFlush(_display);
    }

    private void RefreshGeometry()
    {
        var bounds = new DesktopBounds(0, 0, Math.Max(1, XDisplayWidth(_display, _screen)), Math.Max(1, XDisplayHeight(_display, _screen)));
        lock (_snapshotGate) _bounds = bounds;
        if (XQueryPointer(_display, _root, out _, out _, out int x, out int y, out _, out _, out _) != 0)
            lock (_snapshotGate) _position = new(x, y);
    }

    private void RefreshSyntheticDevices()
    {
        nint devices = XIQueryDevice(_display, 0, out int count);
        if (devices == 0) throw new InvalidOperationException("Không đọc được danh sách thiết bị XInput.");
        try
        {
            _syntheticDevices.Clear();
            for (int index = 0; index < count; index++)
            {
                var device = Marshal.PtrToStructure<DeviceInfo>(devices + index * Marshal.SizeOf<DeviceInfo>());
                string name = Marshal.PtrToStringUTF8(device.Name) ?? "";
                if (name.Contains("XTEST", StringComparison.OrdinalIgnoreCase)) _syntheticDevices.Add(device.DeviceId);
            }
        }
        finally { XIFreeDeviceInfo(devices); }
        if (_syntheticDevices.Count < 2)
            throw new PlatformNotSupportedException("Không xác định được thiết bị bàn phím/chuột XTEST để chống vòng lặp đầu vào.");
    }

    private void SetCaptureCore(bool active)
    {
        if (_capture == active) return;
        if (!active)
        {
            XUngrabPointer(_display, 0);
            XUngrabKeyboard(_display, 0);
            _capture = false;
            XUnmapWindow(_display, _confineWindow);
            XFlush(_display);
            return;
        }
        int x = Math.Clamp(_position.X, 0, _bounds.Width - 1), y = Math.Clamp(_position.Y, 0, _bounds.Height - 1);
        XMoveWindow(_display, _confineWindow, x, y);
        XMapWindow(_display, _confineWindow);
        XSync(_display, 0);
        int keyboard = XGrabKeyboard(_display, _root, 0, 1, 1, 0);
        if (keyboard != 0)
        {
            XUnmapWindow(_display, _confineWindow);
            XFlush(_display);
            throw new InvalidOperationException($"Không giữ được bàn phím X11 (mã {keyboard}); đóng menu hoặc ứng dụng đang giữ input rồi thử lại.");
        }
        int pointer = XGrabPointer(_display, _root, 0, (1u << 2) | (1u << 3), 1, 1, _confineWindow, _blankCursor, 0);
        if (pointer != 0)
        {
            XUngrabKeyboard(_display, 0);
            XUnmapWindow(_display, _confineWindow);
            XFlush(_display);
            throw new InvalidOperationException($"Không giữ được chuột X11 (mã {pointer}); đóng menu hoặc ứng dụng đang giữ input rồi thử lại.");
        }
        _capture = true;
        XFlush(_display);
    }

    private unsafe void HandleRaw(RawEvent input)
    {
        // XTest is also used by this backend's simulator. Never echo it back to the peer.
        if (input.SendEvent != 0 || _syntheticDevices.Contains(input.SourceId)) return;
        if (input.EventType == 17)
        {
            double dx = 0, dy = 0;
            int valueIndex = 0;
            if (input.Valuators.Mask == 0 || input.Valuators.Values == 0 || input.Valuators.MaskLength is < 0 or > 128) return;
            for (int axis = 0; axis < input.Valuators.MaskLength * 8; axis++)
            {
                if ((((byte*)input.Valuators.Mask)[axis / 8] & (1 << (axis % 8))) == 0) continue;
                double value = ((double*)input.Valuators.Values)[valueIndex++];
                if (axis == 0) dx = value;
                if (axis == 1) dy = value;
            }
            if (!double.IsFinite(dx) || !double.IsFinite(dy)) return;
            if (!_capture)
            {
                if (XQueryPointer(_display, _root, out _, out _, out int x, out int y, out _, out _, out _) == 0) return;
                lock (_snapshotGate) _position = new(x, y);
            }
            // Always include both forms. If capture changes while this event waits for the
            // ordered consumer, the router can use actual coordinates locally and deltas remotely.
            Dispatch(new(PhysicalInputKind.Move, X: _position.X, Y: _position.Y,
                Relative: true, DeltaX: dx, DeltaY: dy));
        }
        else if (input.EventType is 15 or 16)
        {
            bool down = input.EventType == 15;
            if (input.Detail is >= 4 and <= 7)
            {
                if (down) Dispatch(new(PhysicalInputKind.Wheel,
                    WheelX: input.Detail == 6 ? -120 : input.Detail == 7 ? 120 : 0,
                    WheelY: input.Detail == 4 ? 120 : input.Detail == 5 ? -120 : 0));
            }
            else
            {
                string? button = input.Detail switch { 1 => "left", 2 => "middle", 3 => "right", 8 => "x1", 9 => "x2", _ => null };
                if (button != null) Dispatch(new(PhysicalInputKind.Button, Button: button, Down: down));
            }
        }
        else if (input.EventType is 13 or 14 && input.Detail is >= 8 and <= 255)
        {
            XkbGetState(_display, 0x100 /* XkbUseCoreKbd */, out var state);
            ulong baseSymbol = XkbKeycodeToKeysym(_display, (byte)input.Detail, state.Group, 0);
            int key = LinuxKeyMap.ToWindowsVirtualKey(baseSymbol, input.Detail);
            var physical = CanonicalKeyMap.ToSharpHookKeyCode(key, extended: baseSymbol == 0xFF8D);
            var mapped = (Key: key, Scan: CanonicalKeyMap.ToWindowsScan(physical), Extended: CanonicalKeyMap.IsExtended(physical));
            if (_downKeys.TryGetValue(input.Detail, out var original)) mapped = original;
            if (input.EventType == 13) _downKeys[input.Detail] = mapped; else _downKeys.Remove(input.Detail);
            string? text = null;
            if (input.EventType == 13 && XkbLookupKeySym(_display, (byte)input.Detail,
                    LinuxKeyMap.LookupModifierMask(state.Mods, _downKeys.Values.Select(value => value.Key))
                    | (uint)state.Group << 13, out _, out nuint symbol) != 0)
            {
                uint unicode = LinuxKeyMap.BasicUnicode(symbol);
                if (_hasXkbCommon)
                {
                    try { unicode = xkb_keysym_to_utf32((uint)symbol); }
                    catch (DllNotFoundException) { _hasXkbCommon = false; }
                    catch (EntryPointNotFoundException) { _hasXkbCommon = false; }
                }
                text = LinuxKeyMap.UnicodeText(unicode);
            }
            if (mapped.Key == 0) return;
            Dispatch(new(PhysicalInputKind.Key, Key: mapped.Key, Scan: mapped.Scan,
                Extended: mapped.Extended, Down: input.EventType == 13, Text: text));
        }
    }

    private void Dispatch(PhysicalInput input)
    {
        // Linux X11's raw observer cannot suppress a first activation key. Once SetCapture(true)
        // succeeds, the server's active grabs suppress all local keyboard/button input.
        // The application recommends F9, not a letter, for this platform's activation shortcut.
        // Never call the router while owning Xlib. The router holds its session lock while
        // waiting for SetCapture/Warp, and the independent owner must be able to service them.
        if (!_physical.Writer.TryWrite(input))
            throw new InvalidOperationException("Hàng đợi đầu vào X11 quá tải; đã trả điều khiển về máy cục bộ.");
    }

    private async Task DeliverPhysicalAsync()
    {
        await foreach (var input in _physical.Reader.ReadAllAsync())
        {
            if (Volatile.Read(ref _disposed) != 0 || Volatile.Read(ref _started) == 0) break;
            try { Input?.Invoke(input); }
            catch (Exception error)
            {
                try { SetCapture(false); } catch { }
                ReportError("Lỗi xử lý đầu vào X11: " + error.Message);
            }
        }
    }

    private void ReportError(string message)
        => _ = Task.Run(() => { try { Error?.Invoke(message); } catch { } });

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _wake.Set();
        bool stopped = _thread == null || Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(5));
        _injector.Dispose();
        if (stopped) _wake.Dispose();
    }
}
