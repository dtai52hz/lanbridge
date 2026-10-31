using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LanBridge;

internal static partial class Program
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
        await CheckInboxPressureAsync();
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-SessionSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string receiveA = Path.Combine(root, "receive-a"), receiveB = Path.Combine(root, "receive-b");
        using var a = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: receiveA);
        using var b = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: receiveB);
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

                await CheckMinimizedSessionAsync(a, b, first, aInput, bInput);
                await CheckDesktopSessionAsync(a, b, first, aInput, bInput);
                await CheckMoveAndScrollLatencyAsync(a, b, first, aInput, bInput);
                await CheckInputAcknowledgementAsync(a, b, first, aInput, bInput);
                await CheckInteractiveTrafficAsync(a, b, first, aInput, bInput);
                await CheckSlowNativeInjectionAsync(a, b, first, aInput, bInput);

                // Compose real Unicode correction packets, enqueue them through the input router's
                // production worker and TLS transport, then reconstruct B's received document.
                // The receiver's explicit test pump cannot install hooks or inject desktop input.
                var composer = new VietnameseComposer();
                int textBeforeComposition = first.AtB.Count(p => p.Type == "text");
                int expectedEdits = 0;
                foreach (char key in "tieengs Vieetj, ddieeuf khieenr chuootj")
                {
                    Packet edit = composer.Process(key);
                    if (edit.Dx == 0 && string.IsNullOrEmpty(edit.Text)) continue;
                    expectedEdits++;
                    Call(aInput, "QueueText", edit);
                }
                await WaitUntilAsync(() => first.AtB.Count(p => p.Type == "text") == textBeforeComposition + expectedEdits,
                    "ordered Vietnamese correction packets did not reach B over TLS");
                string receivedText = "";
                Packet[] receivedEdits = first.AtB.Where(p => p.Type == "text").Skip(textBeforeComposition).ToArray();
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

                string source = Path.Combine(root, "tiếng-Việt-payload.bin");
                byte[] payload = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize * 2 + 79);
                await File.WriteAllBytesAsync(source, payload);
                await Get<FileTransfer>(a, "_files").SendFileAsync(source, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                await WaitUntilAsync(() => first.AtA.Any(p => p.Type == "file-received"), "file verification acknowledgement did not return over TLS");
                string destination = Directory.GetFiles(receiveB).Single();
                Check(!destination.EndsWith(".partial", StringComparison.Ordinal), "Verified transfer left a partial file.");
                Check(SHA256.HashData(await File.ReadAllBytesAsync(destination)).SequenceEqual(SHA256.HashData(payload)), "Actual TLS/UI-dispatched file transfer changed its bytes.");
                await WaitUntilAsync(() => Log(b).Contains("Đã nhận và xác minh", StringComparison.Ordinal), "Receiver did not report successful SHA-256 verification.");
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
                    await CheckCloseWhileUiPausedAsync(a, b, second, bInput);
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

    private static void SendChat(MainForm form, string text) { Get<TextBox>(form, "_chat").Text = text; Call(form, "SendChat"); }
    private static string Log(MainForm form) => Get<RichTextBox>(form, "_log").Text;
    private static Dictionary<string, CheckBox> Checks(MainForm form) => Get<Dictionary<string, CheckBox>>(form, "_checks");

    private static async Task CheckInboxPressureAsync()
    {
        var type = typeof(MainForm).Assembly.GetType("LanBridge.SessionInbox", throwOnError: true)!;
        var inbox = Activator.CreateInstance(type, new object[] { 8 })!;
        var write = type.GetMethod("TryWrite")!;
        bool Enqueue(Packet packet) => (bool)write.Invoke(inbox, new object[] { packet })!;
        bool accepted = true;
        for (var index = 0; index < 4096; index++) accepted &= Enqueue(new Packet { Type = "move", X = index, Y = 123 });
        var keyDown = new Packet { Type = "key", Key = 0x41, Down = true };
        accepted &= Enqueue(keyDown);
        for (var index = 0; index < 4096; index++) accepted &= Enqueue(new Packet { Type = "move", X = 10000 + index, Y = 456 });
        var edit = new Packet { Type = "text", Dx = 2, Text = "tiếng Việt" };
        var keyUp = new Packet { Type = "key", Key = 0x41, Down = false };
        var buttonDown = new Packet { Type = "button", Button = "left", X = 14095, Y = 456, Down = true };
        var buttonUp = new Packet { Type = "button", Button = "left", X = 14095, Y = 456, Down = false };
        var leave = new Packet { Type = "leave" };
        foreach (var packet in new[] { edit, keyUp, buttonDown, buttonUp, leave }) accepted &= Enqueue(packet);
        Check(accepted, "An ordered inbox must coalesce thousands of adjacent mouse positions without overflowing.");
        Check(Enqueue(new Packet { Type = "move", X = 20000 }) && Enqueue(new Packet { Type = "audio", Data = new byte[] { 1 } }),
            "A full receive queue must tolerate disposable movement/audio instead of closing an otherwise healthy session.");
        Check(!Enqueue(new Packet { Type = "key", Key = 0x42, Down = true }),
            "A saturated queue must refuse critical input explicitly rather than silently dropping or overwriting it.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var stream = (IAsyncEnumerable<Packet>)type.GetMethod("ReadAllAsync")!.Invoke(inbox, new object[] { deadline.Token })!;
        await using var reader = stream.GetAsyncEnumerator(deadline.Token);
        var expected = new[]
        {
            new Packet { Type = "move", X = 4095, Y = 123 }, keyDown,
            new Packet { Type = "move", X = 14095, Y = 456 }, edit, keyUp, buttonDown, buttonUp, leave
        };
        foreach (var packet in expected)
        {
            Check(await reader.MoveNextAsync(), "The receive queue lost an ordered input boundary under pressure.");
            Check(InputBoundary(reader.Current) == InputBoundary(packet),
                "The receive queue coalesced across a keyboard, Unicode, button or leave boundary.");
        }
        type.GetMethod("Complete")!.Invoke(inbox, null);
        Check(!Enqueue(new Packet { Type = "move" }) && !await reader.MoveNextAsync(),
            "A completed session inbox must reject stale packets and finish its consumer.");
    }

    private static async Task CheckMinimizedSessionAsync(MainForm a, MainForm b, Session session,
        InputRouter aInput, InputRouter bInput)
    {
        // Run the real input dispatcher on its own STA message pump, with its explicit test
        // mode disabling every native hook and injection. The user's desktop stays untouched.
        Call(bInput, "StartForTesting");
        await ((Task)Call(bInput, "InvokeForTestingAsync", (Action)(() => { }))!);
        AssertNoHooks(aInput, bInput);
        var applied = new ConcurrentQueue<Packet>();
        using var beginBurst = new ManualResetEventSlim();
        using var reachedLeave = new ManualResetEventSlim();
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)
            ?? throw new MissingMemberException(nameof(InputRouter), "PacketAppliedForTesting");
        observer.SetValue(bInput, (Action<Packet>)(packet =>
        {
            applied.Enqueue(packet);
            if (packet.Type == "leave") reachedLeave.Set();
        }));
        var packets = new List<Packet> { new() { Type = "enter", Name = "Right", X = 10, Y = 20 } };
        for (var group = 0; group < 16; group++)
        {
            for (var move = 0; move < 128; move++)
                packets.Add(new Packet { Type = "move", X = group * 1000 + move, Y = 2000 + group });
            packets.Add(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = true });
            packets.Add(new Packet { Type = "text", Dx = group % 3, Text = "tiếng Việt " + group });
            packets.Add(new Packet { Type = "key", Key = 0x41, Scan = 0x1e, Down = false });
            packets.Add(new Packet { Type = "button", Button = "left", X = group * 1000 + 127, Y = 2000 + group, Down = true });
            packets.Add(new Packet { Type = "button", Button = "left", X = group * 1000 + 127, Y = 2000 + group, Down = false });
        }
        packets.Add(new Packet { Type = "leave", Name = "Right" });
        var marker = "MINIMIZED_SESSION_" + Guid.NewGuid().ToString("N");
        Exception? sendError = null;
        var sender = Task.Run(async () =>
        {
            try
            {
                beginBurst.Wait();
                await session.A.SendAsync(new Packet { Type = "chat", Text = "BEFORE_" + marker });
                foreach (var packet in packets) await session.A.SendAsync(packet);
                await session.A.SendAsync(new Packet { Type = "chat", Text = marker });
            }
            catch (Exception error) { sendError = error; reachedLeave.Set(); }
        });
        bool inputContinued;
        try
        {
            // Both handles remain hidden. WindowState exercises WinForms' minimize/restore
            // lifecycle without activating a window or stealing the user's focus. The bounded
            // pause reproduces animation/layout stalls, independently of Windows animations.
            a.WindowState = b.WindowState = FormWindowState.Minimized;
            Check(a.WindowState == FormWindowState.Minimized && b.WindowState == FormWindowState.Minimized
                && !a.Visible && !b.Visible, "Minimization regression must minimize both hidden windows without showing or activating them.");
            beginBurst.Set();
            inputContinued = reachedLeave.Wait(TimeSpan.FromSeconds(3));
        }
        finally { beginBurst.Set(); a.WindowState = b.WindowState = FormWindowState.Normal; }
        await sender.WaitAsync(TimeSpan.FromSeconds(8));
        await ((Task)Call(bInput, "InvokeForTestingAsync", (Action)(() => { }))!);
        observer.SetValue(bInput, null);
        Check(sendError is null, "The TLS session closed while minimized and flooded with mouse movement: " + sendError?.Message);
        Check(inputContinued && applied.Any(packet => packet.Type == "leave"),
            "Remote input must continue on its own pump while the main UI thread is paused.");
        Check(!session.A.IsClosed && !session.B.IsClosed, "Minimizing the window must not close either authenticated TLS peer.");
        Check(ReferenceEquals(Get<PeerConnection?>(a, "_peer"), session.A)
            && ReferenceEquals(Get<PeerConnection?>(b, "_peer"), session.B), "Minimization detached the current UI session.");
        var actual = applied.ToArray();
        var expectedCritical = packets.Where(packet => packet.Type != "move").Select(InputBoundary).ToArray();
        Check(actual.Where(packet => packet.Type != "move").Select(InputBoundary).SequenceEqual(expectedCritical),
            "Mouse coalescing dropped or reordered an enter, key, Unicode edit, button, or leave boundary.");
        var keyDowns = actual.Select((packet, index) => (packet, index)).Where(item => item.packet.Type == "key" && item.packet.Down).ToArray();
        Check(keyDowns.Length == 16, "The minimized session lost a keyboard boundary.");
        for (var group = 0; group < keyDowns.Length; group++)
        {
            var latestMove = actual.Take(keyDowns[group].index).LastOrDefault(packet => packet.Type == "move");
            Check(latestMove?.X == group * 1000 + 127 && latestMove.Y == 2000 + group,
                "A coalesced mouse position crossed a following keyboard boundary.");
        }
        Check(!bInput.IsRemote, "The final leave did not release the receiver while the main window was minimized.");
        await WaitUntilAsync(() => Log(b).Contains(marker, StringComparison.Ordinal), "A queued chat did not reach the restored receiver UI");
        Check(Log(b).Contains("BEFORE_" + marker, StringComparison.Ordinal), "A cosmetic chat before the input burst was lost or held input behind the paused UI.");
        for (var cycle = 0; cycle < 3; cycle++)
        {
            a.WindowState = FormWindowState.Minimized;
            b.WindowState = FormWindowState.Minimized;
            string toA = $"B_MINIMIZED_{cycle}_" + marker, toB = $"A_MINIMIZED_{cycle}_" + marker;
            SendChat(a, toB); SendChat(b, toA);
            await WaitUntilAsync(() => Log(a).Contains(toA, StringComparison.Ordinal) && Log(b).Contains(toB, StringComparison.Ordinal),
                "Duplex chat stopped while both UI windows were minimized");
            a.WindowState = b.WindowState = FormWindowState.Normal;
            Check(!a.Visible && !b.Visible && !session.A.IsClosed && !session.B.IsClosed,
                "A minimize/restore cycle showed a test window or closed the session.");
        }
        AssertNoHooks(aInput, bInput);
    }

    private static string InputBoundary(Packet packet) =>
        $"{packet.Type}|{packet.Key}|{packet.Scan}|{packet.Flags}|{packet.Button}|{packet.Down}|{packet.Text}|{packet.Dx}|{packet.Dy}|{packet.X}|{packet.Y}|{packet.Name}|{packet.Allow}";

    private static async Task CheckCloseWhileUiPausedAsync(MainForm a, MainForm b, Session session, InputRouter receiver)
    {
        await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 100, Y = 200 });
        await WaitUntilAsync(() => receiver.IsRemote, "Close regression did not enter the receiver's safe test session");
        receiver.SetBlockLocalInput(true);
        await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => { }))!);
        Check(Get<bool>(receiver, "_blockLocal"), "Close regression did not enable the receiver's logical input lock.");
        using var begin = new ManualResetEventSlim();
        using var released = new ManualResetEventSlim();
        string marker = "CLOSING_WITH_UI_PAUSED_" + Guid.NewGuid().ToString("N");
        Exception? failure = null;
        int lateInputsApplied = 0;
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)(packet => { if (packet.Type == "enter" && packet.X == 60000) Interlocked.Increment(ref lateInputsApplied); }));
        var closing = Task.Run(async () =>
        {
            begin.Wait();
            try
            {
                // A UI-bound hello deliberately holds the ordered reader ahead of a late enter.
                // Closing must cancel both before queued UI callbacks can re-enable input.
                await session.A.SendAsync(new Packet { Type = "hello", Name = marker, Flags = 2, Width = 1920, Height = 1080 });
                await session.A.SendAsync(new Packet { Type = "enter", Name = "Right", X = 60000, Y = 3333 });
                var deadline = Stopwatch.StartNew();
                while (!session.AtB.Any(packet => packet.Type == "enter" && packet.X == 60000))
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Late input did not arrive before the close probe.");
                    await Task.Delay(5);
                }
                session.A.Dispose();
                deadline.Restart();
                while (receiver.CanControl || receiver.IsRemote || Get<bool>(receiver, "_blockLocal"))
                {
                    if (deadline.Elapsed > TimeSpan.FromSeconds(2)) throw new TimeoutException("Closing peer did not release input while its UI was paused.");
                    await Task.Delay(5);
                }
            }
            catch (Exception error) { failure = error; }
            finally { released.Set(); }
        });
        bool releasedDuringPause;
        try
        {
            a.WindowState = b.WindowState = FormWindowState.Minimized;
            begin.Set();
            releasedDuringPause = released.Wait(TimeSpan.FromSeconds(3));
        }
        finally { begin.Set(); a.WindowState = b.WindowState = FormWindowState.Normal; }
        await closing.WaitAsync(TimeSpan.FromSeconds(8));
        Check(releasedDuringPause && failure is null, "A closed peer must release its input owner before the main UI resumes: " + failure?.Message);
        await WaitUntilAsync(() => Get<PeerConnection?>(a, "_peer") is null && Get<PeerConnection?>(b, "_peer") is null,
            "Network close did not clean up both UI sessions after restore");
        await ((Task)Call(receiver, "InvokeForTestingAsync", (Action)(() => { }))!);
        observer.SetValue(receiver, null);
        Check(Volatile.Read(ref lateInputsApplied) == 0 && !receiver.CanControl && !receiver.IsRemote,
            "Input queued by a closed session was applied or re-enabled control after its UI resumed.");
    }

    private static void AssertNoHooks(params InputRouter[] routers)
    {
        foreach (var router in routers)
        {
            Check(!Get<bool>(router, "_started") || !Get<bool>(router, "_nativeInputEnabled"),
                "Session smoke may run only the explicit pump with native input disabled.");
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
