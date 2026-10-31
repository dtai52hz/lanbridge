using System.Collections.Concurrent;
using System.Security.Cryptography;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckDesktopSessionAsync(MainForm a, MainForm b, Session session,
        InputRouter source, InputRouter receiver)
    {
        var inputSessionA = Get<CancellationTokenSource>(a, "_session");
        var inputSessionB = Get<CancellationTokenSource>(b, "_session");
        var applied = new ConcurrentQueue<Packet>();
        var observer = typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!;
        observer.SetValue(receiver, (Action<Packet>)applied.Enqueue);
        bool originalClipboardPermission = Checks(b)["Clipboard"].Checked;
        string name = "hidden-tray-" + Guid.NewGuid().ToString("N") + ".bin";
        string receiveDirectory = Get<string>(b, "_receivedDirectory");
        string sourcePath = Path.Combine(Path.GetDirectoryName(receiveDirectory)!, name);
        string? receivedPath = null;
        try
        {
            Call(a, "HideToTray"); Call(b, "HideToTray");
            Check(Get<bool>(a, "_hiddenInTray") && Get<bool>(b, "_hiddenInTray")
                && !a.ShowInTaskbar && !b.ShowInTaskbar && !a.Visible && !b.Visible,
                "The tray regression must keep both preview windows hidden without publishing a real tray icon.");
            Check(ReferenceEquals(Get<CancellationTokenSource>(a, "_session"), inputSessionA)
                && ReferenceEquals(Get<CancellationTokenSource>(b, "_session"), inputSessionB)
                && !inputSessionA.IsCancellationRequested && !inputSessionB.IsCancellationRequested,
                "Hiding the main window must preserve the exact authenticated session and its lifetime tokens.");

            var packets = new Packet[]
            {
                new() { Type = "enter", Name = "Right", X = 1234, Y = 5678 },
                new() { Type = "move", X = 6789, Y = 2345 },
                new() { Type = "key", Key = 0xA2, Scan = 0x1d, Down = true },
                new() { Type = "button", Button = "left", X = 6789, Y = 2345, Down = true },
                new() { Type = "wheel", X = 6789, Y = 2345, Dx = 15, Dy = -30 },
                new() { Type = "button", Button = "left", X = 6789, Y = 2345, Down = false },
                new() { Type = "key", Key = 0xA2, Scan = 0x1d, Down = false },
                new() { Type = "leave", Name = "Right" }
            };
            foreach (var packet in packets) await session.A.SendAsync(packet);
            await WaitUntilAsync(() => applied.Any(packet => packet.Type == "leave"),
                "Input did not finish on B's native-disabled worker while both main windows were hidden.");
            Check(applied.Select(InputBoundary).SequenceEqual(packets.Select(InputBoundary)) && !receiver.IsRemote,
                "Hiding in the tray dropped, reordered or changed remote pointer, modifier, drag, wheel or release input.");

            string marker = "TRAY_SESSION_" + Guid.NewGuid().ToString("N");
            SendChat(a, "A_" + marker); SendChat(b, "B_" + marker);
            await WaitUntilAsync(() => Log(b).Contains("A_" + marker, StringComparison.Ordinal)
                && Log(a).Contains("B_" + marker, StringComparison.Ordinal),
                "Bidirectional chat stopped while the window handles were hidden in the tray.");

            Checks(b)["Clipboard"].Checked = true;
            await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").Clipboard,
                "The hidden receiver's local clipboard grant did not reach A.");
            var clipboardA = Get<IClipboardAccess>(a, "_clipboard");
            var clipboardB = Get<IClipboardAccess>(b, "_clipboard");
            clipboardA.SetText("A clipboard " + marker);
            Call(a, "SyncClipboard");
            await WaitUntilAsync(() => clipboardB.Read().Text == "A clipboard " + marker,
                "A's fake text clipboard did not publish on hidden B.");
            clipboardB.SetText("B clipboard " + marker);
            Call(b, "SyncClipboard");
            await WaitUntilAsync(() => clipboardA.Read().Text == "B clipboard " + marker,
                "B's fake text clipboard did not publish on hidden A.");

            byte[] payload = RandomNumberGenerator.GetBytes(FileTransfer.ChunkSize + 23);
            await File.WriteAllBytesAsync(sourcePath, payload);
            await Get<FileTransfer>(a, "_files").SendFileAsync(sourcePath, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            receivedPath = Directory.GetFiles(receiveDirectory).Single(path => Path.GetFileName(path).EndsWith(name, StringComparison.Ordinal));
            Check(SHA256.HashData(await File.ReadAllBytesAsync(receivedPath)).SequenceEqual(SHA256.HashData(payload)),
                "The hidden receiver's normal file transfer changed its verified bytes.");

            Call(a, "RefreshDesktopStatus"); Call(b, "RefreshDesktopStatus");
            Check(Get<Label>(a, "_connectionSummary").Text.StartsWith("TLS · ", StringComparison.Ordinal)
                && Get<Label>(b, "_connectionSummary").Text.StartsWith("TLS · ", StringComparison.Ordinal)
                && Get<long>(a, "_connectionStartedAt") > 0 && Get<long>(b, "_connectionStartedAt") > 0,
                "Connected status lost its authenticated session timer while the window was hidden.");
            Check(!session.A.IsClosed && !session.B.IsClosed && source.CanControl && receiver.CanControl,
                "Tray hiding revoked input or disconnected a healthy TLS session.");
            Check(Get<NotifyIcon?>(a, "_trayIcon") is null && Get<NotifyIcon?>(b, "_trayIcon") is null
                && !a.Visible && !b.Visible,
                "The safe tray integration test showed a notification icon or desktop window.");
            AssertNoHooks(source, receiver);
        }
        finally
        {
            observer.SetValue(receiver, null);
            Call(a, "RestoreFromTray"); Call(b, "RestoreFromTray");
            a.WindowState = b.WindowState = FormWindowState.Normal;
            Checks(b)["Clipboard"].Checked = originalClipboardPermission;
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => Get<Permissions>(a, "_permissions").Clipboard == originalClipboardPermission,
                    "The tray regression did not restore its original clipboard grant.");
            if (File.Exists(sourcePath)) File.Delete(sourcePath);
            if (receivedPath != null && File.Exists(receivedPath)) File.Delete(receivedPath);
        }
        Check(!Get<bool>(a, "_hiddenInTray") && !Get<bool>(b, "_hiddenInTray")
            && a.ShowInTaskbar && b.ShowInTaskbar && !a.Visible && !b.Visible,
            "Restoring tray state must leave preview windows hidden and the session available to later checks.");
    }
}
