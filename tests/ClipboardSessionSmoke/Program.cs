using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LanBridge;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        int result = 1;
        bool running = false;
        using var context = new ApplicationContext();
        Application.Idle += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await RunAsync();
                Console.WriteLine($"Clipboard session smoke passed: {_assertions} assertions; hidden forms, real loopback TLS, injected clipboards, no OS clipboard or input hooks.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { context.ExitThread(); }
        };
        Application.Run(context);
        return result;
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-ClipboardSessionSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clipboardA = new MemoryClipboard();
        var clipboardB = new MemoryClipboard();
        string cacheA = Path.Combine(root, "cache-a"), cacheB = Path.Combine(root, "cache-b");
        using var a = new MainForm(preview: true, clipboard: clipboardA, clipboardCacheDirectory: cacheA);
        using var b = new MainForm(preview: true, clipboard: clipboardB, clipboardCacheDirectory: cacheB);
        Action<string[]>? staleFilesReady = null;
        try
        {
            _ = a.Handle; _ = b.Handle;
            Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
            Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
            Get<ComboBox>(b, "_role").SelectedIndex = 1;
            Check(!a.Visible && !b.Visible, "Clipboard integration forms must stay hidden.");
            AssertNoHooks(a, b);
            using (var session = await ConnectAsync(a, b))
            {
                await ReadyAsync(a, b, session);
                Check(session.AtA.Any(p => p.Type == "hello" && p.ClipboardFilesVersion == 1)
                    && session.AtB.Any(p => p.Type == "hello" && p.ClipboardFilesVersion == 1),
                    "Both production form hello packets must advertise clipboard-file support.");

                await CheckTextAsync(a, b, clipboardA, clipboardB, session);
                await CheckFilesBothWaysAsync(root, a, b, clipboardA, clipboardB, session, cacheA, cacheB);
                await CheckPermissionsAsync(root, a, b, clipboardA, clipboardB, session);
                await CheckControlDuringCopyAsync(root, a, b, clipboardA, clipboardB, session);
                await CheckRetryAndOverwriteAsync(root, a, b, clipboardA, clipboardB, session);
                var oldService = Get<object>(a, "_clipboardFiles");
                staleFilesReady = (Action<string[]>?)oldService.GetType().GetField("FilesReady", Private)?.GetValue(oldService);
                Check(staleFilesReady != null, "The current session must subscribe its verified-file clipboard callback.");
                await CheckDisconnectDuringReceiveAsync(root, a, b, clipboardA, clipboardB, session, cacheA);
            }

            // Reusing the same forms exposes old-session callbacks and staged clipboard echoes.
            using (var second = await ConnectAsync(a, b))
            {
                await ReadyAsync(a, b, second);
                int writesBeforeStale = clipboardA.SetFilesCalls;
                string stalePath = Path.Combine(root, "stale-session.txt");
                await File.WriteAllTextAsync(stalePath, "This callback belongs to the disconnected session.");
                staleFilesReady!(new[] { stalePath });
                await DrainAsync(a, b, second);
                Check(clipboardA.SetFilesCalls == writesBeforeStale,
                    "A completed file callback from a disconnected peer overwrote the new session's clipboard.");
                string text = "Phiên mới giữ nguyên clipboard chữ " + Guid.NewGuid().ToString("N");
                clipboardB.CopyText(text);
                Call(b, "SyncClipboard");
                await WaitUntilAsync(() => clipboardA.Content.Text == text, "Clipboard text stopped after reconnect.");
                Check(second.AtA.Count(p => p.Type == "clipboard-files-offer") == 0
                    && second.AtB.Count(p => p.Type == "clipboard-files-offer") == 0,
                    "A completed remote cache selection was re-offered to the new session.");

                await CheckOldPeerAsync(root, a, b, clipboardA, clipboardB, second);
                AssertNoHooks(a, b);
                Call(a, "Disconnect");
                await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "Disconnect did not detach B.");
                Check(Get<object?>(a, "_clipboardFiles") is null && Get<object?>(b, "_clipboardFiles") is null,
                    "Disconnect retained a clipboard-file session service.");
            }
            Check(clipboardA.AllAccessOnOwnerThread && clipboardB.AllAccessOnOwnerThread,
                "MainForm must access clipboard adapters only on their owning STA UI thread.");
        }
        finally
        {
            a.Close(); b.Close();
            var absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("LanBridge-ClipboardSessionSmoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's temporary directory.");
            await DeleteTestTreeAsync(absolute);
        }
    }

    private static async Task CheckTextAsync(MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        string textA = "Sao chép tiếng Việt A → B: Đặng Thái, ảnh và tài liệu 📄 " + Guid.NewGuid().ToString("N");
        clipboardA.CopyText(textA);
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.Content.Text == textA, "A's Vietnamese text did not reach B's clipboard adapter.");
        int echoedAtA = session.AtA.Count(p => p.Type == "clipboard");
        Call(b, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(session.AtA.Count(p => p.Type == "clipboard") == echoedAtA, "Receiving text must not echo the same text back.");

        string textB = "Ctrl+C ở B, Ctrl+V ở A " + Guid.NewGuid().ToString("N");
        clipboardB.CopyText(textB);
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.Content.Text == textB, "B's Vietnamese text did not reach A's clipboard adapter.");
        Check(clipboardA.Content.Files is null && clipboardB.Content.Files is null,
            "Text synchronization left a stale file-drop selection.");
    }

    private static async Task CheckFilesBothWaysAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session, string cacheA, string cacheB)
    {
        string sources = Path.Combine(root, "source-b");
        string loose = Path.Combine(sources, "tệp lẻ tiếng Việt.txt");
        string folder = Path.Combine(sources, "Thư mục Đặng");
        string nested = Path.Combine(folder, "ảnh con", "ghi chú.txt");
        string binary = Path.Combine(folder, "ảnh con", "dữ liệu.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        Directory.CreateDirectory(Path.Combine(folder, "thư mục rỗng"));
        await File.WriteAllTextAsync(loose, "File được copy trên B rồi dán sang A.");
        await File.WriteAllTextAsync(nested, "Tiếng Việt giữ nguyên ở thư mục con.");
        byte[] payload = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize * 2 + 91);
        await File.WriteAllBytesAsync(binary, payload);
        int receivedBefore = clipboardA.SetFilesCalls;
        clipboardB.CopyFiles(loose, folder);
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetFilesCalls > receivedBefore, "B's copied file/folder selection was not staged on A.");
        Check(clipboardA.Content.Files is { Length: 2 }, "A must receive the two selected roots, preserving one loose file and one folder.");
        string[] staged = clipboardA.Content.Files!;
        Check(staged.All(path => IsWithin(path, cacheA)), "File-drop clipboard exposed a source PC path instead of verified local cache files.");
        string receivedLoose = staged.Single(File.Exists), receivedFolder = staged.Single(Directory.Exists);
        Check(Path.GetFileName(receivedLoose) == Path.GetFileName(loose)
            && Path.GetFileName(receivedFolder) == Path.GetFileName(folder), "Selected root names changed in transit.");
        Check(await File.ReadAllTextAsync(receivedLoose) == await File.ReadAllTextAsync(loose), "Loose Vietnamese file content changed.");
        Check(await File.ReadAllTextAsync(Path.Combine(receivedFolder, "ảnh con", "ghi chú.txt")) == await File.ReadAllTextAsync(nested),
            "Nested Vietnamese file content or relative path changed.");
        Check(SHA256.HashData(await File.ReadAllBytesAsync(Path.Combine(receivedFolder, "ảnh con", "dữ liệu.bin")))
            .SequenceEqual(SHA256.HashData(payload)), "A's staged binary file failed an independent SHA-256 comparison.");
        Check(Directory.Exists(Path.Combine(receivedFolder, "thư mục rỗng")), "Empty selected subfolders must survive a clipboard copy.");
        Check(clipboardA.SetFilesCalls == receivedBefore + 1, "The clipboard was published before the whole multi-file batch completed.");

        int offersAtB = session.AtB.Count(p => p.Type == "clipboard-files-offer");
        Call(a, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(session.AtB.Count(p => p.Type == "clipboard-files-offer") == offersAtB,
            "Polling a remotely received FileDropList must not resend the batch back to B.");

        string back = Path.Combine(root, "source-a", "ảnh từ A.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(back)!);
        await File.WriteAllBytesAsync(back, payload.AsSpan(0, 279).ToArray());
        int receivedAtB = clipboardB.SetFilesCalls;
        clipboardA.CopyFiles(back);
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.SetFilesCalls > receivedAtB, "A's copied file selection did not reach B.");
        Check(clipboardB.Content.Files is { Length: 1 } && IsWithin(clipboardB.Content.Files[0], cacheB),
            "A → B file copying did not publish one local staged path.");
        byte[] receivedBack = await File.ReadAllBytesAsync(clipboardB.Content.Files![0]);
        byte[] expectedBack = await File.ReadAllBytesAsync(back);
        Check(receivedBack.SequenceEqual(expectedBack),
            "Bidirectional clipboard-file transfer changed the file bytes.");
    }

    private static async Task CheckPermissionsAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session)
    {
        string source = Path.Combine(root, "permission-file.txt");
        await File.WriteAllTextAsync(source, "The receiver must grant both clipboard and file permissions.");
        foreach (string permission in new[] { "Files", "Clipboard" })
        {
            Checks(b)[permission].Checked = false;
            await WaitUntilAsync(() => !(bool)typeof(Permissions).GetProperty(permission)!.GetValue(Get<Permissions>(a, "_permissions"))!,
                "Permission revocation did not reach the controller.");
            int writesA = clipboardA.SetFilesCalls, writesB = clipboardB.SetFilesCalls;
            int offersAtA = session.AtA.Count(p => p.Type == "clipboard-files-offer");
            int offersAtB = session.AtB.Count(p => p.Type == "clipboard-files-offer");
            clipboardA.CopyFiles(source); clipboardB.CopyFiles(source);
            Call(a, "SyncClipboard"); Call(b, "SyncClipboard");
            await DrainAsync(a, b, session);
            Check(session.AtA.Count(p => p.Type == "clipboard-files-offer") == offersAtA
                && session.AtB.Count(p => p.Type == "clipboard-files-offer") == offersAtB,
                $"Revoking {permission} must suppress local clipboard-file offers in both directions.");

            string id = Guid.NewGuid().ToString("N");
            await session.B.SendAsync(new Packet
            {
                Type = "clipboard-files-offer", Id = id,
                ClipboardEntries = new[] { new ClipboardFileEntry { Path = "should-not-arrive.txt", Length = 5 } }
            });
            await DrainAsync(a, b, session);
            Check(clipboardA.SetFilesCalls == writesA && clipboardB.SetFilesCalls == writesB,
                $"An unsolicited clipboard-file offer bypassed disabled {permission} permission.");
            Check(!session.AtB.Any(p => p.Type == "clipboard-files-accept" && p.Id == id && p.Allow),
                $"Receiver accepted an offer after {permission} was disabled.");
            Check(!session.A.IsClosed && !session.B.IsClosed, "A refused clipboard offer must not disconnect the input session.");
            Checks(b)[permission].Checked = true;
            await WaitUntilAsync(() => (bool)typeof(Permissions).GetProperty(permission)!.GetValue(Get<Permissions>(a, "_permissions"))!,
                "Restoring permissions did not reach the controller.");
        }
        // File permission changes must leave the existing text clipboard feature usable.
        Checks(b)["Files"].Checked = false;
        await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Files, "File permission did not become disabled for the text test.");
        string text = "Chữ vẫn copy khi quyền file tắt " + Guid.NewGuid().ToString("N");
        clipboardB.CopyText(text);
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.Content.Text == text, "Disabling file transfers unexpectedly disabled clipboard text.");
        Checks(b)["Files"].Checked = true;
        await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").Files, "File permissions failed to restore.");
    }

    private static async Task CheckControlDuringCopyAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session)
    {
        string large = Path.Combine(root, "large-copy.bin");
        byte[] block = RandomNumberGenerator.GetBytes(1024 * 1024);
        await using (var stream = File.Create(large))
            for (int index = 0; index < 24; index++) await stream.WriteAsync(block);
        string marker = "CONTROL_DURING_COPY_" + Guid.NewGuid().ToString("N");
        int writesBefore = clipboardA.SetFilesCalls;
        bool sawData = false, controlBeforePublication = false;
        Action<Packet> observe = packet =>
        {
            if (!packet.Type.StartsWith("clipboard-files-data-", StringComparison.Ordinal) || sawData) return;
            sawData = true;
            session.B.TrySend(new Packet { Type = "chat", Text = marker });
        };
        session.A.Received += observe;
        try
        {
            clipboardB.CopyFiles(large);
            Call(b, "SyncClipboard");
            await WaitUntilAsync(() => Log(a).Contains(marker, StringComparison.Ordinal), "Chat was blocked behind an ongoing clipboard-file transfer.", 15000);
            controlBeforePublication = clipboardA.SetFilesCalls == writesBefore;
            await WaitUntilAsync(() => clipboardA.SetFilesCalls > writesBefore, "Large copied file failed to finish.", 20000);
        }
        finally { session.A.Received -= observe; }
        Check(sawData && controlBeforePublication,
            "Session control must remain responsive before a copied file batch is published to the clipboard.");
        Check(new FileInfo(clipboardA.Content.Files!.Single()).Length == new FileInfo(large).Length,
            "Large clipboard transfer published a truncated file.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "Large clipboard transfer disconnected the authenticated input session.");
    }

    private static async Task CheckRetryAndOverwriteAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session)
    {
        string source = Path.Combine(root, "retry.txt");
        await File.WriteAllTextAsync(source, "The clipboard can be temporarily held by another application.");
        clipboardA.FailNextFileWrites = 1;
        int failures = clipboardA.FailedFileWrites;
        clipboardB.CopyFiles(source);
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedFileWrites > failures, "The injected clipboard-busy failure was not exercised.");
        int writes = clipboardA.SetFilesCalls;
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetFilesCalls > writes, "A transient clipboard-busy error was not retried on the next poll.");
        Check(File.Exists(clipboardA.Content.Files!.Single()), "Retry published a missing staged file.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "Clipboard contention must not close the TLS session.");

        string second = Path.Combine(root, "newer-local-copy.txt");
        await File.WriteAllTextAsync(second, "A newer local copy must win over a pending remote publication.");
        clipboardA.FailNextFileWrites = 1;
        failures = clipboardA.FailedFileWrites;
        clipboardB.CopyFiles(second);
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedFileWrites > failures, "The pending-publication overwrite test did not reach its busy clipboard.");
        writes = clipboardA.SetFilesCalls;
        string local = "Tôi vừa copy nội dung mới trên A " + Guid.NewGuid().ToString("N");
        clipboardA.CopyText(local);
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.Content.Text == local, "A newer local clipboard change did not reach B.");
        Call(a, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(clipboardA.Content.Text == local && clipboardA.SetFilesCalls == writes,
            "Retrying an older remote file selection overwrote a newer local clipboard copy.");
    }

    private static async Task CheckDisconnectDuringReceiveAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session, string cacheA)
    {
        string id = Guid.NewGuid().ToString("N");
        int writes = clipboardA.SetFilesCalls;
        await session.B.SendAsync(new Packet
        {
            Type = "clipboard-files-offer", Id = id,
            ClipboardEntries = new[] { new ClipboardFileEntry { Path = "partial.bin", Length = 16 * 1024 * 1024 } }
        });
        await WaitUntilAsync(() => session.AtB.Any(p => p.Type == "clipboard-files-accept" && p.Id == id && p.Allow),
            "Incomplete-transfer test was not accepted.");
        string fileId = Guid.NewGuid().ToString("N");
        await session.B.SendAsync(new Packet
        {
            Type = "clipboard-files-data-file-offer", BatchId = id, Id = fileId,
            Name = "00000000", Length = 16 * 1024 * 1024
        });
        await WaitUntilAsync(() => session.AtB.Any(p => p.Type == "clipboard-files-data-file-accept"
            && p.BatchId == id && p.Id == fileId && p.Allow), "The partial clipboard-file data offer was not accepted.");
        await session.B.SendAsync(new Packet
        {
            Type = "clipboard-files-data-file-chunk", BatchId = id, Id = fileId,
            Data = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize), Length = FileTransfer.ChunkSize
        });
        await WaitUntilAsync(() => Directory.Exists(Path.Combine(cacheA, "batch-" + id))
            && Directory.EnumerateFiles(Path.Combine(cacheA, "batch-" + id), "*.partial", SearchOption.AllDirectories).Any(),
            "The disconnect regression must have an actual unfinished data file to clean up.");
        Check(clipboardA.SetFilesCalls == writes, "An accepted manifest was exposed before the file data arrived.");
        Call(a, "Disconnect");
        await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "Disconnect did not release the remote clipboard-file session.");
        await WaitUntilAsync(() => !Directory.Exists(Path.Combine(cacheA, "batch-" + id)),
            "Disconnect left the incomplete clipboard-file batch on disk.");
        Check(clipboardA.SetFilesCalls == writes, "A disconnected partial transfer overwrote the clipboard.");
        Check(Get<object?>(a, "_clipboardFiles") is null && Get<object?>(b, "_clipboardFiles") is null,
            "Clipboard-file services survived a disconnected session.");
    }

    private static async Task CheckOldPeerAsync(string root, MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session)
    {
        // v0.3.1 uses the same input protocol but has no clipboard-file capability field.
        await session.B.SendAsync(new Packet { Type = "hello", Name = "LanBridge-0.3.1", Flags = 2, Width = 1920, Height = 1080 });
        await WaitUntilAsync(() => Get<Label>(a, "_peerName").Text.Contains("LanBridge-0.3.1", StringComparison.Ordinal),
            "Legacy clipboard capability probe did not pass the real hello handler.");
        int offers = session.AtB.Count(p => p.Type == "clipboard-files-offer");
        string source = Path.Combine(root, "old-peer.txt");
        await File.WriteAllTextAsync(source, "Legacy peers must not receive unknown clipboard-file transfers.");
        clipboardA.CopyFiles(source);
        Call(a, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(session.AtB.Count(p => p.Type == "clipboard-files-offer") == offers,
            "A peer without clipboard-file capability received a new file offer.");
        string legacyOffer = Guid.NewGuid().ToString("N");
        int writes = clipboardA.SetFilesCalls;
        await session.B.SendAsync(new Packet
        {
            Type = "clipboard-files-offer", Id = legacyOffer,
            ClipboardEntries = new[] { new ClipboardFileEntry { Path = "unadvertised.txt", Length = 1 } }
        });
        await DrainAsync(a, b, session);
        Check(clipboardA.SetFilesCalls == writes
            && !session.AtB.Any(p => p.Type == "clipboard-files-accept" && p.Id == legacyOffer && p.Allow),
            "A peer without advertised capability was allowed to begin an incoming clipboard-file batch.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "A compatible older input protocol must retain its normal session.");
        string text = "Text works with older peer " + Guid.NewGuid().ToString("N");
        clipboardA.CopyText(text);
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.Content.Text == text, "Older peers lost the existing text clipboard feature.");
    }

    private static async Task<Session> ConnectAsync(MainForm a, MainForm b)
    {
        var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atA = new ConcurrentQueue<Packet>(); var atB = new ConcurrentQueue<Packet>();
        host.Connected += peer => b.BeginInvoke((Action)(() =>
        {
            try { peer.Received += atB.Enqueue; Call(b, "AttachPeer", peer); accepted.TrySetResult(peer); }
            catch (Exception error) { peer.Dispose(); accepted.TrySetException(error); }
        }));
        PeerConnection? controller = null;
        try
        {
            host.Start();
            controller = await PeerConnection.ConnectAsync(host.PairingCode("127.0.0.1")).WaitAsync(TimeSpan.FromSeconds(8));
            var receiver = await accepted.Task.WaitAsync(TimeSpan.FromSeconds(8));
            controller.Received += atA.Enqueue;
            Call(a, "AttachPeer", controller);
            return new Session(host, controller, receiver, atA, atB);
        }
        catch { controller?.Dispose(); host.Dispose(); throw; }
    }

    private static async Task ReadyAsync(MainForm a, MainForm b, Session session)
    {
        await WaitUntilAsync(() => Get<bool>(a, "_helloReceived") && Get<bool>(b, "_helloReceived")
            && session.AtA.Any(p => p.Type == "permissions") && Get<Permissions>(a, "_permissions").Files
            && Get<Permissions>(a, "_permissions").Clipboard, "Hidden form session did not complete hello and permission setup.");
    }
    private static async Task DrainAsync(MainForm a, MainForm b, Session session)
    {
        string marker = "DRAIN_" + Guid.NewGuid().ToString("N");
        await session.A.SendAsync(new Packet { Type = "chat", Text = marker + "B" });
        await session.B.SendAsync(new Packet { Type = "chat", Text = marker + "A" });
        await WaitUntilAsync(() => Log(a).Contains(marker + "A", StringComparison.Ordinal)
            && Log(b).Contains(marker + "B", StringComparison.Ordinal), "TLS session did not drain its bidirectional control messages.");
    }
    private static void AssertNoHooks(params MainForm[] forms)
    {
        foreach (var form in forms)
        {
            var input = Get<InputRouter>(form, "_input");
            Check(!Get<bool>(input, "_started") && Get<IntPtr>(input, "_mouseHook") == IntPtr.Zero
                && Get<IntPtr>(input, "_keyboardHook") == IntPtr.Zero, "Clipboard integration test must never install native input hooks.");
        }
    }
    private static bool IsWithin(string path, string parent) => Path.GetFullPath(path)
        .StartsWith(Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static async Task DeleteTestTreeAsync(string path)
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); return; }
            catch (IOException) when (attempt < 19) { await Task.Delay(50); }
            catch (UnauthorizedAccessException) when (attempt < 19) { await Task.Delay(50); }
        }
    }
    private static async Task WaitUntilAsync(Func<bool> predicate, string message, int milliseconds = 8000)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.ElapsedMilliseconds > milliseconds) throw new TimeoutException(message);
            await Task.Delay(10);
        }
        Check(true, message);
    }
    private static Dictionary<string, CheckBox> Checks(MainForm form) => Get<Dictionary<string, CheckBox>>(form, "_checks");
    private static string Log(MainForm form) => Get<RichTextBox>(form, "_log").Text;
    private static void Check(bool result, string message) { _assertions++; if (!result) throw new InvalidOperationException(message); }
    private static T Get<T>(object instance, string name) => (T)(instance.GetType().GetField(name, Private)?.GetValue(instance)
        ?? (default(T) is null ? default! : throw new MissingFieldException(instance.GetType().Name, name)))!;
    private static object? Call(object instance, string name, params object?[] args)
    {
        try { return (instance.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private sealed record Session(HostServer Host, PeerConnection A, PeerConnection B,
        ConcurrentQueue<Packet> AtA, ConcurrentQueue<Packet> AtB) : IDisposable
    { public void Dispose() { A.Dispose(); B.Dispose(); Host.Dispose(); } }

    // This adapter is the only clipboard implementation used by either hidden form.
    // It intentionally has no dependency on System.Windows.Forms.Clipboard.
    private sealed class MemoryClipboard : IClipboardAccess
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private uint _sequence;
        public ClipboardContent Content { get; private set; } = new();
        public int SetFilesCalls { get; private set; }
        public int FailedFileWrites { get; private set; }
        public int FailNextFileWrites { get; set; }
        public bool AllAccessOnOwnerThread { get; private set; } = true;
        public uint SequenceNumber { get { Touch(); return _sequence; } }
        public ClipboardContent Read() { Touch(); return Content; }
        public void SetText(string text) { Touch(); Content = new(Text: text); _sequence++; }
        public void Clear() { Touch(); Content = new(); _sequence++; }
        public void SetFiles(string[] paths)
        {
            Touch();
            if (FailNextFileWrites > 0)
            {
                FailNextFileWrites--; FailedFileWrites++;
                throw new System.Runtime.InteropServices.ExternalException("Injected clipboard is temporarily busy.");
            }
            Content = new(Files: paths.ToArray()); SetFilesCalls++; _sequence++;
        }
        public void CopyText(string text) { Touch(); Content = new(Text: text); _sequence++; }
        public void CopyFiles(params string[] paths) { Touch(); Content = new(Files: paths.ToArray()); _sequence++; }
        private void Touch() { if (Environment.CurrentManagedThreadId != _owner) AllAccessOnOwnerThread = false; }
    }
}
