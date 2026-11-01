using System.Threading.Channels;
using LanBridge.CrossPlatform;

namespace LanBridge.Desktop;

/// <summary>One authenticated peer; input and background transfers never share a blocking worker.</summary>
internal sealed class PortablePeerSession : IDisposable
{
    readonly PeerConnection _peer;
    readonly PortableInputRouter _input;
    readonly CancellationTokenSource _stop = new();
    readonly Channel<(Packet Packet, long Bytes, long FileEpoch, long ClipboardEpoch)> _incoming = Channel.CreateBounded<(Packet, long, long, long)>(512);
    readonly Channel<(Packet Packet, long FileEpoch, long ClipboardEpoch)> _background = Channel.CreateBounded<(Packet, long, long)>(128);
    readonly Action<Packet> _sender;
    readonly FileTransfer _files;
    Permissions _local, _remote = new() { Input = false, Clipboard = false, Files = false };
    volatile bool _hello;
    long _fileEpoch, _clipboardEpoch, _pendingBytes;
    int _disposed;

    public bool IsHost { get; }
    public bool Connected => Volatile.Read(ref _disposed) == 0 && !_peer.IsClosed;
    public bool InputAllowed => Connected && _hello && (IsHost ? Volatile.Read(ref _local).Input : Volatile.Read(ref _remote).Input);
    public bool ClipboardAllowed => Connected && _hello && Volatile.Read(ref _local).Clipboard && (IsHost || Volatile.Read(ref _remote).Clipboard);
    public bool FilesAllowed => Connected && _hello && (IsHost ? Volatile.Read(ref _local).Files : Volatile.Read(ref _remote).Files);
    public event Action<string>? Log;
    public event Action? Changed;
    public event Action? Closed;
    public Func<string, Task>? SetClipboardText { get; set; }

    public PortablePeerSession(PeerConnection peer, PortableInputRouter input, bool host, Permissions local, string receiveDirectory)
    {
        _peer = peer; _input = input; IsHost = host; _local = local with { };
        _sender = packet => { if (Connected) peer.TrySend(packet); };
        _files = new FileTransfer(receiveDirectory, packet => peer.SendAsync(packet, _stop.Token), text => Log?.Invoke(text));
    }

    public void Start(DesktopBounds bounds, string name)
    {
        _input.IsController = !IsHost;
        _input.CanControl = false;
        _input.Send = _sender;
        _peer.Received += Receive;
        _peer.Closed += PeerClosed;
        _ = Task.Run(ProcessIncomingAsync);
        _ = Task.Run(ProcessBackgroundAsync);
        _peer.TrySend(new Packet { Type = "hello", Name = name, Width = bounds.Width, Height = bounds.Height, Flags = 2 });
        if (IsHost) _peer.TrySend(new Packet { Type = "permissions", Permissions = _local with { } });
        _peer.Start();
    }

    public void SetLocalPermissions(Permissions permissions)
    {
        if (!Connected) return;
        var previous = Volatile.Read(ref _local);
        if (previous.Files != permissions.Files) Interlocked.Increment(ref _fileEpoch);
        if (previous.Clipboard != permissions.Clipboard) Interlocked.Increment(ref _clipboardEpoch);
        Volatile.Write(ref _local, permissions with { });
        _input.ConfigureSession(_sender, InputAllowed);
        if (!permissions.Files) _files.CancelTransfers();
        if (IsHost && Connected) _peer.TrySend(new Packet { Type = "permissions", Permissions = permissions with { } });
        Changed?.Invoke();
    }

    public void RevokeInput()
    {
        if (IsHost) SetLocalPermissions(_local with { Input = false });
        else _input.ReleaseRemote();
    }

    public void SendChat(string text)
    {
        if (Connected && text.Length is > 0 and <= 4096) _peer.TrySend(new Packet { Type = "chat", Text = text });
    }
    public void SendClipboard(string text)
    {
        if (Connected && ClipboardAllowed && text.Length <= 262144) _peer.TrySend(new Packet { Type = "clipboard", Text = text });
    }
    public Task SendFileAsync(string path) => FilesAllowed
        ? _files.SendFileAsync(path, _stop.Token) : Task.FromException(new IOException("Máy nhận chưa cấp quyền truyền file."));

