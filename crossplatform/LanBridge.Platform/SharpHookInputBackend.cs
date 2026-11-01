using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Text;
using SharpHook;
using SharpHook.Data;
using SharpHook.Providers;
using SharpHook.Simulation;

namespace LanBridge.CrossPlatform;

/// <summary>
/// Windows/macOS synchronous hooks and native event simulation. Construction is inert: only
/// Start installs hooks, and the session owner must call it after the peer has been approved.
/// Linux uses this backend for observation/injection inside its X11 capture implementation.
/// </summary>
public sealed class SharpHookInputBackend : IInputBackend
{
    private readonly object _injectionGate = new();
    private readonly HashSet<KeyCode> _injectedKeys = [];
    private readonly HashSet<MouseButton> _injectedButtons = [];
    private SimpleGlobalHook? _hook;
    private EventSimulator? _simulator;
    private Task? _hookTask;
    private volatile bool _started, _disposed, _capturing;
    private DesktopBounds _bounds = new(0, 0, 1920, 1080);
    private int _lastX, _lastY;
    private double _wheelX, _wheelY;
    private readonly PhysicalModifierState _modifiers = new();
    private MouseClickSequence _clicks = new(TimeSpan.FromMilliseconds(500));

    public string Name => OperatingSystem.IsMacOS() ? "macOS · Quartz" : OperatingSystem.IsWindows() ? "Windows · native" : "Linux · XRecord";
    public BackendCapabilities Capabilities => new(
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS(),
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux(),
        !OperatingSystem.IsLinux() || string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")),
        OperatingSystem.IsMacOS()
            ? "Chuột/phím và Unicode; cần cấp quyền Accessibility. Một số ứng dụng có thể từ chối gõ Unicode tổng hợp."
            : OperatingSystem.IsWindows()
                ? "Chuột/phím và Unicode; ứng dụng có quyền quản trị có thể từ chối đầu vào từ tiến trình thường."
                : "X11: quan sát và mô phỏng đầu vào; cần bộ bắt X11 để giữ đầu vào tại máy điều khiển.");

