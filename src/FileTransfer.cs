using System.Security.Cryptography;

namespace LanBridge;

/// <summary>One incoming/outgoing transfer per peer; destination never comes from the network.</summary>
public sealed class FileTransfer : IDisposable
{
    public const long MaxFileSize = 2L * 1024 * 1024 * 1024;
    public const int ChunkSize = 48 * 1024;
    readonly string _directory;
    readonly Func<Packet, Task> _send;
    readonly Action<string> _log;
    readonly Func<Packet, string>? _destinationPath;
    readonly Action<string>? _validateDestination;
    readonly string? _temporaryDirectory;
    readonly bool _acknowledgeChunks;
    readonly SemaphoreSlim _outgoing = new(1, 1);
    readonly SemaphoreSlim _receiveGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    CancellationTokenSource? _outgoingOperation;
    int _receiveVersion;
    FileStream? _incoming;
    IncrementalHash? _hash;
    string? _id, _temporary, _final;
    long _expected, _written;
    TaskCompletionSource<bool>? _accept;
    TaskCompletionSource<bool>? _receipt;
    sealed record ChunkAcknowledgement(long Offset, TaskCompletionSource<bool> Completion);
    ChunkAcknowledgement? _chunkAcknowledgement;
    string? _outgoingId;
    public string DirectoryPath => _directory;

