using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Net.Security;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LanBridge;
using LanBridge.CrossPlatform;
using LanBridge.Desktop;

internal static class SmokeProgram
{
    private static int _assertions;
    [STAThread]
    public static int Main()
    {
        try
        {
            RunSessionsAsync().GetAwaiter().GetResult();
            RunHeadlessUi();
            Console.WriteLine($"Desktop smoke passed: {_assertions} assertions; nine labeled OS protocol pairs over real loopback TLS, fake input/clipboard, headless UI. No native hooks or physical input.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Check(bool condition, string message)
    {
        Interlocked.Increment(ref _assertions);
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task WaitAsync(Func<bool> condition, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!condition())
        {
            if (timer.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(10).ConfigureAwait(false);
        }
    }

    private static async Task RunSessionsAsync()
    {
        string workspace = Path.Combine(Path.GetTempPath(), "LanBridge-DesktopSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            // These names cover protocol/permission interoperability, not native runtime testing on those OSes.
            foreach (string source in new[] { "Windows", "macOS", "Linux" })
                foreach (string receiver in new[] { "Windows", "macOS", "Linux" })
                    await CheckPairAsync(source, receiver, workspace).ConfigureAwait(false);
            await CheckDeclinedConsentAsync().ConfigureAwait(false);
            await CheckHelloGateAsync(workspace).ConfigureAwait(false);
            await CheckAndroidScreenHostAsync(workspace).ConfigureAwait(false);
            await CheckQueuedScreenPermissionAsync(workspace).ConfigureAwait(false);
            foreach (var first in new[]
            {
                new Packet { Type = "enter", X = 32768, Y = 32768 },
                new Packet { Type = "text", Text = "must-not-type" },
                new Packet { Type = "file-offer", Id = Guid.NewGuid().ToString("N"), Name = "pre-hello.txt", Length = 4 },
            }) await CheckRejectedSequenceAsync(workspace, false, first,
                new Packet { Type = "hello", Name = "late-hello", Flags = 2, Width = 1920, Height = 1080 }).ConfigureAwait(false);
            await CheckRejectedSequenceAsync(workspace, true,
                new Packet { Type = "hello", Name = "duplicate", Flags = 2, Width = 1920, Height = 1080 }).ConfigureAwait(false);
            await CheckRejectedSequenceAsync(workspace, true,
                new Packet { Type = "move", X = 32768, Y = 32768, Data = new byte[FileTransfer.ChunkSize * 2] }).ConfigureAwait(false);
            await CheckRejectedSequenceAsync(workspace, true,
                new Packet { Type = "move", X = 32768, Y = 32768, Button = new string('x', 65536) }).ConfigureAwait(false);
            await CheckRejectedSequenceAsync(workspace, true,
                new Packet { Type = "chat", Text = "unused-field-attack", BatchId = new string('x', 65536) }).ConfigureAwait(false);
            foreach (var hello in new[]
            {
                new Packet { Type = "hello", Name = "old-peer", Flags = 1, Width = 1920, Height = 1080 },
                new Packet { Type = "hello", Name = "invalid-size", Flags = 2, Width = 1, Height = 1080 },
                new Packet { Type = "hello", Name = "invalid-size", Flags = 2, Width = 1920, Height = 65537 },
            }) await CheckMalformedHelloAsync(workspace, hello).ConfigureAwait(false);
        }
        finally
        {
            // Only this test's explicitly created unique directory is removed.
            var resolved = Path.GetFullPath(workspace);
            string prefix = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "LanBridge-DesktopSmoke-");
            if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
                Directory.Delete(resolved, true);
        }
    }

    private static async Task CheckPairAsync(string source, string receiver, string workspace)
    {
        using var wire = await WirePair.ConnectAsync(source).ConfigureAwait(false);
        using var sourceBackend = new MemoryBackend(source);
        using var receiverBackend = new MemoryBackend(receiver);
        await using var aInput = new PortableInputRouter(sourceBackend);
        await using var bInput = new PortableInputRouter(receiverBackend);
        Check(sourceBackend.Starts == 0 && receiverBackend.Starts == 0, "Approval must finish before backends start.");
        aInput.Start(); bInput.Start();
        Check(wire.Approvals == 1, "A discovered connection must receive explicit B consent.");
        var grants = new Permissions { Input = true, Clipboard = true, Files = true };
        string receiveA = Path.Combine(workspace, source + "-" + receiver + "-a");
        string receiveB = Path.Combine(workspace, source + "-" + receiver + "-b");
        using var a = new PortablePeerSession(wire.A, aInput, false, grants, receiveA);
        using var b = new PortablePeerSession(wire.B, bInput, true, grants, receiveB);
        var aLog = new ConcurrentQueue<string>(); var bLog = new ConcurrentQueue<string>();
        a.Log += aLog.Enqueue; b.Log += bLog.Enqueue;
        string? clipboardA = null, clipboardB = null;
        a.SetClipboardText = text => { Volatile.Write(ref clipboardA, text); return Task.CompletedTask; };
        b.SetClipboardText = text => { Volatile.Write(ref clipboardB, text); return Task.CompletedTask; };
        a.Start(sourceBackend.Bounds, source); b.Start(receiverBackend.Bounds, receiver);
        await WaitAsync(() => a.InputAllowed && b.InputAllowed && aInput.CanControl && bInput.CanControl,
            "Hello/permission snapshot did not enable the authenticated session.").ConfigureAwait(false);
        Check(aInput.IsController && !bInput.IsController, "A/B role routing changed between OS labels.");
        Check(aInput.RemoteWidth == receiverBackend.Bounds.Width && bInput.RemoteHeight == sourceBackend.Bounds.Height,
            "Desktop dimensions did not cross TLS.");
        Check(wire.AtA.Any(p => p.Type == "hello" && p.Flags == 2) && wire.AtB.Any(p => p.Type == "hello" && p.Flags == 2),
            "Every desktop must advertise Unicode-compatible hello.");

        Check(sourceBackend.Key(0x78, true), "F9 trigger must be consumed on source A.");
        Check(sourceBackend.Key(0x78, false), "F9 keyup must be consumed on source A.");
        await WaitAsync(() => receiverBackend.Applied.Any(p => p.Type == "enter"), "F9 did not enter B.").ConfigureAwait(false);
        Check(aInput.IsRemote && bInput.IsRemote && sourceBackend.Capturing, "F9 did not capture the source and enter receiver.");
        var enter = receiverBackend.Applied.First(p => p.Type == "enter");
        Check(Math.Abs(enter.X - 32768) <= 1 && Math.Abs(enter.Y - 32768) <= 1, "Shortcut entry must center B's pointer.");
        const string typed = "tieengs Vieetj . , ? !";
        foreach (char character in typed) sourceBackend.Character(character);
        await WaitAsync(() => Reconstruct(receiverBackend.Applied) == "tiếng Việt . , ? !", "Telex or punctuation changed over TLS.").ConfigureAwait(false);
        Check(receiverBackend.Applied.Any(p => p.Type == "text" && p.Dx > 0), "Telex must exercise real ordered correction packets.");
        Check(!receiverBackend.Applied.Any(p => p.Type == "key" && p.Key == 0x78), "Shortcut trigger leaked into B.");

        sourceBackend.Emit(new(PhysicalInputKind.Move, Relative: true, DeltaX: 9, DeltaY: 7));
        sourceBackend.Emit(new(PhysicalInputKind.Wheel, WheelY: 120, WheelX: -120));
        sourceBackend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: true));
        sourceBackend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: false));
        await WaitAsync(() => receiverBackend.Applied.Count(p => p.Type == "button") >= 2, "Mouse actions did not reach B.").ConfigureAwait(false);
        var mouse = receiverBackend.Applied.Where(p => p.Type is "move" or "wheel" or "button").ToArray();
        Check(mouse.Any(p => p.Type == "move") && mouse.Any(p => p.Type == "wheel" && p.Dy == 120 && p.Dx == -120), "Move/wheel wire units changed.");
        Check(mouse[^2].Type == "button" && mouse[^2].Down && mouse[^1].Type == "button" && !mouse[^1].Down,
            "Button down/up order changed.");

