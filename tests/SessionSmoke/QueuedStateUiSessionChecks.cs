using LanBridge;

internal static partial class Program
{
    private static async Task CheckQueuedStateUiSessionAsync(string root)
    {
        var pendingCapture = new TaskCompletionSource<ScreenFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var a = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "queued-state-a"));
        using var b = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "queued-state-b"),
            screenCapture: _ => pendingCapture.Task);
        _ = a.Handle; _ = b.Handle;
        Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
        Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
        Get<ComboBox>(b, "_role").SelectedIndex = 1;
        Set(a, "_permissions", new Permissions { Input = false, Clipboard = false, Files = false });
        Set(b, "_permissions", new Permissions { Input = true, Clipboard = false, Files = false });
        Call(a, "ApplyPermissions"); Call(b, "ApplyPermissions");
        var source = Get<InputRouter>(a, "_input");
        var receiver = Get<InputRouter>(b, "_input");
        Call(source, "StartForTesting"); Call(receiver, "StartForTesting");

        void BlockUntil(Task work, string message)
        {
            // Intentionally hold only this test's hidden-form UI. Input/capture
            // workers remain free; no physical desktop input is enabled.
            if (!work.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException(message);
            work.GetAwaiter().GetResult();
        }
        void OnSourceOwner(Action action) => BlockUntil((Task)Call(source, "InvokeForTestingAsync", action)!,
            "The native-free input owner stalled while testing queued visual state.");

        using var session = await ConnectAsync(a, b);
        try
        {
            await WaitUntilAsync(() => source.CanControl && receiver.CanControl
                && Get<bool>(b, "_screenViewingCapable") && Get<bool>(b, "_helloReceived"),
                "Queued-state regression did not establish its approved TLS/capability session.");

            // Queue an ON visual from the independent input owner while UI is
            // blocked, then return A before that old ON visual gets dispatched.
            var bounds = Get<Rectangle>(source, "_bounds");
            OnSourceOwner(() => Call(source, "EnterRemoteCore", new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2), true));
            Check(source.IsRemote, "Queued-state regression did not enter remote control on the input owner.");
            var betweenNotifications = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            a.BeginInvoke((Action)(() => betweenNotifications.TrySetResult(Get<Label>(a, "_remoteState").Text)));
            OnSourceOwner(() => source.ReleaseRemote());
            Check(!source.IsRemote, "Logical return must complete before the delayed ON notification is pumped.");
            string observed = await betweenNotifications.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(observed.Contains("CHUỘT ĐANG Ở MÁY A", StringComparison.Ordinal),
                "An old queued remote-ON visual overwrote the input owner's newer local-A state.");
            await WaitUntilAsync(() => !receiver.IsRemote, "Queued-state input test did not release B's native-free receiver.");

            Checks(b)["ScreenViewing"].Checked = true;
            await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").ScreenViewing,
                "The queued-screen regression did not receive a local viewing grant.");
            var screen = Get<ScreenStreamingSession>(b, "_screenStreaming");
            BlockUntil(Task.Run(() => screen.Handle(new Packet { Type = "screen-request", Allow = true })),
                "The synthetic screen worker did not queue its ON notification.");
            Check(!Get<bool>(b, "_screenViewingOn"),
                "The worker's screen notification must still be queued behind the intentionally held test UI.");
            // Local OFF executes synchronously on UI while the old worker ON
            // remains queued. Pumping it afterward must not revive the display.
            Checks(b)["ScreenViewing"].Checked = false;
            Check(!Get<bool>(b, "_screenViewingOn"), "Local viewing revocation did not publish its stopped visual immediately.");
            var drained = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            b.BeginInvoke((Action)(() => drained.TrySetResult()));
            await drained.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(!Get<bool>(b, "_screenViewingOn")
                && !Get<Label>(b, "_featureStatus").Text.Contains("CHIA SẺ MÀN HÌNH", StringComparison.Ordinal),
                "An old queued screen-ON callback revived the host's visual sharing state after local OFF.");
            Check(!session.A.IsClosed && !session.B.IsClosed && !a.Visible && !b.Visible,
                "Queued-state tests must keep their authenticated TLS pair healthy without showing test windows.");
            AssertNoHooks(source, receiver);
            Console.WriteLine("Queued visuals: delayed remote/screen ON notifications cannot override a newer local return or screen revocation.");
        }
        finally
        {
            pendingCapture.TrySetCanceled();
            Call(a, "Disconnect"); Call(b, "Disconnect");
        }
    }
}
