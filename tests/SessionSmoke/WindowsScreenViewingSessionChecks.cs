using System.Drawing.Imaging;
using System.Reflection;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckWindowsScreenViewingAsync(string root)
    {
        ScreenFrame frame;
        var encode = typeof(WindowsScreenCapture).GetMethod("EncodeFrame", BindingFlags.Static | BindingFlags.NonPublic)!;
        foreach (var size in new[] { new Size(1920, 1080), new Size(1080, 1920), new Size(3000, 500), new Size(63, 47) })
        {
            using var bitmap = new Bitmap(size.Width, size.Height, PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.DarkBlue);
                graphics.FillRectangle(Brushes.OrangeRed, 0, 0, size.Width / 2, size.Height);
            }
            frame = (ScreenFrame)encode.Invoke(null, [bitmap, CancellationToken.None])!;
            Check(frame.Width is > 0 and <= 1280 && frame.Height is > 0 and <= 800 && frame.Jpeg.Length <= 512 * 1024,
                "Synthetic Windows screen encoding exceeded the protocol bounds.");
            double originalRatio = (double)size.Width / size.Height, encodedRatio = (double)frame.Width / frame.Height;
            Check(Math.Abs(originalRatio - encodedRatio) < .01, "Windows screen scaling distorted the virtual desktop aspect ratio.");
            using var stream = new MemoryStream(frame.Jpeg);
            using var decoded = new Bitmap(stream);
            Check(decoded.Width == frame.Width && decoded.Height == frame.Height,
                "Windows JPEG metadata disagreed with its actual dimensions.");
            Check(decoded.GetPixel(decoded.Width / 4, decoded.Height / 2).R > 180
                && decoded.GetPixel(decoded.Width * 3 / 4, decoded.Height / 2).B > 80,
                "Screen encoding changed the left/right coordinate regions.");
        }
        using (var image = new Bitmap(64, 48))
        {
            using var graphics = Graphics.FromImage(image);
            graphics.Clear(Color.Teal);
            frame = (ScreenFrame)encode.Invoke(null, [image, CancellationToken.None])!;
        }
        int captures = 0;
        TaskCompletionSource<ScreenFrame>? pending = null;
        Task<ScreenFrame> Capture(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref captures);
            return Volatile.Read(ref pending)?.Task ?? Task.FromResult(frame);
        }
        using var a = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "screen-a"));
        using var b = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "screen-b"), screenCapture: Capture);
        _ = a.Handle; _ = b.Handle;
        Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
        Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
        Get<ComboBox>(b, "_role").SelectedIndex = 1;
        try
        {
            Check(!Checks(b)["ScreenViewing"].Checked, "Screen viewing must be off by default on the receiving PC.");
            using var session = await ConnectAsync(a, b);
            await WaitUntilAsync(() => Get<bool>(b, "_screenViewingCapable") && Get<bool>(b, "_helloReceived"),
                "Screen viewing capability was not negotiated in the ordered hello barrier.");
            Check(session.AtA.Any(packet => packet.Type == "hello" && packet.ScreenViewingVersion == 1)
                && session.AtB.Any(packet => packet.Type == "hello" && packet.ScreenViewingVersion == 1),
                "Both PC hello messages must expose the optional screen-viewing protocol.");
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await WaitUntilAsync(() => session.AtA.Any(packet => packet.Type == "screen-state" && !packet.Allow),
                "A denied screen request did not report its stopped state.");
            Check(captures == 0, "A paired device captured the screen without local permission.");

            Checks(b)["ScreenViewing"].Checked = true;
            await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").ScreenViewing,
                "The host's separate screen grant did not traverse TLS.");
            await Task.Delay(230);
            Check(captures == 0, "Granting screen permission must not start capture without an explicit request.");
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await WaitUntilAsync(() => session.AtA.Count(packet => packet.Type == "screen-frame") >= 2 && Get<bool>(b, "_screenViewingOn"),
                "An approved explicit screen request did not stream synthetic images.");
            var received = session.AtA.First(packet => packet.Type == "screen-frame");
            Check(received.Encoding == 1 && received.Width == frame.Width && received.Height == frame.Height
                && received.Data!.SequenceEqual(frame.Jpeg), "The host changed the JPEG or normalized image dimensions over TLS.");
            Check(Get<Label>(b, "_featureStatus").Text.Contains("CHIA SẺ MÀN HÌNH", StringComparison.Ordinal),
                "The receiving PC must visibly report active screen sharing.");

            // Simulate a slow, non-cancellable native capture. Revoking then granting
            // permission must discard its old image and require a fresh request.
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = false });
            await WaitUntilAsync(() => !Get<bool>(b, "_screenViewingOn"), "An explicit stop left screen sharing active.");
            var delayedCapture = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pending, delayedCapture);
            int beforeCapture = captures;
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await WaitUntilAsync(() => captures > beforeCapture, "The slow synthetic capture did not enter the worker.");
            Checks(b)["ScreenViewing"].Checked = false;
            await WaitUntilAsync(() => !Get<bool>(b, "_screenViewingOn") && !Get<Permissions>(a, "_permissions").ScreenViewing,
                "Revoking screen permission did not stop and notify the controller.");
            int oldFrames = session.AtA.Count(packet => packet.Type == "screen-frame");
            Checks(b)["ScreenViewing"].Checked = true;
            await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").ScreenViewing, "The screen permission regrant did not arrive.");
            Volatile.Write(ref pending, null);
            delayedCapture.SetResult(frame);
            await Task.Delay(260);
            Check(session.AtA.Count(packet => packet.Type == "screen-frame") == oldFrames,
                "An image captured under the revoked grant leaked into a later permission generation.");
            Check(!Get<bool>(b, "_screenViewingOn"), "Regranting permission silently restarted an old stream.");

            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await WaitUntilAsync(() => session.AtA.Count(packet => packet.Type == "screen-frame") > oldFrames,
                "A fresh request did not restart screen sharing after permission was regranted.");
            // Exercise the same gate used by the privacy toggle without opening any
            // real privacy windows or reading a physical display in this test.
            Set(b, "_privacyOn", true); Call(b, "UpdateScreenViewingPermission");
            await WaitUntilAsync(() => !Get<bool>(b, "_screenViewingOn"), "The privacy gate left screen sharing active.");
            int privateFrames = session.AtA.Count(packet => packet.Type == "screen-frame");
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await Task.Delay(260);
            Check(session.AtA.Count(packet => packet.Type == "screen-frame") == privateFrames,
                "A screen request bypassed the software privacy gate.");
            Set(b, "_privacyOn", false); Call(b, "UpdateScreenViewingPermission");
            await session.A.SendAsync(new Packet { Type = "hello", Name = "Legacy phone", Flags = 2, Width = 640, Height = 480 });
            await WaitUntilAsync(() => !Get<bool>(b, "_screenViewingCapable"), "A peer without screen capability was treated as supporting screen viewing.");
            int legacyCaptures = captures;
            await session.A.SendAsync(new Packet { Type = "screen-request", Allow = true });
            await session.A.SendAsync(new Packet { Type = "chat", Text = "legacy-screen-request-barrier" });
            await WaitUntilAsync(() => Log(b).Contains("legacy-screen-request-barrier", StringComparison.Ordinal), "The legacy screen request barrier did not reach the host.");
            await Task.Delay(230);
            Check(captures == legacyCaptures, "A peer that omitted screen capability started native capture.");
            Call(b, "Emergency");
            Check(!Checks(b)["ScreenViewing"].Checked && !Get<Permissions>(b, "_permissions").ScreenViewing,
                "Emergency release must revoke screen viewing as well as input.");
            Check(!a.Visible && !b.Visible, "Screen-viewing tests must not show a user's desktop window.");
            AssertNoHooks(Get<InputRouter>(a, "_input"), Get<InputRouter>(b, "_input"));
            Call(a, "Disconnect");
            await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null, "Screen-sharing session close did not clean up the host.");
            Check(Get<ScreenStreamingSession?>(b, "_screenStreaming") is null,
                "Disconnected host retained its old screen streaming service.");
        }
        finally { Call(a, "Disconnect"); Call(b, "Disconnect"); a.Close(); b.Close(); }
    }
}
