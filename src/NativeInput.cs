using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
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
/// B plans input on the hook owner and submits ordered native arrays on one separate worker.
/// A retains synchronous local entry/return warps. Input injection follows Windows integrity
/// boundaries; UAC/secure desktops cannot be controlled.
/// </remarks>
public sealed class InputRouter : IDisposable
{
    public Action<Packet>? Send { get => Volatile.Read(ref _send); set => Volatile.Write(ref _send, value); }
    public Action<bool>? RemoteControlChanged { get; set; }
    public Action? EmergencyReleased { get; set; }
    public string? LastError { get; private set; }
    /// <summary>Payload-free timing counters; safe to read while the input owner or SendInput is blocked.</summary>
    public InputDiagnosticsSnapshot GetDiagnosticsSnapshot()
    {
        int pendingOutgoing;
        lock (_outboundLock) pendingOutgoing = _outbound.Count;
        return _diagnostics.Snapshot(_started && _nativeInputEnabled, pendingOutgoing);
    }
    /// <summary>Lifetime counters only; no typed key, character, word or document content is retained.</summary>
    public TypingDiagnosticsSnapshot GetTypingDiagnosticsSnapshot() => new(
        _composer.Mode, _controller, _remote,
        Interlocked.Read(ref _outgoingTextPackets), Interlocked.Read(ref _outgoingNonAsciiCodeUnits),
        Interlocked.Read(ref _outgoingBackspaces), Interlocked.Read(ref _printableTranslationSuccesses),
        Interlocked.Read(ref _printableTranslationFailures), Interlocked.Read(ref _printableFallbacks),
        Interlocked.Read(ref _physicalModeKeyDowns), Interlocked.Read(ref _shortcutBypassKeyDowns), Interlocked.Read(ref _incomingTextPackets),
        Interlocked.Read(ref _incomingNonAsciiCodeUnits));
    /// <summary>Hook maintenance counts only; does not retain keys, text or document content.</summary>
    public KeyboardHookDiagnosticsSnapshot GetKeyboardHookDiagnosticsSnapshot() =>
        _keyboardHooks?.Snapshot(_keyboardRefreshRequested) ?? new(0, 0, 0, -1, _keyboardRefreshRequested);
    /// <summary>Suppression counts only; no key, text, coordinate or application content is retained.</summary>
    public ExclusiveInputDiagnosticsSnapshot GetExclusiveInputDiagnosticsSnapshot() => new(
        _controller && _remote, Interlocked.Read(ref _foreignKeyboardSuppressed), Interlocked.Read(ref _foreignMouseSuppressed),
        Interlocked.Read(ref _quarantinedKeyEvents), Interlocked.Read(ref _quarantinedButtonEvents));
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
    /// <summary>Scales A's remote pointer movement without changing Windows' global mouse settings.</summary>
    public int MouseSpeedPercent
    {
        get => Volatile.Read(ref _mouseSpeedPercent);
        set => OnOwner(() => Volatile.Write(ref _mouseSpeedPercent, Math.Clamp(value, 50, 200)));
    }
    /// <summary>A physical shortcut on controller A toggles control of its approved peer. Null disables it.</summary>
    public SwitchShortcut? SwitchShortcut
    {
        get => Volatile.Read(ref _switchShortcut);
        set => OnOwner(() =>
        {
            Volatile.Write(ref _switchShortcut, value is { IsValid: true } ? value : null);
            _switchEntrySuspended = false;
        });
    }
    /// <summary>Returns control to A and suspends hotkey/edge entry until SwitchShortcut is restored after capture UI.</summary>
    public Task SuspendSwitchShortcutAsync() => OnOwnerAsync(() =>
    {
        _switchEntrySuspended = true;
        Volatile.Write(ref _switchShortcut, null);
        ReleaseCore(true);
    }, CancellationToken.None);
    public TypingMode TypingMode
    {
        get => _composer.Mode;
        set => OnOwner(() => { if (_composer.Mode != value) { ReleaseHeldInputs(); _textKeys.Clear(); _composer.Mode = value; } });
    }
    /// <summary>Returns to local control and resets composition even when the selected mode is unchanged.</summary>
    public Task ResetTypingAsync(TypingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return OnOwnerAsync(() =>
        {
            ReleaseCore(true);
            _composer.Mode = mode;
            _composer.Reset();
            _textKeys.Clear();
            _textCapsLock = (GetKeyState(0x14) & 1) != 0;
        }, CancellationToken.None);
    }

    /// <summary>
    /// Explicit recovery after input capture was interrupted. Returns control to A and
    /// abandons old physical-chord tracking; the user must release held keys/buttons.
    /// Ordinary reset/return paths deliberately preserve genuinely held physical keys.
    /// </summary>
    public Task RecoverTypingAsync(TypingMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return OnOwnerAsync(() =>
        {
            ReleaseCore(true);
            _composer.Mode = mode; _composer.Reset(); _textKeys.Clear();
            _physicalKeys.Clear(); _physicalButtons.Clear();
            // Already-consumed switch components must stay owned until their actual up;
            // abandoning tracking must not leak an auto-repeat or trailing up locally.
            _emergencyChord = false;
            _textCapsLock = (GetKeyState(0x14) & 1) != 0;
            _lastKeyboardActivityAt = KeyboardClockMilliseconds();
            _keyboardRefreshRequested = true;
            LastError = null;
            // Explicit user recovery also repairs a hook Windows silently removed, so
            // F9 can work again while local. Automatic maintenance remains remote-A only.
            if (_started && _controller && _keyboardHooks is not null)
                RenewKeyboardHook(_lastKeyboardActivityAt);
        }, CancellationToken.None);
    }

    private volatile bool _controller, _allowed, _remote, _blockLocal, _disposed, _started;
    private Action<Packet>? _send;
    private IntPtr _mouseHook;
    // Native-only diagnostic mirror for inert preview tests. Lease owns lifetime.
    private IntPtr _keyboardHook;
    private readonly HookProc _mouseProc;
    private KeyboardHookLease? _keyboardHooks;
    private volatile bool _keyboardRefreshRequested;
    private long _lastKeyboardActivityAt, _lastKeyboardRenewalAttemptAt;
    private bool _keyboardRenewalAttempted;
    private bool _keyboardRenewalFailed;
    private const int KeyboardRenewalIntervalMilliseconds = 10_000;
    private const int KeyboardRenewalQuietMilliseconds = 150;
    private const int KeyboardRenewalRetryMilliseconds = 1_000;
    private SynchronizationContext? _context;
    private int _ownerThread;
    private readonly object _lifecycle = new();
    private readonly InputDiagnostics _diagnostics = new();
    private readonly SemaphoreSlim _remoteApply = new(1, 1);
    private readonly object _nativeLock = new();
    private readonly Queue<NativeWork> _nativeWork = new();
    private readonly AutoResetEvent _nativeReady = new(false);
    private Thread? _nativeWorker;
    private bool _nativeStopping;
    private int _nativeOutstanding, _inputGeneration;
    private List<NativeInput>? _nativeCollector;
    private List<Packet>? _appliedCollector;
    private Thread? _worker;
    private ApplicationContext? _application;
    private TaskCompletionSource? _startup;
    private int _disposeRequested;
    private bool _nativeInputEnabled;
    private int _mouseSpeedPercent = 100;
    private SwitchShortcut? _switchShortcut;
    private bool _switchEntrySuspended;
    private long _outgoingTextPackets, _outgoingNonAsciiCodeUnits, _outgoingBackspaces,
        _printableTranslationSuccesses, _printableTranslationFailures, _printableFallbacks,
        _physicalModeKeyDowns, _shortcutBypassKeyDowns, _incomingTextPackets, _incomingNonAsciiCodeUnits;
    private System.Windows.Forms.Timer? _timer;
    private Rectangle _bounds;
    private Point _lastLocalPoint, _anchor;
    private Point? _shortcutReturnPoint;
    private double _remoteX, _remoteY;
    private int _lastNormX, _lastNormY;
    private long _edgeSince, _boundsReadAt;
    private bool _edgeCandidate;
    private readonly Dictionary<int, KeyStroke> _physicalKeys = [];
    // Shortcut components remain physically tracked for matching/emergency release, but are
    // absent from remote typing until their real key ups (including key-down autorepeat).
    private readonly HashSet<int> _switchConsumedKeys = [];
    // Inputs owned by B remain swallowed after returning to A until their actual physical up.
    // Otherwise autorepeat or an orphan up can trigger an unrelated action in A's application.
    private readonly HashSet<int> _returnConsumedKeys = [];
    private readonly HashSet<string> _returnConsumedButtons = [];
    private long _foreignKeyboardSuppressed, _foreignMouseSuppressed, _quarantinedKeyEvents, _quarantinedButtonEvents;
    private readonly Dictionary<int, KeyStroke> _receivedKeys = [];
    private readonly Dictionary<int, KeyStroke> _sentKeys = [];
    private readonly VietnameseComposer _composer = new();
    private readonly HashSet<int> _textKeys = [];
    private readonly byte[] _translationState = new byte[256];
    private bool _textCapsLock;
    private readonly HashSet<string> _physicalButtons = [];
    private readonly HashSet<string> _receivedButtons = [];
    private readonly HashSet<string> _sentButtons = [];
    private readonly object _outboundLock = new();
    private readonly LinkedList<(Packet Packet, Action<Packet> Sender, long QueuedAt)> _outbound = new();
    private bool _sending;
    private readonly PointerMovePacer _movePacer = new();
    private Action<Packet>? _pacedMoveSender;
    private System.Threading.Timer? _moveTimer;
    private bool _moveTimerArmed;
    private int _moveTickPosted;
    private static readonly double PointerMillisecondsPerTick = 1000d / Stopwatch.Frequency;
    private bool _emergencyChord;
    internal const uint InjectionTag = 0x4C424447; // "LBDG", also checked in addition to injected flags.
    private readonly record struct KeyStroke(int Key, int Scan, bool Extended);

