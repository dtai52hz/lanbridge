using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace LanBridge;

/// <summary>
/// Windows keyboard/mouse bridge. Start creates its own STA message-loop thread so window
/// animations, dragging, dialogs and other UI work cannot stall the global low-level hooks.
/// No hook or input injection is performed by the constructor. Incoming packets must already have
/// passed the connection's authentication and permission checks. Send is dispatched, in order,
/// on a worker thread; it must enqueue network work and return promptly.
/// </summary>
/// <remarks>
/// Input wire messages (X/Y are normalized over the entire virtual desktop, 0..65535):
/// enter: X,Y,Name=controller's exit edge; move: X,Y;
/// button: X,Y,Button=left/right/middle/x1/x2,Down;
/// wheel: X,Y,Dy=vertical wheel delta,Dx=horizontal wheel delta (120 is one detent);
/// key: Key=Windows VK,Scan=hardware scan code,Flags bit 0=extended,Down;
/// text: Dx=number of characters to erase at caret,Text=NFC Unicode suffix to insert;
/// leave: X,Y,Name=exit edge,Allow=true when the host asks the controller to return.
/// A sends leave with Allow=false. B releases every injected key/button on any leave.
/// Input injection follows Windows integrity boundaries; UAC/secure desktops cannot be controlled.
/// </remarks>
public sealed class InputRouter : IDisposable
{
    public Action<Packet>? Send { get => Volatile.Read(ref _send); set => Volatile.Write(ref _send, value); }
    public Action<bool>? RemoteControlChanged { get; set; }
    public Action? EmergencyReleased { get; set; }
    public string? LastError { get; private set; }
    public bool IsRemote => _remote;
    public bool IsController
    {
        get => _controller;
        set => OnOwner(() => { if (_controller == value) return; ReleaseCore(true); _controller = value; _blockLocal = false; });
    }
    public bool CanControl
    {
        get => _allowed;
        set => OnOwner(() => { _allowed = value; if (!value) ReleaseCore(true); });
    }
    public ScreenEdge ExitEdge { get; set; } = ScreenEdge.Right;
    public int RemoteWidth { get; set; } = 1920;
    public int RemoteHeight { get; set; } = 1080;
    public int EdgeDwellMilliseconds { get; set; } = 0;
    public TypingMode TypingMode
    {
        get => _composer.Mode;
        set => OnOwner(() => { if (_composer.Mode != value) { ReleaseHeldInputs(); _textKeys.Clear(); _composer.Mode = value; } });
    }

    private volatile bool _controller, _allowed, _remote, _blockLocal, _disposed, _started;
    private Action<Packet>? _send;
    private IntPtr _mouseHook, _keyboardHook;
    private readonly HookProc _mouseProc, _keyboardProc;
    private SynchronizationContext? _context;
    private int _ownerThread;
    private readonly object _lifecycle = new();
    private Thread? _worker;
    private ApplicationContext? _application;
    private TaskCompletionSource? _startup;
    private int _disposeRequested;
    private bool _nativeInputEnabled;
    private System.Windows.Forms.Timer? _timer;
    private Rectangle _bounds;
    private Point _lastLocalPoint, _anchor;
    private double _remoteX, _remoteY;
    private int _lastNormX, _lastNormY;
    private long _edgeSince, _boundsReadAt;
    private bool _edgeCandidate;
    private readonly Dictionary<int, KeyStroke> _physicalKeys = [];
    private readonly Dictionary<int, KeyStroke> _receivedKeys = [];
    private readonly Dictionary<int, KeyStroke> _sentKeys = [];
    private readonly VietnameseComposer _composer = new();
    private readonly HashSet<int> _textKeys = [];
    private readonly byte[] _translationState = new byte[256];
    private bool _textCapsLock;
    private readonly HashSet<string> _physicalButtons = [];
    private readonly HashSet<string> _receivedButtons = [];
    private readonly HashSet<string> _sentButtons = [];
    private readonly ConcurrentQueue<(Packet Packet, Action<Packet> Sender)> _outbound = new();
    private int _sending;
    private bool _emergencyChord;
    private const uint InjectionTag = 0x4C424447; // "LBDG", also checked in addition to injected flags.
    private readonly record struct KeyStroke(int Key, int Scan, bool Extended);

    public InputRouter()
    {
        _mouseProc = MouseHook;
        _keyboardProc = KeyboardHook;
    }

    public void Start() => StartDispatcher(true);

    // Uses the real, independent message loop without touching physical input or installing hooks.
    internal void StartForTesting() => StartDispatcher(false);
    internal Action<Packet>? PacketAppliedForTesting { get; set; }
    internal Task InvokeForTestingAsync(Action action) => OnOwnerAsync(action, CancellationToken.None);

