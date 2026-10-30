namespace LanBridge;

/// <summary>Copies a file clipboard into a private cache; never publishes remote paths as local paths.</summary>
public sealed class ClipboardFiles : IDisposable
{
    public const int ProtocolVersion = 1;
    public const int MaxEntries = 10_000;
    public const int MaxDepth = 64;
    public const long MaxBatchBytes = 8L * 1024 * 1024 * 1024;
    const int MaxManifestCharacters = 512 * 1024;
    const string Prefix = "clipboard-files-";
    const string DataPrefix = Prefix + "data-";
    readonly string _directory;
    readonly Func<Packet, Task> _send;
    readonly Action<string> _log;
    readonly SemaphoreSlim _receiveGate = new(1, 1);
    readonly CancellationTokenSource _lifetime = new();
    readonly object _outgoingLock = new();
    Outgoing? _outgoing;
    Incoming? _incoming;
    int _generation;

    sealed class Outgoing(string id, CancellationTokenSource cancellation, int generation)
    {
        public readonly string Id = id;
        public readonly CancellationTokenSource Cancellation = cancellation;
        public readonly int Generation = generation;
        public readonly TaskCompletionSource<bool> Accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource<bool> Received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public FileTransfer? Transfer;
    }

    sealed class Incoming(string id, string directory, ClipboardFileEntry[] entries, int generation)
    {
        public readonly string Id = id, Directory = directory;
        public readonly ClipboardFileEntry[] Entries = entries;
        public readonly int Generation = generation;
        public readonly HashSet<int> Finished = [];
        public FileTransfer Transfer = null!;
        public int? ActiveIndex;
        public string? ActiveId;
        public bool Complete;
        public string FilesDirectory => Path.Combine(Directory, "files");
    }

    sealed record LocalFile(int Index, string Path);
    sealed record Manifest(ClipboardFileEntry[] Entries, LocalFile[] Files);

    public event Action? ReceivingStarted;
    public event Action<string[]>? FilesReady;
    public string DirectoryPath => _directory;

    public ClipboardFiles(string directory, Func<Packet, Task> send, Action<string> log)
    {
        _directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        _send = send; _log = log;
        EnsureNoReparsePoints(_directory);
        Directory.CreateDirectory(_directory);
        _ = Task.Run(() =>
        {
            try { PruneOldCache(); }
            catch { } // Best-effort maintenance must never delay or fail a new session.
        });
    }

