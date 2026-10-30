using System.Security.Cryptography;

namespace LanBridge;

/// <summary>Bounded, acknowledged image clipboard transport. PNG decoding belongs to the clipboard adapter.</summary>
public sealed class ClipboardImages : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaxImageBytes = 32 * 1024 * 1024;
    public const int ChunkSize = 48 * 1024;
    const string Prefix = "clipboard-image-";
    static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(30);
    readonly Func<Packet, Task> _send;
    readonly Action<string> _log;
    readonly object _sync = new();
    readonly SemaphoreSlim _outgoingGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    Outgoing? _outgoing;
    Incoming? _incoming;
    int _generation;
    bool _disposed;

    sealed class Outgoing(string id, CancellationTokenSource cancellation, int generation)
    {
        public readonly string Id = id;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly int Generation = generation;
        public readonly TaskCompletionSource<bool> Accepted = NewCompletion();
        public readonly TaskCompletionSource<bool> Received = NewCompletion();
        public ChunkAcknowledgement? Acknowledgement;
    }

    sealed record ChunkAcknowledgement(long Offset, TaskCompletionSource<bool> Completion);

    sealed class Incoming(string id, int length, byte[] digest, int generation) : IDisposable
    {
        public readonly string Id = id;
        public readonly byte[] Buffer = GC.AllocateUninitializedArray<byte>(length);
        public readonly byte[] Digest = digest;
        public readonly int Generation = generation;
        public readonly IncrementalHash Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        public int Written;
        public void Dispose() => Hash.Dispose();
    }

    public event Action? ReceivingStarted;
    public event Action<byte[]>? ImageReady;

    public ClipboardImages(Func<Packet, Task> send, Action<string> log)
    {
        _send = send;
        _log = log;
    }

    static TaskCompletionSource<bool> NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Does not hash or copy a large image on the caller's UI thread.</summary>
    public async Task SetLocalImageAsync(byte[] png, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(png);
        if (png.Length == 0 || png.Length > MaxImageBytes) throw new IOException("Ảnh clipboard phải có kích thước từ 1 byte đến 32 MB.");
        CancelTransfers();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        Outgoing state;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            state = new Outgoing(Guid.NewGuid().ToString("N"), cancellation, _generation);
            _outgoing = state;
        }
        bool complete = false, entered = false;
        try
        {
            // Replacing images cancels the older operation before another large snapshot is allocated.
            await _outgoingGate.WaitAsync(cancellation.Token).ConfigureAwait(false);
            entered = true;
            EnsureCurrent(state);
            // The snapshot protects the transfer from later changes to the caller's image buffer.
            var image = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var snapshot = png.ToArray();
                var digest = Convert.ToHexString(SHA256.HashData(snapshot));
                cancellation.Token.ThrowIfCancellationRequested();
                return (Bytes: snapshot, Digest: digest);
            }, cancellation.Token).ConfigureAwait(false);
            EnsureCurrent(state);
            await SendOutgoingAsync(new Packet { Type = Prefix + "offer", Id = state.Id, Length = image.Bytes.Length, Text = image.Digest }, state).ConfigureAwait(false);
            if (!await state.Accepted.Task.WaitAsync(ResponseTimeout, cancellation.Token).ConfigureAwait(false)) throw new IOException("Máy kia từ chối ảnh clipboard.");
            for (int offset = 0; offset < image.Bytes.Length;)
            {
                EnsureCurrent(state);
                int count = Math.Min(ChunkSize, image.Bytes.Length - offset);
                var acknowledgement = new ChunkAcknowledgement(offset + count, NewCompletion());
                lock (_sync)
                {
                    EnsureCurrentCore(state);
                    state.Acknowledgement = acknowledgement;
                }
                await SendOutgoingAsync(new Packet { Type = Prefix + "chunk", Id = state.Id, Data = image.Bytes.AsSpan(offset, count).ToArray(), Length = acknowledgement.Offset }, state).ConfigureAwait(false);
                if (!await acknowledgement.Completion.Task.WaitAsync(ResponseTimeout, cancellation.Token).ConfigureAwait(false)) throw new IOException("Máy kia không nhận được khối ảnh clipboard.");
                offset += count;
            }
            await SendOutgoingAsync(new Packet { Type = Prefix + "end", Id = state.Id, Length = image.Bytes.Length, Text = image.Digest }, state).ConfigureAwait(false);
            if (!await state.Received.Task.WaitAsync(ResponseTimeout, cancellation.Token).ConfigureAwait(false)) throw new IOException("Máy kia chưa xác minh được ảnh clipboard.");
            EnsureCurrent(state);
            complete = true;
            _log("Ảnh clipboard đã sẵn sàng trên máy kia. Bấm Ctrl+V để dán.");
        }
        finally
        {
            lock (_sync) { if (ReferenceEquals(_outgoing, state)) _outgoing = null; }
            if (entered) _outgoingGate.Release();
            if (!complete) _ = SendQuietlyAsync(CancelPacket(state.Id));
        }
    }

    async Task SendOutgoingAsync(Packet packet, Outgoing state)
    {
        EnsureCurrent(state);
        await _send(packet).WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
        EnsureCurrent(state);
    }

    void EnsureCurrent(Outgoing state) { lock (_sync) EnsureCurrentCore(state); }
    void EnsureCurrentCore(Outgoing state)
    {
        state.Cancellation.Token.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(_outgoing, state) || state.Generation != _generation) throw new OperationCanceledException(state.Cancellation.Token);
    }

    public async Task HandleAsync(Packet packet, bool allowed)
    {
        if (!packet.Type.StartsWith(Prefix, StringComparison.Ordinal)) return;
        var responses = new List<Packet>(2);
        byte[]? ready = null;
        int readyGeneration = -1;
        bool started = false;
        string? error = null;
        lock (_sync)
        {
            if (_disposed) return;
            // Acknowledgements never wait behind image reception or a network write.
            if (packet.Type is Prefix + "accept" or Prefix + "received" or Prefix + "chunk-received")
            {
                if (_outgoing is { } outgoing && outgoing.Id == packet.Id && outgoing.Generation == _generation)
                {
                    if (!allowed) CancelOutgoingCore(outgoing);
                    else if (packet.Type == Prefix + "accept") outgoing.Accepted.TrySetResult(packet.Allow);
                    else if (packet.Type == Prefix + "received") outgoing.Received.TrySetResult(packet.Allow);
                    else if (outgoing.Acknowledgement is { } acknowledgement && packet.Length == acknowledgement.Offset) acknowledgement.Completion.TrySetResult(packet.Allow);
                }
                return;
            }
            if (packet.Type == Prefix + "cancel")
            {
                if (_outgoing is { } canceled && canceled.Id == packet.Id) CancelOutgoingCore(canceled);
                if (_incoming?.Id == packet.Id) RemoveIncomingCore();
                return;
            }
            try
            {
                if (!allowed)
                {
                    _generation++;
                    if (_outgoing != null) CancelOutgoingCore(_outgoing);
                    RemoveIncomingCore();
                    responses.Add(new Packet { Type = packet.Type == Prefix + "offer" ? Prefix + "accept" : Prefix + "cancel", Id = packet.Id, Allow = false });
                }
                else if (packet.Type == Prefix + "offer")
                {
                    if (!Guid.TryParseExact(packet.Id, "N", out _) || packet.Length <= 0 || packet.Length > MaxImageBytes || !TryDigest(packet.Text, out var digest))
                        throw new InvalidDataException("Thông tin ảnh clipboard không hợp lệ.");
                    _generation++;
                    if (_outgoing != null)
                    {
                        responses.Add(CancelPacket(_outgoing.Id));
                        CancelOutgoingCore(_outgoing);
                    }
                    if (_incoming != null) responses.Add(CancelPacket(_incoming.Id));
                    RemoveIncomingCore();
                    _incoming = new Incoming(packet.Id!, (int)packet.Length, digest!, _generation);
                    started = true;
                    readyGeneration = _generation;
                    responses.Add(new Packet { Type = Prefix + "accept", Id = packet.Id, Allow = true });
                }
                else if (_incoming is { } incoming && incoming.Id == packet.Id && incoming.Generation == _generation)
                {
                    if (packet.Type == Prefix + "chunk")
                    {
                        if (packet.Data is not { Length: > 0 and <= ChunkSize } data || data.Length > incoming.Buffer.Length - incoming.Written || packet.Length != incoming.Written + data.Length)
                            throw new InvalidDataException("Khối ảnh clipboard bị thiếu, lặp hoặc sai thứ tự.");
                        data.CopyTo(incoming.Buffer, incoming.Written);
                        incoming.Hash.AppendData(data);
                        incoming.Written += data.Length;
                        responses.Add(new Packet { Type = Prefix + "chunk-received", Id = incoming.Id, Length = incoming.Written, Allow = true });
                    }
                    else if (packet.Type == Prefix + "end")
                    {
                        if (incoming.Written != incoming.Buffer.Length || packet.Length != incoming.Buffer.Length || !TryDigest(packet.Text, out var digest)
                            || !CryptographicOperations.FixedTimeEquals(incoming.Digest, digest!)
                            || !CryptographicOperations.FixedTimeEquals(incoming.Digest, incoming.Hash.GetHashAndReset()))
                            throw new InvalidDataException("Ảnh clipboard chưa đủ hoặc sai SHA-256.");
                        ready = incoming.Buffer;
                        readyGeneration = incoming.Generation;
                        responses.Add(new Packet { Type = Prefix + "received", Id = incoming.Id, Allow = true });
                        RemoveIncomingCore();
                    }
                }
                else if (packet.Type is Prefix + "chunk" or Prefix + "end") responses.Add(CancelPacket(packet.Id));
            }
            catch (InvalidDataException ex)
            {
                if (packet.Type != Prefix + "offer" && _incoming?.Id == packet.Id) RemoveIncomingCore();
                responses.Add(new Packet { Type = packet.Type == Prefix + "offer" ? Prefix + "accept" : Prefix + "cancel", Id = packet.Id, Allow = false });
                error = ex.Message;
            }
        }
        if (error != null) _log("Không đồng bộ được ảnh clipboard: " + error);
        if (started && IsCurrent(readyGeneration)) ReceivingStarted?.Invoke();
        // Send outside the state lock: a direct peer can synchronously return the ACK.
        foreach (var response in responses) await _send(response).ConfigureAwait(false);
        if (ready != null && IsCurrent(readyGeneration))
        {
            ImageReady?.Invoke(ready);
            _log("Đã nhận ảnh clipboard. Bấm Ctrl+V để dán.");
        }
    }

    static bool TryDigest(string? text, out byte[]? digest)
    {
        digest = null;
        if (text?.Length != 64) return false;
        try { digest = Convert.FromHexString(text); return true; }
        catch (FormatException) { return false; }
    }

    bool IsCurrent(int generation) { lock (_sync) return !_disposed && generation == _generation; }
    static Packet CancelPacket(string? id) => new() { Type = Prefix + "cancel", Id = id, Allow = false };
    void CancelOutgoingCore(Outgoing state)
    {
        state.Cancellation.Cancel();
        state.Accepted.TrySetCanceled();
        state.Received.TrySetCanceled();
        state.Acknowledgement?.Completion.TrySetCanceled();
        if (ReferenceEquals(_outgoing, state)) _outgoing = null;
    }
    void RemoveIncomingCore() { _incoming?.Dispose(); _incoming = null; }

    public void CancelTransfers()
    {
        string? outgoing, incoming;
        lock (_sync)
        {
            _generation++;
            outgoing = _outgoing?.Id;
            incoming = _incoming?.Id;
            if (_outgoing != null) CancelOutgoingCore(_outgoing);
            RemoveIncomingCore();
        }
        if (outgoing != null) _ = SendQuietlyAsync(CancelPacket(outgoing));
        if (incoming != null) _ = SendQuietlyAsync(CancelPacket(incoming));
    }

    async Task SendQuietlyAsync(Packet packet) { try { await _send(packet).ConfigureAwait(false); } catch { } }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _lifetime.Cancel();
        }
        CancelTransfers();
    }
}
