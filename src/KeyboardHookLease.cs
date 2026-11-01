namespace LanBridge;

internal delegate IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam);

/// <summary>
/// Owner-thread keyboard-hook replacement. A replacement is installed before the old
/// registration is removed. Only one registration may process input; overlapping and
/// retired callbacks pass through. No keyboard/composer/session state is changed here.
/// </summary>
internal sealed class KeyboardHookLease : IDisposable
{
    private sealed class Registration
    {
        internal IntPtr Handle;
        internal KeyboardHookCallback Callback = null!;
        internal int Calls;
        internal bool Installed;
    }

    // A failed native unhook can outlive the owning router. Keep the inactive callback
    // rooted until process exit rather than let native code call a collected delegate.
    private static readonly object AbandonedGate = new();
    private static readonly List<Registration> Abandoned = [];
    private readonly Func<KeyboardHookCallback, IntPtr> _install;
    private readonly Func<IntPtr, bool> _uninstall;
    private readonly KeyboardHookCallback _process, _next;
    private readonly Func<long> _clock;
    private readonly List<Registration> _retired = [];
    private Registration? _active;
    private bool _disposed;
    private bool _renewing, _collecting;
    private long _successes, _failures, _lastRenewedAt;
    private int _pendingRetired;
    internal IntPtr ActiveHandle => _active?.Handle ?? IntPtr.Zero;

    internal KeyboardHookLease(Func<KeyboardHookCallback, IntPtr> install, Func<IntPtr, bool> uninstall,
        KeyboardHookCallback process, KeyboardHookCallback next, Func<long> clock)
    {
        _install = install; _uninstall = uninstall; _process = process; _next = next; _clock = clock;
    }

    internal IntPtr PassThrough(int code, IntPtr wParam, IntPtr lParam) => _next(code, wParam, lParam);
    internal void CollectCompletedCallbacks() => CollectRetired(retryUnhook: false);

    internal bool TryRenew()
    {
        if (_disposed || _renewing) return false;
        _renewing = true;
        try
        {
            CollectRetired(retryUnhook: true);
            // At most one old installed registration is retained. A failed unhook must
            // recover before another native hook is added, keeping resource growth bounded.
            if (_retired.Any(registration => registration.Installed)) return Failed();
            var replacement = new Registration();
            replacement.Callback = (code, wParam, lParam) => Dispatch(replacement, code, wParam, lParam);
            replacement.Handle = _install(replacement.Callback);
            if (replacement.Handle == IntPtr.Zero) return Failed();
            replacement.Installed = true;
            if (_disposed)
            {
                _retired.Add(replacement); TryUnhook(replacement); CollectRetired(retryUnhook: false);
                RootFailedRemovals(); return false;
            }
            var previous = _active;
            _active = replacement;
            Interlocked.Exchange(ref _lastRenewedAt, _clock());
            Interlocked.Increment(ref _successes);
            if (previous is not null)
            {
                _retired.Add(previous);
                TryUnhook(previous);
            }
            // Even when no callback has entered yet, keep the just-unhooked prior
            // generation until a later owner operation rather than retire it here.
            Volatile.Write(ref _pendingRetired, _retired.Count);
            return true;
        }
        finally { _renewing = false; }
    }

    private bool Failed() { Interlocked.Increment(ref _failures); return false; }

    private IntPtr Dispatch(Registration registration, int code, IntPtr wParam, IntPtr lParam)
    {
        registration.Calls++;
        try
        {
            return !_disposed && ReferenceEquals(_active, registration)
                ? _process(code, wParam, lParam) : _next(code, wParam, lParam);
        }
        finally
        {
            registration.Calls--;
            // Do not collect this registration from inside its own native callback.
            // A later owner maintenance pass can retire it after the callback returned.
            GC.KeepAlive(registration.Callback);
        }
    }

    private void TryUnhook(Registration registration)
    {
        if (registration.Installed && _uninstall(registration.Handle)) registration.Installed = false;
    }

    private void CollectRetired(bool retryUnhook)
    {
        if (_collecting) return;
        _collecting = true;
        try
        {
            foreach (var registration in _retired.ToArray())
            {
                if (retryUnhook) TryUnhook(registration);
                // A callback can renew its own hook. Keep its delegate alive until the
                // native-to-managed call has returned through its wrapper's finally.
                if (!registration.Installed && registration.Calls == 0) _retired.Remove(registration);
            }
        }
        finally { Volatile.Write(ref _pendingRetired, _retired.Count); _collecting = false; }
    }

    internal KeyboardHookDiagnosticsSnapshot Snapshot(bool refreshRequested)
    {
        long successes = Interlocked.Read(ref _successes);
        return new(successes, Interlocked.Read(ref _failures), Volatile.Read(ref _pendingRetired),
            successes == 0 ? -1 : Math.Max(0, _clock() - Interlocked.Read(ref _lastRenewedAt)), refreshRequested);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var previous = _active; _active = null;
        if (previous is not null) { _retired.Add(previous); TryUnhook(previous); }
        CollectRetired(retryUnhook: true);
        RootFailedRemovals();
        // In-flight successful removals stay owned by their running wrapper. Failed
        // removals stay additionally rooted above, with this manager permanently inactive.
    }

    private void RootFailedRemovals()
    {
        lock (AbandonedGate)
            foreach (var registration in _retired.Where(registration => registration.Installed))
                if (!Abandoned.Contains(registration)) Abandoned.Add(registration);
    }
}

/// <summary>Payload-free lifetime maintenance counts; includes initial hook registration.</summary>
public sealed record KeyboardHookDiagnosticsSnapshot(long SuccessfulRenewals, long FailedRenewals,
    int PendingRetired, double LastRenewalAgeMilliseconds, bool RefreshRequested);

// Tests may provide a managed chain before StartForTesting. Native-enabled Start never
// reads this seam. In particular, this cannot enable real hooks or SendInput in tests.
internal sealed record KeyboardHookTestApi(Func<Func<int, IntPtr, IntPtr, IntPtr>, IntPtr> Install,
    Func<IntPtr, bool> Uninstall, Func<int, IntPtr, IntPtr, IntPtr> Next, Func<long> ClockMilliseconds);
