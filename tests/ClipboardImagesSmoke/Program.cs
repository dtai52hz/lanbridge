using LanBridge;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

int checks = 0;
var payload = RandomNumberGenerator.GetBytes(ClipboardImages.ChunkSize * 4 + 23);
var logs = new List<string>();
ClipboardImages? a = null, b = null;
int readyA = 0, readyB = 0, startedB = 0, chunks = 0;
byte[]? imageA = null, imageB = null;
a = new ClipboardImages(p => b!.HandleAsync(p, true), _ => { });
b = new ClipboardImages(p =>
{
    if (p.Type == "clipboard-image-chunk-received") chunks++;
    return a!.HandleAsync(p, true);
}, logs.Add);
a.ImageReady += png => { readyA++; imageA = png; };
b.ImageReady += png => { readyB++; imageB = png; };
b.ReceivingStarted += () => startedB++;
using (a) using (b)
{
    await a.SetLocalImageAsync(payload).WaitAsync(TimeSpan.FromSeconds(5));
    Check(readyB == 1 && startedB == 1 && imageB!.SequenceEqual(payload), "whole image published exactly once");
    Check(chunks == 5, "one cumulative ACK per 48 KiB chunk");
    Check(logs.Any(s => s.Contains("Ctrl+V")), "ready log");
    await b.SetLocalImageAsync([1, 2, 3]).WaitAsync(TimeSpan.FromSeconds(5));
    Check(readyA == 1 && imageA!.SequenceEqual(new byte[] { 1, 2, 3 }), "reverse direction");
    Check(Clear(a) && Clear(b), "completed transfer releases all service payload references");
    a.CancelTransfers(); b.CancelTransfers();
    Check(Clear(a) && Clear(b), "cancel remains safe after completion");
    await Fails(() => a.SetLocalImageAsync([]), "empty outgoing image");
    await Fails(() => a.SetLocalImageAsync(new byte[ClipboardImages.MaxImageBytes + 1]), "oversized outgoing image");
}

// Every malformed sender packet exercises the same handler used by the TLS session.
foreach (var mutation in new Func<Packet, Packet>[]
{
    p => p.Type == "clipboard-image-offer" ? p with { Length = 0 } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Length = ClipboardImages.MaxImageBytes + 1L } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Text = new string('x', 64) } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Text = "short" } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Text = new string('0', 64) } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Length = 1 } : p,
    p => p.Type == "clipboard-image-offer" ? p with { Length = p.Length + 1 } : p,
    p => p.Type == "clipboard-image-chunk" ? p with { Data = [] } : p,
    p => p.Type == "clipboard-image-chunk" ? p with { Data = new byte[ClipboardImages.ChunkSize + 1] } : p,
    p => p.Type == "clipboard-image-chunk" ? p with { Length = p.Length + 1 } : p,
    p => p.Type == "clipboard-image-chunk" ? p with { Length = -1 } : p,
    p => p.Type == "clipboard-image-chunk" ? p with { Data = Enumerable.Repeat((byte)0x42, p.Data!.Length).ToArray() } : p,
    p => p.Type == "clipboard-image-end" ? p with { Length = p.Length - 1 } : p,
    p => p.Type == "clipboard-image-end" ? p with { Text = new string('0', 64) } : p,
})
{
    ClipboardImages? sender = null, receiver = null; int ready = 0;
    sender = new ClipboardImages(p => receiver!.HandleAsync(mutation(p), true), _ => { });
    receiver = new ClipboardImages(p => sender!.HandleAsync(p, true), _ => { });
    receiver.ImageReady += _ => ready++;
    using (sender) using (receiver)
    {
        await Fails(() => sender.SetLocalImageAsync(payload), "reject malformed offer/chunk/end");
        Check(ready == 0 && Clear(sender) && Clear(receiver), "malformed image never published or retained");
    }
}

// No Files permission is needed; allowed is the independent Clipboard permission.
ClipboardImages? deniedSender = null, deniedReceiver = null; int deniedReady = 0;
deniedSender = new ClipboardImages(p => deniedReceiver!.HandleAsync(p, false), _ => { });
deniedReceiver = new ClipboardImages(p => deniedSender!.HandleAsync(p, true), _ => { });
deniedReceiver.ImageReady += _ => deniedReady++;
using (deniedSender) using (deniedReceiver)
{
    await Fails(() => deniedSender.SetLocalImageAsync(payload), "clipboard permission denied");
    Check(deniedReady == 0 && Clear(deniedSender) && Clear(deniedReceiver), "denial leaves no state");
}

