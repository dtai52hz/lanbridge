using System.Diagnostics;
using System.Threading.Channels;

namespace LanBridge.CrossPlatform;

/// <summary>
/// Platform-neutral input state machine. A backend supplies physical events and native injection;
/// the existing LanBridge enter/key/text/move wire contract is retained across operating systems.
/// Send must enqueue quickly rather than write to a socket synchronously.
/// </summary>
public sealed class PortableInputRouter : IDisposable, IAsyncDisposable
{
    private const int MaximumPendingInput = 4096;
    private readonly IInputBackend _backend;
    private readonly object _gate = new();
    private readonly Channel<InjectionWork> _injections = Channel.CreateUnbounded<InjectionWork>(new()
    {
        SingleReader = true,
        AllowSynchronousContinuations = false,
    });
    private readonly Task _worker;
    private readonly Timer _moveTimer;
    private readonly VietnameseComposer _composer = new();
    private readonly Dictionary<int, PhysicalInput> _physicalKeys = [];
    private readonly HashSet<string> _physicalButtons = [];
    private readonly Dictionary<int, PhysicalInput> _sentKeys = [];
    private readonly HashSet<string> _sentButtons = [];
    private readonly HashSet<int> _consumedKeys = [];
    // This set is used only by the injection worker, never by the hook callback.
    private readonly HashSet<int> _receivedKeys = [];
    private Action<Packet>? _sender;
    private SwitchShortcut? _switchShortcut = new(0x78); // F9.
    private Packet? _pendingMove;
    private long _lastMoveAt;
    private int _generation;
    private int _pendingInput;
    private int _nativeOutstanding;
    private bool _nativeDirty;
    private bool _started, _disposed, _controller = true, _allowed, _remote, _capsLock;
    private bool _emergencyChord;
    private ScreenEdge _exitEdge = ScreenEdge.Right;
    private DesktopPoint _anchor, _lastLocal;
    private DesktopPoint? _returnPoint;
    private double _remoteX, _remoteY;
    private int _normX, _normY, _remoteWidth = 1920, _remoteHeight = 1080, _mouseSpeed = 100;

    public PortableInputRouter(IInputBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _backend.Input += OnPhysicalInput;
        _backend.Error += OnBackendError;
        _worker = Task.Run(InjectionLoopAsync);
        _moveTimer = new(_ => PaceMove(), null, Timeout.Infinite, Timeout.Infinite);
    }

    public event Action<bool>? RemoteChanged;
    public event Action? EmergencyReleased;
    public event Action<string>? Error;
    public bool IsRemote { get { lock (_gate) return _remote; } }
    public string? LastError { get; private set; }
    public IInputBackend Backend => _backend;