    public bool IsCachePath(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            return full.Equals(_directory, StringComparison.OrdinalIgnoreCase) || full.StartsWith(_directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    /// <summary>Run without awaiting from a UI timer or the transport's incoming packet reader.</summary>
    public async Task SetLocalFilesAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        if (paths.Count == 0 || paths.Count > MaxEntries) throw new IOException("Clipboard có quá nhiều file hoặc không có file.");
        if (paths.Any(IsCachePath)) return; // Received files already exist on the other PC; never bounce them back.
        var localPaths = paths.ToArray();
        CancelTransfers();
        int generation = Volatile.Read(ref _generation);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var state = new Outgoing(Guid.NewGuid().ToString("N"), cancellation, generation);
        lock (_outgoingLock)
        {
            if (generation != Volatile.Read(ref _generation)) cancellation.Cancel();
            _outgoing = state;
        }
        bool complete = false;
        try
        {
            var manifest = await Task.Run(() => BuildManifest(localPaths, cancellation.Token), cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            if (state.Generation != Volatile.Read(ref _generation)) throw new OperationCanceledException(cancellation.Token);
            state.Transfer = new FileTransfer(_directory, packet => SendDataAsync(packet, state.Id), _ => { }, acknowledgeChunks: true);
            await _send(new Packet { Type = Prefix + "offer", Id = state.Id, ClipboardEntries = manifest.Entries }).ConfigureAwait(false);
            if (!await state.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellation.Token).ConfigureAwait(false)) throw new IOException("Máy kia từ chối nhận file từ clipboard.");
            foreach (var file in manifest.Files)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                EnsureNoReparsePoints(file.Path);
                await state.Transfer.SendFileAsync(file.Path, cancellation.Token, file.Index.ToString("D8")).ConfigureAwait(false);
            }
            await _send(new Packet { Type = Prefix + "end", Id = state.Id }).ConfigureAwait(false);
            if (!await state.Received.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellation.Token).ConfigureAwait(false)) throw new IOException("Máy kia chưa nhận đủ clipboard file.");
            complete = true;
            _log($"Clipboard file đã sẵn sàng trên máy kia ({manifest.Files.Length:N0} file). Bấm Ctrl+V để dán.");
        }
        finally
        {
            lock (_outgoingLock) { if (ReferenceEquals(_outgoing, state)) _outgoing = null; }
            state.Transfer?.Dispose();
            if (!complete) await SendQuietlyAsync(new Packet { Type = Prefix + "cancel", Id = state.Id }).ConfigureAwait(false);
        }
    }

    public async Task HandleAsync(Packet packet, bool allowed)
    {
        Outgoing? outgoing;
        lock (_outgoingLock) outgoing = _outgoing;
        if (packet.Type == Prefix + "accept" || packet.Type == Prefix + "received")
        {
            if (outgoing != null && packet.Id == outgoing.Id && outgoing.Generation == Volatile.Read(ref _generation))
            {
                if (!allowed) { CancelOutgoing(outgoing); return; }
                if (packet.Type == Prefix + "accept") outgoing.Accepted.TrySetResult(packet.Allow);
                else outgoing.Received.TrySetResult(packet.Allow);
            }
            return;
        }
        if (packet.Type.StartsWith(DataPrefix, StringComparison.Ordinal) && outgoing != null && packet.BatchId == outgoing.Id)
        {
            var inner = Unwrap(packet);
            // ACKs must bypass incoming batch serialization: SendFileAsync waits for them on a separate task.
            if (inner.Type is "file-accept" or "file-received" or "file-chunk-received" or "file-cancel")
            {
                if (!allowed) CancelOutgoing(outgoing);
                else if (outgoing.Transfer != null) await outgoing.Transfer.HandleAsync(inner, true).ConfigureAwait(false);
                return;
            }
        }
        if (packet.Type == Prefix + "cancel" && outgoing != null && packet.Id == outgoing.Id) { CancelOutgoing(outgoing); return; }

        Packet? response = null;
        string[]? ready = null;
        int readyGeneration = -1;
        bool started = false;
        await _receiveGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lifetime.IsCancellationRequested) return;
            if (!allowed)
            {
                if (_incoming != null) await RemoveIncomingAsync(_incoming).ConfigureAwait(false);
                if (packet.Type == Prefix + "offer") response = new Packet { Type = Prefix + "accept", Id = packet.Id, Allow = false };
            }
            else if (packet.Type == Prefix + "offer")
            {
                response = new Packet { Type = Prefix + "accept", Id = packet.Id, Allow = false };
                if (!Guid.TryParseExact(packet.Id, "N", out _)) throw new InvalidDataException("Mã clipboard file không hợp lệ.");
                var entries = ValidateManifest(packet.ClipboardEntries);
                var batchDirectory = Path.Combine(_directory, "batch-" + packet.Id);
                EnsureNoReparsePoints(batchDirectory);
                if (Directory.Exists(batchDirectory)) throw new InvalidDataException("Clipboard file trùng mã phiên trước.");
                int generation = Interlocked.Increment(ref _generation);
                lock (_outgoingLock) { if (_outgoing != null) CancelOutgoing(_outgoing); }
                if (_incoming != null) await RemoveIncomingAsync(_incoming).ConfigureAwait(false);
                var state = new Incoming(packet.Id!, batchDirectory, entries, generation);
                _incoming = state;
                Directory.CreateDirectory(state.FilesDirectory);
                foreach (var entry in entries.Where(e => e.IsDirectory))
                {
                    var target = TargetPath(state, entry.Path);
                    EnsureNoReparsePoints(target);
                    Directory.CreateDirectory(target);
                }
                state.Transfer = new FileTransfer(state.FilesDirectory, p => SendDataAsync(p, state.Id), _ => { }, offer => IncomingDestination(state, offer), EnsureNoReparsePoints, Path.Combine(state.Directory, "staging"), acknowledgeChunks: true);
                response.Allow = generation == Volatile.Read(ref _generation);
                if (!response.Allow) await RemoveIncomingAsync(state).ConfigureAwait(false);
                else { started = true; _log("Đang đồng bộ file trong clipboard…"); }
            }
            else if (_incoming is { } incoming && incoming.Generation == Volatile.Read(ref _generation))
            {
                if (packet.Type == Prefix + "cancel" && packet.Id == incoming.Id) await RemoveIncomingAsync(incoming).ConfigureAwait(false);
                else if (packet.Type.StartsWith(DataPrefix, StringComparison.Ordinal) && packet.BatchId == incoming.Id)
                {
                    var inner = Unwrap(packet);
                    if (inner.Type == "file-offer")
                    {
                        if (incoming.ActiveId != null || !int.TryParse(inner.Name, out var index) || inner.Name != index.ToString("D8") || index < 0 || index >= incoming.Entries.Length || incoming.Entries[index].IsDirectory || incoming.Finished.Contains(index) || inner.Length != incoming.Entries[index].Length)
                            throw new InvalidDataException("File không khớp danh sách clipboard.");
                        incoming.ActiveIndex = index; incoming.ActiveId = inner.Id;
                    }
                    else if (inner.Id != incoming.ActiveId || incoming.ActiveIndex == null) throw new InvalidDataException("Gói file không thuộc clipboard hiện tại.");
                    await incoming.Transfer.HandleAsync(inner, true).ConfigureAwait(false);
                    if (inner.Type == "file-end")
                    {
                        int index = incoming.ActiveIndex!.Value;
                        var target = TargetPath(incoming, incoming.Entries[index].Path);
                        EnsureNoReparsePoints(target);
                        if (!File.Exists(target) || new FileInfo(target).Length != incoming.Entries[index].Length) throw new InvalidDataException("File clipboard chưa được xác minh.");
                        incoming.Finished.Add(index); incoming.ActiveId = null; incoming.ActiveIndex = null;
                    }
                    else if (inner.Type == "file-cancel") { await RemoveIncomingAsync(incoming).ConfigureAwait(false); }
                }
                else if (packet.Type == Prefix + "end" && packet.Id == incoming.Id)
                {
                    if (incoming.ActiveId != null || incoming.Finished.Count != incoming.Entries.Count(e => !e.IsDirectory)) throw new InvalidDataException("Clipboard file chưa nhận đủ.");
                    foreach (var entry in incoming.Entries) EnsureNoReparsePoints(TargetPath(incoming, entry.Path));
                    if (incoming.Generation != Volatile.Read(ref _generation)) { await RemoveIncomingAsync(incoming).ConfigureAwait(false); return; }
                    File.WriteAllText(Path.Combine(incoming.Directory, ".complete"), DateTimeOffset.UtcNow.ToString("O"));
                    incoming.Complete = true;
                    ready = incoming.Entries.Where(e => !e.Path.Contains('/')).Select(e => TargetPath(incoming, e.Path)).ToArray();
                    readyGeneration = incoming.Generation;
                    response = new Packet { Type = Prefix + "received", Id = incoming.Id, Allow = true };
                    incoming.Transfer.Dispose();
                    _incoming = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            var failed = _incoming;
            if (failed != null) await RemoveIncomingAsync(failed).ConfigureAwait(false);
            response = new Packet { Type = packet.Type == Prefix + "offer" ? Prefix + "accept" : Prefix + "cancel", Id = packet.Type == Prefix + "offer" ? packet.Id : packet.BatchId ?? packet.Id, Allow = false };
            _log("Không đồng bộ được clipboard file: " + ex.Message);
        }
        finally { _receiveGate.Release(); }
        if (started) ReceivingStarted?.Invoke();
        if (response != null) await _send(response).ConfigureAwait(false);
        if (ready != null && readyGeneration == Volatile.Read(ref _generation) && !_lifetime.IsCancellationRequested)
        {
            FilesReady?.Invoke(ready);
            _log("Đã nhận đủ file trong clipboard. Bấm Ctrl+V để dán.");
        }
    }

    string IncomingDestination(Incoming incoming, Packet offer)
    {
        if (!int.TryParse(offer.Name, out var index) || incoming.ActiveIndex != index || incoming.ActiveId != offer.Id) throw new InvalidDataException("Clipboard file không hợp lệ.");
        var target = TargetPath(incoming, incoming.Entries[index].Path);
        EnsureNoReparsePoints(target);
        return target;
    }

    static string TargetPath(Incoming state, string relative) => Path.GetFullPath(Path.Combine(state.FilesDirectory, relative.Replace('/', Path.DirectorySeparatorChar)));
    Task SendDataAsync(Packet packet, string batchId) => _send(packet with { Type = DataPrefix + packet.Type, BatchId = batchId });
    static Packet Unwrap(Packet packet) => packet with { Type = packet.Type[DataPrefix.Length..] };

    Manifest BuildManifest(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var entries = new List<ClipboardFileEntry>();
        var files = new List<LocalFile>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            if (IsCachePath(full)) throw new IOException("Không gửi lại file đã nhận từ clipboard.");
            string name = FileTransfer.SafeFileName(Path.GetFileName(full));
            if (!names.Add(name)) throw new IOException("Các mục đã sao chép trùng tên. Hãy sao chép từng nhóm riêng.");
            Walk(full, name, 1);
        }
        return new(ValidateManifest(entries.ToArray()), files.ToArray());

        void Walk(string path, string relative, int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > MaxDepth || entries.Count >= MaxEntries) throw new IOException("Clipboard vượt giới hạn 10.000 mục hoặc 64 cấp thư mục.");
            EnsureNoReparsePoints(path);
            var attributes = File.GetAttributes(path);
            bool directory = (attributes & FileAttributes.Directory) != 0;
            long length = directory ? 0 : new FileInfo(path).Length;
            int index = entries.Count;
            entries.Add(new ClipboardFileEntry { Path = relative, IsDirectory = directory, Length = length });
            if (!directory) files.Add(new LocalFile(index, path));
            else
                foreach (string child in Directory.EnumerateFileSystemEntries(path))
                    Walk(child, relative + "/" + FileTransfer.SafeFileName(Path.GetFileName(child)), depth + 1);
        }
    }

    public static ClipboardFileEntry[] ValidateManifest(ClipboardFileEntry[]? entries)
    {
        if (entries is not { Length: > 0 and <= MaxEntries }) throw new InvalidDataException("Danh sách clipboard file không hợp lệ.");
        var result = new ClipboardFileEntry[entries.Length];
        var paths = new Dictionary<string, ClipboardFileEntry>(StringComparer.OrdinalIgnoreCase);
        long bytes = 0; int characters = 0;
        for (int i = 0; i < entries.Length; i++)
        {
            var entry = entries[i] ?? throw new InvalidDataException("Mục clipboard file rỗng.");
            if (string.IsNullOrEmpty(entry.Path) || entry.Path.Length > 1024 || entry.Path.Contains('\\') || Path.IsPathRooted(entry.Path)) throw new InvalidDataException("Đường dẫn clipboard file không hợp lệ.");
            string[] components = entry.Path.Split('/');
            if (components.Length > MaxDepth) throw new InvalidDataException("Thư mục clipboard quá sâu.");
            foreach (string component in components) FileTransfer.SafeFileName(component);
            if (entry.Length < 0 || entry.Length > FileTransfer.MaxFileSize || entry.IsDirectory && entry.Length != 0) throw new InvalidDataException("Kích thước clipboard file không hợp lệ (tối đa 2 GB/file).");
            bytes += entry.Length; characters += entry.Path.Length;
            if (bytes > MaxBatchBytes || characters > MaxManifestCharacters) throw new InvalidDataException("Clipboard file quá lớn (tối đa 8 GB/lần).");
            var copy = entry with { };
            if (!paths.TryAdd(entry.Path, copy)) throw new InvalidDataException("Tên file clipboard bị trùng.");
            result[i] = copy;
        }
        foreach (var entry in result)
        {
            int slash = entry.Path.LastIndexOf('/');
            while (slash >= 0)
            {
                string parent = entry.Path[..slash];
                if (!paths.TryGetValue(parent, out var directory) || !directory.IsDirectory) throw new InvalidDataException("Thư mục cha clipboard không hợp lệ.");
                slash = parent.LastIndexOf('/');
            }
        }
        return result;
    }

    public static void EnsureNoReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("Không sao chép liên kết hoặc junction trong clipboard."); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void CancelTransfers()
    {
        int generation = Interlocked.Increment(ref _generation);
        lock (_outgoingLock) { if (_outgoing != null) CancelOutgoing(_outgoing); }
        _ = CancelIncomingBeforeAsync(generation);
    }

    static void CancelOutgoing(Outgoing outgoing)
    {
        try { outgoing.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
        outgoing.Transfer?.CancelTransfers();
    }

    async Task CancelIncomingBeforeAsync(int generation)
    {
        await _receiveGate.WaitAsync().ConfigureAwait(false);
        try { if (_incoming != null && _incoming.Generation < generation) await RemoveIncomingAsync(_incoming).ConfigureAwait(false); }
        finally { _receiveGate.Release(); }
    }

    async Task RemoveIncomingAsync(Incoming incoming)
    {
        if (ReferenceEquals(_incoming, incoming)) _incoming = null;
        if (incoming.Transfer != null)
        {
            await incoming.Transfer.CancelIncomingAsync().ConfigureAwait(false);
            incoming.Transfer.Dispose();
        }
        if (!incoming.Complete)
        {
            DeleteCacheDirectory(incoming.Directory);
            await SendQuietlyAsync(new Packet { Type = Prefix + "cancel", Id = incoming.Id }).ConfigureAwait(false);
        }
    }

    async Task SendQuietlyAsync(Packet packet) { try { await _send(packet).ConfigureAwait(false); } catch { } }

    void DeleteCacheDirectory(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (Path.GetDirectoryName(full) != _directory || !Path.GetFileName(full).StartsWith("batch-", StringComparison.Ordinal) || !Guid.TryParseExact(Path.GetFileName(full)[6..], "N", out _)) return;
            EnsureNoReparsePoints(full);
            if (Directory.Exists(full)) DeleteUnlinkedTree(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("Chưa dọn được bộ nhớ tạm clipboard: " + ex.Message); }
    }

    static void DeleteUnlinkedTree(string directory)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) == 0) DeleteUnlinkedTree(entry);
                else Directory.Delete(entry); // Remove the link itself; never recurse through it.
            }
            else File.Delete(entry);
        }
        Directory.Delete(directory);
    }

    void PruneOldCache()
    {
        // Completed paths may still be in Explorer's clipboard after disconnect or app exit.
        // Keep them for 30 days; clean only our own, unlinked batch folders.
        try
        {
            foreach (string batch in Directory.EnumerateDirectories(_directory, "batch-*"))
            {
                EnsureNoReparsePoints(batch);
                string marker = Path.Combine(batch, ".complete");
                DateTime stamp = File.Exists(marker) ? File.GetLastWriteTimeUtc(marker) : Directory.GetLastWriteTimeUtc(batch);
                if (stamp < DateTime.UtcNow.AddDays(-30)) DeleteCacheDirectory(batch);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { _log("Chưa dọn được cache clipboard cũ: " + ex.Message); }
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        CancelTransfers();
    }
}
