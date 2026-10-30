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
        using var context = new ApplicationContext();
        bool running = false;
        Application.Idle += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await RunAsync();
                Console.WriteLine($"Session smoke passed: {_assertions} assertions; hidden preview forms, real loopback TLS, no hooks or physical input.");
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
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-SessionSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var a = new MainForm(preview: true);
        using var b = new MainForm(preview: true);
        try
        {
            // Force native window handles for correct InvokeRequired behavior, without showing
            // either window. Preview=true guarantees AttachPeer never installs OS input hooks.
            _ = a.Handle; _ = b.Handle;
            Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
            Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
            Get<ComboBox>(b, "_role").SelectedIndex = 1;
            Set(a, "_permissions", new Permissions { Input = false, Files = false, Clipboard = false });
            Set(b, "_permissions", new Permissions { Input = true, Files = true, Clipboard = false });
            Call(a, "ApplyPermissions"); Call(b, "ApplyPermissions");
            var aInput = Get<InputRouter>(a, "_input");
            var bInput = Get<InputRouter>(b, "_input");
            aInput.RemoteWidth = bInput.RemoteWidth = -1;
            Check(!a.Visible && !b.Visible, "Test forms must remain hidden.");
            AssertNoHooks(aInput, bInput);

            using (var first = await ConnectAsync(a, b))
            {
                await WaitUntilAsync(() => aInput.CanControl && Get<Permissions>(a, "_permissions").Files
                    && aInput.RemoteWidth == SystemInformation.VirtualScreen.Width
                    && bInput.RemoteWidth == SystemInformation.VirtualScreen.Width,
                    "hello and host permissions did not reach both form handlers");
                Check(first.AtA.Any(p => p.Type == "hello" && p.Flags == 2) && first.AtB.Any(p => p.Type == "hello" && p.Flags == 2),
                    "Both UI sessions must advertise the Unicode input protocol version over TLS.");
                Check(first.AtA.Any(p => p.Type == "permissions" && p.Permissions?.Clipboard == false), "Host permission snapshot did not traverse TLS.");
                Check(aInput.IsController && !bInput.IsController && bInput.CanControl, "UI assigned incorrect controller/host input roles.");
                Check(Get<Label>(a, "_peerName").Text.StartsWith("Đối tác:", StringComparison.Ordinal)
                    && Get<Label>(b, "_peerName").Text.StartsWith("Đối tác:", StringComparison.Ordinal), "Hello packets did not update both peer labels.");
                AssertNoHooks(aInput, bInput);

                // Compose real Unicode correction packets, enqueue them through the input router's
                // production worker and TLS transport, then reconstruct B's received document.
                // Preview routers never Start, so ApplyRemote cannot inject any desktop input.
                var composer = new VietnameseComposer();
                int expectedEdits = 0;
                foreach (char key in "tieengs Vieetj, ddieeuf khieenr chuootj")
                {
                    Packet edit = composer.Process(key);
                    if (edit.Dx == 0 && string.IsNullOrEmpty(edit.Text)) continue;
                    expectedEdits++;
                    Call(aInput, "QueueText", edit);
                }
                await WaitUntilAsync(() => first.AtB.Count(p => p.Type == "text") == expectedEdits,
                    "ordered Vietnamese correction packets did not reach B over TLS");
                string receivedText = "";
                Packet[] receivedEdits = first.AtB.Where(p => p.Type == "text").ToArray();
                foreach (Packet edit in receivedEdits)
                {
                    Check(edit.Dx >= 0 && edit.Dx <= receivedText.Length, "TLS changed the correction's backspace count or order.");
                    receivedText = receivedText[..(receivedText.Length - edit.Dx)] + edit.Text;
                }
                Check(receivedText == "tiếng Việt, điều khiển chuột", "Actual input-worker/TLS Unicode edits changed the final Vietnamese text.");
                Check(receivedEdits.Any(p => p.Dx > 0) && receivedEdits.Any(p => p.Text?.Contains('ế') == true),
                    "The session test must include actual tone corrections, not only literal text.");
                AssertNoHooks(aInput, bInput);

                // Exercise the production permission checkbox handlers and incoming UI dispatch.
                Checks(b)["Input"].Checked = false;
                await WaitUntilAsync(() => !aInput.CanControl && !Get<Permissions>(a, "_permissions").Input,
                    "revoked host input permission did not reach A");
                Check(!bInput.CanControl, "Host must revoke its own input gate immediately.");
                Checks(b)["Input"].Checked = true;
                await WaitUntilAsync(() => aInput.CanControl, "re-enabled input permission did not reach A");
                Check(bInput.CanControl, "Host did not restore its approved input gate.");

                string chatA = "A → B: kiểm tra chuột và bàn phím " + Guid.NewGuid().ToString("N");
                SendChat(a, chatA);
                await WaitUntilAsync(() => Log(b).Contains(chatA, StringComparison.Ordinal), "A chat did not reach B's UI log");
                string chatB = "B → A: đã nhận " + Guid.NewGuid().ToString("N");
                SendChat(b, chatB);
                await WaitUntilAsync(() => Log(a).Contains(chatB, StringComparison.Ordinal), "B chat did not reach A's UI log");
                Check(Log(a).Contains("Bạn: " + chatA, StringComparison.Ordinal), "Sending chat did not update A's local UI log.");

                string receiveA = Path.Combine(root, "receive-a"), receiveB = Path.Combine(root, "receive-b");
                var fileLog = new ConcurrentQueue<string>();
                ReplaceFiles(a, receiveA, first.A, fileLog);
                ReplaceFiles(b, receiveB, first.B, fileLog);
                string source = Path.Combine(root, "tiếng-Việt-payload.bin");
                byte[] payload = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize * 2 + 79);
                await File.WriteAllBytesAsync(source, payload);
                await Get<FileTransfer>(a, "_files").SendFileAsync(source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                await WaitUntilAsync(() => first.AtA.Any(p => p.Type == "file-received"), "file verification acknowledgement did not return over TLS");
                string destination = Directory.GetFiles(receiveB).Single();
                Check(!destination.EndsWith(".partial", StringComparison.Ordinal), "Verified transfer left a partial file.");
                Check(SHA256.HashData(await File.ReadAllBytesAsync(destination)).SequenceEqual(SHA256.HashData(payload)), "Actual TLS/UI-dispatched file transfer changed its bytes.");
                Check(fileLog.Any(line => line.Contains("Đã nhận và xác minh", StringComparison.Ordinal)), "Receiver did not report successful SHA-256 verification.");
                Check(!Directory.Exists(receiveA), "One-way file transfer unexpectedly wrote to A's destination.");

                Checks(b)["Files"].Checked = false;
                await WaitUntilAsync(() => !Get<Permissions>(a, "_permissions").Files, "revoked host file permission did not reach A");
                bool rejected = false;
                try { await Get<FileTransfer>(a, "_files").SendFileAsync(source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (IOException) { rejected = true; }
                Check(rejected, "Host file handler accepted a transfer after file permission was revoked.");
                Check(Directory.GetFiles(receiveB).Length == 1, "Rejected transfer created an additional destination file.");

                var staleInputSender = aInput.Send ?? throw new InvalidOperationException("Attached input sender is missing.");
                var staleAudioSender = Get<AudioBridge>(a, "_audio").Send ?? throw new InvalidOperationException("Attached audio sender is missing.");
                Call(a, "Disconnect");
                await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "closing A did not clean up B's session");
                AssertDisconnected(a, b);
                Check(first.A.IsClosed && first.B.IsClosed, "UI disconnect did not close both TLS peers.");

                // Reuse the same forms to exercise production reconnect and stale callback guards.
                Checks(b)["Files"].Checked = true;
                using (var second = await ConnectAsync(a, b))
                {
                    await WaitUntilAsync(() => aInput.CanControl && Get<Permissions>(a, "_permissions").Files
                        && second.AtA.Any(p => p.Type == "hello") && second.AtB.Any(p => p.Type == "hello"),
                        "reconnected UI sessions did not complete their handshake");
                    string staleInput = "STALE_INPUT_" + Guid.NewGuid().ToString("N");
                    string staleAudio = "STALE_AUDIO_" + Guid.NewGuid().ToString("N");
                    staleInputSender(new Packet { Type = "chat", Text = staleInput });
                    staleAudioSender(new Packet { Type = "chat", Text = staleAudio });
                    string live = "LIVE_SECOND_SESSION_" + Guid.NewGuid().ToString("N");
                    SendChat(a, live);
                    await WaitUntilAsync(() => Log(b).Contains(live, StringComparison.Ordinal), "new-session sender failed after reconnect");
                    Check(!Log(b).Contains(staleInput, StringComparison.Ordinal) && !Log(b).Contains(staleAudio, StringComparison.Ordinal),
                        "A callback captured by the old session leaked packets into the new session.");
                    Check(!second.AtB.Any(p => p.Text == staleInput || p.Text == staleAudio), "New TLS peer received stale input/audio callbacks.");
                    AssertNoHooks(aInput, bInput);
                    Call(b, "Disconnect");
                    await WaitUntilAsync(() => Get<PeerConnection?>(a, "_peer") is null, "closing B did not clean up A after reconnect");
                    AssertDisconnected(a, b);
                }

                // Mixing a pre-Unicode binary with this release must stop the session with a
                // useful update message, rather than silently dropping Vietnamese text packets.
                using (var incompatible = await ConnectAsync(a, b))
                {
                    await WaitUntilAsync(() => aInput.CanControl && incompatible.AtB.Any(p => p.Type == "hello" && p.Flags == 2),
                        "version-rejection test did not establish its initial session");
                    Check(incompatible.A.TrySend(new Packet { Type = "hello", Name = "Legacy-0.1.0", Width = 1920, Height = 1080 }),
                        "Failed to send the legacy version declaration.");
                    await WaitUntilAsync(() => Get<PeerConnection?>(a, "_peer") is null && Get<PeerConnection?>(b, "_peer") is null,
                        "A legacy protocol declaration did not close both UI sessions");
                    Check(Log(b).Contains("LanBridge 0.2.0", StringComparison.Ordinal) && Log(b).Contains("Hãy cập nhật cả A và B.", StringComparison.Ordinal),
                        "Version mismatch did not report the required update to the user.");
                    AssertDisconnected(a, b);
                    AssertNoHooks(aInput, bInput);
                }
            }
        }
        finally
        {
            // MainForm's FormClosing handler disposes its services; hooks were never started.
            a.Close(); b.Close();
            var absolute = Path.GetFullPath(root);
            var expectedParent = Path.GetFullPath(Path.GetTempPath());
            if (!absolute.StartsWith(expectedParent, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("LanBridge-SessionSmoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing cleanup outside the test's temporary directory.");
            if (Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    private static async Task<Session> ConnectAsync(MainForm a, MainForm b)
    {
        var host = new HostServer(IPAddress.Loopback, 0);
        var accepted = new TaskCompletionSource<PeerConnection>(TaskCreationOptions.RunContinuationsAsynchronously);
        var atA = new ConcurrentQueue<Packet>();
        var atB = new ConcurrentQueue<Packet>();
        host.Connected += peer => b.BeginInvoke((Action)(() =>
        {
            try
            {
                peer.Received += atB.Enqueue;
                Call(b, "AttachPeer", peer);
                accepted.TrySetResult(peer);
            }
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

    private sealed record Session(HostServer Host, PeerConnection A, PeerConnection B,
        ConcurrentQueue<Packet> AtA, ConcurrentQueue<Packet> AtB) : IDisposable
    {
        public void Dispose() { A.Dispose(); B.Dispose(); Host.Dispose(); }
    }

    private static void ReplaceFiles(MainForm form, string directory, PeerConnection peer, ConcurrentQueue<string> logs)
    {
        Get<FileTransfer>(form, "_files").Dispose();
        Set(form, "_files", new FileTransfer(directory, packet => peer.SendAsync(packet), logs.Enqueue));
    }
    private static void SendChat(MainForm form, string text) { Get<TextBox>(form, "_chat").Text = text; Call(form, "SendChat"); }
    private static string Log(MainForm form) => Get<RichTextBox>(form, "_log").Text;
    private static Dictionary<string, CheckBox> Checks(MainForm form) => Get<Dictionary<string, CheckBox>>(form, "_checks");
    private static void AssertNoHooks(params InputRouter[] routers)
    {
        foreach (var router in routers)
        {
            Check(!Get<bool>(router, "_started"), "Session smoke must never call InputRouter.Start.");
            Check(Get<IntPtr>(router, "_mouseHook") == IntPtr.Zero && Get<IntPtr>(router, "_keyboardHook") == IntPtr.Zero,
                "Session smoke must never install mouse/keyboard hooks.");
        }
    }
    private static void AssertDisconnected(params MainForm[] forms)
    {
        foreach (var form in forms)
        {
            var input = Get<InputRouter>(form, "_input");
            Check(Get<PeerConnection?>(form, "_peer") is null && Get<FileTransfer?>(form, "_files") is null,
                "Disconnect left a peer/file service attached.");
            Check(Get<CancellationTokenSource?>(form, "_session") is null, "Disconnect left its session token attached.");
            Check(!input.CanControl && !input.IsRemote && input.Send is null, "Disconnect left input enabled or a sender attached.");
            Check(Get<AudioBridge>(form, "_audio").Send is null, "Disconnect left an audio sender attached.");
            Check(!Get<bool>(form, "_audioOn") && !Get<bool>(form, "_recordingOn") && !Get<bool>(form, "_blockingOn") && !Get<bool>(form, "_privacyOn"),
                "Disconnect left a feature state active.");
            Check(Get<ComboBox>(form, "_role").Enabled && !Get<Button>(form, "_disconnect").Enabled, "Disconnect did not reset its UI controls.");
        }
    }
    private static async Task WaitUntilAsync(Func<bool> predicate, string message)
    {
        var timeout = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timeout.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException(message);
            await Task.Delay(20);
        }
        Check(true, message);
    }
    private static void Check(bool result, string message) { _assertions++; if (!result) throw new InvalidOperationException(message); }
    private static T Get<T>(object instance, string name) => (T)(instance.GetType().GetField(name, Private)?.GetValue(instance)
        ?? (default(T) is null ? default! : throw new MissingFieldException(instance.GetType().Name, name)))!;
    private static void Set(object instance, string name, object value) =>
        (instance.GetType().GetField(name, Private) ?? throw new MissingFieldException(instance.GetType().Name, name)).SetValue(instance, value);
    private static object? Call(object instance, string name, params object?[] args)
    {
        try { return (instance.GetType().GetMethod(name, Private) ?? throw new MissingMethodException(instance.GetType().Name, name)).Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
}
