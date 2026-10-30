using LanBridge;
using System.Diagnostics;
using System.Security.Cryptography;

var root = Path.Combine(Path.GetTempPath(), "LanBridge-clipboard-files-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
try
{
    foreach (string bad in new[] { "../escape", "..\\escape", "/escape", "C:/escape", "a//b", "folder/../escape", "folder\\escape", "CON", "nul.txt", "CON .txt", "COM¹", "LPT².txt", "CONIN$", "CONOUT$.txt", "file.", "file ", "a:b", "a\0b", ".", ".." })
        Reject([new() { Path = bad }], "reject path " + bad);
    Reject(null, "null manifest"); Reject([], "empty manifest");
    Reject([new() { Path = "a", Length = -1 }], "negative length");
    Reject([new() { Path = "a", Length = FileTransfer.MaxFileSize + 1 }], "oversize file");
    Reject([new() { Path = "a", IsDirectory = true, Length = 1 }], "directory bytes");
    Reject([new() { Path = "a" }, new() { Path = "A" }], "case-insensitive duplicate");
    Reject([new() { Path = "a" }, new() { Path = "a/child" }], "file cannot be a parent");
    Reject([new() { Path = "a/child" }], "unlisted parent");
    Reject(Enumerable.Range(0, ClipboardFiles.MaxEntries + 1).Select(i => new ClipboardFileEntry { Path = "f" + i }).ToArray(), "entry count");
    Reject(Enumerable.Range(0, 5).Select(i => new ClipboardFileEntry { Path = "f" + i, Length = FileTransfer.MaxFileSize }).ToArray(), "batch bytes");
    Reject([new() { Path = string.Join('/', Enumerable.Repeat("a", ClipboardFiles.MaxDepth + 1)) }], "depth bound");
    Assert(ClipboardFiles.ValidateManifest([new() { Path = "Tiếng Việt", IsDirectory = true }, new() { Path = "Tiếng Việt/ảnh.txt", Length = 2 }]).Length == 2, "Unicode relative manifest");

    var source = Path.Combine(root, "source"); Directory.CreateDirectory(source);
    var folder = Path.Combine(source, "Tài liệu"); Directory.CreateDirectory(Path.Combine(folder, "Thư mục trống"));
    Directory.CreateDirectory(Path.Combine(folder, "con" + " hợp lệ"));
    var bytes = RandomNumberGenerator.GetBytes(1_000_003);
    File.WriteAllBytes(Path.Combine(folder, "ảnh.bin"), bytes);
    File.WriteAllText(Path.Combine(folder, "xin chào.txt"), "Tiếng Việt có dấu");
    File.WriteAllBytes(Path.Combine(folder, "zero.txt"), []);
    // Names ending in .partial must not collide with transfer staging files.
    File.WriteAllText(Path.Combine(folder, "a.partial"), "existing original name");
    File.WriteAllText(Path.Combine(folder, "a"), "another original name");
    var loose = Path.Combine(source, "file lẻ.txt"); File.WriteAllText(loose, "Ctrl+C rồi Ctrl+V");
    var logs = new List<string>(); int aPackets = 0, bPackets = 0;
    ClipboardFiles? a = null, b = null;
    a = new ClipboardFiles(Path.Combine(root, "cache-a"), p => { Interlocked.Increment(ref aPackets); return b!.HandleAsync(p, true); }, logs.Add);
    b = new ClipboardFiles(Path.Combine(root, "cache-b"), p => { Interlocked.Increment(ref bPackets); return a!.HandleAsync(p, true); }, logs.Add);
    var readyB = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
    int bStarted = 0, bReadyCount = 0;
    b.ReceivingStarted += () => bStarted++;
    b.FilesReady += files => { bReadyCount++; readyB.TrySetResult(files); };
    string[] received;
    using (a) using (b)
    {
        await a.SetLocalFilesAsync([folder, loose]).WaitAsync(TimeSpan.FromSeconds(10));
        received = await readyB.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(bStarted == 1 && bReadyCount == 1, "one receiving event and whole-batch publish");
        Assert(received.Select(Path.GetFileName).SequenceEqual(new[] { "Tài liệu", "file lẻ.txt" }), "multiple top-level original names preserved");
        var receivedFolder = received[0];
        Assert(SHA256.HashData(File.ReadAllBytes(Path.Combine(receivedFolder, "ảnh.bin"))).SequenceEqual(SHA256.HashData(bytes)), "chunked SHA256 binary roundtrip");
        Assert(File.ReadAllText(Path.Combine(receivedFolder, "xin chào.txt")) == "Tiếng Việt có dấu", "Unicode filename and content");
        Assert(Directory.Exists(Path.Combine(receivedFolder, "Thư mục trống")) && !Directory.EnumerateFileSystemEntries(Path.Combine(receivedFolder, "Thư mục trống")).Any(), "empty directory preserved");
        Assert(new FileInfo(Path.Combine(receivedFolder, "zero.txt")).Length == 0, "zero-byte file");
        Assert(File.ReadAllText(Path.Combine(receivedFolder, "a.partial")) == "existing original name" && File.ReadAllText(Path.Combine(receivedFolder, "a")) == "another original name", "original partial suffix does not collide with staging");
        int previous = bPackets;
        await b.SetLocalFilesAsync(received);
        Assert(bPackets == previous, "received cache never echoed");
        Assert(b.IsCachePath(received[0]) && !b.IsCachePath(source), "cache path detection");
        var readyA = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        a.FilesReady += files => readyA.TrySetResult(files);
        var reverse = Path.Combine(source, "Từ máy B.txt"); File.WriteAllText(reverse, "chiều ngược lại");
        await b.SetLocalFilesAsync([reverse]).WaitAsync(TimeSpan.FromSeconds(10));
        Assert(File.ReadAllText((await readyA.Task.WaitAsync(TimeSpan.FromSeconds(5)))[0]) == "chiều ngược lại", "bidirectional files");
        b.CancelTransfers();
        Assert(received.All(p => File.Exists(p) || Directory.Exists(p)), "completed cache retained on cancel");
    }
    Assert(received.All(p => File.Exists(p) || Directory.Exists(p)), "completed cache retained after disposal");

    // Permission denial must fail promptly, never create a receiver batch or publish a clipboard.
    int deniedReady = 0; ClipboardFiles? deniedSender = null, deniedReceiver = null;
    deniedSender = new ClipboardFiles(Path.Combine(root, "deny-a"), p => deniedReceiver!.HandleAsync(p, false), _ => { });
    deniedReceiver = new ClipboardFiles(Path.Combine(root, "deny-b"), p => deniedSender!.HandleAsync(p, true), _ => { });
    deniedReceiver.FilesReady += _ => deniedReady++;
    using (deniedSender) using (deniedReceiver)
    {
        await ExpectFailure(() => deniedSender.SetLocalFilesAsync([loose]));
        Assert(deniedReady == 0 && !Directory.EnumerateDirectories(deniedReceiver.DirectoryPath).Any(), "permission denial leaves no partial cache");
    }

    // Inject a bad final digest through the same packet API used by TLS.
    int tamperedReady = 0; ClipboardFiles? badSender = null, badReceiver = null;
    badSender = new ClipboardFiles(Path.Combine(root, "bad-a"), p => badReceiver!.HandleAsync(p.Type == "clipboard-files-data-file-end" ? p with { Text = new string('0', 64) } : p, true), _ => { });
    badReceiver = new ClipboardFiles(Path.Combine(root, "bad-b"), p => badSender!.HandleAsync(p, true), _ => { });
    badReceiver.FilesReady += _ => tamperedReady++;
    using (badSender) using (badReceiver)
    {
        await ExpectFailure(() => badSender.SetLocalFilesAsync([loose]));
        await Eventually(() => !Directory.EnumerateDirectories(badReceiver.DirectoryPath).Any());
        Assert(tamperedReady == 0, "bad hash never publishes or retains payload");
    }

    // Pause actual data after the offer; cancel during reception, then release a stale queued chunk.
    var chunkHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseChunk = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ClipboardFiles? cancelSender = null, cancelReceiver = null; int canceledReady = 0;
    cancelSender = new ClipboardFiles(Path.Combine(root, "cancel-a"), async p =>
    {
        if (p.Type == "clipboard-files-data-file-chunk") { chunkHeld.TrySetResult(); await releaseChunk.Task; }
        await cancelReceiver!.HandleAsync(p, true);
    }, _ => { });
    cancelReceiver = new ClipboardFiles(Path.Combine(root, "cancel-b"), p => cancelSender!.HandleAsync(p, true), _ => { });
    cancelReceiver.FilesReady += _ => canceledReady++;
    using (cancelSender) using (cancelReceiver)
    {
        var transfer = cancelSender.SetLocalFilesAsync([Path.Combine(folder, "ảnh.bin")]);
        await chunkHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(Directory.EnumerateFiles(cancelReceiver.DirectoryPath, "*.partial", SearchOption.AllDirectories).Any(), "partial created before cancel");
        cancelReceiver.CancelTransfers();
        await Eventually(() => !Directory.EnumerateDirectories(cancelReceiver.DirectoryPath).Any());
        releaseChunk.TrySetResult();
        await ExpectFailure(() => transfer);
        Assert(canceledReady == 0, "cancel plus stale chunk never publishes");
    }

    // Model a transport that has accepted writes while receiver chunk receipts are delayed.
    // There must never be a second 48 KiB chunk outstanding, even for a large source file.
    ClipboardFiles? flowSender = null, flowReceiver = null; int flowChunks = 0, flowReady = 0, receipts = 0;
    var firstReceipt = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
    var secondReceipt = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
    flowSender = new ClipboardFiles(Path.Combine(root, "flow-a"), p =>
    {
        if (p.Type == "clipboard-files-data-file-chunk") Interlocked.Increment(ref flowChunks);
        return flowReceiver!.HandleAsync(p, true);
    }, _ => { });
    flowReceiver = new ClipboardFiles(Path.Combine(root, "flow-b"), p =>
    {
        if (p.Type == "clipboard-files-data-file-chunk-received")
        {
            int number = Interlocked.Increment(ref receipts);
            if (number == 1) { firstReceipt.TrySetResult(p); return Task.CompletedTask; }
            if (number == 2) { secondReceipt.TrySetResult(p); return Task.CompletedTask; }
        }
        return flowSender!.HandleAsync(p, true);
    }, _ => { });
    flowReceiver.FilesReady += _ => flowReady++;
    using (flowSender) using (flowReceiver)
    {
        var transfer = flowSender.SetLocalFilesAsync([Path.Combine(folder, "ảnh.bin")]);
        Packet first = await firstReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(first.Length == FileTransfer.ChunkSize, "chunk receipt contains exact cumulative stored offset");
        await Task.Delay(100);
        Assert(flowChunks == 1 && !transfer.IsCompleted && flowReady == 0, "slow receiver bounds first chunk in flight");
        await flowSender.HandleAsync(first with { Id = Guid.NewGuid().ToString("N") }, true);
        await flowSender.HandleAsync(first with { Length = first.Length + 1 }, true);
        await flowSender.HandleAsync(first with { BatchId = Guid.NewGuid().ToString("N") }, true);
        await Task.Delay(100);
        Assert(flowChunks == 1 && !transfer.IsCompleted, "wrong file ID, offset and batch receipts ignored");
        await flowSender.HandleAsync(first, true);
        Packet second = await secondReceipt.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert(flowChunks == 2 && second.Length == FileTransfer.ChunkSize * 2L, "exactly one next chunk after correct receipt");
        await flowSender.HandleAsync(first, true); // Delayed duplicate must not acknowledge a newer chunk.
        await Task.Delay(100);
        Assert(flowChunks == 2 && !transfer.IsCompleted, "old-offset duplicate cannot release next chunk");
        await flowSender.HandleAsync(second, true);
        await transfer.WaitAsync(TimeSpan.FromSeconds(10));
        Assert(flowChunks > 2 && flowReady == 1, "remaining acknowledged chunks complete verified batch");
    }

    ClipboardFiles? ackCancelSender = null, ackCancelReceiver = null; int ackCancelChunks = 0, ackCancelReady = 0;
    var waitingForAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    ackCancelSender = new ClipboardFiles(Path.Combine(root, "ack-cancel-a"), p =>
    {
        if (p.Type == "clipboard-files-data-file-chunk") Interlocked.Increment(ref ackCancelChunks);
        return ackCancelReceiver!.HandleAsync(p, true);
    }, _ => { });
    ackCancelReceiver = new ClipboardFiles(Path.Combine(root, "ack-cancel-b"), p =>
    {
        if (p.Type == "clipboard-files-data-file-chunk-received") { waitingForAck.TrySetResult(); return Task.CompletedTask; }
        return ackCancelSender!.HandleAsync(p, true);
    }, _ => { });
    ackCancelReceiver.FilesReady += _ => ackCancelReady++;
    using (ackCancelSender) using (ackCancelReceiver)
    {
        var transfer = ackCancelSender.SetLocalFilesAsync([Path.Combine(folder, "ảnh.bin")]);
        await waitingForAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
        ackCancelSender.CancelTransfers();
        await ExpectFailure(() => transfer);
        await Eventually(() => !Directory.EnumerateDirectories(ackCancelReceiver.DirectoryPath).Any());
        Assert(ackCancelChunks == 1 && ackCancelReady == 0, "cancel while waiting for chunk ACK releases promptly and removes partial batch");
    }

    // A new offer supersedes an incomplete batch; old chunks cannot damage the new one.
    var responses = new List<Packet>();
    using (var receiver = new ClipboardFiles(Path.Combine(root, "supersede"), p => { responses.Add(p); return Task.CompletedTask; }, _ => { }))
    {
        string first = Guid.NewGuid().ToString("N"), second = Guid.NewGuid().ToString("N"), fileId = Guid.NewGuid().ToString("N");
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = first, ClipboardEntries = [new() { Path = "old.txt", Length = 10 }] }, true);
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-data-file-offer", BatchId = first, Id = fileId, Name = "00000000", Length = 10 }, true);
        string[]? nextReady = null; receiver.FilesReady += paths => nextReady = paths;
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = second, ClipboardEntries = [new() { Path = "rỗng", IsDirectory = true }] }, true);
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-data-file-chunk", BatchId = first, Id = fileId, Data = [1, 2], Length = 2 }, true);
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-end", Id = second }, true);
        Assert(!Directory.Exists(Path.Combine(receiver.DirectoryPath, "batch-" + first)), "superseded batch deleted");
        Assert(nextReady is { Length: 1 } && Directory.Exists(nextReady[0]), "empty-directory-only batch commits despite stale old chunk");
        int before = responses.Count;
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = "bad id", ClipboardEntries = [new() { Path = "a" }] }, true);
        Assert(responses.Count == before + 1 && responses[^1].Type == "clipboard-files-accept" && !responses[^1].Allow, "invalid ID actively rejected");
        await receiver.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = Guid.NewGuid().ToString("N"), ClipboardEntries = [new() { Path = "../escape" }] }, true);
        Assert(!responses[^1].Allow && !File.Exists(Path.Combine(root, "escape")), "remote traversal rejected without disk escape");
    }

    // A queued remote offer must cancel the current local send, not a stale snapshot taken before its gate wait.
    var gateHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var releaseGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var localOfferSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    using (var race = new ClipboardFiles(Path.Combine(root, "copy-race"), async p =>
    {
        if (p.Type == "clipboard-files-data-file-accept") { gateHeld.TrySetResult(); await releaseGate.Task; }
        if (p.Type == "clipboard-files-offer") localOfferSent.TrySetResult();
    }, _ => { }))
    {
        string initial = Guid.NewGuid().ToString("N");
        await race.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = initial, ClipboardEntries = [new() { Path = "old.txt", Length = 0 }] }, true);
        var holding = race.HandleAsync(new Packet { Type = "clipboard-files-data-file-offer", BatchId = initial, Id = Guid.NewGuid().ToString("N"), Name = "00000000", Length = 0 }, true);
        await gateHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var queuedRemote = race.HandleAsync(new Packet { Type = "clipboard-files-offer", Id = Guid.NewGuid().ToString("N"), ClipboardEntries = [new() { Path = "new folder", IsDirectory = true }] }, true);
        var newLocal = race.SetLocalFilesAsync([loose]);
        await localOfferSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseGate.TrySetResult();
        await Task.WhenAll(holding, queuedRemote).WaitAsync(TimeSpan.FromSeconds(5));
        await ExpectFailure(() => newLocal);
        Assert(true, "simultaneous queued remote/local copy cancels promptly instead of waiting 20 seconds");
    }

    // Local junctions must not bypass source/cache validation. Junction creation needs no admin on Windows.
    if (OperatingSystem.IsWindows())
    {
        string link = Path.Combine(root, "junction"), target = Path.Combine(root, "outside"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "keep.txt"), "outside");
        var info = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("/c"); info.ArgumentList.Add("mklink"); info.ArgumentList.Add("/J"); info.ArgumentList.Add(link); info.ArgumentList.Add(target);
        using var process = Process.Start(info)!; await process.WaitForExitAsync();
        Assert(process.ExitCode == 0, "test junction created");
        try
        {
            using var junctionSender = new ClipboardFiles(Path.Combine(root, "junction-cache"), _ => throw new Exception("must not send junction"), _ => { });
            await ExpectFailure(() => junctionSender.SetLocalFilesAsync([link]));
            try { ClipboardFiles.EnsureNoReparsePoints(Path.Combine(link, "keep.txt")); throw new Exception("accepted junction ancestor"); } catch (IOException) { checks++; }
            Assert(File.ReadAllText(Path.Combine(target, "keep.txt")) == "outside", "junction target untouched");
        }
        finally { Directory.Delete(link); }
    }
    Console.WriteLine($"PASS clipboard files: {checks} checks; bidirectional Unicode files/folders, SHA256, one-chunk flow control, wrong/stale ACKs, no echo, permission denial, tamper rejection, cancellation, supersession, limits, traversal and junction protection.");
}
finally { Directory.Delete(root, true); }

void Assert(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); checks++; }
void Reject(ClipboardFileEntry[]? entries, string label)
{
    try { ClipboardFiles.ValidateManifest(entries); } catch (InvalidDataException) { checks++; return; }
    throw new Exception("FAIL accepted " + label);
}
async Task ExpectFailure(Func<Task> action)
{
    try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { checks++; return; }
    throw new Exception("FAIL expected transfer failure");
}
async Task Eventually(Func<bool> predicate)
{
    for (int i = 0; i < 100; i++) { if (predicate()) { checks++; return; } await Task.Delay(20); }
    throw new Exception("FAIL asynchronous cleanup");
}