// A forged/stale ACK cannot release the next chunk; the expected transfer ID and offset are both required.
ClipboardImages? ackSender = null, ackReceiver = null;
var heldAck = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
var releaseAck = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
int sentChunks = 0;
ackSender = new ClipboardImages(p =>
{
    if (p.Type == "clipboard-image-chunk") Interlocked.Increment(ref sentChunks);
    Check(JsonSerializer.SerializeToUtf8Bytes(p, WireJson.Options).Length < 4 * 1024 * 1024, "packet fits transport bound");
    return ackReceiver!.HandleAsync(p, true);
}, _ => { });
ackReceiver = new ClipboardImages(async p =>
{
    if (p.Type == "clipboard-image-chunk-received" && p.Length == ClipboardImages.ChunkSize)
    {
        heldAck.TrySetResult(p);
        await releaseAck.Task;
    }
    await ackSender!.HandleAsync(p, true);
}, _ => { });
using (ackSender) using (ackReceiver)
{
    var transfer = ackSender.SetLocalImageAsync(payload);
    var ack = await heldAck.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await ackSender.HandleAsync(ack with { Id = Guid.NewGuid().ToString("N") }, true);
    await ackSender.HandleAsync(ack with { Length = ack.Length + 1 }, true);
    await Task.Delay(50);
    Check(sentChunks == 1 && !transfer.IsCompleted, "one chunk in flight despite wrong-ID/offset ACKs");
    releaseAck.TrySetResult();
    await transfer.WaitAsync(TimeSpan.FromSeconds(5));
    Check(sentChunks == 5, "matching ACK resumes flow");
}