    void Receive(Packet packet)
    {
        if (!Connected) return;
        // Reject unrelated large fields before enqueueing; the transport has a separate 4 MiB frame cap.
        if (packet.Name?.Length > 256 || packet.Id?.Length > 80 || packet.Text?.Length > 262144
            || packet.Button?.Length > 16 || packet.BatchId?.Length > 80
            || packet.ClipboardEntries != null || packet.Data != null && (packet.Type != "file-chunk" || packet.Data.Length > FileTransfer.ChunkSize)
            || IsInput(packet.Type) && packet.Text?.Length > 256)
        { Dispose(); return; }
        // Include every retained string field, even fields unused by this preview's packet type.
        long bytes = 512L + ((long)packet.Type.Length + (packet.Text?.Length ?? 0) + (packet.Name?.Length ?? 0)
            + (packet.Id?.Length ?? 0) + (packet.Button?.Length ?? 0) + (packet.BatchId?.Length ?? 0)) * 2L
            + (packet.Data?.Length ?? 0);
        if (Interlocked.Add(ref _pendingBytes, bytes) > 8 * 1024 * 1024 || !_incoming.Writer.TryWrite((packet, bytes,
                Interlocked.Read(ref _fileEpoch), Interlocked.Read(ref _clipboardEpoch))))
        { Interlocked.Add(ref _pendingBytes, -bytes); Dispose(); }
    }

    async Task ProcessIncomingAsync()
    {
        try
        {
            await foreach (var queued in _incoming.Reader.ReadAllAsync(_stop.Token))
            {
                var packet = queued.Packet;
                Interlocked.Add(ref _pendingBytes, -queued.Bytes);
                if (!Connected) break;
                if (packet.Type == "hello")
                {
                    if (_hello || packet.Flags != 2 || packet.Width is < 2 or > 65536 || packet.Height is < 2 or > 65536)
                        throw new IOException("Máy bên kia cần LanBridge hỗ trợ gõ Unicode (Windows 0.2.0 trở lên).");
                    _hello = true;
                    if (!_input.ConfigureSession(_sender, InputAllowed, packet.Width, packet.Height)) break;
                    Log?.Invoke("Đối tác: " + packet.Name); Changed?.Invoke();
                }
                else if (!_hello) throw new IOException("Máy bên kia gửi dữ liệu trước khi khai báo giao thức.");
                else if (packet.Type == "permissions" && !IsHost && packet.Permissions is { } permissions)
                {
                    var previous = Volatile.Read(ref _remote);
                    if (previous.Files != permissions.Files) Interlocked.Increment(ref _fileEpoch);
                    if (previous.Clipboard != permissions.Clipboard) Interlocked.Increment(ref _clipboardEpoch);
                    Volatile.Write(ref _remote, permissions with { });
                    if (!_input.ConfigureSession(_sender, InputAllowed)) break;
                    if (!permissions.Files) _files.CancelTransfers();
                    Changed?.Invoke();
                }
                else if (IsInput(packet.Type))
                {
                    if (!_hello) continue;
                    if (packet.Type == "leave" || IsHost && Volatile.Read(ref _local).Input)
                        await _input.ApplyAsync(packet, _sender, _stop.Token).ConfigureAwait(false);
                }
                else if (!_background.Writer.TryWrite((packet,
                    IsHost ? queued.FileEpoch : Interlocked.Read(ref _fileEpoch),
                    IsHost ? queued.ClipboardEpoch : Interlocked.Read(ref _clipboardEpoch))))
                    throw new IOException("Hàng đợi dữ liệu nền đã đầy; phiên đã dừng.");
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Log?.Invoke(error.Message); Dispose(); }
    }

    async Task ProcessBackgroundAsync()
    {
        try
        {
            await foreach (var item in _background.Reader.ReadAllAsync(_stop.Token))
            {
                if (!Connected) break;
                var packet = item.Packet;
                if (packet.Type == "chat" && packet.Text is { Length: <= 4096 } text) Log?.Invoke("Đối tác: " + text);
                else if (packet.Type == "clipboard" && item.ClipboardEpoch == Interlocked.Read(ref _clipboardEpoch)
                    && ClipboardAllowed && packet.Text is { Length: <= 262144 } clipboard && SetClipboardText is { } set)
                    await set(clipboard).WaitAsync(_stop.Token).ConfigureAwait(false);
                else if (packet.Type.StartsWith("file-", StringComparison.Ordinal))
                {
                    bool allowed = item.FileEpoch == Interlocked.Read(ref _fileEpoch) && FilesAllowed;
                    if (packet.Type == "file-offer" && !PortableFileName(packet.Name)) allowed = false;
                    await _files.HandleAsync(packet, allowed).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception error) { Log?.Invoke(error.Message); Dispose(); }
    }

    static bool PortableFileName(string? name) => name is { Length: > 0 and <= 160 }
        && !name.Any(c => char.IsControl(c) || "\\/:*?\"<>|".Contains(c));
    static bool IsInput(string type) => type is "enter" or "leave" or "move" or "key" or "text" or "wheel" or "button";
    void PeerClosed(string? reason) { if (reason != null) Log?.Invoke(reason); Dispose(); }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _peer.Received -= Receive; _peer.Closed -= PeerClosed;
        _stop.Cancel(); _incoming.Writer.TryComplete(); _background.Writer.TryComplete();
        _input.ReleaseSession(_sender);
        _files.Dispose(); _peer.Dispose();
        Closed?.Invoke();
    }
}