    public FileTransfer(string directory, Func<Packet, Task> send, Action<string> log, Func<Packet, string>? destinationPath = null, Action<string>? validateDestination = null, string? temporaryDirectory = null, bool acknowledgeChunks = false)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _send = send;
        _log = log;
        _destinationPath = destinationPath;
        _validateDestination = validateDestination;
        _temporaryDirectory = temporaryDirectory == null ? null : Path.GetFullPath(temporaryDirectory);
        _acknowledgeChunks = acknowledgeChunks;
    }

    public static string SafeFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 160 || name != Path.GetFileName(name)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.EndsWith('.') || name.EndsWith(' '))
            throw new InvalidDataException("Tên file không hợp lệ.");
        var stem = name.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³" }.Contains(stem))
            throw new InvalidDataException("Tên file dành riêng của Windows.");
        return name;
    }

    public async Task SendFileAsync(string path, CancellationToken cancellationToken, string? transferName = null)
    {
        if (!await _outgoing.WaitAsync(0, cancellationToken)) throw new IOException("Một file khác đang được gửi.");
        try
        {
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            _outgoingOperation = operation;
            cancellationToken = operation.Token;
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkSize, true);
            if (file.Length > MaxFileSize) throw new IOException("Bản này hỗ trợ tối đa 2 GB mỗi file.");
            _outgoingId = Guid.NewGuid().ToString("N");
            _accept = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _receipt = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await _send(new Packet { Type = "file-offer", Id = _outgoingId, Name = SafeFileName(transferName ?? Path.GetFileName(path)), Length = file.Length });
            if (!await _accept.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken)) throw new IOException("Máy kia từ chối nhận file.");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[ChunkSize];
            long offset = 0;
            int count;
            while ((count = await file.ReadAsync(buffer, cancellationToken)) > 0)
            {
                hash.AppendData(buffer, 0, count);
                offset += count;
                ChunkAcknowledgement? acknowledgement = _acknowledgeChunks
                    ? new(offset, new(TaskCreationOptions.RunContinuationsAsynchronously)) : null;
                Volatile.Write(ref _chunkAcknowledgement, acknowledgement);
                await _send(new Packet { Type = "file-chunk", Id = _outgoingId, Data = buffer[..count], Length = _acknowledgeChunks ? offset : 0 });
                // A TLS write only queues bytes; wait until the receiver has written and hashed this chunk.
                if (acknowledgement != null) await acknowledgement.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }
            await _send(new Packet { Type = "file-end", Id = _outgoingId, Text = Convert.ToHexString(hash.GetHashAndReset()) });
            await _receipt.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            _log($"Đã gửi và máy nhận đã xác minh: {Path.GetFileName(path)} ({file.Length:N0} byte).");
        }
        finally { _outgoingId = null; _accept = null; _receipt = null; Volatile.Write(ref _chunkAcknowledgement, null); _outgoingOperation = null; _outgoing.Release(); }
    }

    public async Task HandleAsync(Packet packet, bool allowed)
    {
        if (packet.Type == "file-accept") { if (packet.Id == _outgoingId) _accept?.TrySetResult(packet.Allow); return; }
        if (packet.Type == "file-received") { if (packet.Id == _outgoingId) _receipt?.TrySetResult(true); return; }
        if (packet.Type == "file-chunk-received")
        {
            var acknowledgement = Volatile.Read(ref _chunkAcknowledgement);
            if (_acknowledgeChunks && packet.Id == _outgoingId && acknowledgement?.Offset == packet.Length) acknowledgement.Completion.TrySetResult(true);
            return;
        }
        if (packet.Type == "file-cancel")
        {
            if (packet.Id == _outgoingId) { var error = new IOException("Máy kia đã hủy truyền file: " + packet.Text); _accept?.TrySetException(error); _receipt?.TrySetException(error); CancelOutgoing(); }
            await _receiveGate.WaitAsync();
            try { if (packet.Id == _id) CancelIncomingCore(); } finally { _receiveGate.Release(); }
            return;
        }
        Packet? response = null;
        await _receiveGate.WaitAsync();
        int version = Volatile.Read(ref _receiveVersion);
        try
        {
            if (!allowed || _lifetime.IsCancellationRequested)
            {
                CancelIncomingCore();
                response = new Packet { Type = packet.Type == "file-offer" ? "file-accept" : "file-cancel", Id = packet.Id, Allow = false, Text = "Quyền truyền file đã tắt." };
            }
            else if (packet.Type == "file-offer")
            {
                response = new Packet { Type = "file-accept", Id = packet.Id, Allow = false };
                if (_incoming == null && packet.Length >= 0 && packet.Length <= MaxFileSize && Guid.TryParseExact(packet.Id, "N", out _))
                {
                    try
                    {
                        var safe = SafeFileName(packet.Name);
                        Directory.CreateDirectory(_directory);
                        if (new DriveInfo(Path.GetPathRoot(_directory)!).AvailableFreeSpace < packet.Length + 16 * 1024 * 1024) throw new IOException("Không đủ dung lượng nhận file.");
                        _final = _destinationPath == null
                            ? Path.Combine(_directory, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}-{safe}")
                            : Path.GetFullPath(_destinationPath(packet));
                        var directoryPrefix = Path.EndsInDirectorySeparator(_directory) ? _directory : _directory + Path.DirectorySeparatorChar;
                        if (!_final.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Đường dẫn nhận file nằm ngoài thư mục cho phép.");
                        if (_temporaryDirectory != null)
                        {
                            _validateDestination?.Invoke(_temporaryDirectory);
                            Directory.CreateDirectory(_temporaryDirectory);
                        }
                        _temporary = _temporaryDirectory == null ? _final + ".partial" : Path.Combine(_temporaryDirectory, Guid.NewGuid().ToString("N") + ".partial");
                        _validateDestination?.Invoke(_temporary);
                        _incoming = new FileStream(_temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkSize, true);
                        _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                        _id = packet.Id; _expected = packet.Length; _written = 0; response.Allow = true;
                        _log($"Đang nhận: {safe} ({packet.Length:N0} byte)");
                    }
                    catch (Exception ex) { CancelIncomingCore(); _log(ex.Message); }
                }
            }
            else if (_incoming != null && packet.Id == _id)
            {
                if (packet.Type == "file-chunk")
                {
                    if (packet.Data == null || packet.Data.Length > ChunkSize || _written + packet.Data.Length > _expected) throw new InvalidDataException("Kích thước file nhận không hợp lệ.");
                    if (_acknowledgeChunks && (packet.Data.Length == 0 || packet.Length != _written + packet.Data.Length)) throw new InvalidDataException("Vị trí khối file nhận không hợp lệ.");
                    await _incoming.WriteAsync(packet.Data); _hash!.AppendData(packet.Data); _written += packet.Data.Length;
                    if (_acknowledgeChunks) response = new Packet { Type = "file-chunk-received", Id = _id, Length = _written, Allow = true };
                }
                else if (packet.Type == "file-end")
                {
                    if (_written != _expected || !string.Equals(Convert.ToHexString(_hash!.GetHashAndReset()), packet.Text, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File nhận chưa đủ hoặc sai SHA-256.");
                    await _incoming.DisposeAsync(); _incoming = null;
                    if (version != Volatile.Read(ref _receiveVersion)) CancelIncomingCore();
                    else
                    {
                        _validateDestination?.Invoke(_temporary!);
                        _validateDestination?.Invoke(_final!);
                        File.Move(_temporary!, _final!);
                        _log("Đã nhận và xác minh: " + _final);
                        response = new Packet { Type = "file-received", Id = _id, Name = Path.GetFileName(_final) }; ClearState();
                    }
                }
            }
            if (version != Volatile.Read(ref _receiveVersion)) { CancelIncomingCore(); response = new Packet { Type = "file-cancel", Id = packet.Id, Text = "Đã hủy truyền file." }; }
        }
        catch { CancelIncomingCore(); throw; }
        finally { _receiveGate.Release(); }
        // Never hold the receive lock during a network write (the peer may reply immediately).
        if (response != null) await _send(response);
    }

    void ClearState() { _hash?.Dispose(); _hash = null; _id = _temporary = _final = null; _expected = _written = 0; }
    void CancelIncomingCore() { _incoming?.Dispose(); _incoming = null; if (_temporary != null) { try { File.Delete(_temporary); } catch (IOException) { } } ClearState(); }
    public void CancelIncoming() { _ = CancelIncomingAsync(); }
    public Task CancelIncomingAsync() { Interlocked.Increment(ref _receiveVersion); return CancelSerialized(); }
    async Task CancelSerialized()
    {
        await _receiveGate.WaitAsync(); string? id;
        try { id = _id; CancelIncomingCore(); } finally { _receiveGate.Release(); }
        if (id != null) { try { await _send(new Packet { Type = "file-cancel", Id = id, Text = "Đã hủy hoặc thu hồi quyền truyền file." }); } catch { } }
    }
    void CancelOutgoing() { try { _outgoingOperation?.Cancel(); } catch (ObjectDisposedException) { } }
    public void CancelTransfers() { CancelOutgoing(); CancelIncoming(); }
    public void Dispose() { _lifetime.Cancel(); _accept?.TrySetCanceled(); _receipt?.TrySetCanceled(); Volatile.Read(ref _chunkAcknowledgement)?.Completion.TrySetCanceled(); CancelTransfers(); }
}
