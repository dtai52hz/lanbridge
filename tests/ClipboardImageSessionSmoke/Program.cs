using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
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
                Console.WriteLine($"Clipboard image session smoke passed: {_assertions} assertions; hidden forms, real loopback TLS, injected clipboards, no OS clipboard or input hooks.");
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
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-ClipboardImageSessionSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var clipboardA = new MemoryClipboard();
        var clipboardB = new MemoryClipboard();
        using var a = new MainForm(preview: true, clipboard: clipboardA, clipboardCacheDirectory: Path.Combine(root, "cache-a"));
        using var b = new MainForm(preview: true, clipboard: clipboardB, clipboardCacheDirectory: Path.Combine(root, "cache-b"));
        Action<byte[]>? staleImageReady = null;
        try
        {
            _ = a.Handle; _ = b.Handle;
            Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
            Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
            Get<ComboBox>(b, "_role").SelectedIndex = 1;
            Check(!a.Visible && !b.Visible, "Image integration forms must remain hidden.");
            AssertNoHooks(a, b);
            using (var first = await ConnectAsync(a, b))
            {
                await ReadyAsync(a, b, first);
                Check(first.AtA.Any(p => p.Type == "hello" && p.ClipboardImagesVersion == 1)
                    && first.AtB.Any(p => p.Type == "hello" && p.ClipboardImagesVersion == 1),
                    "Both production hello packets must advertise screenshot clipboard support.");
                await CheckBothWaysAndPriorityAsync(a, b, clipboardA, clipboardB, first);
                await CheckBusyWindowAsync(a, b, clipboardA, clipboardB, first);
                await CheckPermissionsAsync(a, b, clipboardA, clipboardB, first);
                await CheckLargeImageAndChatAsync(a, b, clipboardA, clipboardB, first);
                await CheckSupersessionAsync(root, a, b, clipboardA, clipboardB, first);
                await CheckBusyRetryAsync(root, a, b, clipboardA, clipboardB, first);
                await CheckRetryBeforeIncomingUiCallbackAsync(a, b, clipboardA, clipboardB, first);
                var oldService = Get<object>(a, "_clipboardImages");
                staleImageReady = (Action<byte[]>?)oldService.GetType().GetField("ImageReady", Private)?.GetValue(oldService);
                Check(staleImageReady != null, "Current session did not attach its image publication callback.");
                await BeginPartialImageAsync(first, MakePng(128, 96, 71));
                int writes = clipboardA.SetImageCalls;
                Call(a, "Disconnect");
                await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "Disconnect did not detach the remote form.");
                Check(Get<object?>(a, "_clipboardImages") is null && Get<object?>(b, "_clipboardImages") is null,
                    "Disconnect retained a clipboard image service.");
                Check(clipboardA.SetImageCalls == writes, "A disconnected partial image was published.");
            }

            using (var second = await ConnectAsync(a, b))
            {
                await ReadyAsync(a, b, second);
                int writes = clipboardA.SetImageCalls;
                staleImageReady!(MakePng(31, 23, 79));
                await DrainAsync(a, b, second);
                Check(clipboardA.SetImageCalls == writes, "An old disconnected session image callback overwrote the new session clipboard.");
                await CopyImageAsync(b, clipboardB, clipboardA, MakePng(47, 33, 83));
                await CheckOldPeerAsync(root, a, b, clipboardA, clipboardB, second);
                AssertNoHooks(a, b);
                Call(a, "Disconnect");
                await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "Second session did not disconnect cleanly.");
            }
            Check(clipboardA.AllAccessOnOwnerThread && clipboardB.AllAccessOnOwnerThread,
                "Image clipboard adapters must only be accessed on the owning STA UI thread.");
        }
        finally
        {
            a.Close(); b.Close();
            string absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("LanBridge-ClipboardImageSessionSmoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside this test's temporary directory.");
            await DeleteTestTreeAsync(absolute);
        }
    }

    private static async Task CheckBothWaysAndPriorityAsync(MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        byte[] fromB = MakePng(97, 61, 7);
        clipboardB.CopyImage(fromB, "A screenshot must win over this text representation.");
        int imageWrites = clipboardA.SetImageCalls;
        Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetImageCalls > imageWrites, "B's screenshot did not reach A's clipboard.");
        AssertSamePixels(fromB, clipboardA.Content.ImagePng!, "B → A screenshot pixels changed.");
        Check(clipboardA.Content.Text is null && clipboardA.Content.Files is null,
            "Screenshot synchronization published the alternate text or a stale file selection.");
        Check(!session.AtA.Any(p => p.Type == "clipboard" && p.Text == "A screenshot must win over this text representation."),
            "Image clipboard content was demoted to its text representation.");
        int offersAtB = Count(session.AtB, "clipboard-image-offer");
        Call(a, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(Count(session.AtB, "clipboard-image-offer") == offersAtB, "A received screenshot was echoed back to its sender.");

        await CopyImageAsync(a, clipboardA, clipboardB, MakePng(81, 53, 11));
        int offersAtA = Count(session.AtA, "clipboard-image-offer");
        Call(b, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(Count(session.AtA, "clipboard-image-offer") == offersAtA, "Reverse-direction screenshot synchronization echoed back.");
    }

    private static async Task CheckPermissionsAsync(MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        Checks(b)["Files"].Checked = false;
        await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Files, "File permission revocation did not reach A.");
        await CopyImageAsync(b, clipboardB, clipboardA, MakePng(42, 31, 13));
        await CopyImageAsync(a, clipboardA, clipboardB, MakePng(39, 28, 17));
        Check(!Get<Permissions>(a, "_permissions").Files, "Screenshot copying unexpectedly reenabled file permissions.");

        Checks(b)["Clipboard"].Checked = false;
        await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Clipboard, "Clipboard permission revocation did not reach A.");
        int offersAtA = Count(session.AtA, "clipboard-image-offer"), offersAtB = Count(session.AtB, "clipboard-image-offer");
        int writesA = clipboardA.SetImageCalls, writesB = clipboardB.SetImageCalls;
        clipboardA.CopyImage(MakePng(33, 21, 19)); clipboardB.CopyImage(MakePng(35, 23, 23));
        Call(a, "SyncClipboard"); Call(b, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(Count(session.AtA, "clipboard-image-offer") == offersAtA && Count(session.AtB, "clipboard-image-offer") == offersAtB,
            "Disabled clipboard permission still sent a screenshot offer.");
        byte[] png = MakePng(44, 32, 29);
        string id = Guid.NewGuid().ToString("N");
        await session.B.SendAsync(ImageOffer(png, id));
        await DrainAsync(a, b, session);
        Check(clipboardA.SetImageCalls == writesA && clipboardB.SetImageCalls == writesB
            && !session.AtB.Any(p => p.Type == "clipboard-image-accept" && p.Id == id && p.Allow),
            "An unsolicited screenshot bypassed disabled clipboard permission.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "Refusing an image must preserve the normal input session.");
        Checks(b)["Clipboard"].Checked = true;
        Checks(b)["Files"].Checked = true;
        await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").Clipboard && Get<Permissions>(a, "_permissions").Files,
            "Clipboard and file permissions did not restore.");
        // Copies made while sharing was disabled are deliberately still local changes.
        // Observe empty local clipboards before starting the next independent transfer.
        clipboardA.Clear(); clipboardB.Clear();
        Call(a, "SyncClipboard"); Call(b, "SyncClipboard");
    }

    private static async Task CheckBusyWindowAsync(MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        byte[] png = MakePng(63, 45, 101);
        int writes = clipboardA.SetImageCalls;
        using var verified = new ManualResetEventSlim();
        Action<Packet> observe = packet =>
        {
            if (packet.Type == "clipboard-image-received" && packet.Allow) verified.Set();
        };
        session.B.Received += observe;
        try
        {
            clipboardB.CopyImage(png); Call(b, "SyncClipboard");
            // Deliberately hold both forms' shared UI thread until the real TLS/core
            // transfer has verified its full image. Native hooks remain disabled.
            Check(verified.Wait(TimeSpan.FromSeconds(3)), "Screenshot transfer stopped while the window UI thread was busy.");
            Check(clipboardA.SetImageCalls == writes, "An image adapter was accessed from a background thread while the UI was blocked.");
            await WaitUntilAsync(() => clipboardA.SetImageCalls > writes, "A screenshot completed during a busy window was discarded after the UI resumed.");
            AssertSamePixels(png, clipboardA.Content.ImagePng!, "A screenshot received during UI contention changed its pixels.");
            Check(!session.A.IsClosed && !session.B.IsClosed, "A busy window disconnected the screenshot/input session.");
        }
        finally { session.B.Received -= observe; }
    }

    private static async Task CheckLargeImageAndChatAsync(MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        // Random pixels produce a real PNG larger than the transport's single-frame limit.
        byte[] large = MakePng(1200, 1200, 31);
        Check(large.Length > 4 * 1024 * 1024, "Large-image regression requires a PNG exceeding four MiB.");
        int writes = clipboardA.SetImageCalls;
        string marker = "CHAT_DURING_SCREENSHOT_" + Guid.NewGuid().ToString("N");
        bool sawData = false;
        Action<Packet> observe = packet =>
        {
            if (packet.Type != "clipboard-image-chunk" || sawData) return;
            sawData = true;
            session.B.TrySend(new Packet { Type = "chat", Text = marker });
        };
        session.A.Received += observe;
        try
        {
            clipboardB.CopyImage(large);
            Call(b, "SyncClipboard");
            await WaitUntilAsync(() => Log(a).Contains(marker, StringComparison.Ordinal), "Screenshot chunks blocked normal session chat.", 15000);
            Check(sawData && clipboardA.SetImageCalls == writes, "Chat was not delivered while the screenshot was still transferring.");
            await WaitUntilAsync(() => clipboardA.SetImageCalls > writes, "The PNG larger than four MiB did not finish transferring.", 20000);
            AssertSamePixels(large, clipboardA.Content.ImagePng!, "Chunked screenshot pixels changed.");
            Check(session.AtA.Count(p => p.Type == "clipboard-image-chunk") > 50, "The large image did not exercise chunked transport.");
            Check(!session.A.IsClosed && !session.B.IsClosed, "A large screenshot disconnected the TLS session.");
        }
        catch
        {
            Console.Error.WriteLine($"Large-image diagnostics: bytes={large.Length}, chunks={Count(session.AtA, "clipboard-image-chunk")}, ACKs={Count(session.AtB, "clipboard-image-chunk-received")}, end={Count(session.AtA, "clipboard-image-end")}, writes={clipboardA.SetImageCalls}, A closed={session.A.IsClosed}, B closed={session.B.IsClosed}");
            Console.Error.WriteLine("A: " + Log(a));
            Console.Error.WriteLine("B: " + Log(b));
            throw;
        }
        finally { session.A.Received -= observe; }
    }

    private static async Task CheckBusyRetryAsync(string root, MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        byte[] png = MakePng(67, 43, 37);
        clipboardA.FailNextImageWrites = 1;
        int failures = clipboardA.FailedImageWrites;
        clipboardB.CopyImage(png); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedImageWrites > failures, "Injected image clipboard contention was not exercised.");
        int writes = clipboardA.SetImageCalls;
        Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetImageCalls > writes, "A transient busy clipboard image write was not retried.");
        AssertSamePixels(png, clipboardA.Content.ImagePng!, "Retried screenshot bytes changed.");

        clipboardA.FailNextImageWrites = 1; failures = clipboardA.FailedImageWrites;
        clipboardB.CopyImage(MakePng(69, 45, 41)); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedImageWrites > failures, "Pending image overwrite test did not reach clipboard contention.");
        writes = clipboardA.SetImageCalls;
        string local = "Ảnh cũ không được ghi đè nội dung mới " + Guid.NewGuid().ToString("N");
        clipboardA.CopyText(local); Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.Content.Text == local, "A newer local text copy did not reach B.");
        Call(a, "SyncClipboard"); await DrainAsync(a, b, session);
        Check(clipboardA.Content.Text == local && clipboardA.SetImageCalls == writes,
            "An older pending screenshot overwrote a newer local text copy.");

        clipboardA.FailNextImageWrites = 1; failures = clipboardA.FailedImageWrites;
        clipboardB.CopyImage(MakePng(71, 47, 43)); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedImageWrites > failures, "Remote supersession test did not stage a busy image.");
        writes = clipboardA.SetImageCalls;
        string newer = "Chữ mới từ B thắng ảnh đang chờ " + Guid.NewGuid().ToString("N");
        clipboardB.CopyText(newer); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.Content.Text == newer, "A newer remote text copy failed to replace a pending screenshot.");
        Call(a, "SyncClipboard"); await DrainAsync(a, b, session);
        Check(clipboardA.Content.Text == newer && clipboardA.SetImageCalls == writes,
            "Image retry overwrote a newer remote text clipboard.");

        clipboardA.FailNextImageWrites = 1; failures = clipboardA.FailedImageWrites;
        clipboardB.CopyImage(MakePng(73, 49, 47)); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedImageWrites > failures, "File supersession test did not stage a busy image.");
        string file = Path.Combine(root, "ảnh thay bằng file tiếng Việt.txt");
        await File.WriteAllTextAsync(file, "A newer file selection wins over a pending screenshot.");
        writes = clipboardA.SetImageCalls;
        int fileWrites = clipboardA.SetFilesCalls;
        clipboardB.CopyFiles(file); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetFilesCalls > fileWrites, "A copied file did not supersede a pending screenshot.");
        Call(a, "SyncClipboard"); await DrainAsync(a, b, session);
        Check(clipboardA.Content.Files is { Length: 1 } && clipboardA.Content.ImagePng is null && clipboardA.SetImageCalls == writes,
            "A stale screenshot overwrote a newer remote file selection.");
    }

    private static async Task CheckSupersessionAsync(string root, MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        byte[] image = MakePng(128, 96, 53);
        string imageId = await BeginPartialImageAsync(session, image);
        int imageWrites = clipboardA.SetImageCalls;
        string text = "Chữ thắng ảnh chưa nhận xong " + Guid.NewGuid().ToString("N");
        clipboardB.CopyText(text); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.Content.Text == text, "Text did not supersede an unfinished screenshot transfer.");
        await FinishStaleImageAsync(session, image, imageId);
        await DrainAsync(a, b, session);
        Check(clipboardA.Content.Text == text && clipboardA.SetImageCalls == imageWrites,
            "Finishing a superseded screenshot overwrote newer text.");

        imageId = await BeginPartialImageAsync(session, image);
        string file = Path.Combine(root, "newer-than-screenshot.txt");
        await File.WriteAllTextAsync(file, "A file copied while an image is incomplete.");
        int fileWrites = clipboardA.SetFilesCalls;
        clipboardB.CopyFiles(file); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.SetFilesCalls > fileWrites, "A new file selection did not supersede an unfinished screenshot.");
        await FinishStaleImageAsync(session, image, imageId);
        await DrainAsync(a, b, session);
        Check(clipboardA.Content.Files is { Length: 1 } && clipboardA.SetImageCalls == imageWrites,
            "A completed stale image overwrote a newer file-drop clipboard.");

        // Opposite direction of arbitration: an image must cancel an unfinished file manifest.
        string batch = Guid.NewGuid().ToString("N");
        await session.B.SendAsync(new Packet { Type = "clipboard-files-offer", Id = batch,
            ClipboardEntries = new[] { new ClipboardFileEntry { Path = "unfinished.bin", Length = 8192 } } });
        await WaitUntilAsync(() => session.AtB.Any(p => p.Type == "clipboard-files-accept" && p.Id == batch && p.Allow),
            "The unfinished file manifest was not accepted.");
        await CopyImageAsync(b, clipboardB, clipboardA, MakePng(51, 37, 59));
        await session.B.SendAsync(new Packet { Type = "clipboard-files-end", Id = batch });
        await DrainAsync(a, b, session);
        Check(clipboardA.Content.ImagePng is not null && clipboardA.Content.Files is null,
            "A stale file transfer overwrote a newer screenshot.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "Cross-format clipboard supersession closed the TLS session.");
    }

    private static async Task CheckRetryBeforeIncomingUiCallbackAsync(MainForm a, MainForm b,
        MemoryClipboard clipboardA, MemoryClipboard clipboardB, Session session)
    {
        clipboardA.FailNextImageWrites = 1;
        int failures = clipboardA.FailedImageWrites;
        clipboardB.CopyImage(MakePng(75, 51, 103)); Call(b, "SyncClipboard");
        await WaitUntilAsync(() => clipboardA.FailedImageWrites > failures,
            "Incoming-offer retry regression did not stage a pending busy image.");
        int writes = clipboardA.SetImageCalls;
        byte[] newer = MakePng(77, 53, 107);
        string id = Guid.NewGuid().ToString("N");
        using var accepted = new ManualResetEventSlim();
        Action<Packet> observe = packet =>
        {
            if (packet.Type == "clipboard-image-accept" && packet.Id == id && packet.Allow) accepted.Set();
        };
        session.B.Received += observe;
        try
        {
            // Hold the UI while the ordered background reader accepts a newer offer.
            // Its ReceivingStarted action is now queued, behind this simulated timer tick.
            Check(session.B.TrySend(ImageOffer(newer, id)), "Could not enqueue the newer screenshot offer.");
            Check(accepted.Wait(TimeSpan.FromSeconds(3)), "New screenshot offer was not accepted while the UI was blocked.");
            Call(a, "SyncClipboard");
            Check(clipboardA.SetImageCalls == writes,
                "A clipboard retry published an old screenshot after a newer wire offer was accepted but before its UI callback ran.");
            await session.B.SendAsync(new Packet { Type = "clipboard-image-chunk", Id = id, Length = newer.Length, Data = newer });
            await session.B.SendAsync(new Packet { Type = "clipboard-image-end", Id = id, Length = newer.Length,
                Text = Convert.ToHexString(SHA256.HashData(newer)) });
            await WaitUntilAsync(() => clipboardA.SetImageCalls > writes,
                "The newer screenshot did not publish after its delayed UI callback resumed.");
            Check(clipboardA.SetImageCalls == writes + 1, "The superseded pending image was retried during the newer image transfer.");
            AssertSamePixels(newer, clipboardA.Content.ImagePng!, "The delayed incoming offer published the wrong screenshot.");
            Check(!session.A.IsClosed && !session.B.IsClosed, "Clipboard retry arbitration closed the TLS session.");
        }
        finally { session.B.Received -= observe; }
    }

    private static async Task CheckOldPeerAsync(string root, MainForm a, MainForm b, MemoryClipboard clipboardA,
        MemoryClipboard clipboardB, Session session)
    {
        await session.B.SendAsync(new Packet { Type = "hello", Name = "LanBridge-0.4.0", Flags = 2,
            Width = 1920, Height = 1080, ClipboardFilesVersion = 1, ClipboardImagesVersion = 0 });
        await WaitUntilAsync(() => Get<Label>(a, "_peerName").Text.Contains("LanBridge-0.4.0", StringComparison.Ordinal),
            "The older-peer capability probe did not pass the production hello handler.");
        int offers = Count(session.AtB, "clipboard-image-offer");
        clipboardA.CopyImage(MakePng(41, 29, 89)); Call(a, "SyncClipboard");
        await DrainAsync(a, b, session);
        Check(Count(session.AtB, "clipboard-image-offer") == offers, "A peer without image capability received an unsupported image offer.");
        string id = Guid.NewGuid().ToString("N");
        int writes = clipboardA.SetImageCalls;
        await session.B.SendAsync(ImageOffer(MakePng(43, 31, 97), id));
        await DrainAsync(a, b, session);
        Check(clipboardA.SetImageCalls == writes && !session.AtB.Any(p => p.Type == "clipboard-image-accept" && p.Id == id && p.Allow),
            "A peer without advertised image capability was permitted to start an image transfer.");
        string text = "Clipboard chữ vẫn dùng với bản cũ " + Guid.NewGuid().ToString("N");
        clipboardA.CopyText(text); Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.Content.Text == text, "An older image capability broke the existing text clipboard.");
        string file = Path.Combine(root, "legacy-file-copy.txt");
        await File.WriteAllTextAsync(file, "LanBridge 0.4.0 still supports clipboard files.");
        int fileWrites = clipboardB.SetFilesCalls;
        clipboardA.CopyFiles(file); Call(a, "SyncClipboard");
        await WaitUntilAsync(() => clipboardB.SetFilesCalls > fileWrites, "An older image capability broke the existing file clipboard.");
        Check(await File.ReadAllTextAsync(clipboardB.Content.Files!.Single()) == await File.ReadAllTextAsync(file),
            "The legacy peer's copied file content changed.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "An older peer lost its compatible input session.");
    }

    private static Packet ImageOffer(byte[] png, string id) => new()
    { Type = "clipboard-image-offer", Id = id, Length = png.Length, Text = Convert.ToHexString(SHA256.HashData(png)) };

    private static async Task<string> BeginPartialImageAsync(Session session, byte[] png)
    {
        string id = Guid.NewGuid().ToString("N");
        await session.B.SendAsync(ImageOffer(png, id));
        await WaitUntilAsync(() => session.AtB.Any(p => p.Type == "clipboard-image-accept" && p.Id == id && p.Allow),
            "The partial screenshot offer was not accepted.");
        await session.B.SendAsync(new Packet { Type = "clipboard-image-chunk", Id = id, Length = 1024, Data = png[..1024] });
        await WaitUntilAsync(() => session.AtB.Any(p => p.Type == "clipboard-image-chunk-received" && p.Id == id && p.Allow && p.Length == 1024),
            "The partial screenshot chunk was not acknowledged.");
        return id;
    }

    private static async Task FinishStaleImageAsync(Session session, byte[] png, string id)
    {
        await session.B.SendAsync(new Packet { Type = "clipboard-image-chunk", Id = id, Length = png.Length, Data = png[1024..] });
        await session.B.SendAsync(new Packet { Type = "clipboard-image-end", Id = id, Length = png.Length,
            Text = Convert.ToHexString(SHA256.HashData(png)) });
    }

    private static async Task CopyImageAsync(MainForm source, MemoryClipboard sourceClipboard, MemoryClipboard destinationClipboard, byte[] png)
    {
        int before = destinationClipboard.SetImageCalls;
        sourceClipboard.CopyImage(png); Call(source, "SyncClipboard");
        await WaitUntilAsync(() => destinationClipboard.SetImageCalls > before, "A copied screenshot did not reach the other clipboard.");
        AssertSamePixels(png, destinationClipboard.Content.ImagePng!, "A synchronized screenshot changed its pixels.");
    }

    private static byte[] MakePng(int width, int height, int seed)
    {
        using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[width * height * 4];
            new Random(seed).NextBytes(pixels);
            for (int i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
            Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        }
        finally { bitmap.UnlockBits(locked); }
        using var output = new MemoryStream();
        bitmap.Save(output, ImageFormat.Png);
        return output.ToArray();
    }

    private static void AssertSamePixels(byte[] expected, byte[] actual, string message)
    {
        using var expectedStream = new MemoryStream(expected); using var actualStream = new MemoryStream(actual);
        using var expectedImage = new Bitmap(expectedStream); using var actualImage = new Bitmap(actualStream);
        Check(expectedImage.Size == actualImage.Size, message + " Dimensions differ.");
        Check(PixelHash(expectedImage).SequenceEqual(PixelHash(actualImage)), message);
    }
    private static byte[] PixelHash(Bitmap source)
    {
        using var normalized = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized)) graphics.DrawImageUnscaled(source, Point.Empty);
        var bits = normalized.LockBits(new Rectangle(Point.Empty, normalized.Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] pixels = new byte[normalized.Width * normalized.Height * 4];
            Marshal.Copy(bits.Scan0, pixels, 0, pixels.Length);
            return SHA256.HashData(pixels);
        }
        finally { normalized.UnlockBits(bits); }
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
            && Get<Permissions>(a, "_permissions").Clipboard, "The hidden forms did not finish hello and permissions setup.");
    }
    private static async Task DrainAsync(MainForm a, MainForm b, Session session)
    {
        string marker = "DRAIN_" + Guid.NewGuid().ToString("N");
        await session.A.SendAsync(new Packet { Type = "chat", Text = marker + "B" });
        await session.B.SendAsync(new Packet { Type = "chat", Text = marker + "A" });
        await WaitUntilAsync(() => Log(a).Contains(marker + "A", StringComparison.Ordinal)
            && Log(b).Contains(marker + "B", StringComparison.Ordinal), "The TLS session did not drain bidirectional control messages.");
    }
    private static void AssertNoHooks(params MainForm[] forms)
    {
        foreach (var form in forms)
        {
            var input = Get<InputRouter>(form, "_input");
            Check(!Get<bool>(input, "_started") && Get<IntPtr>(input, "_mouseHook") == IntPtr.Zero
                && Get<IntPtr>(input, "_keyboardHook") == IntPtr.Zero, "Image integration tests must never install input hooks.");
        }
    }
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
    private static int Count(ConcurrentQueue<Packet> packets, string type) => packets.Count(packet => packet.Type == type);
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

    // Both forms use only these adapters, leaving the user's clipboard completely untouched.
    private sealed class MemoryClipboard : IClipboardAccess
    {
        private readonly int _owner = Environment.CurrentManagedThreadId;
        private uint _sequence;
        public ClipboardContent Content { get; private set; } = new();
        public int SetFilesCalls { get; private set; }
        public int SetImageCalls { get; private set; }
        public int FailedImageWrites { get; private set; }
        public int FailNextImageWrites { get; set; }
        public bool AllAccessOnOwnerThread { get; private set; } = true;
        public uint SequenceNumber { get { Touch(); return _sequence; } }
        public ClipboardContent Read() { Touch(); return Content; }
        public void SetText(string text) { Touch(); Content = new(Text: text); _sequence++; }
        public void Clear() { Touch(); Content = new(); _sequence++; }
        public void SetFiles(string[] paths) { Touch(); Content = new(Files: paths.ToArray()); SetFilesCalls++; _sequence++; }
        public void SetImage(byte[] png)
        {
            Touch();
            if (FailNextImageWrites > 0)
            {
                FailNextImageWrites--; FailedImageWrites++;
                throw new ExternalException("Injected image clipboard is temporarily busy.");
            }
            Content = new(ImagePng: png.ToArray()); SetImageCalls++; _sequence++;
        }
        public void CopyText(string text) { Touch(); Content = new(Text: text); _sequence++; }
        public void CopyFiles(params string[] paths) { Touch(); Content = new(Files: paths.ToArray()); _sequence++; }
        public void CopyImage(byte[] png, string? text = null) { Touch(); Content = new(Text: text, ImagePng: png.ToArray()); _sequence++; }
        private void Touch() { if (Environment.CurrentManagedThreadId != _owner) AllAccessOnOwnerThread = false; }
    }
}