        // Receiver revoke is immediate; source learns it over the same authenticated session.
        int cleanupBefore = receiverBackend.Releases;
        b.SetLocalPermissions(grants with { Input = false });
        Check(!bInput.CanControl && !bInput.IsRemote, "B did not revoke its local input gate immediately.");
        await WaitAsync(() => !aInput.CanControl && !aInput.IsRemote && receiverBackend.Releases > cleanupBefore,
            "Revocation failed to release A capture/B held input.").ConfigureAwait(false);
        int entersBefore = receiverBackend.Applied.Count(p => p.Type == "enter");
        Check(!sourceBackend.Key(0x78, true) && !sourceBackend.Key(0x78, false), "Revoked shortcut must pass locally.");
        a.SendChat("revoke-barrier");
        await WaitAsync(() => bLog.Any(s => s.Contains("revoke-barrier", StringComparison.Ordinal)), "Revoke barrier did not arrive.").ConfigureAwait(false);
        Check(receiverBackend.Applied.Count(p => p.Type == "enter") == entersBefore, "Shortcut re-entered without B's permission.");
        Check(a.Connected && b.Connected, "Permission revocation must preserve the TLS session.");

        b.SetLocalPermissions(grants);
        await WaitAsync(() => aInput.CanControl, "Regrant did not restore A input permission.").ConfigureAwait(false);
        sourceBackend.Key(0x78, true); sourceBackend.Key(0x78, false);
        await WaitAsync(() => receiverBackend.Applied.Count(p => p.Type == "enter") == entersBefore + 1,
            "Fresh shortcut after regrant did not work.").ConfigureAwait(false);
        sourceBackend.Key(0x78, true); sourceBackend.Key(0x78, false);
        await WaitAsync(() => !bInput.IsRemote, "Second F9 did not return to A.").ConfigureAwait(false);
        Check(!aInput.IsRemote && !sourceBackend.Capturing, "Returning by shortcut did not release source capture.");

