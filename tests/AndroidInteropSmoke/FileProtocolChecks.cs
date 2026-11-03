using System.Net;
using System.Threading.Channels;
using LanBridge;

internal static class FileProtocolChecks
{
    public static async Task RunAsync(string authentication)
    {
        string root = Path.Combine(Path.GetTempPath(), "lanbridge-android-file-interop-" + Guid.NewGuid().ToString("N"));
        string received = Path.Combine(root, "received"); Directory.CreateDirectory(received);
        try
        {
            string desktop = Path.Combine(root, "desktop-data.bin"), cancel = Path.Combine(root, "cancel-from-pc.bin");
            await File.WriteAllBytesAsync(desktop, Pattern(8 * FileTransfer.ChunkSize + 137, 29));
            await File.WriteAllBytesAsync(cancel, Pattern(4 * 1024 * 1024, 71));
            var ready = NewCompletion(); var phoneReceived = NewCompletion(); var inputDuringFile = NewCompletion();
            var revokedObserved = NewCompletion(); var closed = NewCompletion();
            var incoming = Channel.CreateBounded<Packet>(128);
            PeerConnection? peer = null; FileTransfer? files = null; bool allowed = true; int approvals = 0;
            using var host = new HostServer(IPAddress.Loopback, 0);
            host.ApprovalRequested += (request, _) => { if (request.Name != "Android file interop") throw new IOException("Unexpected Android name."); approvals++; return Task.FromResult(true); };
            host.Connected += connected =>
            {
                peer = connected;
                files = new FileTransfer(received, p => connected.SendAsync(p), _ => { },
                    p => Path.Combine(received, FileTransfer.SafeFileName(p.Name)));
                connected.Received += packet =>
                {
                    if (packet.Type.StartsWith("file-", StringComparison.Ordinal))
                    { if (!incoming.Writer.TryWrite(packet)) phoneReceived.TrySetException(new IOException("Interop file queue overflow.")); }
                    else if (packet.Type == "hello") ready.TrySetResult();
                    else if (packet.Type == "text" && packet.Text == "file-transfer-input-ok") inputDuringFile.TrySetResult();
                    else if (packet.Type == "text" && packet.Text == "revoke-error-observed") revokedObserved.TrySetResult();
                };
                connected.Closed += _ => { incoming.Writer.TryComplete(); closed.TrySetResult(); };
                connected.TrySend(new Packet { Type = "hello", Name = ".NET file interop", Width = 1920, Height = 1080, Flags = 2 });
                connected.TrySend(new Packet { Type = "permissions", Permissions = new Permissions { Input = true, Files = true } }); connected.Start();
            };
            host.Start(); Console.WriteLine(host.PairingCode("127.0.0.1"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var consumer = Task.Run(async () =>
            {
                var names = new Dictionary<string, string>();
                await foreach (var packet in incoming.Reader.ReadAllAsync(timeout.Token))
                {
                    if (packet.Type == "file-offer") names[packet.Id!] = packet.Name!;
                    await files!.HandleAsync(packet, allowed);
                    string name = names.GetValueOrDefault(packet.Id ?? "", "");
                    if (packet.Type == "file-end" && name == "phone-data.bin") phoneReceived.TrySetResult();
                    if (packet.Type == "file-chunk" && name == "revoke-from-phone.bin" && allowed)
                    {
                        allowed = false;
                        peer!.TrySend(new Packet { Type = "permissions", Permissions = new Permissions { Input = true, Files = false } });
                        files.CancelTransfers();
                    }
                }
            }, timeout.Token);
            await ready.Task.WaitAsync(timeout.Token);
            await files!.SendFileAsync(desktop, timeout.Token);
            await phoneReceived.Task.WaitAsync(timeout.Token); await inputDuringFile.Task.WaitAsync(timeout.Token);
            if (!(await File.ReadAllBytesAsync(Path.Combine(received, "phone-data.bin"), timeout.Token)).SequenceEqual(Pattern(2 * 1024 * 1024 + 17, 13)))
                throw new IOException("Phone -> C# file contents did not match.");
            var rttDeadline = DateTime.UtcNow.AddSeconds(3);
            while (peer!.Latency is null && DateTime.UtcNow < rttDeadline) await Task.Delay(25, timeout.Token);
            if (peer.Latency is null) throw new IOException("Heartbeats did not remain responsive during file transfer.");
            bool cancelled = false;
            try { await files.SendFileAsync(cancel, timeout.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { cancelled = true; }
            if (!cancelled) throw new IOException("Android cancellation was not observed by actual C# sender.");
            peer.TrySend(new Packet { Type = "chat", Text = "start-revoke" });
            await revokedObserved.Task.WaitAsync(timeout.Token); await files.CancelIncomingAsync();
            if (File.Exists(Path.Combine(received, "revoke-from-phone.bin")) || Directory.GetFiles(received, "*.partial").Length != 0)
                throw new IOException("Revoked file left a published/partial file.");
            if (approvals != (authentication == "device" ? 1 : 0)) throw new IOException("Wrong approval count.");
            peer.TrySend(new Packet { Type = "chat", Text = "files-interop-ok" });
            await closed.Task.WaitAsync(timeout.Token); await consumer;
            files.Dispose(); peer.Dispose();
            Console.WriteLine("PASS actual C# FileTransfer <-> Java: bidirectional SHA-256, input/heartbeat, cancel/revoke (" + authentication + ")");
        }
        finally
        {
            string resolved = Path.GetFullPath(root), temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
            if (Path.GetDirectoryName(resolved) == temp && Path.GetFileName(resolved).StartsWith("lanbridge-android-file-interop-", StringComparison.Ordinal)) Directory.Delete(resolved, true);
        }
    }
    static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    static byte[] Pattern(int length, int seed) { var data = new byte[length]; for (int i = 0; i < data.Length; i++) data[i] = (byte)(i * 17 + seed); return data; }
}
