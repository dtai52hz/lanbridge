namespace LanBridge;

/// <summary>A bounded JPEG of the same desktop rectangle used by normalized input coordinates.</summary>
public sealed record ScreenFrame(byte[] Jpeg, int Width, int Height);

/// <summary>Opt-in screen viewing. One capture/write at a time bounds memory and network backlog.</summary>
public sealed class ScreenStreamingSession : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaximumJpegBytes = 512 * 1024;
    public const int MaximumWidth = 1280;
    public const int MaximumHeight = 800;
    readonly PeerConnection _peer;
    readonly Func<CancellationToken, Task<ScreenFrame>> _capture;
    readonly Func<Packet, Task> _sendFrame;
    readonly object _gate = new();
    // Do not dispose this while a stopped native capture/write can still return.
    // Every generation shares it, including after an OFF followed immediately by ON.
    readonly SemaphoreSlim _captureWrite = new(1, 1);
    CancellationTokenSource? _stream;
    bool _permission, _disposed;
    long _generation;
    public event Action<string>? Error;
    public event Action<bool>? StateChanged;

    public ScreenStreamingSession(PeerConnection peer, Func<CancellationToken, Task<ScreenFrame>> capture)
        : this(peer, capture, packet => peer.SendAsync(packet)) { }

    internal ScreenStreamingSession(PeerConnection peer, Func<CancellationToken, Task<ScreenFrame>> capture, Func<Packet, Task> sendFrame)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
        _capture = capture ?? throw new ArgumentNullException(nameof(capture));
        _sendFrame = sendFrame ?? throw new ArgumentNullException(nameof(sendFrame));
        _peer.Closed += PeerClosed;
    }

    public void SetPermission(bool allowed)
    {
        lock (_gate)
        {
            if (_disposed || _permission == allowed) return;
            _permission = allowed;
            if (!allowed)
            {
                bool stopped = StopLocked();
                Status(false, "Chủ máy đã tắt quyền xem màn hình.");
                if (stopped) Changed(false);
            }
        }
    }

    public bool Handle(Packet packet)
    {
        if (packet.Type != "screen-request") return false;
        lock (_gate)
        {
            if (_disposed) return true;
            if (!packet.Allow)
            {
                bool stopped = StopLocked();
                Status(false, "Đã dừng xem màn hình.");
                if (stopped) Changed(false);
            }
            else if (!_permission) Status(false, "Chủ máy chưa cấp quyền xem màn hình.");
            else if (_stream is null)
            {
                var source = new CancellationTokenSource();
                _stream = source;
                long generation = ++_generation;
                // Publish ON before the worker can fail or a concurrent OFF can
                // publish its later state. Notifications share the transition gate.
                Status(true, "Đang chia sẻ màn hình với đối tác đã kết nối.");
                Changed(true);
                // Capture providers may enter native code synchronously: never call them on the UI/read loop.
                _ = Task.Run(() => StreamAsync(source, generation));
            }
            else Status(true, "Đang chia sẻ màn hình với đối tác đã kết nối.");
        }
        return true;
    }

    async Task StreamAsync(CancellationTokenSource source, long generation)
    {
        var token = source.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                long started = System.Diagnostics.Stopwatch.GetTimestamp();
                await _captureWrite.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    lock (_gate) if (!IsCurrent(source, generation)) return;
                    var frame = await _capture(token).ConfigureAwait(false);
                    Validate(frame);
                    Task write;
                    lock (_gate)
                    {
                        if (!IsCurrent(source, generation)) return;
                        token.ThrowIfCancellationRequested();
                        // Keep this slot until the actual write completes, even if
                        // this stream is stopped. Cancelling SendAsync only cancels
                        // its wait; it does not remove an already queued TLS frame.
                        write = _sendFrame(new Packet { Type = "screen-frame", Data = frame.Jpeg,
                            Width = frame.Width, Height = frame.Height, Encoding = 1 });
                    }
                    await write.ConfigureAwait(false);
                }
                finally { _captureWrite.Release(); }
                var remaining = TimeSpan.FromMilliseconds(200) - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception)
        {
            lock (_gate)
            {
                if (IsCurrent(source, generation))
                {
                    _stream = null; ++_generation;
                    const string prefix = "Không thể chia sẻ màn hình: ";
                    string detail = exception is IOException or InvalidOperationException or NotSupportedException
                        ? exception.Message : "kiểm tra quyền ghi màn hình trên máy tính.";
                    Status(false, prefix + detail);
                    try { Error?.Invoke(prefix + detail); } catch { }
                    Changed(false);
                }
            }
        }
        finally { source.Dispose(); }
    }

    bool IsCurrent(CancellationTokenSource source, long generation) => !_disposed && _permission
        && generation == _generation && ReferenceEquals(_stream, source);

    static void Validate(ScreenFrame frame)
    {
        if (frame is null || frame.Width is < 1 or > MaximumWidth || frame.Height is < 1 or > MaximumHeight
            || frame.Jpeg is not { Length: >= 4 and <= MaximumJpegBytes }
            || frame.Jpeg[0] != 0xff || frame.Jpeg[1] != 0xd8
            || frame.Jpeg[^2] != 0xff || frame.Jpeg[^1] != 0xd9)
            throw new InvalidDataException("Ảnh chia sẻ vượt giới hạn hoặc không đúng định dạng JPEG.");
    }

    bool StopLocked()
    {
        var source = _stream;
        if (source is null) return false;
        _stream = null;
        ++_generation;
        source.Cancel();
        return true;
    }
    void Status(bool active, string text)
    {
        if (!_peer.IsClosed) _peer.TrySend(new Packet { Type = "screen-state", Allow = active, Text = text });
    }
    void Changed(bool active) { try { StateChanged?.Invoke(active); } catch { } }
    void PeerClosed(string? reason) => Dispose();
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _permission = false;
            bool stopped = StopLocked();
            if (stopped) Changed(false);
        }
        _peer.Closed -= PeerClosed;
    }
}