    public InputRouter()
    {
        _mouseProc = MouseHook;
    }

    public void Start() => StartDispatcher(true);

    // Uses the real, independent message loop without touching physical input or installing hooks.
    internal void StartForTesting() => StartDispatcher(false);
    internal Action<Packet>? PacketAppliedForTesting { get; set; }
    // Measures native work requested by real routing paths while native input is disabled.
    // It cannot enable hooks or injection and is never called on a live native dispatcher.
    internal Action<int>? NativeInputRequestedForTesting { get; set; }
    internal Action<Array>? NativeInputBatchForTesting { get; set; }
    internal Action? NativeWorkQueuedForTesting { get; set; }
    // The override cannot affect a native-enabled router and never installs hooks or injects input.
    internal Func<int, int, (int Length, string Text)>? PrintableTranslationForTesting { get; set; }
    internal KeyboardHookTestApi? KeyboardHooksForTesting { get; set; }
    internal Func<int, IntPtr, IntPtr, IntPtr>? KeyboardHookNextForTesting { get; set; }
    internal Func<int, IntPtr, IntPtr, IntPtr>? MouseHookNextForTesting { get; set; }
    internal Task<bool> RouteKeyboardHookForTestingAsync(int key, bool down, uint flags = 0, ulong extraInfo = 0, int scan = 0, int code = 0)
    {
        EnsureHookSimulation();
        return SimulateHookAsync(new KeyboardHookData { VkCode = (uint)key, ScanCode = (uint)scan, Flags = flags, ExtraInfo = (UIntPtr)extraInfo },
            code, down ? 0x100 : 0x101, KeyboardHook);
    }
    internal Task<bool> RouteMouseHookForTestingAsync(int message, int x, int y, uint mouseData = 0, uint flags = 0, ulong extraInfo = 0, int code = 0)
    {
        EnsureHookSimulation();
        return SimulateHookAsync(new MouseHookData { Point = new NativePoint { X = x, Y = y }, MouseData = mouseData,
            Flags = flags, ExtraInfo = (UIntPtr)extraInfo }, code, message, MouseHook);
    }
    private void EnsureHookSimulation()
    {
        if (!_started || _nativeInputEnabled || _disposed)
            throw new InvalidOperationException("Hook callback simulations require a started native-disabled router.");
    }
    private async Task<bool> SimulateHookAsync<T>(T payload, int code, int message, HookProc callback) where T : struct
    {
        bool consumed = false;
        await OnOwnerAsync(() =>
        {
            var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            try
            {
                Marshal.StructureToPtr(payload, pointer, false);
                consumed = callback(code, new IntPtr(message), pointer) != IntPtr.Zero;
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }, CancellationToken.None).ConfigureAwait(false);
        return consumed;
    }
    internal Task MaintainKeyboardHookForTestingAsync()
    {
        if (_nativeInputEnabled) throw new InvalidOperationException("Hook-maintenance tests require a native-disabled router.");
        return OnOwnerAsync(MaintainKeyboardHook, CancellationToken.None);
    }
    internal Task InvokeForTestingAsync(Action action) => OnOwnerAsync(action, CancellationToken.None);
    internal async Task<bool> RoutePhysicalKeyForTestingAsync(int key, bool down, int scan = 0, bool extended = false)
    {
        if (_nativeInputEnabled) throw new InvalidOperationException("Physical-key tests require a native-disabled router.");
        bool consumed = false;
        await OnOwnerAsync(() => consumed = ProcessPhysicalKey(new(key, scan, extended), down), CancellationToken.None).ConfigureAwait(false);
        return consumed;
    }

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
            if (!_disposed && _started) ReleaseCore(false);
            StopHooks();
            RequestNativeStop();
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
        // A one-shot timer wakes the independent input owner; it never uses the window's UI
        // timer or changes Windows' global timer resolution. Physical hooks remain immediate;
        // the final idle-position wake-up remains subject to Windows timer scheduling.
        _moveTimer = new System.Threading.Timer(_ =>
        {
            if (_disposed || Interlocked.Exchange(ref _moveTickPosted, 1) != 0) return;
            Post(() =>
            {
                Interlocked.Exchange(ref _moveTickPosted, 0);
                _moveTimerArmed = false;
                FlushPacedMove(false);
                ArmMoveTimer();
            });
        }, null, Timeout.Infinite, Timeout.Infinite);
        if (!nativeInputEnabled)
        {
            if (KeyboardHooksForTesting is { } api)
            {
                _keyboardHooks = new(callback => api.Install(callback.Invoke), api.Uninstall,
                    KeyboardHook, (code, wParam, lParam) => api.Next(code, wParam, lParam), api.ClockMilliseconds);
                if (!_keyboardHooks.TryRenew()) throw new InvalidOperationException("The fake keyboard hook could not be installed.");
                _lastKeyboardActivityAt = _lastKeyboardRenewalAttemptAt = api.ClockMilliseconds();
                _keyboardRenewalAttempted = true;
            }
            _started = true; return;
        }
        // Remember modifiers already held when the visible application enables the hooks.
        foreach (var vk in new[] { 0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA5, 0x5B, 0x5C })
            if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                _physicalKeys[vk] = new(vk, (int)(MapVirtualKey((uint)vk, 0) & 0xff), vk is 0xA3 or 0xA5 or 0x5B or 0x5C);
        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(14, _mouseProc, module, 0);
        if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể theo dõi chuột Windows.");
        _keyboardHooks = new(callback => SetWindowsKeyboardHookEx(13, callback, module, 0), UninstallKeyboardHook,
            KeyboardHook, (code, wParam, lParam) => CallNextHookEx(IntPtr.Zero, code, wParam, lParam), PointerClockMilliseconds);
        if (!_keyboardHooks.TryRenew())
        {
            var error = Marshal.GetLastWin32Error();
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
            throw new Win32Exception(error, "Không thể theo dõi bàn phím Windows.");
        }
        _keyboardHook = _keyboardHooks.ActiveHandle;
        _lastKeyboardActivityAt = _lastKeyboardRenewalAttemptAt = KeyboardClockMilliseconds();
        _keyboardRenewalAttempted = true;
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

    /// <summary>Queues receiver input for owner planning followed by ordered native-worker injection.</summary>
    public void ApplyRemote(Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        _ = ObserveRemoteApplicationAsync(ApplyRemoteAsync(packet));
    }

    /// <summary>Completes after native injection and owner finalization; neither the UI nor hook owner waits on B's SendInput.</summary>
    public Task ApplyRemoteAsync(Packet packet, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return ApplyRemotePacketsAsync([packet], cancellationToken);
    }

    /// <summary>Plans up to 32 ordered packets together, submits one native array and completes after owner finalization.</summary>
    public Task ApplyRemoteBatchAsync(IReadOnlyList<Packet> packets, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(packets);
        if (packets.Count > 32) throw new ArgumentOutOfRangeException(nameof(packets), "Input batches are limited to 32 packets.");
        return ApplyRemotePacketsAsync(packets, cancellationToken);
    }

    private async Task ObserveRemoteApplicationAsync(Task application)
    {
        // Fire-and-forget API still observes failures; the ordered application path has
        // already reported native errors and released its state on the owner thread.
        try { await application.ConfigureAwait(false); }
        catch { }
    }

    private Task ApplyRemotePacketsAsync(IReadOnlyList<Packet> packets, CancellationToken cancellationToken)
    {
        var operation = ApplyRemotePacketsCoreAsync(packets, cancellationToken);
        // Cancellation releases the caller promptly. The serialized pipeline still awaits
        // accepted native work; cleanup keeps its FIFO position before the next native batch.
        return cancellationToken.CanBeCanceled ? operation.WaitAsync(cancellationToken) : operation;
    }

    private async Task ApplyRemotePacketsCoreAsync(IReadOnlyList<Packet> packets, CancellationToken cancellationToken)
    {
        await _remoteApply.WaitAsync(cancellationToken).ConfigureAwait(false);
        CancellationTokenRegistration cancellationRegistration = default;
        int operationComplete = 0;
        try
        {
            Task<bool>? native = null;
            List<Packet>? applied = null;
            Exception? planningError = null;
            int generation = 0;
            bool finalized = false;
            bool stale = false;
            await OnOwnerAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_controller)
                {
                    // A's entry/return warp must complete synchronously before its hook
                    // suppresses the edge event. This change only moves B's native work.
                    foreach (var packet in packets)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ApplyPacketCore(packet);
                    }
                    finalized = true;
                    return;
                }
                var priorReleases = BuildHeldReleaseInputs();
                var inputs = new List<NativeInput>();
                applied = new List<Packet>(packets.Count);
                _nativeCollector = inputs;
                _appliedCollector = applied;
                try
                {
                    foreach (var packet in packets)
                    {
                        if (_disposed) break;
                        if (cancellationToken.IsCancellationRequested)
                        {
                            ReleaseCore(false);
                            cancellationToken.ThrowIfCancellationRequested();
                        }
                        ArgumentNullException.ThrowIfNull(packet);
                        ApplyPacketCore(packet);
                        if (LastError is not null) break;
                    }
                }
                catch (Exception error)
                {
                    planningError = error;
                    ReleaseCore(true);
                }
                finally { _nativeCollector = null; _appliedCollector = null; }
                generation = _inputGeneration;
                // Invalid/canceled batches have not reached Windows yet. Discard their
                // prefix and release only keys/buttons held before this planning operation.
                var actual = planningError is null ? inputs.ToArray() : priorReleases;
                var recovery = planningError is null
                    ? priorReleases.Concat(BuildPotentialReleases(actual)).ToArray() : priorReleases;
                native = QueueNativeWork(actual, recovery, generation, cleanup: planningError is not null, staleRecovery: priorReleases);
                if (cancellationToken.CanBeCanceled)
                    cancellationRegistration = cancellationToken.Register(() => Post(() =>
                    {
                        if (Volatile.Read(ref operationComplete) == 0 && generation == _inputGeneration)
                            ReleaseCore(false);
                    }));
                if (!_nativeInputEnabled) NativeWorkQueuedForTesting?.Invoke();
                if (native.IsCompletedSuccessfully && planningError is null)
                {
                    if (cancellationToken.IsCancellationRequested && generation == _inputGeneration) ReleaseCore(false);
                    stale = !CompleteAppliedPackets(applied, generation, native.Result);
                    if (cancellationToken.IsCancellationRequested && generation == _inputGeneration)
                    {
                        ReleaseCore(false);
                        stale = true;
                    }
                    finalized = true;
                }
            }, CancellationToken.None, measureInputQueue: true).ConfigureAwait(false);
            if (finalized)
            {
                if (stale) throw new OperationCanceledException("Phiên điều khiển hoặc quyền nhập đã thay đổi.");
                return;
            }
            Exception? nativeError = null;
            bool executed = true;
            if (native is not null)
            {
                try { executed = await native.ConfigureAwait(false); }
                catch (Exception error) { nativeError = error; }
            }
            await OnOwnerAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested && generation == _inputGeneration)
                    ReleaseCore(false);
                if (nativeError is not null && generation == _inputGeneration)
                {
                    LastError = "Windows từ chối thao tác nhập: " + nativeError.Message;
                    ReleaseCore(true);
                }
                if (planningError is null && nativeError is null && applied is not null)
                    stale = !CompleteAppliedPackets(applied, generation, executed);
                if (cancellationToken.IsCancellationRequested && generation == _inputGeneration)
                {
                    ReleaseCore(false);
                    stale = true;
                }
            }, CancellationToken.None).ConfigureAwait(false);
            if (_disposed) stale = true;
            if (planningError is not null) ExceptionDispatchInfo.Capture(planningError).Throw();
            if (nativeError is not null) ExceptionDispatchInfo.Capture(nativeError).Throw();
            if (stale) throw new OperationCanceledException("Phiên điều khiển hoặc quyền nhập đã thay đổi.");
        }
        finally
        {
            Volatile.Write(ref operationComplete, 1);
            cancellationRegistration.Dispose();
            _remoteApply.Release();
        }
    }

    private bool CompleteAppliedPackets(List<Packet> packets, int generation, bool executed)
    {
        if (!executed || generation != _inputGeneration) return false;
        if (!_nativeInputEnabled)
            foreach (var packet in packets)
            {
                if (generation != _inputGeneration) return false;
                PacketAppliedForTesting?.Invoke(packet);
            }
        return true;
    }

    private void ApplyPacketCore(Packet packet)
    {
        try
        {
            ApplyCore(packet);
            if (LastError is not null) ReleaseCore(true);
            if (!_nativeInputEnabled)
            {
                if (_appliedCollector is not null) _appliedCollector.Add(packet);
                else PacketAppliedForTesting?.Invoke(packet);
            }
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
            Interlocked.Increment(ref _inputGeneration);
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
                InjectPointerAction(packet);
                if (packet.Down) _receivedButtons.Add(button); else _receivedButtons.Remove(button);
                break;
            }
            case "wheel":
                InjectPointerAction(packet);
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

    private long KeyboardClockMilliseconds() => !_nativeInputEnabled && KeyboardHooksForTesting is { } api
        ? api.ClockMilliseconds() : PointerClockMilliseconds();

    private void MaintainKeyboardHook()
    {
        if (_disposed || !_started || _keyboardHooks is null) return;
        _keyboardHooks.CollectCompletedCallbacks();
        if (!_controller || !_remote) return;
        long now = KeyboardClockMilliseconds();
        if (_keyboardRenewalFailed && now - _lastKeyboardRenewalAttemptAt < KeyboardRenewalRetryMilliseconds) return;
        if (!_keyboardRefreshRequested && _keyboardRenewalAttempted &&
            now - _lastKeyboardRenewalAttemptAt < KeyboardRenewalIntervalMilliseconds) return;
        if ((!_keyboardRefreshRequested && now - _lastKeyboardActivityAt < KeyboardRenewalQuietMilliseconds) ||
            _physicalKeys.Count != 0 || _physicalButtons.Count != 0 || _sentKeys.Count != 0 ||
            _sentButtons.Count != 0 || _textKeys.Count != 0 || _switchConsumedKeys.Count != 0) return;
        RenewKeyboardHook(now);
    }

    private void RenewKeyboardHook(long now)
    {
        if (_keyboardHooks is null) return;
        _lastKeyboardRenewalAttemptAt = now;
        _keyboardRenewalAttempted = true;
        // Replacement preserves all routing/composer/native FIFO state. If installation
        // fails, the previous active registration stays installed and is retried later.
        _keyboardRenewalFailed = !_keyboardHooks.TryRenew();
        if (_nativeInputEnabled) _keyboardHook = _keyboardHooks.ActiveHandle;
        _keyboardRefreshRequested = _keyboardRenewalFailed;
    }

    private static bool UninstallKeyboardHook(IntPtr handle)
    {
        if (UnhookWindowsHookEx(handle)) return true;
        int error = Marshal.GetLastWin32Error();
        // Windows can silently remove a timed-out low-level hook. Its stale handle
        // is already absent, so it must not permanently block later replacements.
        return error is 1404 or 1431; // ERROR_INVALID_HOOK_HANDLE / ERROR_HOOK_NOT_INSTALLED
    }

    private void TimerTick(object? sender, EventArgs args)
    {
        if (_disposed) return;
        MaintainKeyboardHook();
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
        if (!_controller || !_allowed || _remote || _switchEntrySuspended || !_edgeCandidate) return;
        var point = Cursor.Position;
        if (!IsAtExit(point)) { _edgeCandidate = false; return; }
        if (now - _edgeSince >= Math.Clamp(EdgeDwellMilliseconds, 0, 2000)) EnterRemote(point);
    }

    private void EnterRemote(Point point) => EnterRemoteCore(point, false);

    private void EnterRemoteCore(Point point, bool fromShortcut)
    {
        if (_remote || !_allowed || _switchEntrySuspended || Send is null || !_started || Volatile.Read(ref _nativeOutstanding) != 0) return;
        _edgeCandidate = false;
        _shortcutReturnPoint = fromShortcut ? point : null;
        _remote = true;
        _composer.Reset(); _textKeys.Clear(); _textCapsLock = (GetKeyState(0x14) & 1) != 0;
        LastError = null;
        int width = Math.Clamp(RemoteWidth, 2, 65536), height = Math.Clamp(RemoteHeight, 2, 65536);
        var nx = Normalize(point.X, _bounds.Left, _bounds.Width);
        var ny = Normalize(point.Y, _bounds.Top, _bounds.Height);
        _remoteX = nx * (width - 1d) / 65535d;
        _remoteY = ny * (height - 1d) / 65535d;
        if (fromShortcut)
        {
            _remoteX = (width - 1d) / 2;
            _remoteY = (height - 1d) / 2;
        }
        else switch (ExitEdge)
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
            if (_returnConsumedKeys.Contains(key.Key)) continue;
            // The triggering down is consumed before Windows sees it. Held modifiers and
            // unrelated keys already reached A and still need their local release.
            if (!fromShortcut || key.Key != _switchShortcut?.Key) InjectKey(key, false);
            if (!_switchConsumedKeys.Contains(key.Key)) SendKey(key, true);
        }
        foreach (var button in _physicalButtons)
        {
            if (_returnConsumedButtons.Contains(button)) continue;
            InjectButton(button, false);
            SendButton(button, true);
        }
        var screen = Screen.FromPoint(point).Bounds;
        _anchor = new(screen.Left + screen.Width / 2, screen.Top + screen.Height / 2);
        WarpLocal(_anchor);
        NotifyRemoteChanged(true);
        _keyboardRefreshRequested = true;
        MaintainKeyboardHook();
    }

    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _disposed) return NextMouseHook(code, message, data);
        bool capturedEvent = _controller && _remote;
        long callbackAt = _diagnostics.BeginHookCallback();
        try
        {
            var item = Marshal.PtrToStructure<MouseHookData>(data);
            _diagnostics.RecordHookEventArrival(item.Flags, 1, item.ExtraInfo.ToUInt64(), item.Time);
            // Our releases/warps must reach Windows. Other injected events must not
            // bypass exclusive routing and click/scroll the application left open on A.
            if (item.ExtraInfo.ToUInt64() == InjectionTag) return NextMouseHook(code, message, data);
            if ((item.Flags & 1) != 0)
            {
                if (_controller && _remote) { Interlocked.Increment(ref _foreignMouseSuppressed); return new IntPtr(1); }
                return NextMouseHook(code, message, data);
            }
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
                _lastKeyboardActivityAt = KeyboardClockMilliseconds();
                bool quarantined = _returnConsumedButtons.Contains(button);
                capturedEvent |= quarantined;
                if (down) _physicalButtons.Add(button); else _physicalButtons.Remove(button);
                if (!down) _returnConsumedButtons.Remove(button);
                if (quarantined) { Interlocked.Increment(ref _quarantinedButtonEvents); return new IntPtr(1); }
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
            if (_remote && (_controller || _blockLocal)) return new IntPtr(1);
            return NextMouseHook(code, message, data);
        }
        catch (Exception error)
        {
            capturedEvent |= _controller && _remote;
            RecoverHookError(error);
            return capturedEvent ? new IntPtr(1) : NextMouseHook(code, message, data);
        }
        finally { _diagnostics.EndHookCallback(callbackAt); }
    }

    private IntPtr NextMouseHook(int code, IntPtr message, IntPtr data) => !_nativeInputEnabled
        ? MouseHookNextForTesting?.Invoke(code, message, data) ?? IntPtr.Zero
        : CallNextHookEx(_mouseHook, code, message, data);

    // Plans a local move without native calls; an immediate crossing runs in the same hook event.
    internal bool TrackExitEdge(Point point, long now)
    {
        var dx = point.X - _lastLocalPoint.X;
        var dy = point.Y - _lastLocalPoint.Y;
        _lastLocalPoint = point;
        if (!_controller || !_allowed || _remote || _switchEntrySuspended) { _edgeCandidate = false; return false; }
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
        double speed = _mouseSpeedPercent / 100d;
        _remoteX += dx * speed;
        _remoteY += dy * speed;
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
        QueueControllerMove(new() { Type = "move", X = _lastNormX, Y = _lastNormY });
        // MouseHook consumes this physical move (returns nonzero), so Windows never applies
        // it to A's cursor. It remains at the entry anchor for the next relative delta.
        // Re-injecting that unchanged anchor here used to add another global mouse-hook
        // round trip for every hardware report and stall clicks/keys on this same thread.
        // Only entering/returning needs a local cursor warp.
    }

    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || _disposed) return NextKeyboardHook(code, message, data);
        bool capturedEvent = _controller && _remote;
        long callbackAt = _diagnostics.BeginHookCallback();
        try
        {
            var item = Marshal.PtrToStructure<KeyboardHookData>(data);
            _diagnostics.RecordHookEventArrival(item.Flags, 0x10, item.ExtraInfo.ToUInt64(), item.Time);
            // Preserve our tagged local cleanup, while swallowing foreign synthetic
            // input on A. Never feed IME Unicode replacements back through physical Telex.
            if (item.ExtraInfo.ToUInt64() == InjectionTag) return NextKeyboardHook(code, message, data);
            if ((item.Flags & 0x10) != 0)
            {
                if (_controller && _remote) { Interlocked.Increment(ref _foreignKeyboardSuppressed); return new IntPtr(1); }
                return NextKeyboardHook(code, message, data);
            }
            var msg = message.ToInt32();
            if (msg is not (0x0100 or 0x0101 or 0x0104 or 0x0105))
                return NextKeyboardHook(code, message, data);
            bool down = msg is 0x0100 or 0x0104;
            var key = new KeyStroke((int)item.VkCode, (int)(item.ScanCode & 0xff), (item.Flags & 1) != 0);
            capturedEvent |= _returnConsumedKeys.Contains(key.Key) || _switchConsumedKeys.Contains(key.Key);
            bool consumed = ProcessPhysicalKey(key, down);
            // An explicit entry/recovery request must precede the first quickly typed
            // letter. Waiting for a posted timer can let a newer IME consume that letter.
            // The lease retains this old callback until it has fully returned.
            if (!down && _keyboardRefreshRequested) MaintainKeyboardHook();
            if (consumed) return new IntPtr(1);
            return NextKeyboardHook(code, message, data);
        }
        catch (Exception error)
        {
            capturedEvent |= _controller && _remote;
            RecoverHookError(error);
            return capturedEvent ? new IntPtr(1) : NextKeyboardHook(code, message, data);
        }
        finally { _diagnostics.EndHookCallback(callbackAt); }
    }

    private IntPtr NextKeyboardHook(int code, IntPtr message, IntPtr data) => !_nativeInputEnabled && KeyboardHookNextForTesting is { } next
        ? next(code, message, data)
        : _keyboardHooks?.PassThrough(code, message, data) ?? (!_nativeInputEnabled ? IntPtr.Zero : CallNextHookEx(IntPtr.Zero, code, message, data));

    private bool HasPhysical(int generic, int left, int right) =>
        _physicalKeys.ContainsKey(generic) || _physicalKeys.ContainsKey(left) || _physicalKeys.ContainsKey(right);

    private bool ProcessPhysicalKey(KeyStroke key, bool down)
    {
        _lastKeyboardActivityAt = KeyboardClockMilliseconds();
        bool quarantined = _returnConsumedKeys.Contains(key.Key);
        bool switchConsumed = _switchConsumedKeys.Contains(key.Key);
        bool repeat = _physicalKeys.ContainsKey(key.Key);
        if (down) _physicalKeys[key.Key] = key; else _physicalKeys.Remove(key.Key);
        if (!down) { _returnConsumedKeys.Remove(key.Key); _switchConsumedKeys.Remove(key.Key); }
        bool control = HasPhysical(0x11, 0xA2, 0xA3), alt = HasPhysical(0x12, 0xA4, 0xA5);
        if (key.Key == 0x7B && down && control && alt && !_emergencyChord)
        {
            bool exclusiveEscape = _controller && (_remote || quarantined ||
                _returnConsumedKeys.Any(IsControlOrAlt));
            EmergencyReleaseCore();
            if (exclusiveEscape) _returnConsumedKeys.Add(key.Key);
            return exclusiveEscape;
        }
        if (_emergencyChord)
        {
            // Emergency detection remains physical, but a chord owned by B must not
            // leak its trailing ups/repeats into A after it has returned control.
            if (!control && !alt && !_physicalKeys.ContainsKey(0x7B)) _emergencyChord = false;
            if (quarantined) Interlocked.Increment(ref _quarantinedKeyEvents);
            return quarantined || switchConsumed;
        }
        if (switchConsumed)
        {
            return true;
        }
        if (quarantined) { Interlocked.Increment(ref _quarantinedKeyEvents); return true; }
        var shortcut = _switchShortcut;
        if (down && !repeat && _controller && _allowed && _started && !_disposed && Send is not null
            && shortcut is not null && shortcut.Key == key.Key && shortcut.Modifiers == PhysicalModifiers())
        {
            // Never consume a shortcut that cannot currently enter its approved receiver.
            // Return can always proceed, while entry waits for any former receiver cleanup.
            if (_remote || Volatile.Read(ref _nativeOutstanding) == 0)
            {
                _switchConsumedKeys.Add(key.Key);
                foreach (int held in _physicalKeys.Keys.Where(IsModifier)) _switchConsumedKeys.Add(held);
                if (_remote) ReleaseCore(true);
                else EnterRemoteCore(Cursor.Position, true);
                return true;
            }
        }
        if (_controller && _remote) { RouteControllerKey(key, down); return true; }
        return !_controller && _remote && _blockLocal;
    }

    private ShortcutModifiers PhysicalModifiers()
    {
        ShortcutModifiers modifiers = ShortcutModifiers.None;
        bool Active(int key) => _physicalKeys.ContainsKey(key) && !_returnConsumedKeys.Contains(key);
        if (Active(0x11) || Active(0xA2) || Active(0xA3)) modifiers |= ShortcutModifiers.Control;
        if (Active(0x12) || Active(0xA4) || Active(0xA5)) modifiers |= ShortcutModifiers.Alt;
        if (Active(0x10) || Active(0xA0) || Active(0xA1)) modifiers |= ShortcutModifiers.Shift;
        if (Active(0x5B) || Active(0x5C)) modifiers |= ShortcutModifiers.Windows;
        return modifiers;
    }

    private bool HasRoutedPhysical(int generic, int left, int right) =>
        IsRoutedPhysical(generic) || IsRoutedPhysical(left) || IsRoutedPhysical(right);

    private bool IsRoutedPhysical(int key) => _physicalKeys.ContainsKey(key) && !_switchConsumedKeys.Contains(key) && !_returnConsumedKeys.Contains(key);

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
    private static bool IsControlOrAlt(int key) => key is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5;
    private static bool IsShortcutModifier(int key) => key is 0x11 or 0x12 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0x5B or 0x5C;
    private static bool IsModifier(int key) => IsShift(key) || IsShortcutModifier(key);
    private bool HasShortcutModifier() => HasRoutedPhysical(0x11, 0xA2, 0xA3) || HasRoutedPhysical(0x12, 0xA4, 0xA5)
        || IsRoutedPhysical(0x5B) || IsRoutedPhysical(0x5C);

    private void RouteControllerKey(KeyStroke key, bool down)
    {
        if (_composer.Mode == TypingMode.Physical)
        {
            if (down) Interlocked.Increment(ref _physicalModeKeyDowns);
            _composer.Reset(); SendKey(key, down); return;
        }
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
            Interlocked.Increment(ref _shortcutBypassKeyDowns);
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
        foreach (var shift in _physicalKeys.Values.Where(k => IsShift(k.Key) && !_switchConsumedKeys.Contains(k.Key) && !_returnConsumedKeys.Contains(k.Key)))
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
        foreach (int held in _physicalKeys.Keys)
            if (held is >= 0 and < 256 && !_switchConsumedKeys.Contains(held) && !_returnConsumedKeys.Contains(held)) _translationState[held] |= 0x80;
        _translationState[0x10] = HasRoutedPhysical(0x10, 0xA0, 0xA1) ? (byte)0x80 : (byte)0;
        _translationState[0x14] = _textCapsLock ? (byte)1 : (byte)0;
        int length;
        string translated;
        if (!_nativeInputEnabled && PrintableTranslationForTesting is { } translation)
            (length, translated) = translation(key.Key, key.Scan);
        else
        {
            var layoutThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            var output = new StringBuilder(16);
            // Flag4 prevents mutating the local foreground application's dead-key state.
            length = ToUnicodeEx((uint)key.Key, (uint)key.Scan, _translationState, output, output.Capacity, 4, GetKeyboardLayout(layoutThread));
            translated = output.ToString();
        }
        if (length > 0 && length <= translated.Length)
        {
            text = translated[..length];
            if (text.All(c => !char.IsControl(c)))
            {
                Interlocked.Increment(ref _printableTranslationSuccesses);
                return true;
            }
        }
        Interlocked.Increment(ref _printableTranslationFailures);
        text = "";
        // A foreground IME can return no printable character for a Latin key. Preserve the
        // built-in Telex/VNI chain instead of turning each failed letter into a raw key and
        // resetting its word. A negative result is a dead key: never replace that with Latin.
        // Valid layout translations and layout-dependent OEM/shifted digit keys stay intact.
        if (length < 0 || _composer.Mode == TypingMode.Physical || HasShortcutModifier()) return false;
        bool shift = HasRoutedPhysical(0x10, 0xA0, 0xA1);
        if (key.Key is >= 0x41 and <= 0x5A)
            text = (shift ^ _textCapsLock ? (char)key.Key : char.ToLowerInvariant((char)key.Key)).ToString();
        else if (key.Key == 0x20 || (!shift && key.Key is >= 0x30 and <= 0x39))
            text = ((char)key.Key).ToString();
        if (text.Length == 0) return false;
        Interlocked.Increment(ref _printableFallbacks);
        return true;
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
        Interlocked.Increment(ref _inputGeneration);
        bool wasRemote = _remote;
        if (wasRemote && _controller)
        {
            _returnConsumedKeys.UnionWith(_physicalKeys.Keys);
            _returnConsumedButtons.UnionWith(_physicalButtons);
        }
        _remote = false;
        _composer.Reset(); _textKeys.Clear();
        _blockLocal = false;
        _edgeCandidate = false;
        ReleaseHeldInputs();
        if (wasRemote && notify)
            Queue(new() { Type = "leave", X = _lastNormX, Y = _lastNormY, Name = ExitEdge.ToString(), Allow = !_controller });
        // A return, disconnect or emergency ends this motion cadence. A later crossing on
        // the same authenticated connection starts with an immediate first move again.
        _movePacer.Clear();
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
            var target = ClampToScreen(_shortcutReturnPoint ?? new Point(x, y));
            WarpLocal(target);
            _lastLocalPoint = target;
        }
        _shortcutReturnPoint = null;
        if (wasRemote) NotifyRemoteChanged(false);
    }

    private void ReleaseHeldInputs()
    {
        if (_controller || _nativeCollector is not null || !_started)
        {
            ReleaseHeldInputsCore();
            return;
        }
        var inputs = new List<NativeInput>();
        _nativeCollector = inputs;
        try { ReleaseHeldInputsCore(); }
        finally { _nativeCollector = null; }
        var releases = inputs.ToArray();
        _ = ObserveCleanupAsync(QueueNativeWork(releases, releases, _inputGeneration, cleanup: true), _inputGeneration);
    }

    private void ReleaseHeldInputsCore()
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

    private static NativeInput AbsoluteInput(int x, int y) => new()
    {
        Type = 0,
        Union = new InputUnion { Mouse = new MouseInput { Dx = x, Dy = y, Flags = 0x0001 | 0x8000 | 0x4000, ExtraInfo = InjectionTag } },
    };

    private void InjectAbsolute(int x, int y) => Inject(AbsoluteInput(x, y));

    private void InjectKey(KeyStroke key, bool down) => Inject(KeyInput(key, down));

    private static NativeInput KeyInput(KeyStroke key, bool down)
    {
        // Pause uses an E1 sequence, which KEYEVENTF_EXTENDEDKEY cannot represent; use its VK.
        bool scan = key.Scan != 0 && key.Key != 0x13;
        return new NativeInput
        {
            Type = 1,
            Union = new InputUnion { Keyboard = new KeyboardInput
            {
                Vk = scan ? (ushort)0 : (ushort)key.Key,
                Scan = scan ? (ushort)key.Scan : (ushort)0,
                Flags = (scan ? 8u : 0u) | (key.Extended ? 1u : 0u) | (down ? 0u : 2u),
                ExtraInfo = InjectionTag,
            } },
        };
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
        Interlocked.Increment(ref _incomingTextPackets);
        Interlocked.Add(ref _incomingNonAsciiCodeUnits, CountNonAscii(packet.Text));
        var modifiers = _receivedKeys.Values.Where(k => IsModifier(k.Key)).ToArray();
        // Unicode and edit Backspaces must not turn into Ctrl/Alt/Shift shortcuts.
        foreach (var modifier in modifiers) InjectKey(modifier, false);
        InjectBatch(inputs);
        foreach (var modifier in modifiers) InjectKey(modifier, true);
    }

    private void InjectButton(string button, bool down)
        => Inject(MouseInputEvent(ButtonFlag(button, down), button == "x1" ? 1 : button == "x2" ? 2 : 0));

    private static uint ButtonFlag(string button, bool down)
    {
        return button switch
        {
            "left" => down ? 0x0002u : 0x0004u,
            "right" => down ? 0x0008u : 0x0010u,
            "middle" => down ? 0x0020u : 0x0040u,
            _ => down ? 0x0080u : 0x0100u,
        };
    }

    private static NativeInput MouseInputEvent(uint flags, int data) => new()
    {
        Type = 0,
        Union = new InputUnion { Mouse = new MouseInput { MouseData = unchecked((uint)data), Flags = flags, ExtraInfo = InjectionTag } },
    };

    // Windows inserts one SendInput array atomically and in order. The hit-test position and
    // wheel/button action must stay together, including both wheel axes, without 2-3 syscalls.
    private static NativeInput[] BuildPointerInputs(Packet packet)
    {
        var position = AbsoluteInput(Math.Clamp(packet.X, 0, 65535), Math.Clamp(packet.Y, 0, 65535));
        if (packet.Type == "button" && IsButton(packet.Button))
            return [position, MouseInputEvent(ButtonFlag(packet.Button!, packet.Down), packet.Button == "x1" ? 1 : packet.Button == "x2" ? 2 : 0)];
        if (packet.Type != "wheel") throw new ArgumentException("A wheel or button packet is required.", nameof(packet));
        if (packet.Dy == 0 && packet.Dx == 0) return [position];
        // MouseData is a signed 32-bit wheel delta. Do not truncate high-resolution scroll
        // or a valid accumulated wheel delta back to the hook's original signed 16-bit range.
        if (packet.Dy == 0) return [position, MouseInputEvent(0x1000, packet.Dx)];
        if (packet.Dx == 0) return [position, MouseInputEvent(0x0800, packet.Dy)];
        return [position, MouseInputEvent(0x0800, packet.Dy), MouseInputEvent(0x1000, packet.Dx)];
    }

    private void InjectPointerAction(Packet packet)
    {
        _lastNormX = Math.Clamp(packet.X, 0, 65535);
        _lastNormY = Math.Clamp(packet.Y, 0, 65535);
        InjectBatch(BuildPointerInputs(packet));
    }

    private void Inject(NativeInput input)
        => InjectBatch([input]);

    private void InjectBatch(NativeInput[] inputs)
    {
        if (!_started || inputs.Length == 0) return;
        if (_nativeCollector is not null)
        {
            _nativeCollector.AddRange(inputs);
            return;
        }
        if (!_controller)
        {
            int generation = _inputGeneration;
            _ = ObserveCleanupAsync(QueueNativeWork(inputs, BuildPotentialReleases(inputs), generation, cleanup: true), generation);
            return;
        }
        try { ExecuteNativeInputs(inputs); }
        catch (Exception error) when (_nativeInputEnabled)
        {
            // Preserve A's existing synchronous failure reporting at its entry/return warp.
            LastError = error.Message;
        }
    }

    private void ExecuteNativeInputs(NativeInput[] inputs)
    {
        if (inputs.Length == 0) return;
        var testObserver = _nativeInputEnabled ? null : NativeInputRequestedForTesting;
        var batchObserver = _nativeInputEnabled ? null : NativeInputBatchForTesting;
        if (!_nativeInputEnabled && testObserver is null && batchObserver is null) return;
        long startedAt = _diagnostics.BeginNativeInput(inputs.Length);
        bool failed = true;
        try
        {
            if (!_nativeInputEnabled)
            {
                batchObserver?.Invoke(inputs);
                testObserver?.Invoke(inputs.Length);
                failed = false;
                return;
            }
            failed = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeInput>()) != inputs.Length;
            if (failed) throw new InvalidOperationException("Windows từ chối thao tác nhập. Hãy chạy hai ứng dụng với cùng mức quyền; UAC/desktop bảo mật không hỗ trợ.");
        }
        finally { _diagnostics.EndNativeInput(startedAt, failed); }
    }

    private NativeInput[] BuildHeldReleaseInputs() => _receivedKeys.Values.Select(key => KeyInput(key, false))
        .Concat(_receivedButtons.Select(button => MouseInputEvent(ButtonFlag(button, false), button == "x1" ? 1 : button == "x2" ? 2 : 0))).ToArray();

    // A combined native call can fail after any accepted prefix. Planned held-state may
    // already be balanced by later ups in the array, so cover every potential down as well.
    private static NativeInput[] BuildPotentialReleases(NativeInput[] inputs)
    {
        var releases = new List<NativeInput>();
        var keys = new HashSet<(ushort Vk, ushort Scan, uint Flags)>();
        var buttons = new HashSet<(uint Flags, uint Data)>();
        foreach (var input in inputs)
        {
            if (input.Type == 1)
            {
                var keyboard = input.Union.Keyboard;
                if ((keyboard.Flags & 2) != 0 || !keys.Add((keyboard.Vk, keyboard.Scan, keyboard.Flags))) continue;
                keyboard.Flags |= 2;
                releases.Add(new NativeInput { Type = 1, Union = new InputUnion { Keyboard = keyboard } });
            }
            else
            {
                var mouse = input.Union.Mouse;
                uint flags = (mouse.Flags & 2) != 0 ? 4u : (mouse.Flags & 8) != 0 ? 16u :
                    (mouse.Flags & 32) != 0 ? 64u : (mouse.Flags & 128) != 0 ? 256u : 0;
                if (flags == 0 || !buttons.Add((flags, mouse.MouseData))) continue;
                releases.Add(MouseInputEvent(flags, unchecked((int)mouse.MouseData)));
            }
        }
        return releases.ToArray();
    }

    private Task<bool> QueueNativeWork(NativeInput[] inputs, NativeInput[] recovery, int generation, bool cleanup,
        NativeInput[]? staleRecovery = null)
    {
        if (inputs.Length == 0 || (!_nativeInputEnabled && NativeInputRequestedForTesting is null && NativeInputBatchForTesting is null))
            return Task.FromResult(true);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_nativeLock)
        {
            if (_nativeStopping) return Task.FromException<bool>(new ObjectDisposedException(nameof(InputRouter)));
            if (!cleanup && _nativeWork.Count >= 32)
                return Task.FromException<bool>(new IOException("Hàng đợi nhập Windows đã đầy."));
            _nativeWork.Enqueue(new(inputs, recovery, staleRecovery ?? [], generation, cleanup, completion));
            Interlocked.Increment(ref _nativeOutstanding);
            if (_nativeWorker is null)
            {
                _nativeWorker = new Thread(RunNativeWorker) { IsBackground = true, Name = "LanBridge native input" };
                _nativeWorker.SetApartmentState(ApartmentState.MTA);
                _nativeWorker.Start();
            }
        }
        _nativeReady.Set();
        return completion.Task;
    }

    private void RunNativeWorker()
    {
        while (true)
        {
            NativeWork? work;
            lock (_nativeLock)
            {
                work = _nativeWork.Count == 0 ? null : _nativeWork.Dequeue();
                if (work is null && _nativeStopping) return;
            }
            if (work is null) { _nativeReady.WaitOne(); continue; }
            bool executed = false;
            Exception? failure = null;
            try
            {
                // Revoke/disconnect may occur while an older native call is stalled. Do
                // not start its queued ordinary inputs afterwards; cleanup barriers survive.
                if (work.Cleanup || work.Generation == Volatile.Read(ref _inputGeneration))
                {
                    executed = true;
                    ExecuteNativeInputs(work.Inputs);
                }
                else
                {
                    // Planning may have removed a previously injected key because this
                    // now-stale batch contained its up/leave. Preserve those prior releases
                    // even though none of this batch's new downs should reach Windows.
                    ExecuteNativeInputs(work.StaleRecovery);
                }
            }
            catch (Exception error)
            {
                failure = error;
                // Recovery executes on this same worker before completion or the next job,
                // even if the owner has changed role or shut down meanwhile.
                try { ExecuteNativeInputs(work.Recovery); }
                catch { }
            }
            finally { Interlocked.Decrement(ref _nativeOutstanding); }
            if (failure is not null) work.Completion.TrySetException(failure);
            else work.Completion.TrySetResult(executed);
        }
    }

    private async Task ObserveCleanupAsync(Task<bool> cleanup, int generation)
    {
        try { await cleanup.ConfigureAwait(false); }
        catch (Exception error)
        {
            Post(() =>
            {
                if (generation == _inputGeneration) LastError = "Windows từ chối nhả thao tác nhập: " + error.Message;
            });
        }
    }

    private sealed record NativeWork(NativeInput[] Inputs, NativeInput[] Recovery, NativeInput[] StaleRecovery, int Generation,
        bool Cleanup, TaskCompletionSource<bool> Completion);

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
        // Critical actions are never delayed by the motion cadence. Flush the exact last
        // position before shortcuts, text and session boundaries. A click/wheel already
        // carries that position and can absorb the pending move for its same session.
        if (packet.Type != "move") FlushPacedMove(true,
            ReferenceEquals(sender, _pacedMoveSender) && (packet.Type is "button" or "wheel") ? packet : null);
        if (sender is null) return;
        if (packet.Type == "text")
        {
            Interlocked.Increment(ref _outgoingTextPackets);
            Interlocked.Add(ref _outgoingNonAsciiCodeUnits, CountNonAscii(packet.Text));
            Interlocked.Add(ref _outgoingBackspaces, packet.Dx);
        }
        QueueOutbound(packet, sender);
    }

    private static int CountNonAscii(string? text) => text?.Count(character => character > 0x7f) ?? 0;

    private void QueueControllerMove(Packet packet)
    {
        var sender = Send;
        if (sender is null) return;
        if (!ReferenceEquals(sender, _pacedMoveSender)) _movePacer.Clear();
        _pacedMoveSender = sender;
        var ready = _movePacer.Offer(packet, PointerClockMilliseconds());
        if (ready is not null) QueueOutbound(ready, sender);
        ArmMoveTimer();
    }

    private void FlushPacedMove(bool force, Packet? followingPointerAction = null)
    {
        var ready = _movePacer.Flush(PointerClockMilliseconds(), force);
        if (ready is null || _pacedMoveSender is null) return;
        if (followingPointerAction is not null && ready.X == followingPointerAction.X && ready.Y == followingPointerAction.Y) return;
        QueueOutbound(ready, _pacedMoveSender);
    }

    private void ArmMoveTimer()
    {
        if (!_movePacer.HasPending || _moveTimerArmed || _moveTimer is null || _disposed) return;
        _moveTimerArmed = true;
        _moveTimer.Change(_movePacer.DelayMilliseconds(PointerClockMilliseconds()), Timeout.Infinite);
    }

    // GetTickCount64 may advance only every 10-16 ms. The motion cadence must see elapsed
    // hardware-report time at sub-millisecond resolution even while that coarse clock stalls.
    internal static long PointerClockMilliseconds() => (long)(Stopwatch.GetTimestamp() * PointerMillisecondsPerTick);

    private void QueueOutbound(Packet packet, Action<Packet> sender)
    {
        bool startWorker;
        long queuedAt = Stopwatch.GetTimestamp();
        lock (_outboundLock)
        {
            // A slow sender used to accumulate every 1000 Hz mouse report before a wheel
            // could run. Only replace adjacent, unsent absolute moves for this same session.
            // Every wheel tick, key, text edit, drag boundary and enter/leave stays ordered.
            if (packet.Type == "move" && _outbound.Last is { } last &&
                last.Value.Packet.Type == "move" && ReferenceEquals(last.Value.Sender, sender))
                last.Value = (packet, sender, queuedAt);
            else _outbound.AddLast((packet, sender, queuedAt));
            startWorker = !_sending;
            _sending = true;
        }
        if (startWorker) ThreadPool.QueueUserWorkItem(_ => DrainOutbound());
    }

    private void DrainOutbound()
    {
        while (true)
        {
            (Packet Packet, Action<Packet> Sender, long QueuedAt) item;
            lock (_outboundLock)
            {
                if (_outbound.First is not { } first)
                {
                    _sending = false;
                    return;
                }
                item = first.Value;
                _outbound.RemoveFirst();
            }
            _diagnostics.RecordOutgoingWait(item.QueuedAt);
            try { item.Sender(item.Packet); }
            catch (Exception error)
            {
                Post(() =>
                {
                    if (!ReferenceEquals(Send, item.Sender)) return;
                    LastError = "Không gửi được thao tác: " + error.Message;
                    ReleaseCore(false);
                });
            }
        }
    }

    private void NotifyRemoteChanged(bool value) => Post(() => RemoteControlChanged?.Invoke(value));
    private void OnOwner(Action action, bool measureInputQueue = false)
    {
        if (_disposed) return;
        if (_context is not null && Environment.CurrentManagedThreadId != _ownerThread) Post(action, measureInputQueue);
        else
        {
            if (measureInputQueue && _started) _diagnostics.RecordInlineDispatch();
            action();
        }
    }
    private void Post(Action action, bool measureInputQueue = false)
    {
        var context = _context;
        if (context is null) { if (!_disposed) action(); return; }
        long queuedAt = measureInputQueue && _started ? _diagnostics.QueueDispatcher() : 0;
        try
        {
            context.Post(_ =>
            {
                if (queuedAt != 0) _diagnostics.StartDispatcher(queuedAt);
                if (!_disposed) action();
            }, null);
        }
        catch (InvalidAsynchronousStateException) when (_disposed || Volatile.Read(ref _disposeRequested) != 0)
        {
            if (queuedAt != 0) _diagnostics.CancelDispatcher();
        }
        catch
        {
            if (queuedAt != 0) _diagnostics.CancelDispatcher();
            throw;
        }
    }
    private Task OnOwnerAsync(Action action, CancellationToken cancellationToken, bool measureInputQueue = false)
    {
        if (cancellationToken.IsCancellationRequested) return Task.FromCanceled(cancellationToken);
        if (_disposed) return Task.CompletedTask;
        var context = _context;
        if (context is null || Environment.CurrentManagedThreadId == _ownerThread)
        {
            if (measureInputQueue && _started) _diagnostics.RecordInlineDispatch();
            try { action(); return Task.CompletedTask; }
            catch (Exception error) { return Task.FromException(error); }
        }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long queuedAt = measureInputQueue && _started ? _diagnostics.QueueDispatcher() : 0;
        try
        {
            context.Post(_ =>
            {
                if (queuedAt != 0) _diagnostics.StartDispatcher(queuedAt);
                if (_disposed || cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(); return; }
                try { action(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            }, null);
        }
        catch (InvalidAsynchronousStateException)
        {
            if (queuedAt != 0) _diagnostics.CancelDispatcher();
            completion.TrySetCanceled();
        }
        catch
        {
            if (queuedAt != 0) _diagnostics.CancelDispatcher();
            throw;
        }
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
            _nativeWorker?.Join(TimeSpan.FromSeconds(2));
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
        _diagnostics.StopDispatcher();
        RequestNativeStop();
        _application?.ExitThread();
    }

    private void RequestNativeStop()
    {
        lock (_nativeLock) _nativeStopping = true;
        _nativeReady.Set();
    }

    private void StopHooks()
    {
        _moveTimer?.Dispose();
        _moveTimer = null;
        _moveTimerArmed = false;
        _movePacer.Clear();
        _timer?.Stop();
        _timer?.Dispose();
        _timer = null;
        if (_mouseHook != IntPtr.Zero) UnhookWindowsHookEx(_mouseHook);
        _keyboardHooks?.Dispose();
        _keyboardHooks = null;
        _mouseHook = IntPtr.Zero;
        _keyboardHook = IntPtr.Zero;
        GC.KeepAlive(_mouseProc);
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
    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)] private static extern IntPtr SetWindowsKeyboardHookEx(int idHook, KeyboardHookCallback callback, IntPtr module, uint threadId);
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

/// <summary>Lifetime suppression counts only; no key, button, text or application content is retained.</summary>
public sealed record ExclusiveInputDiagnosticsSnapshot(bool IsExclusiveController, long ForeignKeyboardSuppressed,
    long ForeignMouseSuppressed, long QuarantinedKeyEvents, long QuarantinedButtonEvents);

/// <summary>Payload-free lifetime typing counters. Queue acceptance does not confirm that a target rendered text.</summary>
public sealed record TypingDiagnosticsSnapshot(
    TypingMode EffectiveMode,
    bool IsController,
    bool IsRemote,
    long OutgoingTextPackets,
    long OutgoingNonAsciiCodeUnits,
    long OutgoingBackspaces,
    long PrintableTranslationSuccesses,
    long PrintableTranslationFailures,
    long PrintableFallbacks,
    long PhysicalModeKeyDowns,
    long ShortcutBypassKeyDowns,
    long IncomingTextPackets,
    long IncomingNonAsciiCodeUnits);

/// <summary>Aggregate timings only; contains no packets, keys, text, clipboard data or coordinates.</summary>
public sealed record InputDiagnosticsSnapshot(
    InputTimingSnapshot DispatcherQueue,
    InputTimingSnapshot NativeInjection,
    InputTimingSnapshot OutgoingQueue,
    InputTimingSnapshot HookEventAge,
    InputTimingSnapshot OwnInjectedEventAge,
    InputTimingSnapshot HookCallback,
    int PendingDispatcherWork,
    int PendingOutgoingWork,
    long NativeInputs,
    long NativeFailures,
    bool NativeInjectionInProgress,
    double NativeInjectionInProgressMilliseconds,
    bool HookCallbackInProgress,
    double HookCallbackInProgressMilliseconds,
    bool NativeInputEnabled);

public readonly record struct InputTimingSnapshot(
    long Count,
    double LastMilliseconds,
    double AverageMilliseconds,
    double MaxMilliseconds,
    long SlowCount,
    double LastSampleAgeMilliseconds);

// Locks protect only numeric counter updates/copies, never input work, callbacks or dispatch
// waits. UI snapshots therefore remain available during a stalled external global hook.
internal sealed class InputDiagnostics
{
    private readonly object _sync = new();
    private readonly TimingSamples _dispatcher = new(), _native = new(), _outgoing = new(),
        _hookAge = new(), _ownInjectedAge = new(), _hook = new();
    private int _pendingDispatcher, _nativeActive, _hookActive;
    private long _nativeInputs, _nativeFailures, _nativeActiveSince, _hookActiveSince;

    internal long QueueDispatcher()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync) _pendingDispatcher++;
        return now;
    }

    internal void StartDispatcher(long queuedAt)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _pendingDispatcher = Math.Max(0, _pendingDispatcher - 1);
            _dispatcher.Record(Math.Max(0, now - queuedAt), now);
        }
    }

    internal void RecordInlineDispatch()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync) _dispatcher.Record(0, now);
    }

    internal void CancelDispatcher() { lock (_sync) _pendingDispatcher = Math.Max(0, _pendingDispatcher - 1); }
    internal void StopDispatcher() { lock (_sync) _pendingDispatcher = 0; }

    internal long BeginNativeInput(int count)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            if (_nativeActive++ == 0) _nativeActiveSince = now;
            _nativeInputs += count;
        }
        return now;
    }

    internal void EndNativeInput(long startedAt, bool failed)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _native.Record(Math.Max(0, now - startedAt), now);
            if (failed) _nativeFailures++;
            if (--_nativeActive == 0) _nativeActiveSince = 0;
        }
    }

    internal void RecordOutgoingWait(long queuedAt)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync) _outgoing.Record(Math.Max(0, now - queuedAt), now);
    }

    internal long BeginHookCallback()
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync) if (_hookActive++ == 0) _hookActiveSince = now;
        return now;
    }

    internal void EndHookCallback(long startedAt)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync)
        {
            _hook.Record(Math.Max(0, now - startedAt), now);
            if (--_hookActive == 0) _hookActiveSince = 0;
        }
    }

    internal void RecordHookEventArrival(uint flags, uint injectedMask, ulong extraInfo, uint eventTime)
    {
        var target = SelectHookAge(flags, injectedMask, extraInfo);
        if (target is not null) RecordNativeEventAge(target, eventTime, GetTickCount());
    }

    internal void RecordHookEventArrival(uint flags, uint injectedMask, ulong extraInfo, uint eventTime, uint nowMilliseconds)
    {
        var target = SelectHookAge(flags, injectedMask, extraInfo);
        if (target is not null) RecordNativeEventAge(target, eventTime, nowMilliseconds);
    }

    private TimingSamples? SelectHookAge(uint flags, uint injectedMask, ulong extraInfo)
    {
        // Own injection must be observed before the hook's existing early-return filter.
        // Other software's injected events cannot establish our Windows input-queue delay.
        if (extraInfo == InputRouter.InjectionTag) return _ownInjectedAge;
        return (flags & injectedMask) == 0 ? _hookAge : null;
    }

    // Native hook timestamps are DWORD uptime milliseconds, unlike QPC durations. Subtract
    // modulo 2^32 to survive the 49.7-day wrap; discard invalid/future timestamp differences.
    internal void RecordHookEventAge(uint eventTime, uint nowMilliseconds)
        => RecordNativeEventAge(_hookAge, eventTime, nowMilliseconds);

    private void RecordNativeEventAge(TimingSamples target, uint eventTime, uint nowMilliseconds)
    {
        uint age = unchecked(nowMilliseconds - eventTime);
        if (age > int.MaxValue) return;
        long now = Stopwatch.GetTimestamp();
        lock (_sync) target.Record(age * (Stopwatch.Frequency / 1000d), now);
    }

    internal InputDiagnosticsSnapshot Snapshot(bool nativeInputEnabled, int pendingOutgoing = 0)
    {
        long now = Stopwatch.GetTimestamp();
        lock (_sync) return new(_dispatcher.Snapshot(now), _native.Snapshot(now), _outgoing.Snapshot(now),
            _hookAge.Snapshot(now), _ownInjectedAge.Snapshot(now), _hook.Snapshot(now), _pendingDispatcher, pendingOutgoing,
            _nativeInputs, _nativeFailures, _nativeActive != 0,
            _nativeActive == 0 ? 0 : Milliseconds(Math.Max(0, now - _nativeActiveSince)),
            _hookActive != 0, _hookActive == 0 ? 0 : Milliseconds(Math.Max(0, now - _hookActiveSince)), nativeInputEnabled);
    }

    private static double Milliseconds(long ticks) => ticks * (1000d / Stopwatch.Frequency);

    // Match MSLLHOOKSTRUCT/KBDLLHOOKSTRUCT's native uptime clock explicitly, including its
    // sleep accounting and DWORD wrap; do not assume managed runtime clock implementations.
    [DllImport("kernel32.dll")] private static extern uint GetTickCount();

    private sealed class TimingSamples
    {
        private long _count, _slow, _at;
        private double _last, _max;
        private double _totalTicks;
        internal void Record(double ticks, long now)
        {
            _count++; _last = ticks; _max = Math.Max(_max, ticks); _totalTicks += ticks; _at = now;
            if (ticks * (1000d / Stopwatch.Frequency) >= 50) _slow++;
        }
        internal InputTimingSnapshot Snapshot(long now) => new(_count, _last * (1000d / Stopwatch.Frequency),
            _count == 0 ? 0 : _totalTicks * (1000d / Stopwatch.Frequency) / _count,
            _max * (1000d / Stopwatch.Frequency), _slow, _count == 0 ? -1 : Milliseconds(Math.Max(0, now - _at)));
    }
}

/// <summary>Pure latest-position pacing; critical input can force a flush with no wait.</summary>
internal sealed class PointerMovePacer
{
    internal const int IntervalMilliseconds = 8;
    private Packet? _pending;
    private bool _sent;
    private long _lastSentAt;
    internal bool HasPending => _pending is not null;

    internal Packet? Offer(Packet move, long now)
    {
        _pending = move;
        return Flush(now, false);
    }

    internal Packet? Flush(long now, bool force)
    {
        if (_pending is null || (!force && _sent && now - _lastSentAt < IntervalMilliseconds)) return null;
        var ready = _pending;
        _pending = null;
        _lastSentAt = now;
        _sent = true;
        return ready;
    }

    internal int DelayMilliseconds(long now) => _sent
        ? (int)Math.Clamp(IntervalMilliseconds - (now - _lastSentAt), 1, IntervalMilliseconds)
        : 1;

    internal void Clear() { _pending = null; _sent = false; }
}
