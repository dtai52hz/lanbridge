using System.Net;
using LanBridge;

// This executable intentionally links only the protocol. It cannot capture a screen,
// install hooks, inject input or discover the user's LAN. The server binds 127.0.0.1:0.
if (args.Length == 1 && args[0] is "files-device" or "files-code") { await FileProtocolChecks.RunAsync(args[0][6..]); return; }
if (args.Length != 1 || args[0] is not ("device" or "code"))
    throw new ArgumentException("Choose the device or code loopback test.");
var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
string[] expected = ["hello", "screen-request", "enter", "move", "button", "button", "key", "key", "text"];
int next = 0, approvals = 0;
using var host = new HostServer(IPAddress.Loopback, 0);
host.ApprovalRequested += (request, _) =>
{
    if (request.Name != "Android interop") throw new InvalidDataException("Unexpected approval name.");
    approvals++;
    return Task.FromResult(true);
};
host.Connected += peer =>
{
    peer.Received += packet =>
    {
        try
        {
            if (next >= expected.Length || packet.Type != expected[next++])
                throw new InvalidDataException("Unexpected packet ordering: " + packet.Type);
            switch (packet.Type)
            {
                case "hello":
                    if (packet.Flags != 2 || packet.Width < 2 || packet.Height < 2 || packet.ScreenViewingVersion != 1)
                        throw new InvalidDataException("Unicode/screen capability hello mismatch.");
                    break;
                case "screen-request":
                    if (!packet.Allow) throw new InvalidDataException("Expected opt-in viewing request.");
                    // This is a framing fixture, not a rendered JPEG or an actual screenshot.
                    peer.TrySend(new Packet { Type = "screen-frame", Data = [0xff, 0xd8, 0xff, 0xd9],
                        Width = 1, Height = 1, Encoding = 1 });
                    break;
                case "move":
                    if (packet.X != 12345 || packet.Y != 54321) throw new InvalidDataException("Normalized position mismatch.");
                    break;
                case "button":
                    if (packet.Button != "left" || packet.Down != (next == 5)) throw new InvalidDataException("Button barrier mismatch.");
                    break;
                case "key":
                    if (packet.Key != 65 || packet.Down != (next == 7)) throw new InvalidDataException("Key barrier mismatch.");
                    break;
                case "text":
                    if (packet.Text != "một tiếng Việt 😀 . , ? !") throw new InvalidDataException("Committed Unicode text mismatch.");
                    peer.TrySend(new Packet { Type = "permissions", Permissions = new Permissions { Input = false, ScreenViewing = false } });
                    peer.TrySend(new Packet { Type = "chat", Text = "interop-ok" });
                    break;
            }
        }
        catch (Exception error) { done.TrySetException(error); peer.Dispose(); }
    };
    peer.Closed += reason =>
    {
        if (next != expected.Length) done.TrySetException(new IOException("Disconnected before verification: " + reason));
        else done.TrySetResult();
    };
    peer.TrySend(new Packet { Type = "hello", Name = ".NET fake PC", Width = 1920, Height = 1080, Flags = 2, ScreenViewingVersion = 1 });
    peer.TrySend(new Packet { Type = "permissions", Permissions = new Permissions { Input = true, ScreenViewing = true } });
    peer.Start();
};
host.Start();
// The runner consumes this line privately; no real PC pairing secrets are used.
Console.WriteLine(host.PairingCode("127.0.0.1"));
await done.Task.WaitAsync(TimeSpan.FromSeconds(20));
if (approvals != (args[0] == "device" ? 1 : 0)) throw new InvalidDataException("Approval count mismatch.");
Console.WriteLine("PASS .NET HostServer <-> Java Android protocol: " + args[0]);