        string chatA = source + " → " + receiver + ": tiếng Việt chat";
        string chatB = receiver + " → " + source + ": đã nhận";
        a.SendChat(chatA); b.SendChat(chatB);
        await WaitAsync(() => aLog.Any(s => s.Contains(chatB, StringComparison.Ordinal)) && bLog.Any(s => s.Contains(chatA, StringComparison.Ordinal)),
            "Bidirectional chat did not traverse the existing TLS session.").ConfigureAwait(false);
        a.SendClipboard("A Unicode clipboard: tiếng Việt 👋"); b.SendClipboard("B Unicode clipboard: đã nhận 👋");
        await WaitAsync(() => Volatile.Read(ref clipboardA) == "B Unicode clipboard: đã nhận 👋" && Volatile.Read(ref clipboardB) == "A Unicode clipboard: tiếng Việt 👋",
            "In-memory clipboard exchange changed Unicode text.").ConfigureAwait(false);
        Check(a.ClipboardAllowed && b.ClipboardAllowed, "Both sides' clipboard grants were not honored.");

        string file = Path.Combine(workspace, "payload-" + source + "-" + receiver + ".bin");
        byte[] payload = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize * 2 + 79);
        await File.WriteAllBytesAsync(file, payload).ConfigureAwait(false);
        if (source == "Windows" && receiver == "Windows")
        {
            // Hold the real receive worker after it has opened the offered partial file. Input
            // permission changes must not cancel a separately approved transfer or its queued chunks.
            var offered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var continueOffer = new ManualResetEventSlim();
            void HoldOffer(string message)
            {
                if (!message.StartsWith("Đang nhận:", StringComparison.Ordinal)) return;
                offered.TrySetResult();
                if (!continueOffer.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Test file-offer gate was not released.");
            }
            b.Log += HoldOffer;
            Task transfer = a.SendFileAsync(file);
            try
            {
                await offered.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                Check(Directory.Exists(receiveB) && Directory.GetFiles(receiveB).Any(p => p.EndsWith(".partial", StringComparison.Ordinal)),
                    "Permission test must change input after a real incoming file has opened.");
                b.SetLocalPermissions(grants with { Input = false });
                await WaitAsync(() => !aInput.CanControl, "Input revoke during file transfer did not reach A.").ConfigureAwait(false);
                Check(a.FilesAllowed && b.FilesAllowed, "Input-only revocation changed file permission.");
            }
            finally { continueOffer.Set(); b.Log -= HoldOffer; }
            await transfer.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            Check(a.Connected && b.Connected, "Input-only revoke interrupted a separately approved file transfer.");
            b.SetLocalPermissions(grants);
            await WaitAsync(() => aInput.CanControl, "Could not regrant input after the concurrent file test.").ConfigureAwait(false);
        }
        else await a.SendFileAsync(file).WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        await WaitAsync(() => Directory.Exists(receiveB) && Directory.GetFiles(receiveB).Length == 1, "File destination missing.").ConfigureAwait(false);
        string destination = Directory.GetFiles(receiveB).Single();
        Check(!destination.EndsWith(".partial", StringComparison.Ordinal) &&
            SHA256.HashData(await File.ReadAllBytesAsync(destination).ConfigureAwait(false)).SequenceEqual(SHA256.HashData(payload)),
            "TLS file transfer failed its SHA-256 check.");
        b.SetLocalPermissions(grants with { Files = false });
        await WaitAsync(() => !a.FilesAllowed, "File revocation did not reach A.").ConfigureAwait(false);
        bool rejected = false;
        try { await a.SendFileAsync(file).ConfigureAwait(false); } catch (IOException) { rejected = true; }
        Check(rejected && Directory.GetFiles(receiveB).Length == 1, "Revoked file transfer must not create another file.");
        Check(sourceBackend.Starts == 1 && receiverBackend.Starts == 1, "Session reuse must not restart input hooks.");

        sourceBackend.Key(0x78, true); sourceBackend.Key(0x78, false);
        await WaitAsync(() => bInput.IsRemote, "Could not enter before disconnect cleanup test.").ConfigureAwait(false);
        sourceBackend.Key(0xA2, true);
        sourceBackend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: true));
        await WaitAsync(() => receiverBackend.HasHeldKey(0xA2) && receiverBackend.HasHeldButton("left"),
            "Disconnect test must begin with an applied held Control key and mouse button.").ConfigureAwait(false);
        Check(receiverBackend.HeldCount == 2, "Disconnect setup did not hold exactly the intended key/button.");
        int receiverCleanupBeforeDisconnect = receiverBackend.Releases;
        a.Dispose();
        await WaitAsync(() => !b.Connected && !bInput.CanControl && !bInput.IsRemote &&
            receiverBackend.Releases > receiverCleanupBeforeDisconnect && receiverBackend.HeldCount == 0,
            "Disconnect did not revoke the receiver and finish cleanup of its held key/button.").ConfigureAwait(false);
        Check(!sourceBackend.Capturing && !aInput.CanControl && aInput.Send is null && bInput.Send is null,
            "Disconnect retained a sender, capture or permission.");
        Check(receiverBackend.HeldCount == 0, "Disconnect cleanup retained a held key or mouse button.");
        Console.WriteLine($"  Protocol pair {source} → {receiver}: passed (fake native backends).");
    }

    private static string Reconstruct(IEnumerable<Packet> packets)
    {
        string text = "";
        foreach (var packet in packets.Where(p => p.Type == "text"))
        {
            if (packet.Dx > text.Length) throw new InvalidOperationException("Unicode correction ordering changed.");
            text = text[..(text.Length - packet.Dx)] + packet.Text;
        }
        return text;
    }

    private static async Task CheckAndroidScreenHostAsync(string workspace)
    {
        using var wire = await WirePair.ConnectAsync("Android screen controller").ConfigureAwait(false);
        using var backend = new MemoryBackend("screen host");
        await using var input = new PortableInputRouter(backend);
        input.Start();
        var capture = new FakeScreenCapture();
        using var host = new PortablePeerSession(wire.B, input, true, new Permissions { Input = true },
            Path.Combine(workspace, "screen-host"), capture);
        var logs = new ConcurrentQueue<string>(); host.Log += logs.Enqueue;
        host.Start(backend.Bounds, "desktop screen host"); wire.A.Start();
        await wire.A.SendAsync(new Packet { Type = "hello", Name = "Android", Flags = 2, Width = 1080, Height = 2400, ScreenViewingVersion = 1 });
        await WaitAsync(() => host.InputAllowed, "Android hello did not establish Desktop input permissions.").ConfigureAwait(false);
        Check(wire.AtA.Any(p => p.Type == "hello" && p.ScreenViewingVersion == 1), "Screen-capable host must advertise protocol 1.");
        Check(wire.AtA.Any(p => p.Type == "permissions" && p.Permissions?.ScreenViewing == false), "Screen viewing must default to off.");
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => wire.AtA.Any(p => p.Type == "screen-state" && !p.Allow), "Unapproved screen request was not denied.").ConfigureAwait(false);
        Check(capture.Calls == 0 && !host.ScreenViewingActive, "Denied screen request captured the desktop.");

        host.SetLocalPermissions(new Permissions { Input = true, ScreenViewing = true });
        Check(capture.Calls == 0, "Granting screen permission alone must not capture the screen.");
        capture.Block = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => capture.Calls == 1 && host.ScreenViewingActive, "Approved request did not start the screen worker.").ConfigureAwait(false);
        await wire.A.SendAsync(new Packet { Type = "enter", X = 32768, Y = 32768 });
        await wire.A.SendAsync(new Packet { Type = "text", Text = "một . , ? !" });
        await WaitAsync(() => backend.Applied.Any(p => p.Type == "text" && p.Text == "một . , ? !"),
            "A blocked screen capture stalled Android input.").ConfigureAwait(false);
        Check(host.Connected, "Screen capture must preserve the authenticated input session.");
        host.SetLocalPermissions(new Permissions { Input = true, ScreenViewing = false });
        Check(!host.ScreenViewingActive, "Revoking screen permission did not update the visible host state immediately.");
        capture.Block.TrySetResult(FakeScreenCapture.Frame);
        await wire.A.SendAsync(new Packet { Type = "chat", Text = "screen-revoke-barrier" });
        await WaitAsync(() => logs.Any(s => s.Contains("screen-revoke-barrier", StringComparison.Ordinal)), "Screen revoke barrier did not arrive.").ConfigureAwait(false);
        Check(!wire.AtA.Any(p => p.Type == "screen-frame"), "An in-flight frame escaped the screen-permission revocation.");

        capture.Block = null;
        host.SetLocalPermissions(new Permissions { Input = true, ScreenViewing = true });
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => wire.AtA.Any(p => p.Type == "screen-frame"), "Granted fake JPEG did not traverse real TLS.").ConfigureAwait(false);
        var frame = wire.AtA.First(p => p.Type == "screen-frame");
        Check(frame.Encoding == 1 && frame.Width == 1280 && frame.Height == 720 && frame.Data!.SequenceEqual(FakeScreenCapture.Frame.Jpeg),
            "Desktop screen dimensions or JPEG payload changed over TLS.");
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = false });
        await WaitAsync(() => !host.ScreenViewingActive, "Explicit stop request did not stop Desktop screen sharing.").ConfigureAwait(false);
        Check(host.Connected && host.InputAllowed, "Stopping screen viewing revoked separately granted input.");
        capture.Fail = true;
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => host.ScreenViewingError != null, "Capture failures are not exposed to the host UI.").ConfigureAwait(false);
        Check(!host.ScreenViewingActive && host.Connected, "Capture failure must stop viewing and retain the input session.");
        capture.Fail = false;
        await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => host.ScreenViewingActive, "Screen viewing could not restart after a recoverable capture failure.").ConfigureAwait(false);
        host.Dispose();
        Check(!host.ScreenViewingActive, "Disconnect retained active screen sharing.");

        using var earlyWire = await WirePair.ConnectAsync("early screen controller").ConfigureAwait(false);
        using var earlyBackend = new MemoryBackend("early screen host");
        await using var earlyInput = new PortableInputRouter(earlyBackend);
        var earlyCapture = new FakeScreenCapture();
        using var earlyHost = new PortablePeerSession(earlyWire.B, earlyInput, true,
            new Permissions { ScreenViewing = true }, Path.Combine(workspace, "early-screen-host"), earlyCapture);
        earlyHost.Start(earlyBackend.Bounds, "early screen host"); earlyWire.A.Start();
        await earlyWire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => !earlyHost.Connected, "Screen requests before hello must close the malformed session.").ConfigureAwait(false);
        Check(earlyCapture.Calls == 0, "Malformed pre-hello session captured a screen.");

        using var oldWire = await WirePair.ConnectAsync("old controller").ConfigureAwait(false);
        using var oldBackend = new MemoryBackend("screen host for old peer");
        await using var oldInput = new PortableInputRouter(oldBackend);
        var oldCapture = new FakeScreenCapture();
        using var oldHost = new PortablePeerSession(oldWire.B, oldInput, true,
            new Permissions { Input = true, ScreenViewing = true }, Path.Combine(workspace, "old-screen-host"), oldCapture);
        oldHost.Start(oldBackend.Bounds, "old-screen-host"); oldWire.A.Start();
        await oldWire.A.SendAsync(new Packet { Type = "hello", Flags = 2, Width = 1920, Height = 1080 });
        await WaitAsync(() => oldHost.InputAllowed, "Old compatible peers must retain input support.").ConfigureAwait(false);
        await oldWire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
        await WaitAsync(() => oldWire.AtA.Any(p => p.Type == "screen-state" && !p.Allow), "Screen requests without capability negotiation were not denied.").ConfigureAwait(false);
        Check(oldCapture.Calls == 0 && oldHost.Connected, "Unnegotiated screen capture ran or broke old-peer input compatibility.");
    }

    private static async Task CheckQueuedScreenPermissionAsync(string workspace)
    {
        foreach (bool initiallyGranted in new[] { false, true })
        {
            using var wire = await WirePair.ConnectAsync("queued Android screen controller").ConfigureAwait(false);
            using var backend = new MemoryBackend("stalled screen host");
            await using var input = new PortableInputRouter(backend);
            input.Start();
            var capture = new FakeScreenCapture();
            var grants = new Permissions { Input = true, ScreenViewing = initiallyGranted };
            using var host = new PortablePeerSession(wire.B, input, true, grants,
                Path.Combine(workspace, "queued-screen-" + initiallyGranted), capture);
            var logs = new ConcurrentQueue<string>(); host.Log += logs.Enqueue;
            host.Start(backend.Bounds, "stalled screen host"); wire.A.Start();
            await wire.A.SendAsync(new Packet { Type = "hello", Name = "Android", Flags = 2, Width = 1080, Height = 2400, ScreenViewingVersion = 1 });
            await WaitAsync(() => host.InputAllowed, "Queued-screen test did not finish hello.").ConfigureAwait(false);
            await wire.A.SendAsync(new Packet { Type = "enter", X = 32768, Y = 32768 });
            await WaitAsync(() => backend.Applied.Any(p => p.Type == "enter"), "Queued-screen test did not enter remote input.").ConfigureAwait(false);

            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            backend.BeforeInject = packet =>
            {
                if (packet.Type != "text" || packet.Text != "stall-screen-permission") return;
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(8))) throw new TimeoutException("Synthetic input stall was not released.");
            };
            void Observed(Packet packet)
            {
                // Registered after PortablePeerSession.Receive: this barrier means the earlier
                // request is already queued, while ProcessIncoming is blocked on fake input.
                if (packet.Type == "chat" && packet.Text == "queued-screen-barrier") arrived.TrySetResult();
            }
            wire.B.Received += Observed;
            try
            {
                await wire.A.SendAsync(new Packet { Type = "text", Text = "stall-screen-permission" });
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
                await wire.A.SendAsync(new Packet { Type = "chat", Text = "queued-screen-barrier" });
                await arrived.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                Check(capture.Calls == 0, "Queued screen test must begin before its capture worker starts.");
                if (initiallyGranted) host.SetLocalPermissions(grants with { ScreenViewing = false });
                host.SetLocalPermissions(grants with { ScreenViewing = true });
            }
            finally { release.Set(); wire.B.Received -= Observed; }
            await WaitAsync(() => logs.Any(s => s.Contains("queued-screen-barrier", StringComparison.Ordinal)),
                "Input-stalled screen request never resumed.").ConfigureAwait(false);
            Check(capture.Calls == 0 && !host.ScreenViewingActive,
                initiallyGranted ? "Regrant revived a queued screen request from the revoked permission generation."
                    : "A later grant authorized a screen request received before local consent.");
            await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await WaitAsync(() => capture.Calls > 0 && host.ScreenViewingActive,
                "Fresh screen request after the permission change did not work.").ConfigureAwait(false);
            Check(host.Connected && host.InputAllowed, "Queued screen permission fencing broke the separate input session.");
            await wire.A.SendAsync(new Packet { Type = "screen-request", Allow = false });
            await WaitAsync(() => !host.ScreenViewingActive, "Stop request must remain accepted after screen permission generations change.").ConfigureAwait(false);
        }
    }

    private static async Task CheckDeclinedConsentAsync()
    {
        using var host = new HostServer(IPAddress.Loopback, 0);
        using var backend = new MemoryBackend("unapproved");
        int approvals = 0, connected = 0;
        host.ApprovalRequested += (_, _) => { Interlocked.Increment(ref approvals); return Task.FromResult(false); };
        host.Connected += _ => Interlocked.Increment(ref connected);
        host.Start();
        var device = new DiscoveredDevice(Guid.NewGuid().ToString("N"), "receiver", IPAddress.Loopback, host.Port, host.Fingerprint, false, DateTimeOffset.UtcNow);
        bool rejected = false;
        try { using var peer = await PeerConnection.ConnectDiscoveredAsync(device, "unapproved-source").ConfigureAwait(false); }
        catch (AuthenticationException) { rejected = true; }
        Check(rejected && approvals == 1 && connected == 0 && backend.Starts == 0, "Declined consent published a session or started input.");
    }

    private static async Task CheckHelloGateAsync(string workspace)
    {
        using var wire = await WirePair.ConnectAsync("raw-source").ConfigureAwait(false);
        using var backend = new MemoryBackend("hello-gate");
        await using var input = new PortableInputRouter(backend);
        input.Start();
        using var host = new PortablePeerSession(wire.B, input, true, new Permissions { Input = true }, Path.Combine(workspace, "hello-gate"));
        var logs = new ConcurrentQueue<string>(); host.Log += logs.Enqueue;
        host.Start(backend.Bounds, "host"); wire.A.Start();
        Check(!host.InputAllowed && !input.CanControl && backend.Applied.IsEmpty, "Input was accepted before compatible hello.");
        wire.A.TrySend(new Packet { Type = "hello", Name = "valid-source", Flags = 2, Width = 1000, Height = 800 });
        wire.A.TrySend(new Packet { Type = "enter", X = 32768, Y = 32768 });
        wire.A.TrySend(new Packet { Type = "text", Text = "đã có hello" });
        await WaitAsync(() => backend.Applied.Any(p => p.Text == "đã có hello"), "Valid hello did not open the input gate.").ConfigureAwait(false);
        Check(host.Connected && host.InputAllowed && input.RemoteWidth == 1000, "Valid hello did not retain authenticated session.");
    }

    private static async Task CheckRejectedSequenceAsync(string workspace, bool helloFirst, params Packet[] packets)
    {
        using var wire = await WirePair.ConnectAsync("rejected-sequence").ConfigureAwait(false);
        using var backend = new MemoryBackend("rejected-sequence");
        await using var input = new PortableInputRouter(backend);
        input.Start();
        string receiveDirectory = Path.Combine(workspace, "rejected-sequence-" + Guid.NewGuid().ToString("N"));
        using var host = new PortablePeerSession(wire.B, input, true,
            new Permissions { Input = true, Clipboard = true, Files = true }, receiveDirectory);
        host.Start(backend.Bounds, "host"); wire.A.Start();
        if (helloFirst)
        {
            wire.A.TrySend(new Packet { Type = "hello", Name = "valid-source", Flags = 2, Width = 1920, Height = 1080 });
            await WaitAsync(() => host.InputAllowed, "Reject test did not establish a valid initial hello.").ConfigureAwait(false);
        }
        foreach (var packet in packets)
        {
            if (packet.Data is not null || packet.Button is not null || packet.BatchId is not null)
            {
                // The normal compact input encoder removes unrelated fields. Send one malformed
                // application frame directly over this isolated TLS connection to test a peer
                // that does not use our encoder. No other application sender is active here.
                byte[] json = JsonSerializer.SerializeToUtf8Bytes(packet, WireJson.Options);
                byte[] frame = new byte[json.Length + 4];
                BinaryPrimitives.WriteInt32BigEndian(frame, json.Length); json.CopyTo(frame, 4);
                await Get<SslStream>(wire.A, "_stream").WriteAsync(frame).ConfigureAwait(false);
            }
            else wire.A.TrySend(packet);
        }
        await WaitAsync(() => !host.Connected && wire.A.IsClosed, "Invalid protocol sequence did not close the session.").ConfigureAwait(false);
        Check(!input.CanControl && !input.IsRemote && backend.Applied.IsEmpty,
            "Pre-hello, duplicate hello or oversized unrelated packet field reached the input backend.");
        Check(!Directory.Exists(receiveDirectory), "Rejected protocol sequence created a destination file or directory.");
    }

    private static async Task CheckMalformedHelloAsync(string workspace, Packet hello)
    {
        using var wire = await WirePair.ConnectAsync("malformed-source").ConfigureAwait(false);
        using var backend = new MemoryBackend("malformed-hello");
        await using var input = new PortableInputRouter(backend);
        input.Start();
        using var host = new PortablePeerSession(wire.B, input, true, new Permissions { Input = true }, Path.Combine(workspace, "malformed-hello"));
        host.Start(backend.Bounds, "host"); wire.A.Start();
        wire.A.TrySend(hello);
        wire.A.TrySend(new Packet { Type = "enter", X = 32768, Y = 32768 });
        wire.A.TrySend(new Packet { Type = "text", Text = "must-not-type" });
        await WaitAsync(() => !host.Connected && wire.A.IsClosed, "Malformed hello did not close the session.").ConfigureAwait(false);
        Check(!input.CanControl && !input.IsRemote && backend.Applied.IsEmpty, "Malformed hello allowed native input.");
    }

    private static void RunHeadlessUi()
    {
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
        var window = new MainWindow(false, preview: true);
        try
        {
            window.Show(); // Avalonia.Headless creates only an in-memory surface, no desktop window.
            Dispatcher.UIThread.RunJobs();
            Check(window.Title?.Contains("Windows / macOS / Linux", StringComparison.Ordinal) == true, "Desktop title omitted the target platforms.");
            var tabs = Get<TabControl>(window, "_sessionTabs");
            Check(tabs.Items.Count == 2, "Desktop device/session tabs missing.");
            Check(Get<object?>(window, "_backend") is null && Get<object?>(window, "_input") is null &&
                Get<object?>(window, "_server") is null && Get<object?>(window, "_discovery") is null,
                "Headless preview opened a native backend or network service.");
            string? renderDirectory = Environment.GetEnvironmentVariable("LANBRIDGE_UI_ARTIFACTS");
            if (renderDirectory is not null) Directory.CreateDirectory(renderDirectory);
            foreach (var size in new[] { new Size(1080, 840), new Size(850, 660) })
            {
                window.Width = size.Width; window.Height = size.Height;
                foreach (int tab in new[] { 0, 1 })
                {
                    tabs.SelectedIndex = tab;
                    Dispatcher.UIThread.RunJobs();
                    using var frame = window.CaptureRenderedFrame();
                    Check(frame is not null && frame.PixelSize.Width > 0 && frame.PixelSize.Height > 0, "Headless UI failed to render.");
                    var scroll = tabs.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsVisible && s.Bounds.Width > 0);
                    Check(scroll is not null && scroll.Viewport.Width > 0 && scroll.Extent.Width <= scroll.Viewport.Width + 1,
                        $"Desktop tab {tab} needs horizontal scrolling at {size}.");
                    if (frame is not null && renderDirectory is not null)
                        frame.Save(Path.Combine(renderDirectory, $"desktop-{(int)size.Width}-{tab}.png"), PngBitmapEncoderOptions.Default);
                    if (tab == 1 && scroll is not null)
                    {
                        scroll.Offset = new Vector(0, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
                        Dispatcher.UIThread.RunJobs();
                        using var bottomFrame = window.CaptureRenderedFrame();
                        var chat = Get<TextBox>(window, "_chat");
                        Point? relative = chat.TranslatePoint(default, scroll);
                        Check(relative is { Y: >= 0 } p && p.Y + chat.Bounds.Height <= scroll.Viewport.Height + 1,
                            $"Chat input cannot be reached by scrolling the session page at {size}.");
                        if (bottomFrame is not null && renderDirectory is not null)
                            bottomFrame.Save(Path.Combine(renderDirectory, $"desktop-{(int)size.Width}-{tab}-bottom.png"), PngBitmapEncoderOptions.Default);
                        scroll.Offset = default;
                    }
                }
            }
            tabs.SelectedIndex = 1; Dispatcher.UIThread.RunJobs();
            var labels = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Check(labels.Any(t => t.Contains("Phím chuyển A ⇄ B", StringComparison.Ordinal)), "Shortcut label missing from the session page.");
            Check(labels.Any(t => t.Contains("Ctrl + Alt + F12", StringComparison.Ordinal)), "Emergency escape key missing from the UI.");
            Check(Get<object?>(window, "_backend") is null, "Switching preview tabs created native input.");
            var screenGrant = Get<CheckBox>(window, "_screenGrant");
            Check(screenGrant.IsChecked == false, "Screen viewing must be visibly unchecked in a new window.");
            Check(Get<TextBlock>(window, "_screenStatus").Text?.Contains("đang tắt", StringComparison.Ordinal) == true,
                "Screen-viewing permission explanation is missing from the session page.");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static T Get<T>(object target, string field) => (T)(target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(target)!);

    private sealed class WirePair : IDisposable
    {
        private readonly HostServer _host;
        public PeerConnection A { get; }
        public PeerConnection B { get; }
        public ConcurrentQueue<Packet> AtA { get; } = new();
        public ConcurrentQueue<Packet> AtB { get; } = new();
        public int Approvals { get; private set; }
        private WirePair(HostServer host, PeerConnection a, PeerConnection b, int approvals)
        {
            _host = host; A = a; B = b; Approvals = approvals;
            A.Received += AtA.Enqueue; B.Received += AtB.Enqueue;
        }
        public static async Task<WirePair> ConnectAsync(string source)
        {
            var host = new HostServer(IPAddress.Loopback, 0);
            try
            {
                var accepted = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
                int approvals = 0;
                host.ApprovalRequested += (request, _) =>
                {
                    Check(request.Name == source && IPAddress.IsLoopback(request.Address), "Approval request identity changed.");
                    Interlocked.Increment(ref approvals); return Task.FromResult(true);
                };
                host.Connected += peer => accepted.TrySetResult(peer); host.Start();
                var device = new DiscoveredDevice(Guid.NewGuid().ToString("N"), "receiver", IPAddress.Loopback, host.Port, host.Fingerprint, false, DateTimeOffset.UtcNow);
                var a = await PeerConnection.ConnectDiscoveredAsync(device, source).ConfigureAwait(false);
                var b = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
                return new(host, a, b, approvals);
            }
            catch { host.Dispose(); throw; }
        }
        public void Dispose() { A.Dispose(); B.Dispose(); _host.Dispose(); }
    }

    private sealed class FakeScreenCapture : IScreenCapture
    {
        internal static readonly ScreenFrame Frame = new([0xff, 0xd8, 1, 2, 3, 0xff, 0xd9], 1280, 720);
        internal int Calls;
        internal TaskCompletionSource<ScreenFrame>? Block;
        internal bool Fail;
        public Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail) throw new IOException("Fake Screen Recording denied.");
            return Block?.Task ?? Task.FromResult(Frame);
        }
    }

    private sealed class MemoryBackend(string name) : IInputBackend
    {
        public string Name { get; } = name;
        public BackendCapabilities Capabilities { get; } = new(true, true, true, "Smoke: in-memory input, never native.");
        public DesktopBounds Bounds { get; } = new(0, 0, 1920, 1080);
        public DesktopPoint Position { get; private set; } = new(250, 250);
        public event Func<PhysicalInput, bool>? Input;
        public event Action<string>? Error { add { } remove { } }
        public ConcurrentQueue<Packet> Applied { get; } = new();
        private readonly object _heldGate = new();
        private readonly HashSet<int> _heldKeys = [];
        private readonly HashSet<string> _heldButtons = [];
        private int _releases;
        public int Starts;
        public int Releases => Volatile.Read(ref _releases);
        public int HeldCount { get { lock (_heldGate) return _heldKeys.Count + _heldButtons.Count; } }
        public bool HasHeldKey(int key) { lock (_heldGate) return _heldKeys.Contains(key); }
        public bool HasHeldButton(string button) { lock (_heldGate) return _heldButtons.Contains(button); }
        public volatile bool Capturing;
        public Action<Packet>? BeforeInject;
        public void Start() => Interlocked.Increment(ref Starts);
        public void SetCapture(bool active) => Capturing = active;
        public void Warp(DesktopPoint point) => Position = point;
        public void Inject(Packet packet)
        {
            BeforeInject?.Invoke(packet);
            lock (_heldGate)
            {
                if (packet.Type == "key") { if (packet.Down) _heldKeys.Add(packet.Key); else _heldKeys.Remove(packet.Key); }
                if (packet.Type == "button" && packet.Button is { } button) { if (packet.Down) _heldButtons.Add(button); else _heldButtons.Remove(button); }
                Applied.Enqueue(packet with { });
            }
        }
        public void ReleaseAll()
        {
            lock (_heldGate) { _heldKeys.Clear(); _heldButtons.Clear(); Interlocked.Increment(ref _releases); }
        }
        public bool Emit(PhysicalInput input) => Input?.Invoke(input) == true;
        public bool Key(int key, bool down) => Emit(new(PhysicalInputKind.Key, Key: key, Down: down));
        public void Character(char character)
        {
            int key = char.IsLetter(character) ? char.ToUpperInvariant(character) : character switch
            {
                ' ' => 0x20, '.' => 0xBE, ',' => 0xBC, '?' => 0xBF, '!' => 0x31, _ => character,
            };
            Emit(new(PhysicalInputKind.Key, Key: key, Down: true, Text: character.ToString()));
            Emit(new(PhysicalInputKind.Key, Key: key, Down: false));
        }
        public void Dispose() { Capturing = false; }
    }
}