    public DesktopBounds Bounds => _started ? _bounds : ReadBounds();
    public DesktopPoint Position => ReadPosition();
    public event Func<PhysicalInput, bool>? Input;
    public event Action<string>? Error;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hook is not null) return;
        StartInjectionOnly();
        var hook = new SimpleGlobalHook(); // EventLoopGlobalHook would silently defeat suppression.
        hook.KeyPressed += (_, e) => OnKey(e, true);
        hook.KeyReleased += (_, e) => OnKey(e, false);
        hook.MouseMoved += (_, e) => OnMove(e, false);
        hook.MouseDragged += (_, e) => OnMove(e, false);
        hook.MouseMovedRelative += (_, e) => OnMove(e, true);
        hook.MouseDraggedRelative += (_, e) => OnMove(e, true);
        hook.MousePressed += (_, e) => OnButton(e, true);
        hook.MouseReleased += (_, e) => OnButton(e, false);
        hook.MouseClicked += (_, e) => { if (!e.IsEventSimulated && _capturing) e.SuppressEvent = true; };
        hook.MouseWheel += (_, e) => OnWheel(e);
        try
        {
            _hook = hook;
            _hookTask = hook.RunAsync(useBackgroundThread: true);
            _ = ObserveHookAsync(_hookTask);
        }
        catch
        {
            _hook = null;
            hook.Dispose();
            throw;
        }
    }

    /// <summary>
    /// For an approved session whose platform supplies its own capture backend. Installs no global hook.
    /// X11 supplies its native root bounds because libuiohook's screen cache may be empty until XRandR changes.
    /// </summary>
    public void StartInjectionOnly(DesktopBounds? nativeBounds = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) return;
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Nền tảng này chưa có bộ điều khiển chuột/phím.");
        if (OperatingSystem.IsMacOS())
        {
            var provider = UioHookProvider.Instance;
            provider.PromptUserIfAxApiDisabled = false;
            if (!provider.IsAxApiEnabled(false))
                throw new InvalidOperationException("macOS chưa cấp quyền Accessibility. Mở System Settings → Privacy & Security → Accessibility, cho phép LanBridge rồi kết nối lại. Không có quyền nào được tự cấp.");
            int multiClickTime = provider.GetMultiClickTime();
            _clicks = new(TimeSpan.FromMilliseconds(multiClickTime is > 0 and <= 60000 ? multiClickTime : 500));
        }

        _bounds = nativeBounds is { } supplied ? ValidateInjectionBounds(supplied) : ReadBounds();
        var position = ReadPosition();
        _lastX = position.X; _lastY = position.Y;
        _simulator = EventSimulator.Create("LanBridge");
        _started = true;
    }

    private async Task ObserveHookAsync(Task task)
    {
        try { await task.ConfigureAwait(false); }
        catch (Exception error)
        {
            _capturing = false;
            if (!_disposed) ReportError("Không thể chạy bộ bắt chuột/phím: " + error.Message);
        }
    }

    public void SetCapture(bool active)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (active && !_started) throw new InvalidOperationException("Bộ đầu vào chưa được khởi động.");
        if (active && !Capabilities.Capture)
            throw new PlatformNotSupportedException("Nền tảng này cần bộ bắt đầu vào riêng; không thể chặn sự kiện bằng SharpHook.");
        _modifiers.SetCapture(active);
        _capturing = active;
    }

    public void Warp(DesktopPoint point)
    {
        var simulator = RequireSimulator();
        Check(simulator.SimulateMouseMovement(ToNativeCoordinate(point.X), ToNativeCoordinate(point.Y)));
        _lastX = point.X; _lastY = point.Y;
    }

    private void OnKey(KeyboardHookEventArgs args, bool down)
    {
        if (args.IsEventSimulated || _disposed) return;
        int key = CanonicalKeyMap.ToWindowsVirtualKey(args.Data.KeyCode);
        if (key == 0) { args.SuppressEvent = _capturing; return; }
        EventMask effectiveMask = _modifiers.ObserveKey(key, down, args.RawEvent.Mask);
        try
        {
            string? text = down ? NativeTextTranslator.Translate(args, key, effectiveMask) : null;
            Emit(args, new(PhysicalInputKind.Key, Key: key, Down: down, Text: text,
                Scan: CanonicalKeyMap.ToWindowsScan(args.Data.KeyCode), Extended: CanonicalKeyMap.IsExtended(args.Data.KeyCode)));
        }
        catch (Exception error)
        {
            _capturing = false;
            args.SuppressEvent = false;
            ReportError("Không đọc được bố cục bàn phím: " + error.Message);
        }
    }

    private void OnMove(MouseHookEventArgs args, bool relative)
    {
        if (args.IsEventSimulated || _disposed) return;
        _modifiers.ObservePointer(args.RawEvent.Mask);
        if (!relative) { _lastX = args.Data.X; _lastY = args.Data.Y; }
        Emit(args, new(PhysicalInputKind.Move, args.Data.X, args.Data.Y,
            Relative: relative, DeltaX: relative ? args.Data.X : 0, DeltaY: relative ? args.Data.Y : 0));
    }

    private void OnButton(MouseHookEventArgs args, bool down)
    {
        if (args.IsEventSimulated || _disposed) return;
        string? button = args.Data.Button switch
        {
            MouseButton.Button1 => "left", MouseButton.Button2 => "right", MouseButton.Button3 => "middle",
            MouseButton.Button4 => "x1", MouseButton.Button5 => "x2", _ => null,
        };
        if (button is null) { args.SuppressEvent = _capturing; return; }
        Emit(args, new(PhysicalInputKind.Button, args.Data.X, args.Data.Y, Button: button, Down: down));
    }

    private void OnWheel(MouseWheelHookEventArgs args)
    {
        if (args.IsEventSimulated || _disposed) return;
        // Wire deltas use Windows units (120/line); libuiohook horizontal rotation is positive left.
        int amount = args.Data.Rotation;
        if (OperatingSystem.IsMacOS()) amount = (int)Math.Round(amount * 120.0 / Math.Max(1, (int)args.Data.Delta));
        bool horizontal = args.Data.Direction == MouseWheelScrollDirection.Horizontal;
        Emit(args, new(PhysicalInputKind.Wheel, args.Data.X, args.Data.Y,
            WheelX: horizontal ? -amount : 0, WheelY: horizontal ? 0 : amount));
    }

    private void Emit(HookEventArgs args, PhysicalInput input)
    {
        try { args.SuppressEvent = Input?.Invoke(input) == true; }
        catch (Exception error)
        {
            _capturing = false; // Fail open locally, and let the session revoke the peer.
            ReportError("Bộ xử lý đầu vào gặp lỗi: " + error.Message);
        }
    }

    public void Inject(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        lock (_injectionGate)
        {
            var simulator = RequireSimulator();
            switch (packet.Type)
            {
                case "enter": case "move": MoveNormalized(simulator, packet.X, packet.Y); break;
                case "leave": ReleaseAllCore(simulator); break;
                case "key":
                    var key = CanonicalKeyMap.ToSharpHookKeyCode(packet.Key, packet.Scan, (packet.Flags & 1) != 0);
                    if (key == KeyCode.VcUndefined) return;
                    // Record a down before attempting native work: a partial failure must still release it.
                    if (packet.Down) _injectedKeys.Add(key);
                    Check(packet.Down ? simulator.SimulateKeyPress(key) : simulator.SimulateKeyRelease(key));
                    if (!packet.Down) _injectedKeys.Remove(key);
                    break;
                case "button":
                case "local-button-release":
                    var button = packet.Button switch
                    {
                        "left" => MouseButton.Button1, "right" => MouseButton.Button2, "middle" => MouseButton.Button3,
                        "x1" => MouseButton.Button4, "x2" => MouseButton.Button5, _ => MouseButton.NoButton,
                    };
                    if (button == MouseButton.NoButton) throw new InvalidDataException("Nút chuột không hợp lệ.");
                    bool buttonDown = packet.Type == "button" && packet.Down;
                    if (packet.Type == "button") MoveNormalized(simulator, packet.X, packet.Y);
                    if (buttonDown) _injectedButtons.Add(button);
                    if (OperatingSystem.IsMacOS())
                    {
                        var point = new DesktopPoint(_lastX, _lastY);
                        ushort clicks = buttonDown ? _clicks.Press(button, point, Stopwatch.GetTimestamp()) : _clicks.Release(button, point);
                        Check(buttonDown ? simulator.SimulateMousePress(button, clicks) : simulator.SimulateMouseRelease(button, clicks));
                    }
                    else Check(buttonDown ? simulator.SimulateMousePress(button) : simulator.SimulateMouseRelease(button));
                    if (!buttonDown) _injectedButtons.Remove(button);
                    break;
                case "wheel":
                    MoveNormalized(simulator, packet.X, packet.Y);
                    Scroll(simulator, -packet.Dx, true); Scroll(simulator, packet.Dy, false);
                    break;
                case "text": InjectText(simulator, packet); break;
                default: throw new InvalidDataException("Gói đầu vào không được hỗ trợ.");
            }
        }
    }

    private void InjectText(EventSimulator simulator, Packet packet)
    {
        if (packet.Text is null || packet.Text.Length > 256 || packet.Dx is < 0 or > 128)
            throw new InvalidDataException("Gói gõ Unicode không hợp lệ.");
        string text;
        try { text = packet.Text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new InvalidDataException("Chuỗi Unicode không hợp lệ."); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Gói gõ Unicode có ký tự điều khiển không hợp lệ.");
        var modifiers = _injectedKeys.Where(IsModifier).ToArray();
        foreach (var modifier in modifiers) Check(simulator.SimulateKeyRelease(modifier));
        try
        {
            for (int i = 0; i < packet.Dx; i++)
            {
                _injectedKeys.Add(KeyCode.VcBackspace);
                Check(simulator.SimulateKeyPress(KeyCode.VcBackspace));
                Check(simulator.SimulateKeyRelease(KeyCode.VcBackspace));
                _injectedKeys.Remove(KeyCode.VcBackspace);
            }
            if (text.Length > 0) Check(simulator.SimulateTextEntry(text));
        }
        finally
        {
            foreach (var modifier in modifiers) Check(simulator.SimulateKeyPress(modifier));
        }
    }

    private void MoveNormalized(EventSimulator simulator, int x, int y)
    {
        if (x is < 0 or > 65535 || y is < 0 or > 65535) throw new InvalidDataException("Tọa độ đầu vào không hợp lệ.");
        var bounds = _bounds;
        int px = bounds.X + (int)Math.Round(x / 65535.0 * Math.Max(0, bounds.Width - 1));
        int py = bounds.Y + (int)Math.Round(y / 65535.0 * Math.Max(0, bounds.Height - 1));
        Check(simulator.SimulateMouseMovement(ToNativeCoordinate(px), ToNativeCoordinate(py)));
        _lastX = px; _lastY = py;
        if (OperatingSystem.IsMacOS()) _clicks.Move(new(px, py));
    }

    private void Scroll(EventSimulator simulator, int delta, bool horizontal)
    {
        if (delta == 0) return;
        double amount = delta;
        if (OperatingSystem.IsMacOS())
        {
            // Keep sub-line wheel remainders; high-resolution trackpads must not vanish on conversion.
            ref double remainder = ref horizontal ? ref _wheelX : ref _wheelY;
            remainder += amount / 120.0;
            amount = Math.Truncate(remainder); remainder -= amount;
        }
        else if (OperatingSystem.IsLinux())
        {
            ref double remainder = ref horizontal ? ref _wheelX : ref _wheelY;
            remainder += amount;
            amount = Math.Truncate(remainder / 120) * 120; remainder -= amount;
        }
        while (amount != 0)
        {
            short chunk = (short)Math.Clamp(amount, short.MinValue, short.MaxValue);
            if (OperatingSystem.IsLinux()) chunk = (short)(chunk / 120 * 120);
            if (chunk == 0) break;
            Check(simulator.SimulateMouseWheel(chunk, horizontal ? MouseWheelScrollDirection.Horizontal : MouseWheelScrollDirection.Vertical,
                MouseWheelScrollType.BlockScroll));
            amount -= chunk;
        }
    }

    public void ReleaseAll()
    {
        lock (_injectionGate) { if (_simulator is { } simulator) ReleaseAllCore(simulator); }
    }

    private void ReleaseAllCore(EventSimulator simulator)
    {
        Exception? failure = null;
        foreach (var key in _injectedKeys.ToArray())
            try { Check(simulator.SimulateKeyRelease(key)); _injectedKeys.Remove(key); } catch (Exception e) { failure ??= e; }
        foreach (var button in _injectedButtons.ToArray())
            try
            {
                ushort clicks = OperatingSystem.IsMacOS() ? _clicks.Release(button, new(_lastX, _lastY)) : (ushort)1;
                Check(simulator.SimulateMouseRelease(button, clicks)); _injectedButtons.Remove(button);
            }
            catch (Exception e) { failure ??= e; }
        _clicks.Clear();
        _wheelX = _wheelY = 0;
        if (failure is not null) throw new InvalidOperationException("Không thể nhả hết đầu vào đã mô phỏng.", failure);
    }

    private EventSimulator RequireSimulator()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _started && _simulator is { } simulator ? simulator : throw new InvalidOperationException("Bộ đầu vào chưa được khởi động.");
    }
    private static bool IsModifier(KeyCode key) => key is >= KeyCode.VcLeftShift and <= KeyCode.VcRightMeta;
    private static short ToNativeCoordinate(int coordinate) => checked((short)coordinate);
    private static void Check(UioHookResult result)
    {
        if (result != UioHookResult.Success) throw new InvalidOperationException("Hệ điều hành từ chối đầu vào: " + result);
    }
    private void ReportError(string message) { try { Error?.Invoke(message); } catch { /* Never unwind into the native hook. */ } }

    private static DesktopBounds ReadBounds()
    {
        var screens = UioHookProvider.Instance.CreateScreenInfo();
        if (screens.Length == 0) throw new InvalidOperationException("Không đọc được kích thước màn hình hiện tại.");
        int left = screens.Min(s => (int)s.X), top = screens.Min(s => (int)s.Y);
        int right = screens.Max(s => (int)s.X + s.Width), bottom = screens.Max(s => (int)s.Y + s.Height);
        return ValidateInjectionBounds(new(left, top, right - left, bottom - top));
    }

    internal static DesktopBounds ValidateInjectionBounds(DesktopBounds bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Kích thước màn hình phải dương.");
        if (bounds.X < short.MinValue || bounds.Y < short.MinValue
            || (long)bounds.X + bounds.Width - 1 > short.MaxValue
            || (long)bounds.Y + bounds.Height - 1 > short.MaxValue)
            throw new NotSupportedException("Bố trí màn hình vượt giới hạn tọa độ 16 bit của bộ đầu vào đa nền tảng.");
        return bounds;
    }

    private DesktopPoint ReadPosition()
    {
        if (OperatingSystem.IsWindows() && GetCursorPos(out var point)) return new(point.X, point.Y);
        if (OperatingSystem.IsMacOS())
        {
            nint nativeEvent = CGEventCreate(0);
            if (nativeEvent != 0)
            {
                try { var p = CGEventGetLocation(nativeEvent); return new((int)Math.Round(p.X), (int)Math.Round(p.Y)); }
                finally { CFRelease(nativeEvent); }
            }
        }
        return new(_lastX, _lastY);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _capturing = false;
        try { _hook?.Dispose(); }
        catch (Exception error) { ReportError("Không thể dừng bộ bắt đầu vào: " + error.Message); }
        _hook = null;
        lock (_injectionGate)
        {
            try { if (_simulator is { } simulator) ReleaseAllCore(simulator); }
            catch (Exception error) { ReportError(error.Message); }
            _simulator?.Dispose(); _simulator = null;
            _started = false; _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential)] private struct WinPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MacPoint { public double X, Y; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out WinPoint point);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern nint CGEventCreate(nint source);
    [DllImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")] private static extern MacPoint CGEventGetLocation(nint nativeEvent);
    [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation")] private static extern void CFRelease(nint instance);
}