// Cancel, permission revocation and disposal during a held chunk never publish a stale screenshot.
foreach (string action in new[] { "local", "receiver", "permission", "dispose" })
{
    ClipboardImages? sender = null, receiver = null;
    var held = new TaskCompletionSource<Packet>(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int ready = 0; bool allow = true;
    sender = new ClipboardImages(async p =>
    {
        if (p.Type == "clipboard-image-chunk") { held.TrySetResult(p); await release.Task; }
        await receiver!.HandleAsync(p, allow);
    }, _ => { });
    receiver = new ClipboardImages(p => sender!.HandleAsync(p, true), _ => { });
    receiver.ImageReady += _ => ready++;
    using (sender) using (receiver)
    {
        var transfer = sender.SetLocalImageAsync(payload);
        var chunk = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (action == "local") sender.CancelTransfers();
        else if (action == "receiver") receiver.CancelTransfers();
        else if (action == "permission") { allow = false; await receiver.HandleAsync(chunk, false); }
        else receiver.Dispose();
        release.TrySetResult();
        // A disposed peer cannot send a cancellation response through its closed adapter.
        if (action == "dispose") sender.CancelTransfers();
        await Fails(() => transfer, "mid-transfer " + action);
        await receiver.HandleAsync(chunk, true); // late queued data
        Check(ready == 0 && Clear(sender) && Clear(receiver), "late chunk remains canceled: " + action);
    }
}

// A newly copied image replaces an outgoing image even while its network write is blocked.
ClipboardImages? replacingSender = null, replacingReceiver = null;
var oldChunkHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var oldChunkRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
string? oldId = null; int replacedReady = 0; byte[]? replacementImage = null;
replacingSender = new ClipboardImages(async p =>
{
    if (p.Type == "clipboard-image-offer" && oldId == null) oldId = p.Id;
    if (p.Type == "clipboard-image-chunk" && p.Id == oldId) { oldChunkHeld.TrySetResult(); await oldChunkRelease.Task; }
    await replacingReceiver!.HandleAsync(p, true);
}, _ => { });
replacingReceiver = new ClipboardImages(p => replacingSender!.HandleAsync(p, true), _ => { });
replacingReceiver.ImageReady += bytes => { replacedReady++; replacementImage = bytes; };
using (replacingSender) using (replacingReceiver)
{
    var oldTransfer = replacingSender.SetLocalImageAsync(payload);
    await oldChunkHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await replacingSender.SetLocalImageAsync([3, 4, 5]).WaitAsync(TimeSpan.FromSeconds(5));
    await Fails(() => oldTransfer, "new local image cancels blocked old outgoing");
    oldChunkRelease.TrySetResult();
    await Task.Delay(20);
    Check(replacedReady == 1 && replacementImage!.SequenceEqual(new byte[] { 3, 4, 5 }), "only new outgoing image published");
    Check(Clear(replacingSender) && Clear(replacingReceiver), "replaced outgoing payloads released");
}

// Mutation of a caller-owned array after offer cannot change the snapshot being transferred.
var mutable = RandomNumberGenerator.GetBytes(1000);
var original = mutable.ToArray();
ClipboardImages? snapshotSender = null, snapshotReceiver = null; byte[]? snap = null;
snapshotSender = new ClipboardImages(p =>
{
    if (p.Type == "clipboard-image-offer") Array.Clear(mutable);
    return snapshotReceiver!.HandleAsync(p, true);
}, _ => { });
snapshotReceiver = new ClipboardImages(p => snapshotSender!.HandleAsync(p, true), _ => { });
snapshotReceiver.ImageReady += bytes => snap = bytes;
using (snapshotSender) using (snapshotReceiver)
{
    await snapshotSender.SetLocalImageAsync(mutable).WaitAsync(TimeSpan.FromSeconds(5));
    Check(snap!.SequenceEqual(original), "outgoing snapshot independent of caller changes");
}

// A later image supersedes an incomplete earlier offer, with no partial publication.
var emitted = new List<Packet>(); int published = 0; byte[]? latest = null;
using (var receiver = new ClipboardImages(p => { emitted.Add(p); return Task.CompletedTask; }, _ => { }))
{
    receiver.ImageReady += png => { published++; latest = png; };
    await receiver.HandleAsync(Offer([1]) with { Id = "not-a-guid" }, true);
    Check(Clear(receiver) && emitted.Last().Type == "clipboard-image-accept" && !emitted.Last().Allow, "invalid GUID offer rejected");
    var old = Offer(payload);
    var replacement = Offer([8, 9]);
    await receiver.HandleAsync(old, true);
    await receiver.HandleAsync(new Packet { Type = "clipboard-image-chunk", Id = old.Id, Length = ClipboardImages.ChunkSize, Data = payload[..ClipboardImages.ChunkSize] }, true);
    await receiver.HandleAsync(replacement, true);
    Check(emitted.Any(p => p.Type == "clipboard-image-cancel" && p.Id == old.Id), "new offer cancels old sender");
    await receiver.HandleAsync(new Packet { Type = "clipboard-image-end", Id = old.Id, Length = payload.Length, Text = old.Text }, true);
    await receiver.HandleAsync(new Packet { Type = "clipboard-image-chunk", Id = replacement.Id, Length = 2, Data = [8, 9] }, true);
    Check(published == 0, "never publish partial data");
    await receiver.HandleAsync(new Packet { Type = "clipboard-image-end", Id = replacement.Id, Length = 2, Text = replacement.Text }, true);
    Check(published == 1 && latest!.SequenceEqual(new byte[] { 8, 9 }) && Clear(receiver), "latest whole image wins");
    await receiver.HandleAsync(replacement with { Length = ClipboardImages.MaxImageBytes }, true);
    receiver.CancelTransfers();
    Check(Clear(receiver), "maximum declared image canceled without retained buffer");
}

// A duplicate/out-of-order chunk cancels only its matching image, never another transfer ID.
using (var receiver = new ClipboardImages(_ => Task.CompletedTask, _ => { }))
{
    var offer = Offer(payload);
    var first = new Packet { Type = "clipboard-image-chunk", Id = offer.Id, Length = ClipboardImages.ChunkSize, Data = payload[..ClipboardImages.ChunkSize] };
    await receiver.HandleAsync(offer, true);
    await receiver.HandleAsync(first with { Id = Guid.NewGuid().ToString("N") }, true);
    Check(!Clear(receiver), "foreign chunk cannot erase current image");
    await receiver.HandleAsync(first, true);
    await receiver.HandleAsync(first, true);
    Check(Clear(receiver), "duplicate chunk releases state");
    await receiver.HandleAsync(offer, true);
    await receiver.HandleAsync(new Packet { Type = "clipboard-image-end", Id = offer.Id, Length = payload.Length, Text = offer.Text }, true);
    Check(Clear(receiver), "early end releases state");
}

Console.WriteLine($"ClipboardImagesSmoke: {checks} checks passed.");

Packet Offer(byte[] bytes) => new() { Type = "clipboard-image-offer", Id = Guid.NewGuid().ToString("N"), Length = bytes.Length, Text = Convert.ToHexString(SHA256.HashData(bytes)) };
bool Clear(ClipboardImages service) => typeof(ClipboardImages).GetField("_incoming", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) == null
    && typeof(ClipboardImages).GetField("_outgoing", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service) == null;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); checks++; }
async Task Fails(Func<Task> run, string name)
{
    try { await run().WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { checks++; return; }
    throw new Exception("Expected failure: " + name);
}
