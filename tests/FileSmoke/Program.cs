using LanBridge;
using System.Security.Cryptography;

var root = Path.Combine(Path.GetTempPath(), "LanBridge-file-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var logs = new List<string>();
try
{
    foreach (var name in new[] { "../escape", "..\\escape", "a:b", "CON", "nul.txt", "bad.", "bad ", "COM1.exe" })
    { try { FileTransfer.SafeFileName(name); throw new Exception("Accepted unsafe filename: " + name); } catch (InvalidDataException) { } }
    // Unix permits most of these locally; accepting them on the wire would make the same
    // file offer behave differently on Linux/macOS and Windows (including ADS-like colons).
    var incompatibleNames = "\\/:*?\"<>|".Select(character => "bad" + character + "name.txt")
        .Concat(Enumerable.Range(0, 32).Select(value => "bad" + (char)value + "name.txt")).ToArray();
    foreach (string name in incompatibleNames)
    { try { FileTransfer.SafeFileName(name); throw new Exception("Accepted incompatible wire filename."); } catch (InvalidDataException) { } }
    Assert(FileTransfer.SafeFileName("Tiếng Việt.txt") == "Tiếng Việt.txt", "Unicode filename");
    Assert(FileTransfer.SafeFileName("Ảnh (bản sao) + 100%.txt") == "Ảnh (bản sao) + 100%.txt", "Valid Unicode punctuation filename is unchanged");
    string guardedDirectory = Path.Combine(root, "rejected-offers");
    Packet? guardedResponse = null;
    using (var guardedReceiver = new FileTransfer(guardedDirectory,
        packet => { guardedResponse = packet; return Task.CompletedTask; }, logs.Add))
    {
        foreach (string name in incompatibleNames)
        {
            string offerId = Guid.NewGuid().ToString("N");
            await guardedReceiver.HandleAsync(new Packet { Type = "file-offer", Id = offerId, Name = name, Length = 1 }, true);
            Assert(guardedResponse is { Type: "file-accept", Allow: false } && guardedResponse.Id == offerId,
                "Receiver explicitly declines an incompatible filename");
            Assert(!Directory.Exists(guardedDirectory), "Invalid remote filename creates no receive directory or partial file");
        }
    }
    FileTransfer? sender = null, receiver = null;
    sender = new FileTransfer(Path.Combine(root, "sender"), p => receiver!.HandleAsync(p, true), logs.Add);
    receiver = new FileTransfer(Path.Combine(root, "received"), p => sender!.HandleAsync(p, true), logs.Add);
    using (sender) using (receiver)
    {
        var original = Path.Combine(root, "Tiếng Việt.bin");
        var bytes = RandomNumberGenerator.GetBytes(1_000_003); await File.WriteAllBytesAsync(original, bytes);
        await sender.SendFileAsync(original, CancellationToken.None);
        var received = Directory.GetFiles(receiver.DirectoryPath).Single();
        Assert(CryptographicOperations.FixedTimeEquals(SHA256.HashData(await File.ReadAllBytesAsync(received)), SHA256.HashData(bytes)), "binary file SHA256 roundtrip");
        await sender.SendFileAsync(original, CancellationToken.None);
        Assert(Directory.GetFiles(receiver.DirectoryPath).Length == 2, "unique path does not overwrite");
        var empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, []); await sender.SendFileAsync(empty, CancellationToken.None);
        Assert(Directory.GetFiles(receiver.DirectoryPath).Any(p => new FileInfo(p).Length == 0), "empty transfer");
        var id = Guid.NewGuid().ToString("N");
        await receiver.HandleAsync(new Packet { Type = "file-offer", Id = id, Name = "tampered.txt", Length = 3 }, true);
        await receiver.HandleAsync(new Packet { Type = "file-chunk", Id = id, Data = [1, 2, 3] }, true);
        await ExpectInvalid(() => receiver.HandleAsync(new Packet { Type = "file-end", Id = id, Text = new string('0', 64) }, true));
        Assert(!Directory.GetFiles(receiver.DirectoryPath).Any(p => p.Contains("tampered") || p.EndsWith(".partial")), "tampered file removed");
        id = Guid.NewGuid().ToString("N"); await receiver.HandleAsync(new Packet { Type = "file-offer", Id = id, Name = "oversize.txt", Length = 1 }, true);
        await ExpectInvalid(() => receiver.HandleAsync(new Packet { Type = "file-chunk", Id = id, Data = [1, 2] }, true));
        Assert(!Directory.GetFiles(receiver.DirectoryPath).Any(p => p.EndsWith(".partial")), "oversize chunk cleanup");
        await receiver.HandleAsync(new Packet { Type = "file-offer", Id = Guid.NewGuid().ToString("N"), Name = "denied.txt", Length = 10 }, false);
        Assert(!Directory.GetFiles(receiver.DirectoryPath).Any(p => p.Contains("denied")), "permission denied does not create file");
        id = Guid.NewGuid().ToString("N"); await receiver.HandleAsync(new Packet { Type = "file-offer", Id = id, Name = "cancelled.txt", Length = 100 }, true);
        receiver.CancelIncoming(); Assert(!Directory.GetFiles(receiver.DirectoryPath).Any(p => p.EndsWith(".partial")), "cancel cleanup");
        for (var i = 0; i < 30; i++)
        {
            id = Guid.NewGuid().ToString("N");
            await receiver.HandleAsync(new Packet { Type = "file-offer", Id = id, Name = "racing.bin", Length = FileTransfer.ChunkSize }, true);
            var write = receiver.HandleAsync(new Packet { Type = "file-chunk", Id = id, Data = new byte[FileTransfer.ChunkSize] }, true);
            receiver.CancelIncoming();
            await write;
            // Wait behind cancellation in the same receiver serialization gate.
            await receiver.HandleAsync(new Packet { Type = "noop" }, false);
            Assert(!Directory.GetFiles(receiver.DirectoryPath).Any(p => p.EndsWith(".partial")), "concurrent receive cancellation cleanup");
        }
    }
    FileTransfer? ackSender = null, ackReceiver = null; Packet? heldReceipt = null;
    ackSender = new FileTransfer(Path.Combine(root, "ackSender"), p => ackReceiver!.HandleAsync(p, true), logs.Add);
    ackReceiver = new FileTransfer(Path.Combine(root, "ackReceiver"), p => { if (p.Type == "file-received") { heldReceipt = p; return Task.CompletedTask; } return ackSender!.HandleAsync(p, true); }, logs.Add);
    using (ackSender) using (ackReceiver)
    {
        var file = Path.Combine(root, "ack.txt"); File.WriteAllText(file, "wait for receiver verification");
        var transfer = ackSender.SendFileAsync(file, CancellationToken.None);
        for (var i = 0; heldReceipt == null && i < 100; i++) await Task.Delay(10);
        Assert(heldReceipt != null && !transfer.IsCompleted, "sender waits for receiver receipt");
        await ackSender.HandleAsync(heldReceipt!, true); await transfer;
    }
    Console.WriteLine("PASS file transfers: Unicode, binary, SHA256, empty file, no overwrite, portable filename validation/remote rejection, tamper/size rejection, permission denial, concurrent cancellation and final receipt.");
}
finally { Directory.Delete(root, true); }

static void Assert(bool condition, string label) { if (!condition) throw new Exception("FAIL " + label); }
static async Task ExpectInvalid(Func<Task> action) { try { await action(); } catch (InvalidDataException) { return; } throw new Exception("Expected InvalidDataException"); }