    private void StartDispatcher(bool nativeInputEnabled)
    {
        TaskCompletionSource startup;
        Thread worker;
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed || Volatile.Read(ref _disposeRequested) != 0, this);
            if (_startup is null)
            {
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows input hooks are required.");
                _startup = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var signal = _startup;
                _worker = new Thread(() => RunDispatcher(nativeInputEnabled, signal))
                { IsBackground = true, Name = "LanBridge input" };
                _worker.SetApartmentState(ApartmentState.STA);
                _worker.Start();
            }
            startup = _startup;
            worker = _worker!;
        }
        // There are no hooks on the caller's UI thread while this short initialization completes.
        try { startup.Task.GetAwaiter().GetResult(); }
        catch
        {
            worker.Join(TimeSpan.FromSeconds(2));
            lock (_lifecycle)
                if (_worker == worker) { _worker = null; _startup = null; }
            throw;
        }
    }

    private void RunDispatcher(bool nativeInputEnabled, TaskCompletionSource startup)
    {
        _ownerThread = Environment.CurrentManagedThreadId;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposeRequested) != 0, this);
            _application = new ApplicationContext();
            _context = new WindowsFormsSynchronizationContext();
            SynchronizationContext.SetSynchronizationContext(_context);
            InitializeInput(nativeInputEnabled);
            // Signal from inside the running pump, not just after creating the thread.
            _context.Post(_ => startup.TrySetResult(), null);
            Application.Run(_application);
        }
        catch (Exception error) { startup.TrySetException(error); }
        finally
        {
            StopHooks();
            _application?.Dispose();
            _application = null;
            _context = null;
            _started = false;
        }
    }

    private void InitializeInput(bool nativeInputEnabled)
    {
        _nativeInputEnabled = nativeInputEnabled;
        _bounds = SystemInformation.VirtualScreen;
        _lastLocalPoint = Cursor.Position;
        _boundsReadAt = Environment.TickCount64;
        if (!nativeInputEnabled) { _started = true; return; }
        // Remember modifiers already held when the visible application enables the hooks.
        foreach (var vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                _physicalKeys[vk] = new(vk, (int)(MapVirtualKey((uint)vk, 0) & 0xff), vk is 0xA3 or 0xA5 or 0x5B or 0x5C);
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(14, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể theo dõi chuột Windows.");
        _keyboardHook = SetWindowsHookEx(13, _keyboardProc, module, 0);
        if (_keyboardHook == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            throw new Win32Exception(error, "Không thể theo dõi bàn phím Windows.");
        }
        _started = true;
        _timer = new System.Windows.Forms.Timer { Interval = 40 };
        _timer.Tick += TimerTick;
        _timer.Start();
    }

    /// <summary>Use on A. B uses IsController=false and CanControl=the approved input permission.</summary>
    public void ConfigureController(bool allowed, ScreenEdge edge) => OnOwner(() =>
    {
        if (!_controller || ExitEdge != edge || !allowed) ReleaseCore(true);
        _controller = true;
        _allowed = allowed;
        ExitEdge = edge;
        _blockLocal = false;
        _edgeCandidate = false;
    });

    public void SetBlockLocalInput(bool enabled) => OnOwner(() =>
        _blockLocal = enabled && !_controller && _allowed);

    /// <summary>May be called by a receiver thread. Injection is marshalled onto the independent input thread.</summary>
    public void ApplyRemote(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        OnOwner(() => ApplyPacketCore(packet));
    }

    /// <summary>Completes after the input thread applies this packet, preserving receiver order without waiting for the UI.</summary>
    public Task ApplyRemoteAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return OnOwnerAsync(() => ApplyPacketCore(packet), cancellationToken);
    }

    private void ApplyPacketCore(Packet packet)
    {
        try
        {
            ApplyCore(packet);
            if (LastError is not null) ReleaseCore(true);
            if (!_nativeInputEnabled) PacketAppliedForTesting?.Invoke(packet);
        }
        catch
        {
            // Release on the input thread immediately, even when an error dialog or the UI is busy.
            ReleaseCore(true);
            throw;
        }
    }

    public void ReleaseRemote(bool notify = true) => OnOwner(() => ReleaseCore(notify));

    /// <summary>Releases a closed session on the input thread; an old callback cannot revoke a newer session.</summary>
    public void ReleaseSession(Action<Packet> sender)
    {
        ArgumentNullException.ThrowIfNull(sender);
        OnOwner(() =>
        {
            if (!ReferenceEquals(Send, sender)) return;
            _allowed = false;
            ReleaseCore(false);
            _blockLocal = false;
        });
    }

    public void ForceReleaseAll() => OnOwner(ReleaseHeldInputs);

    private void ApplyCore(Packet packet)
    {
        if (_disposed || !_started) return;
        if (packet.Type == "leave")
        {
            if (_controller && _remote)
            {
                _lastNormX = Math.Clamp(packet.X, 0, 65535);
                _lastNormY = Math.Clamp(packet.Y, 0, 65535);
            }
            ReleaseCore(false);
            return;
        }
        if (_controller || !_allowed) return;
        if (packet.Type == "enter")
        {
            ReleaseHeldInputs();
            _remote = true;
            LastError = null;
            // Name describes A's edge; B's corresponding entry edge is its opposite.
            if (Enum.TryParse<ScreenEdge>(packet.Name, true, out var edge)) ExitEdge = edge;
            MoveReceived(packet.X, packet.Y);
            NotifyRemoteChanged(true);
            return;
        }
        if (!_remote) return;
        switch (packet.Type)
        {
            case "text": InjectText(packet); break;
            case "move": MoveReceived(packet.X, packet.Y); break;
            case "button" when IsButton(packet.Button):
            {
                var button = packet.Button!;
                MoveReceived(packet.X, packet.Y);
                InjectButton(button, packet.Down);
                if (packet.Down) _receivedButtons.Add(button); else _receivedButtons.Remove(button);
                break;
            }
            case "wheel":
                MoveReceived(packet.X, packet.Y);
                if (packet.Dy != 0) InjectMouse(0x0800, Math.Clamp(packet.Dy, -32768, 32767));
                if (packet.Dx != 0) InjectMouse(0x1000, Math.Clamp(packet.Dx, -32768, 32767));
                break;
            case "key" when packet.Key is > 0 and < 255 && packet.Scan is >= 0 and <= 255:
            {
                var key = new KeyStroke(packet.Key, packet.Scan, (packet.Flags & 1) != 0);
                // Ctrl+Alt+F12 is reserved for the person physically at either machine.
                if (key.Key == 0x7B && HasRemoteControlAlt()) break;
                InjectKey(key, packet.Down);
                if (packet.Down) _receivedKeys[key.Key] = key; else _receivedKeys.Remove(key.Key);
                break;
            }
        }
    }

    private void MoveReceived(int x, int y)
    {
        _lastNormX = Math.Clamp(x, 0, 65535);
        _lastNormY = Math.Clamp(y, 0, 65535);
        InjectAbsolute(_lastNormX, _lastNormY);
    }

    private void TimerTick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        if (now - _boundsReadAt > 2000)
        {
            var bounds = SystemInformation.VirtualScreen;
            if (bounds != _bounds)
            {
                ReleaseCore(true);
                _bounds = bounds;
            }
            _boundsReadAt = now;
        }
        if (!_controller || !_allowed || _remote || !_edgeCandidate) return;
        var point = Cursor.Position;
        if (!IsAtExit(point)) { _edgeCandidate = false; return; }
        if (now - _edgeSince >= Math.Clamp(EdgeDwellMilliseconds, 0, 2000)) EnterRemote(point);
    }

    private void EnterRemote(Point point)
    {
        if (_remote || !_allowed || Send is null || !_started) return;
        _edgeCandidate = false;
        _remote = true;
        _composer.Reset(); _textKeys.Clear(); _textCapsLock = (GetKeyState(0x14) & 1) != 0;
        LastError = null;
        int width = Math.Clamp(RemoteWidth, 2, 65536), height = Math.Clamp(RemoteHeight, 2, 65536);
        var nx = Normalize(point.X, _bounds.Left, _bounds.Width);
        var ny = Normalize(point.Y, _bounds.Top, _bounds.Height);
        _remoteX = nx * (width - 1d) / 65535d;
        _remoteY = ny * (height - 1d) / 65535d;
        switch (ExitEdge)
        {
            case ScreenEdge.Right: _remoteX = Math.Min(3, width - 1); break;
            case ScreenEdge.Left: _remoteX = Math.Max(0, width - 4); break;
            case ScreenEdge.Top: _remoteY = Math.Max(0, height - 4); break;
            case ScreenEdge.Bottom: _remoteY = Math.Min(3, height - 1); break;
        }
        UpdateNormalizedRemote();
        Queue(new() { Type = "enter", X = _lastNormX, Y = _lastNormY, Name = ExitEdge.ToString() });
        // Release state on A before routing held modifiers/buttons to B. Physical state is tracked
        // separately, so injected releases never confuse the emergency shortcut or subsequent ups.
        foreach (var key in _physicalKeys.Values)
        {
            InjectKey(key, false);
            SendKey(key, true);
        }
        foreach (var button in _physicalButtons)
        {
            InjectButton(button, false);
            SendButton(button, true);
        }
        var screen = Screen.FromPoint(point).Bounds;
        _anchor = new(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        WarpLocal(_anchor);
        NotifyRemoteChanged(true);
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _disposed) return CallNextHookEx(_mouseHook, code, message, data);
        try
        {
            var item = Marshal.PtrToStructure<MouseHookData>(data);
            if ((item.Flags & 1) != 0 || item.ExtraInfo.ToUInt64() == InjectionTag)
                return CallNextHookEx(_mouseHook, code, message, data);
            var msg = message.ToInt32();
            var point = new Point(item.Point.X, item.Point.Y);
            if (msg == 0x0200)
            {
                if (_controller && _remote)
                {
                    ControllerMouseMoved(point);
                    return new IntPtr(1);
                }
                if (TrackExitEdge(point, Environment.TickCount64))
                {
                    EnterRemote(point);
                    // EnterRemote parks A's cursor at its anchor. Swallow the original move
                    // so Windows cannot apply the old edge position after that warp.
                    if (_remote) return new IntPtr(1);
                }
            }
            else if (TryButton(msg, item.MouseData, out var button, out var down))
            {
                if (down) _physicalButtons.Add(button); else _physicalButtons.Remove(button);
                if (_controller && _remote)
                {
                    if (down) { _composer.Reset(); SendPhysicalShifts(); }
                    SendButton(button, down);
                    if (!down) ReleaseUnneededShift();
                    return new IntPtr(1);
                }
            }
            else if (_controller && _remote && (msg is 0x020A or 0x020E))
            {
                int delta = unchecked((short)(item.MouseData >> 16));
                _composer.Reset(); SendPhysicalShifts();
                SendWheel(msg == 0x020E ? delta : 0, msg == 0x020A ? delta : 0);
                ReleaseUnneededShift();
                return new IntPtr(1);
            }
            if (!_controller && _remote && _blockLocal) return new IntPtr(1);
        }
        catch (Exception error) { RecoverHookError(error); }
        return CallNextHookEx(_mouseHook, code, message, data);
    }

    // Plans a local move without native calls; an immediate crossing runs in the same hook event.
    internal bool TrackExitEdge(Point point, long now)
    {
        var dx = point.X - _lastLocalPoint.X;
        var dy = point.Y - _lastLocalPoint.Y;
        _lastLocalPoint = point;
        if (!_controller || !_allowed || _remote) { _edgeCandidate = false; return false; }
        bool outward = ExitEdge switch
        {
            ScreenEdge.Right => dx >= 0,
            ScreenEdge.Left => dx <= 0,
            ScreenEdge.Top => dy <= 0,
            _ => dy >= 0,
        };
        if (!IsAtExit(point) || !outward) { _edgeCandidate = false; return false; }
        if (!_edgeCandidate) { _edgeCandidate = true; _edgeSince = now; }
        return now - _edgeSince >= Math.Clamp(EdgeDwellMilliseconds, 0, 2000);
    }

    private void ControllerMouseMoved(Point point)
    {
        int dx = point.X - _anchor.X, dy = point.Y - _anchor.Y;
        if (dx == 0 && dy == 0) return;
        int width = Math.Clamp(RemoteWidth, 2, 65536), height = Math.Clamp(RemoteHeight, 2, 65536);
        _remoteX += dx;
        _remoteY += dy;
        bool returning = ExitEdge switch
        {
            ScreenEdge.Right => _remoteX <= 0 && dx < 0,
            ScreenEdge.Left => _remoteX >= width - 1 && dx > 0,
            ScreenEdge.Top => _remoteY >= height - 1 && dy > 0,
            _ => _remoteY <= 0 && dy < 0,
        };
        _remoteX = Math.Clamp(_remoteX, 0, width - 1d);
        _remoteY = Math.Clamp(_remoteY, 0, height - 1d);
        UpdateNormalizedRemote();
        if (returning) { ReleaseCore(true); return; }
        Queue(new() { Type = "move", X = _lastNormX, Y = _lastNormY });
        WarpLocal(_anchor);
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _disposed) return CallNextHookEx(_keyboardHook, code, message, data);
        try
        {
            var item = Marshal.PtrToStructure<KeyboardHookData>(data);
            if ((item.Flags & 0x10) != 0 || item.ExtraInfo.ToUInt64() == InjectionTag)
                return CallNextHookEx(_keyboardHook, code, message, data);
            var msg = message.ToInt32();
            if (msg is not (0x0100 or 0x0101 or 0x0104 or 0x0105))
                return CallNextHookEx(_keyboardHook, code, message, data);
            bool down = msg is 0x0100 or 0x0104;
            var key = new KeyStroke((int)item.VkCode, (int)(item.ScanCode & 0xff), (item.Flags & 1) != 0);
            if (down) _physicalKeys[key.Key] = key; else _physicalKeys.Remove(key.Key);
            bool control = HasPhysical(0x11, 0xA2, 0xA3), alt = HasPhysical(0x12, 0xA4, 0xA5);
            if (key.Key == 0x7B && down && control && alt)
            {
                EmergencyReleaseCore();
                return CallNextHookEx(_keyboardHook, code, message, data);
            }
            if (_emergencyChord)
            {
                // Keep the shortcut's releases local even if UI/network callbacks run immediately.
                if (!control && !alt && !_physicalKeys.ContainsKey(0x7B)) _emergencyChord = false;
                return CallNextHookEx(_keyboardHook, code, message, data);
            }
            if (_controller && _remote) { RouteControllerKey(key, down); return new IntPtr(1); }
            if (!_controller && _remote && _blockLocal) return new IntPtr(1);
        }
        catch (Exception error) { RecoverHookError(error); }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }

    private bool HasPhysical(int generic, int left, int right) =>
        _physicalKeys.ContainsKey(generic) || _physicalKeys.ContainsKey(left) || _physicalKeys.ContainsKey(right);

    private void EmergencyReleaseCore()
    {
        _emergencyChord = true;
        // The person at B revokes control immediately; the UI may be busy and publish the
        // permission change later. A new enter packet must not undo this physical escape.
        if (!_controller) _allowed = false;
        ReleaseCore(true);
        _blockLocal = false;
        Post(() => EmergencyReleased?.Invoke());
    }

    private static bool IsShift(int key) => key is 0x10 or 0xA0 or 0xA1;
    private static bool IsShortcutModifier(int key) => key is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    private static bool IsModifier(int key) => IsShift(key) || IsShortcutModifier(key);
    private bool HasShortcutModifier() => HasPhysical(0x11, 0xA2, 0xA3) || HasPhysical(0x12, 0xA4, 0xA5) || _physicalKeys.ContainsKey(0x5B) || _physicalKeys.ContainsKey(0x5C);

    private void RouteControllerKey(KeyStroke key, bool down)
    {
        if (_composer.Mode == TypingMode.Physical) { _composer.Reset(); SendKey(key, down); return; }
        if (!down)
        {
            _textKeys.Remove(key.Key);
            if (_sentKeys.ContainsKey(key.Key)) SendKey(key, false);
            ReleaseUnneededShift();
            return;
        }
        if (key.Key == 0x14) { _textCapsLock = !_textCapsLock; _textKeys.Add(key.Key); return; }
        if (IsShift(key.Key))
        {
            if (HasShortcutModifier() || _sentKeys.Keys.Any(k => !IsModifier(k)) || _sentButtons.Count != 0) SendKey(key, true);
            return;
        }
        if (IsShortcutModifier(key.Key) || HasShortcutModifier() || _sentKeys.ContainsKey(key.Key))
        {
            _textKeys.Remove(key.Key); _composer.Reset(); SendPhysicalShifts(); SendKey(key, true); return;
        }
        if (key.Key == 0x08)
        {
            QueueText(_composer.Backspace()); _textKeys.Add(key.Key); return;
        }
        if (TryTranslatePrintable(key, out var text))
        {
            _textKeys.Add(key.Key);
            if (text.Any(char.IsSurrogate)) { _composer.Reset(); QueueText(new Packet { Type = "text", Text = text }); }
            else foreach (char character in text) QueueText(_composer.Process(character));
            return;
        }
        // Navigation, deletion and function keys keep their physical shortcut semantics.
        _composer.Reset(); SendPhysicalShifts(); SendKey(key, true);
    }

    private void SendPhysicalShifts()
    {
        foreach (var shift in _physicalKeys.Values.Where(k => IsShift(k.Key)))
            if (!_sentKeys.ContainsKey(shift.Key)) SendKey(shift, true);
    }
    private void ReleaseUnneededShift()
    {
        if (_composer.Mode == TypingMode.Physical) return;
        if (HasShortcutModifier() || _sentKeys.Keys.Any(k => !IsModifier(k)) || _sentButtons.Count != 0) return;
        foreach (var shift in _sentKeys.Values.Where(k => IsShift(k.Key)).ToArray()) SendKey(shift, false);
    }
    private void QueueText(Packet packet) { if (packet.Dx != 0 || !string.IsNullOrEmpty(packet.Text)) Queue(packet); }

    private bool TryTranslatePrintable(KeyStroke key, out string text)
    {
        text = "";
        if (IsModifier(key.Key) || key.Key is 0x0D or 0x09 or 0x1B or 0x2E || key.Key is >= 0x21 and <= 0x28 || key.Key is >= 0x70 and <= 0x87) return false;
        Array.Clear(_translationState);
        foreach (int held in _physicalKeys.Keys) if (held is >= 0 and < 256) _translationState[held] |= 0x80;
        _translationState[0x10] = HasPhysical(0x10, 0xA0, 0xA1) ? (byte)0x80 : (byte)0;
        _translationState[0x14] = _textCapsLock ? (byte)1 : (byte)0;
        var layoutThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        var output = new StringBuilder(16);
        // Flag4 prevents mutating the local foreground application's dead-key state.
        int length = ToUnicodeEx((uint)key.Key, (uint)key.Scan, _translationState, output, output.Capacity, 4, GetKeyboardLayout(layoutThread));
        if (length <= 0 || length > output.Length) return false;
        text = output.ToString(0, length);
        return text.All(c => !char.IsControl(c));
    }

    private bool HasRemoteControlAlt() =>
        (_receivedKeys.ContainsKey(0x11) || _receivedKeys.ContainsKey(0xA2) || _receivedKeys.ContainsKey(0xA3)) &&
        (_receivedKeys.ContainsKey(0x12) || _receivedKeys.ContainsKey(0xA4) || _receivedKeys.ContainsKey(0xA5));

    private void SendKey(KeyStroke key, bool down)
    {
        Queue(new() { Type = "key", Key = key.Key, Scan = key.Scan, Flags = key.Extended ? 1 : 0, Down = down });
        if (down) _sentKeys[key.Key] = key; else _sentKeys.Remove(key.Key);
    }

    private void SendButton(string button, bool down)
    {
        Queue(new() { Type = "button", X = _lastNormX, Y = _lastNormY, Button = button, Down = down });
        if (down) _sentButtons.Add(button); else _sentButtons.Remove(button);
    }

    private void SendWheel(int horizontal, int vertical) => Queue(new()
    {
        Type = "wheel", X = _lastNormX, Y = _lastNormY, Dx = horizontal, Dy = vertical,
    });

    private void ReleaseCore(bool notify)
    {
        bool wasRemote = _remote;
        _remote = false;
        _composer.Reset(); _textKeys.Clear();
        _blockLocal = false;
        _edgeCandidate = false;
        ReleaseHeldInputs();
        if (wasRemote && notify)
            Queue(new() { Type = "leave", X = _lastNormX, Y = _lastNormY, Name = ExitEdge.ToString(), Allow = !_controller });
        if (wasRemote && _controller && _started)
        {
            int x = Denormalize(_lastNormX, _bounds.Left, _bounds.Width);
            int y = Denormalize(_lastNormY, _bounds.Top, _bounds.Height);
            switch (ExitEdge)
            {
                case ScreenEdge.Right: x = _bounds.Right - Math.Min(10, _bounds.Width); break;
                case ScreenEdge.Left: x = _bounds.Left + Math.Min(9, _bounds.Width - 1); break;
                case ScreenEdge.Top: y = _bounds.Top + Math.Min(9, _bounds.Height - 1); break;
                case ScreenEdge.Bottom: y = _bounds.Bottom - Math.Min(10, _bounds.Height); break;
            }
            var target = ClampToScreen(new(x, y));
            WarpLocal(target);
            _lastLocalPoint = target;
        }
        if (wasRemote) NotifyRemoteChanged(false);
    }

    private void ReleaseHeldInputs()
    {
        // Only release keys/buttons actually injected by this router, never arbitrary local state.
        foreach (var key in _receivedKeys.Values) InjectKey(key, false);
        foreach (var button in _receivedButtons) InjectButton(button, false);
        _receivedKeys.Clear();
        _receivedButtons.Clear();
        foreach (var key in _sentKeys.Values)
            Queue(new() { Type = "key", Key = key.Key, Scan = key.Scan, Flags = key.Extended ? 1 : 0, Down = false });
        foreach (var button in _sentButtons) Queue(new() { Type = "button", X = _lastNormX, Y = _lastNormY, Button = button, Down = false });
        _sentKeys.Clear();
        _sentButtons.Clear();
    }

    private bool IsAtExit(Point point) => ExitEdge switch
    {
        ScreenEdge.Right => point.X >= _bounds.Right - 1,
        ScreenEdge.Left => point.X <= _bounds.Left,
        ScreenEdge.Top => point.Y <= _bounds.Top,
        _ => point.Y >= _bounds.Bottom - 1,
    };

    private void UpdateNormalizedRemote()
    {
        _lastNormX = (int)Math.Clamp(Math.Round(_remoteX * 65535d / (Math.Clamp(RemoteWidth, 2, 65536) - 1)), 0, 65535);
        _lastNormY = (int)Math.Clamp(Math.Round(_remoteY * 65535d / (Math.Clamp(RemoteHeight, 2, 65536) - 1)), 0, 65535);
    }

    internal static int Normalize(int value, int origin, int length) =>
        (int)Math.Clamp(Math.Round((value - (double)origin) * 65535d / Math.Max(1, length - 1)), 0, 65535);
    internal static int Denormalize(int value, int origin, int length) =>
        origin + (int)Math.Round(Math.Clamp(value, 0, 65535) * Math.Max(0, length - 1d) / 65535d);

    private static Point ClampToScreen(Point point)
    {
        var bounds = Screen.FromPoint(point).Bounds;
        return new(Math.Clamp(point.X, bounds.Left, bounds.Right - 1), Math.Clamp(point.Y, bounds.Top, bounds.Bottom - 1));
    }

    private void WarpLocal(Point point) => InjectAbsolute(Normalize(point.X, _bounds.Left, _bounds.Width), Normalize(point.Y, _bounds.Top, _bounds.Height));

    private void InjectAbsolute(int x, int y) => Inject(new NativeInput
    {
        Type = 0,
        Union = new InputUnion { Mouse = new MouseInput { Dx = x, Dy = y, Flags = 0x0001 | 0x8000 | 0x4000, ExtraInfo = InjectionTag } },
    });

    private void InjectKey(KeyStroke key, bool down)
    {
        // Pause uses an E1 sequence, which KEYEVENTF_EXTENDEDKEY cannot represent; use its VK.
        bool scan = key.Scan != 0 && key.Key != 0x13;
        Inject(new NativeInput
        {
            Type = 1,
            Union = new InputUnion { Keyboard = new KeyboardInput
            {
                Vk = scan ? (ushort)0 : (ushort)key.Key,
                Scan = scan ? (ushort)key.Scan : (ushort)0,
                Flags = (scan ? 8u : 0u) | (key.Extended ? 1u : 0u) | (down ? 0u : 2u),
                ExtraInfo = InjectionTag,
            } },
        });
    }

    // Pure builder: tests validate Unicode/scancode flags without touching the real desktop.
    private static NativeInput[] BuildTextInputs(Packet packet)
    {
        if (packet.Dx is < 0 or > 128 || packet.Text is null || packet.Text.Length > 256)
            throw new InvalidDataException("Gói gõ Unicode không hợp lệ.");
        string text;
        try { text = packet.Text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { throw new InvalidDataException("Chuỗi Unicode không hợp lệ."); }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            throw new InvalidDataException("Gói gõ Unicode có ký tự điều khiển không hợp lệ.");
        var inputs = new List<NativeInput>((packet.Dx + text.Length) * 2);
        for (int i = 0; i < packet.Dx; i++)
        {
            inputs.Add(new NativeInput { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Vk = 0x08, ExtraInfo = InjectionTag } } });
            inputs.Add(new NativeInput { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Vk = 0x08, Flags = 2, ExtraInfo = InjectionTag } } });
        }
        foreach (char character in text)
        {
            inputs.Add(new NativeInput { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 4, ExtraInfo = InjectionTag } } });
            inputs.Add(new NativeInput { Type = 1, Union = new InputUnion { Keyboard = new KeyboardInput { Scan = character, Flags = 4 | 2, ExtraInfo = InjectionTag } } });
        }
        return inputs.ToArray();
    }
    private void InjectText(Packet packet)
    {
        var inputs = BuildTextInputs(packet);
        var modifiers = _receivedKeys.Values.Where(k => IsModifier(k.Key)).ToArray();
        // Unicode and edit Backspaces must not turn into Ctrl/Alt/Shift shortcuts.
        foreach (var modifier in modifiers) InjectKey(modifier, false);
        InjectBatch(inputs);
        foreach (var modifier in modifiers) InjectKey(modifier, true);
    }

    private void InjectButton(string button, bool down)
    {
        uint flag = button switch
        {
            "left" => down ? 0x0002u : 0x0004u,
            "right" => down ? 0x0008u : 0x0010u,
            "middle" => down ? 0x0020u : 0x0040u,
            _ => down ? 0x0080u : 0x0100u,
        };
        InjectMouse(flag, button == "x1" ? 1 : button == "x2" ? 2 : 0);
    }

    private void InjectMouse(uint flags, int data) => Inject(new NativeInput
    {
        Type = 0,
        Union = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)data), Flags = flags, ExtraInfo = InjectionTag } },
    });

    private void Inject(NativeInput input)
        => InjectBatch([input]);

    private void InjectBatch(NativeInput[] inputs)
    {
        if (!_started || !_nativeInputEnabled || inputs.Length == 0) return;
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length)
            LastError = "Windows từ chối thao tác nhập. Hãy chạy hai ứng dụng với cùng mức quyền; UAC/desktop bảo mật không hỗ trợ.";
    }

    private static bool IsButton(string? value) => value is "left" or "right" or "middle" or "x1" or "x2";

    private static bool TryButton(int msg, uint mouseData, out string button, out bool down)
    {
        down = msg is 0x0201 or 0x0204 or 0x0207 or 0x020B;
        button = msg switch
        {
            0x0201 or 0x0202 => "left",
            0x0204 or 0x0205 => "right",
            0x0207 or 0x0208 => "middle",
            0x020B or 0x020C => (mouseData >> 16) == 1 ? "x1" : "x2",
            _ => "",
        };
        return button.Length != 0;
    }

    private void Queue(Packet packet)
    {
        var sender = Send;
        if (sender is null) return;
        _outbound.Enqueue((packet, sender));
        if (Interlocked.CompareExchange(ref _sending, 1, 0) == 0)
            ThreadPool.QueueUserWorkItem(_ => DrainOutbound());
    }

    private void DrainOutbound()
    {
        while (true)
        {
            while (_outbound.TryDequeue(out var item))
            {
                try { item.Sender(item.Packet); }
                catch (Exception error)
                {
                    Post(() => { LastError = "Không gửi được thao tác: " + error.Message; ReleaseCore(false); });
                }
            }
            Interlocked.Exchange(ref _sending, 0);
            if (_outbound.IsEmpty || Interlocked.CompareExchange(ref _sending, 1, 0) != 0) break;
        }
    }

    private void NotifyRemoteChanged(bool value) => Post(() => RemoteControlChanged?.Invoke(value));
    private void OnOwner(Action action)
    {
        if (_disposed) return;
        if (_context is not null && Environment.CurrentManagedThreadId != _ownerThread) Post(action);
        else action();
    }
    private void Post(Action action)
    {
        var context = _context;
        if (context is null) { if (!_disposed) action(); return; }
        try { context.Post(_ => { if (!_disposed) action(); }, null); }
        catch (InvalidAsynchronousStateException) when (_disposed || Volatile.Read(ref _disposeRequested) != 0) { }
    }
    private Task OnOwnerAsync(Action action, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (_disposed) return Task.CompletedTask;
        var context = _context;
        if (context is null || Environment.CurrentManagedThreadId == _ownerThread)
        {
            try { action(); return Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(error); }
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            context.Post(_ =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try { action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            }, null);
        }
        catch (InvalidAsynchronousStateException) { completion.TrySetCanceled(); }
        return completion.Task.WaitAsync(cancellationToken);
    }
    private void RecoverHookError(Exception error)
    {
        LastError = error.Message;
        _blockLocal = false;
        Post(() => ReleaseCore(true));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        var worker = _worker;
        var context = _context;
        if (context is not null && Environment.CurrentManagedThreadId != _ownerThread)
        {
            // Teardown belongs to the same dedicated thread as installation and input state.
            try { context.Post(_ => DisposeOnOwner(), null); }
            catch (InvalidAsynchronousStateException) { }
            worker?.Join(TimeSpan.FromSeconds(2));
            return;
        }
        DisposeOnOwner();
    }

    private void DisposeOnOwner()
    {
        if (_disposed) return;
        ReleaseCore(true);
        StopHooks();
        _started = false;
        _disposed = true;
        _application?.ExitThread();
    }

    private void StopHooks()
    {
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        if (_keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(_keyboardHook);
        _mouseHook = _keyboardHook = IntPtr.Zero;
        GC.KeepAlive(_mouseProc);
        GC.KeepAlive(_keyboardProc);
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseHookData
    { public NativePoint Point; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardHookData
    { public uint VkCode, ScanCode, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeInput { public uint Type; public InputUnion Union; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    { [FieldOffset(0)] public MouseInput Mouse; [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput
    { public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    { public ushort Vk, Scan; public uint Flags, Time; public UIntPtr ExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] NativeInput[] inputs, int size);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] private static extern short GetKeyState(int key);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetKeyboardLayout(uint threadId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, StringBuilder text, int capacity, uint flags, IntPtr layout);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint mapType);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string? moduleName);
}