    /// <summary>Replacing the session clears capture and fences queued input from the old session.</summary>
    public Action<Packet>? Send
    {
        get { lock (_gate) return _sender; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (ReferenceEquals(_sender, value)) return;
                ReleaseCore(true);
                _sender = value;
            }
        }
    }

    public bool IsController
    {
        get { lock (_gate) return _controller; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_controller == value) return;
                ReleaseCore(true);
                _controller = value;
            }
        }
    }

    /// <summary>Set only after authenticated connection and the receiving user's consent.</summary>
    public bool CanControl
    {
        get { lock (_gate) return _allowed; }
        set
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_allowed == value) return;
                _allowed = value;
                if (!value) ReleaseCore(true);
            }
        }
    }

    public int RemoteWidth { get { lock (_gate) return _remoteWidth; } set { lock (_gate) _remoteWidth = Math.Clamp(value, 2, 65536); } }
    public int RemoteHeight { get { lock (_gate) return _remoteHeight; } set { lock (_gate) _remoteHeight = Math.Clamp(value, 2, 65536); } }
    public int MouseSpeedPercent { get { lock (_gate) return _mouseSpeed; } set { lock (_gate) _mouseSpeed = Math.Clamp(value, 50, 200); } }
    public ScreenEdge ExitEdge
    {
        get { lock (_gate) return _exitEdge; }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { if (_exitEdge != value) { ReleaseCore(true); _exitEdge = value; } }
        }
    }
    public TypingMode TypingMode
    {
        get { lock (_gate) return _composer.Mode; }
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            lock (_gate) { _composer.Mode = value; }
        }
    }
    public SwitchShortcut? SwitchShortcut
    {
        get { lock (_gate) return _switchShortcut; }
        set
        {
            if (value is { IsValid: false }) throw new ArgumentException("Phím chuyển không hợp lệ hoặc được hệ điều hành dành riêng.", nameof(value));
            lock (_gate) _switchShortcut = value;
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_started) return;
            _backend.Start();
            _lastLocal = _backend.Position;
            _started = true;
            _moveTimer.Change(8, 8);
        }
    }

    public Task ResetTypingAsync(TypingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate)
        {
            ThrowIfDisposed();
            ReleaseCore(true);
            _composer.Mode = mode;
            _composer.Reset();
            _capsLock = false;
            return QueueCleanup();
        }
    }

    public Task SuspendSwitchShortcutAsync()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _switchShortcut = null;
            ReleaseCore(true);
            return QueueCleanup();
        }
    }

    public void ReleaseRemote(bool notify = true)
    {
        lock (_gate) { if (!_disposed) ReleaseCore(notify); }
    }

    public void ReleaseSession(Action<Packet> sender)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_sender, sender)) return;
            _allowed = false;
            ReleaseCore(false);
            _sender = null;
        }
    }

    /// <summary>Atomically applies peer metadata/permission only while this callback still owns the router.</summary>
    public bool ConfigureSession(Action<Packet> sender, bool canControl, int? remoteWidth = null, int? remoteHeight = null)
    {
        ArgumentNullException.ThrowIfNull(sender);
        if (remoteWidth is < 2 or > 65536) throw new ArgumentOutOfRangeException(nameof(remoteWidth));
        if (remoteHeight is < 2 or > 65536) throw new ArgumentOutOfRangeException(nameof(remoteHeight));
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_sender, sender)) return false;
            if (remoteWidth is { } width) _remoteWidth = width;
            if (remoteHeight is { } height) _remoteHeight = height;
            if (_allowed != canControl)
            {
                _allowed = canControl;
                if (!canControl) ReleaseCore(true);
            }
            return true;
        }
    }

    public Task ApplyAsync(Packet packet, CancellationToken cancellationToken = default)
        => ApplyAsyncCore(packet, null, cancellationToken);

    /// <summary>Use this overload when a connection can finish reading after it has been replaced.</summary>
    public Task ApplyAsync(Packet packet, Action<Packet> sessionSender, CancellationToken cancellationToken = default)
        => ApplyAsyncCore(packet, sessionSender, cancellationToken);

    private Task ApplyAsyncCore(Packet packet, Action<Packet>? sessionSender, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(packet);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || !_started || (sessionSender is not null && !ReferenceEquals(sessionSender, _sender))) return Task.CompletedTask;
            if (packet.Type == "leave")
            {
                _normX = Math.Clamp(packet.X, 0, 65535);
                _normY = Math.Clamp(packet.Y, 0, 65535);
                ReleaseCore(false);
                return QueueCleanup().WaitAsync(cancellationToken);
            }
            if (_controller || !_allowed || !_backend.Capabilities.Inject) return Task.CompletedTask;
            if (packet.Type == "enter")
            {
                _generation++;
                _remote = true;
                if (Enum.TryParse<ScreenEdge>(packet.Name, true, out var edge) && Enum.IsDefined(edge)) _exitEdge = edge;
                NotifyRemote(true);
            }
            else if (!_remote) return Task.CompletedTask;
            if (!TryValidate(packet, out var validated)) return Task.CompletedTask;
            if (_pendingInput >= MaximumPendingInput)
            {
                _allowed = false;
                ReleaseCore(true);
                ReportError("Quá nhiều thao tác đang chờ; đã trả điều khiển về máy nguồn.");
                return QueueCleanup().WaitAsync(cancellationToken);
            }
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingInput++;
            _nativeOutstanding++;
            _nativeDirty = true;
            if (!_injections.Writer.TryWrite(new(validated, _generation, completion, CancellationToken: cancellationToken)))
            {
                _pendingInput--;
                _nativeOutstanding--;
                completion.TrySetException(new ObjectDisposedException(nameof(PortableInputRouter)));
            }
            return completion.Task.WaitAsync(cancellationToken);
        }
    }

    private static bool TryValidate(Packet packet, out Packet validated)
    {
        validated = packet with { X = Math.Clamp(packet.X, 0, 65535), Y = Math.Clamp(packet.Y, 0, 65535) };
        return packet.Type switch
        {
            "enter" or "move" => true,
            "button" => IsButton(packet.Button),
            "wheel" => packet.Dx is >= -120000 and <= 120000 && packet.Dy is >= -120000 and <= 120000,
            "key" => packet.Key is > 0 and < 255 && packet.Scan is >= 0 and <= 255 && (packet.Flags & ~1) == 0,
            "text" => packet.Dx is >= 0 and <= 128 && packet.Text is not null && packet.Text.Length <= 256 && IsValidText(packet.Text),
            _ => false,
        };
    }

    private static bool IsValidText(string? text)
    {
        if (text is null) return true;
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i]) || char.IsControl(text[i])) return false;
        }
        return true;
    }

    private async Task InjectionLoopAsync()
    {
        await foreach (var work in _injections.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            bool cleanupSucceeded = false;
            bool canceled = false;
            Exception? failure = null;
            try
            {
                if (work.CancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    continue;
                }
                if (work.Packet is null)
                {
                    _backend.ReleaseAll();
                    _receivedKeys.Clear();
                    cleanupSucceeded = true;
                }
                else
                {
                    if (work.LocalRelease)
                    {
                        _backend.Inject(work.Packet);
                        continue;
                    }
                    bool valid;
                    lock (_gate) valid = !_disposed && _started && !_controller && _allowed && _remote && work.Generation == _generation;
                    if (valid)
                    {
                        if (work.Packet.Type == "enter") { _backend.ReleaseAll(); _receivedKeys.Clear(); }
                        // An emergency chord is physical-only. Never synthesize it at the receiver.
                        bool reserved = work.Packet.Type == "key" && work.Packet.Key == 0x7B && ReceivedControlAlt();
                        if (!reserved)
                        {
                            _backend.Inject(work.Packet);
                            if (work.Packet.Type == "key")
                            {
                                if (work.Packet.Down) _receivedKeys.Add(work.Packet.Key); else _receivedKeys.Remove(work.Packet.Key);
                            }
                        }
                    }
                }
            }
            catch (Exception error)
            {
                failure = error;
                // A native API may accept a prefix before failing. ReleaseAll must include
                // potential downs as well as previously held inputs in each backend.
                try { _backend.ReleaseAll(); } catch { }
                _receivedKeys.Clear();
                lock (_gate)
                {
                    _allowed = false;
                    // A permanent failure in ReleaseAll must not enqueue another identical
                    // cleanup forever. Ordinary injection failure gets one ordered retry.
                    ReleaseCore(true, queueCleanup: work.Packet is not null);
                    ReportError(error.Message);
                }
            }
            finally
            {
                lock (_gate)
                {
                    if (cleanupSucceeded && _nativeOutstanding == 1) _nativeDirty = false;
                    _nativeOutstanding--;
                    if (work.Packet is not null && !work.LocalRelease) _pendingInput--;
                }
                // An awaited barrier represents completed native work AND its state fence.
                if (failure is not null) work.Completion?.TrySetException(failure);
                else if (canceled) work.Completion?.TrySetCanceled(work.CancellationToken);
                else work.Completion?.TrySetResult();
            }
        }
    }

    private bool OnPhysicalInput(PhysicalInput input)
    {
        lock (_gate)
        {
            if (_disposed || !_started) return false;
            try
            {
                if (input.Kind == PhysicalInputKind.Key) return RoutePhysicalKey(input);
                if (input.Kind == PhysicalInputKind.Button && IsButton(input.Button))
                {
                    if (input.Down) _physicalButtons.Add(input.Button!); else _physicalButtons.Remove(input.Button!);
                }
                if (!_controller) return false;
                if (!_remote)
                {
                    if (input.Kind == PhysicalInputKind.Move)
                    {
                        var position = new DesktopPoint(input.X, input.Y);
                        bool outward = _exitEdge switch
                        {
                            ScreenEdge.Right => position.X >= _lastLocal.X,
                            ScreenEdge.Left => position.X <= _lastLocal.X,
                            ScreenEdge.Top => position.Y <= _lastLocal.Y,
                            _ => position.Y >= _lastLocal.Y,
                        };
                        _lastLocal = position;
                        if (_allowed && outward && IsAtExit(position)) EnterRemote(position, false);
                        return _remote;
                    }
                    return false;
                }
                switch (input.Kind)
                {
                    case PhysicalInputKind.Move: MoveController(input); break;
                    case PhysicalInputKind.Button when IsButton(input.Button):
                        if (input.Down) { _composer.Reset(); SendPhysicalShifts(); }
                        SendButton(input.Button!, input.Down);
                        if (!input.Down) ReleaseUnneededShift();
                        break;
                    case PhysicalInputKind.Wheel:
                        _composer.Reset(); SendPhysicalShifts();
                        SendCore(new() { Type = "wheel", X = _normX, Y = _normY, Dx = input.WheelX, Dy = input.WheelY });
                        ReleaseUnneededShift();
                        break;
                }
                return true;
            }
            catch (Exception error)
            {
                _allowed = false;
                ReleaseCore(false);
                ReportError(error.Message);
                return false;
            }
        }
    }

    private bool RoutePhysicalKey(PhysicalInput key)
    {
        if (key.Key is <= 0 or >= 255) return false;
        bool repeat = _physicalKeys.ContainsKey(key.Key);
        if (key.Down) _physicalKeys[key.Key] = key; else _physicalKeys.Remove(key.Key);
        if (key.Down && key.Key == 0x7B && HasPhysical(0x11, 0xA2, 0xA3) && HasPhysical(0x12, 0xA4, 0xA5))
        {
            _emergencyChord = true;
            if (!_controller) _allowed = false;
            ReleaseCore(true);
            EmergencyReleased?.Invoke();
            return false;
        }
        if (_emergencyChord)
        {
            if (!key.Down) _consumedKeys.Remove(key.Key);
            if (!HasPhysical(0x11, 0xA2, 0xA3) && !HasPhysical(0x12, 0xA4, 0xA5) && !_physicalKeys.ContainsKey(0x7B)) _emergencyChord = false;
            return false;
        }
        if (_consumedKeys.Contains(key.Key))
        {
            if (!key.Down) _consumedKeys.Remove(key.Key);
            return true;
        }
        if (key.Down && !repeat && _controller && _allowed && _sender is not null && _backend.Capabilities.Capture
            && (_remote || _nativeOutstanding == 0 && !_nativeDirty)
            && _switchShortcut is { } shortcut && key.Key == shortcut.Key && PhysicalModifiers() == shortcut.Modifiers)
        {
            _consumedKeys.Add(key.Key);
            foreach (int held in _physicalKeys.Keys.Where(IsModifier)) _consumedKeys.Add(held);
            if (_remote) ReleaseCore(true); else EnterRemote(_backend.Position, true);
            return true;
        }
        if (!_controller || !_remote) return false;
        RouteControllerKey(key, repeat);
        return true;
    }

    private void EnterRemote(DesktopPoint point, bool fromShortcut)
    {
        if (_remote || !_allowed || _sender is null || !_backend.Capabilities.Capture || _nativeOutstanding != 0 || _nativeDirty) return;
        _returnPoint = fromShortcut ? point : null;
        _composer.Reset();
        // Backend-provided text already includes the real OS lock state. During capture,
        // Caps Lock is swallowed, so this flag is a relative case inversion until return.
        _capsLock = false;
        _remoteX = Normalize(point.X, _backend.Bounds.X, _backend.Bounds.Width) * (_remoteWidth - 1d) / 65535;
        _remoteY = Normalize(point.Y, _backend.Bounds.Y, _backend.Bounds.Height) * (_remoteHeight - 1d) / 65535;
        if (fromShortcut) { _remoteX = (_remoteWidth - 1d) / 2; _remoteY = (_remoteHeight - 1d) / 2; }
        else switch (_exitEdge)
        {
            case ScreenEdge.Right: _remoteX = Math.Min(3, _remoteWidth - 1); break;
            case ScreenEdge.Left: _remoteX = Math.Max(0, _remoteWidth - 4); break;
            case ScreenEdge.Top: _remoteY = Math.Max(0, _remoteHeight - 4); break;
            case ScreenEdge.Bottom: _remoteY = Math.Min(3, _remoteHeight - 1); break;
        }
        UpdateNormalized();
        _remote = true;
        _backend.SetCapture(true);
        var bounds = _backend.Bounds;
        _anchor = new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
        _backend.Warp(_anchor);
        SendCore(new() { Type = "enter", X = _normX, Y = _normY, Name = _exitEdge.ToString() });
        // The trigger down is swallowed. Other held keys have already reached A and need
        // local ups before capture; injection runs on the backend worker, never in a hook.
        foreach (var held in _physicalKeys.Values)
        {
            if (!fromShortcut || held.Key != _switchShortcut?.Key)
                QueueLocalRelease(new() { Type = "key", Key = held.Key, Scan = held.Scan, Flags = held.Extended ? 1 : 0, Down = false });
            if (!_consumedKeys.Contains(held.Key)) SendKey(held, true);
        }
        foreach (var button in _physicalButtons)
        {
            // Native button injection includes a pointer position. Keep this asynchronous
            // local release at the parked anchor instead of warping A back to its old edge.
            QueueLocalRelease(new() { Type = "button", Button = button, X = Normalize(_anchor.X, bounds.X, bounds.Width), Y = Normalize(_anchor.Y, bounds.Y, bounds.Height), Down = false });
            SendButton(button, true);
        }
        NotifyRemote(true);
    }

    private void MoveController(PhysicalInput input)
    {
        double dx = input.Relative ? input.DeltaX : input.X - _anchor.X;
        double dy = input.Relative ? input.DeltaY : input.Y - _anchor.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || (dx == 0 && dy == 0)) return;
        _remoteX += dx * _mouseSpeed / 100d;
        _remoteY += dy * _mouseSpeed / 100d;
        bool returning = _exitEdge switch
        {
            ScreenEdge.Right => _remoteX <= 0 && dx < 0,
            ScreenEdge.Left => _remoteX >= _remoteWidth - 1 && dx > 0,
            ScreenEdge.Top => _remoteY >= _remoteHeight - 1 && dy > 0,
            _ => _remoteY <= 0 && dy < 0,
        };
        _remoteX = Math.Clamp(_remoteX, 0, _remoteWidth - 1d);
        _remoteY = Math.Clamp(_remoteY, 0, _remoteHeight - 1d);
        UpdateNormalized();
        if (returning) { ReleaseCore(true); return; }
        var packet = new Packet { Type = "move", X = _normX, Y = _normY };
        long now = Stopwatch.GetTimestamp();
        if (_lastMoveAt == 0 || Stopwatch.GetElapsedTime(_lastMoveAt, now).TotalMilliseconds >= 8)
        {
            _pendingMove = null;
            _lastMoveAt = now;
            _sender?.Invoke(packet);
        }
        else _pendingMove = packet;
    }

    private void PaceMove()
    {
        lock (_gate)
        {
            if (_disposed || !_controller || !_remote || !_allowed) { _pendingMove = null; return; }
            try { FlushMove(); }
            catch (Exception error) { _allowed = false; ReleaseCore(false); ReportError(error.Message); }
        }
    }
    private void FlushMove()
    {
        var pending = _pendingMove;
        _pendingMove = null;
        if (pending is null) return;
        _lastMoveAt = Stopwatch.GetTimestamp();
        _sender?.Invoke(pending);
    }
    private void SendCore(Packet packet) { FlushMove(); _sender?.Invoke(packet); }

    private void RouteControllerKey(PhysicalInput key, bool repeat)
    {
        if (_composer.Mode == TypingMode.Physical) { _composer.Reset(); SendKey(key, key.Down); return; }
        if (!key.Down)
        {
            if (_sentKeys.ContainsKey(key.Key)) SendKey(key, false);
            ReleaseUnneededShift();
            return;
        }
        if (key.Key == 0x14) { if (!repeat) _capsLock = !_capsLock; return; }
        if (IsShift(key.Key))
        {
            if (HasShortcutModifier() || _sentKeys.Keys.Any(k => !IsModifier(k)) || _sentButtons.Count != 0) SendKey(key, true);
            return;
        }
        if (IsShortcutModifier(key.Key) || HasShortcutModifier() || _sentKeys.ContainsKey(key.Key))
        {
            _composer.Reset(); SendPhysicalShifts(); SendKey(key, true); return;
        }
        if (key.Key == 0x08) { SendText(_composer.Backspace()); return; }
        if (TryTranslate(key, out var text))
        {
            if (text.Any(char.IsSurrogate)) { _composer.Reset(); SendText(new() { Type = "text", Text = text }); }
            else foreach (char c in text) SendText(_composer.Process(c));
            return;
        }
        _composer.Reset(); SendPhysicalShifts(); SendKey(key, true);
    }

    private bool TryTranslate(PhysicalInput key, out string text)
    {
        text = key.Text ?? "";
        if (text.Length > 0 && IsValidText(text))
        {
            if (_capsLock) text = string.Concat(text.Select(c => char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c));
            return true;
        }
        bool shift = HasRouted(0x10, 0xA0, 0xA1);
        if (key.Key is >= 0x41 and <= 0x5A) text = (shift ^ _capsLock ? (char)key.Key : char.ToLowerInvariant((char)key.Key)).ToString();
        else if (key.Key == 0x20 || (!shift && key.Key is >= 0x30 and <= 0x39)) text = ((char)key.Key).ToString();
        else text = "";
        return text.Length != 0;
    }
    private void SendText(Packet packet) { if (packet.Dx != 0 || !string.IsNullOrEmpty(packet.Text)) SendCore(packet); }
    private void SendKey(PhysicalInput key, bool down)
    {
        SendCore(new() { Type = "key", Key = key.Key, Scan = key.Scan, Flags = key.Extended ? 1 : 0, Down = down });
        if (down) _sentKeys[key.Key] = key; else _sentKeys.Remove(key.Key);
    }
    private void SendButton(string button, bool down)
    {
        SendCore(new() { Type = "button", Button = button, Down = down, X = _normX, Y = _normY });
        if (down) _sentButtons.Add(button); else _sentButtons.Remove(button);
    }
    private void SendPhysicalShifts()
    {
        foreach (var key in _physicalKeys.Values.Where(k => IsShift(k.Key) && !_consumedKeys.Contains(k.Key)))
            if (!_sentKeys.ContainsKey(key.Key)) SendKey(key, true);
    }
    private void ReleaseUnneededShift()
    {
        if (_composer.Mode == TypingMode.Physical || HasShortcutModifier() || _sentKeys.Keys.Any(k => !IsModifier(k)) || _sentButtons.Count != 0) return;
        foreach (var key in _sentKeys.Values.Where(k => IsShift(k.Key)).ToArray()) SendKey(key, false);
    }

    private void ReleaseCore(bool notify, bool queueCleanup = true)
    {
        _generation++;
        bool wasRemote = _remote;
        _remote = false;
        _composer.Reset();
        _pendingMove = null;
        _lastMoveAt = 0;
        Exception? sendError = null;
        try
        {
            if (_controller)
            {
                foreach (var key in _sentKeys.Values.ToArray())
                    if (notify) SendCore(new() { Type = "key", Key = key.Key, Scan = key.Scan, Flags = key.Extended ? 1 : 0, Down = false });
                foreach (string button in _sentButtons)
                    if (notify) SendCore(new() { Type = "button", Button = button, Down = false, X = _normX, Y = _normY });
            }
            if (wasRemote && notify) SendCore(new() { Type = "leave", X = _normX, Y = _normY, Name = _exitEdge.ToString(), Allow = !_controller });
        }
        catch (Exception error) { _sender = null; _allowed = false; sendError = error; }
        _sentKeys.Clear(); _sentButtons.Clear();
        try { _backend.SetCapture(false); }
        catch (Exception error) { sendError ??= error; }
        if (_started && wasRemote && _controller)
        {
            var bounds = _backend.Bounds;
            int x = Denormalize(_normX, bounds.X, bounds.Width), y = Denormalize(_normY, bounds.Y, bounds.Height);
            switch (_exitEdge)
            {
                case ScreenEdge.Right: x = bounds.X + bounds.Width - Math.Min(10, bounds.Width); break;
                case ScreenEdge.Left: x = bounds.X + Math.Min(9, bounds.Width - 1); break;
                case ScreenEdge.Top: y = bounds.Y + Math.Min(9, bounds.Height - 1); break;
                case ScreenEdge.Bottom: y = bounds.Y + bounds.Height - Math.Min(10, bounds.Height); break;
            }
            _lastLocal = ClampPoint(_returnPoint ?? new(x, y), bounds);
            try { _backend.Warp(_lastLocal); }
            catch (Exception error) { sendError ??= error; }
        }
        _returnPoint = null;
        if (queueCleanup) ObserveCleanup(QueueCleanup());
        if (wasRemote) NotifyRemote(false);
        if (sendError is not null) ReportError(sendError.Message);
    }

    // Local ups need the same FIFO native worker, but bypass receiver permission checks.
    private void QueueLocalRelease(Packet packet)
    {
        _nativeDirty = true;
        _nativeOutstanding++;
        if (!_injections.Writer.TryWrite(new(packet, _generation, null, LocalRelease: true))) _nativeOutstanding--;
    }

    private Task QueueCleanup()
    {
        if (_nativeOutstanding == 0 && !_nativeDirty) return Task.CompletedTask;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _nativeOutstanding++;
        if (!_injections.Writer.TryWrite(new(null, _generation, completion)))
        {
            _nativeOutstanding--;
            completion.TrySetResult();
        }
        return completion.Task;
    }
    private static void ObserveCleanup(Task cleanup)
    {
        _ = cleanup.ContinueWith(static failed => _ = failed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    private void UpdateNormalized()
    {
        _normX = (int)Math.Round(_remoteX * 65535 / (_remoteWidth - 1));
        _normY = (int)Math.Round(_remoteY * 65535 / (_remoteHeight - 1));
    }
    private bool IsAtExit(DesktopPoint p)
    {
        var b = _backend.Bounds;
        return _exitEdge switch { ScreenEdge.Right => p.X >= b.X + b.Width - 1, ScreenEdge.Left => p.X <= b.X, ScreenEdge.Top => p.Y <= b.Y, _ => p.Y >= b.Y + b.Height - 1 };
    }
    private static DesktopPoint ClampPoint(DesktopPoint p, DesktopBounds b) => new(Math.Clamp(p.X, b.X, b.X + Math.Max(0, b.Width - 1)), Math.Clamp(p.Y, b.Y, b.Y + Math.Max(0, b.Height - 1)));
    private static int Normalize(int value, int origin, int length) => (int)Math.Clamp(Math.Round((value - (double)origin) * 65535 / Math.Max(1, length - 1)), 0, 65535);
    private static int Denormalize(int value, int origin, int length) => origin + (int)Math.Round(Math.Clamp(value, 0, 65535) * Math.Max(0, length - 1d) / 65535);
    private static bool IsButton(string? button) => button is "left" or "right" or "middle" or "x1" or "x2";
    private static bool IsShift(int key) => key is 0x10 or 0xA0 or 0xA1;
    private static bool IsShortcutModifier(int key) => key is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    private static bool IsModifier(int key) => IsShift(key) || IsShortcutModifier(key);
    private bool HasPhysical(int generic, int left, int right) => _physicalKeys.ContainsKey(generic) || _physicalKeys.ContainsKey(left) || _physicalKeys.ContainsKey(right);
    private bool HasRouted(int generic, int left, int right) => new[] { generic, left, right }.Any(k => _physicalKeys.ContainsKey(k) && !_consumedKeys.Contains(k));
    private bool HasShortcutModifier() => _physicalKeys.Keys.Any(k => IsShortcutModifier(k) && !_consumedKeys.Contains(k));
    private ShortcutModifiers PhysicalModifiers() => (HasPhysical(0x11, 0xA2, 0xA3) ? ShortcutModifiers.Control : 0)
        | (HasPhysical(0x12, 0xA4, 0xA5) ? ShortcutModifiers.Alt : 0)
        | (HasPhysical(0x10, 0xA0, 0xA1) ? ShortcutModifiers.Shift : 0)
        | (_physicalKeys.ContainsKey(0x5B) || _physicalKeys.ContainsKey(0x5C) ? ShortcutModifiers.Windows : 0);
    private bool ReceivedControlAlt() => (_receivedKeys.Contains(0x11) || _receivedKeys.Contains(0xA2) || _receivedKeys.Contains(0xA3))
        && (_receivedKeys.Contains(0x12) || _receivedKeys.Contains(0xA4) || _receivedKeys.Contains(0xA5));
    private void NotifyRemote(bool value) => RemoteChanged?.Invoke(value);
    private void ReportError(string message) { LastError = message; Error?.Invoke(message); }
    private void OnBackendError(string message)
    {
        lock (_gate) { if (_disposed) return; _allowed = false; ReleaseCore(true); ReportError(message); }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            ReleaseCore(true);
            _disposed = true;
            _started = false;
            _sender = null;
            _backend.Input -= OnPhysicalInput;
            _backend.Error -= OnBackendError;
            _moveTimer.Dispose();
            _injections.Writer.TryComplete();
        }
        await _worker.ConfigureAwait(false);
        _backend.Dispose();
    }

    private sealed record InjectionWork(Packet? Packet, int Generation, TaskCompletionSource? Completion,
        bool LocalRelease = false, CancellationToken CancellationToken = default);
}
