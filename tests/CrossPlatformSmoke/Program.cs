using System.Collections.Concurrent;
using System.Text.Json;
using LanBridge;
using LanBridge.CrossPlatform;

// Native-free tests: no global hook, pointer movement, real clipboard, or native event simulator.
int assertions = 0;
void Check(bool condition, string message)
{
    assertions++;
    if (!condition) throw new InvalidOperationException(message);
}
async Task AwaitAsync(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
PhysicalInput Key(int key, bool down = true, string? text = null) => new(PhysicalInputKind.Key, Key: key, Down: down, Text: text);
void Tap(FakeBackend backend, int key, string? text = null)
{
    backend.Emit(Key(key, true, text)); backend.Emit(Key(key, false, text));
}
void Type(FakeBackend backend, string text)
{
    foreach (char c in text)
    {
        int key = char.IsLetter(c) && c < 128 ? char.ToUpperInvariant(c) : c;
        if (char.IsUpper(c)) backend.Emit(Key(0xA0));
        Tap(backend, key, c.ToString());
        if (char.IsUpper(c)) backend.Emit(Key(0xA0, false));
    }
}

// Exercise all nine directions with the exact existing JSON messages and Unicode edits.
foreach (string source in new[] { "Windows", "macOS", "Linux/X11" })
foreach (string destination in new[] { "Windows", "macOS", "Linux/X11" })
{
    var aBackend = new FakeBackend(source);
    var bBackend = new FakeBackend(destination);
    await using var a = new PortableInputRouter(aBackend);
    await using var b = new PortableInputRouter(bBackend) { IsController = false };
    var wire = new ConcurrentQueue<Packet>();
    var pending = new ConcurrentQueue<Task>();
    Action<Packet>? sendA = null;
    Action<Packet> sendB = packet => pending.Enqueue(a.ApplyAsync(packet));
    sendA = packet =>
    {
        // Do not share mutable packet objects across a simulated network.
        var decoded = JsonSerializer.Deserialize<Packet>(JsonSerializer.Serialize(packet, WireJson.Options), WireJson.Options)!;
        wire.Enqueue(decoded);
        pending.Enqueue(b.ApplyAsync(decoded, sendB));
    };
    a.Send = sendA; b.Send = sendB;
    a.Start(); b.Start();
    Check(!aBackend.Emit(Key(0x78)), $"{source}: unapproved F9 stays local.");
    aBackend.Emit(Key(0x78, false));
    Check(wire.IsEmpty, "An unauthenticated router cannot emit an enter packet.");
    a.CanControl = b.CanControl = true;
    var original = aBackend.Position;
    Check(aBackend.Emit(Key(0x78)), "Approved F9 enters remotely and is consumed.");
    Check(aBackend.Emit(Key(0x78)), "Holding F9 remains consumed.");
    Check(aBackend.Emit(Key(0x78, false)), "F9 up remains consumed.");
    Check(a.IsRemote && aBackend.Capturing && b.IsRemote, $"{source} → {destination} must capture only source A.");
    Check(!bBackend.Capturing, "B's local keyboard and pointer remain usable.");
    Check(wire.Count(p => p.Type == "enter") == 1, "F9 autorepeat must not repeatedly switch.");
    Check(wire.First().X == 32768 && wire.First().Y == 32768, "Shortcut entry centers B's cursor.");
    Type(aBackend, "tieengs Vieetj . , ? !");
    while (pending.TryDequeue(out var task)) await AwaitAsync(task);
    Check(bBackend.Document == "tiếng Việt . , ? !", $"{source} → {destination}: Telex/punctuation survive the wire: {bBackend.Document}");
    Check(!wire.Any(p => p.Type == "key" && p.Key == 0x78), "The switching key never reaches B.");
    Check(!bBackend.Injected.Any(p => p.Type == "key" && p.Key == 0xA0), "Shift used for Unicode letter case must not contaminate the receiver IME.");
    Check(!bBackend.InjectedInline, "Native injection never runs inline on the source hook callback.");

    // Ctrl shortcuts and macOS Command/Windows meta retain physical key semantics.
    aBackend.Emit(Key(0xA2)); Tap(aBackend, 0x43); aBackend.Emit(Key(0xA2, false));
    aBackend.Emit(Key(0x5B)); Tap(aBackend, 0x56); aBackend.Emit(Key(0x5B, false));
    while (pending.TryDequeue(out var task)) await AwaitAsync(task);
    Check(wire.Any(p => p.Type == "key" && p.Key == 0x43 && p.Down), "Ctrl+C is physical, not a text edit.");
    Check(wire.Any(p => p.Type == "key" && p.Key == 0x5B && p.Down), "Command/Windows modifier keeps canonical meta VK.");
    Check(!bBackend.HeldKeys.Any(), "Shortcut modifiers and keys balance at B.");

    // Coalesce only a contiguous burst of moves; preserve the final position before a click.
    int warps = aBackend.Warps.Count;
    for (int n = 0; n < 50; n++) aBackend.Emit(new(PhysicalInputKind.Move, Relative: true, DeltaX: 1, DeltaY: 0));
    aBackend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: true));
    aBackend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: false));
    aBackend.Emit(new(PhysicalInputKind.Wheel, WheelY: 120, WheelX: -120));
    while (pending.TryDequeue(out var task)) await AwaitAsync(task);
    var messages = wire.ToArray();
    int click = Array.FindIndex(messages, p => p.Type == "button");
    Check(click > 0 && messages[click - 1].Type == "move" && messages[click - 1].X == messages[click].X,
        "A pending move flushes before a critical button at its latest position.");
    Check(aBackend.Warps.Count == warps, "Captured motion never warps A for each physical report.");
    Check(messages.Any(p => p.Type == "wheel" && p.Dx == -120 && p.Dy == 120), "Both wheel axes retain order and units.");

    // Release a drag and held key before leaving; restore original A position for shortcut entry.
    a.TypingMode = TypingMode.Physical;
    aBackend.Emit(Key(0x41));
    aBackend.Emit(new(PhysicalInputKind.Button, Button: "right", Down: true));
    Tap(aBackend, 0x78);
    while (pending.TryDequeue(out var task)) await AwaitAsync(task);
    Check(!a.IsRemote && !aBackend.Capturing && !b.IsRemote, "A second shortcut releases capture and leaves B.");
    Check(aBackend.Position == original, "Shortcut return restores A's original pointer position.");
    Check(bBackend.HeldKeys.Count == 0 && bBackend.HeldButtons.Count == 0, "Leave releases every held key and drag.");
    var tail = wire.ToArray();
    int leave = Array.FindLastIndex(tail, p => p.Type == "leave");
    Check(tail.Take(leave).Any(p => p.Type == "key" && p.Key == 0x41 && !p.Down), "Held A key is released before leave.");
    aBackend.Emit(Key(0x41, false));
    aBackend.Emit(new(PhysicalInputKind.Button, Button: "right", Down: false));
    a.CanControl = b.CanControl = false;
    while (pending.TryDequeue(out var task)) await AwaitAsync(task);
}

// Exact chord, consumed modifier ups, reassignment, no stale callbacks after reconnect.
{
    var backend = new FakeBackend("Windows");
    await using var router = new PortableInputRouter(backend) { CanControl = true, SwitchShortcut = new(0x20, ShortcutModifiers.Control | ShortcutModifiers.Alt) };
    var first = new List<Packet>(); var second = new List<Packet>();
    Action<Packet> oldSender = first.Add;
    router.Send = oldSender; router.Start();
    backend.Emit(Key(0xA2)); backend.Emit(Key(0xA4)); backend.Emit(Key(0xA0));
    Check(!backend.Emit(Key(0x20)), "Extra Shift prevents an exact Ctrl+Alt+Space match.");
    backend.Emit(Key(0x20, false)); backend.Emit(Key(0xA0, false));
    Check(backend.Emit(Key(0x20)), "Ctrl+Alt+Space enters after extra modifier is released.");
    backend.Emit(Key(0x20, false));
    Check(backend.Emit(Key(0xA2, false)) && backend.Emit(Key(0xA4, false)), "Entry chord modifier ups are swallowed until real release.");
    Check(first.Count == 1 && first[0].Type == "enter", "Entry chord never forwards Ctrl, Alt, or Space to B.");
    await AwaitAsync(router.SuspendSwitchShortcutAsync());
    Check(!router.IsRemote && router.SwitchShortcut is null && !backend.Capturing, "Recording a shortcut suspends capture and returns to A.");
    Check(backend.Injected.Count(p => p.Type == "key" && !p.Down && p.Key is 0xA2 or 0xA4) == 2, "Held entry modifiers receive local releases through the native worker.");
    router.SwitchShortcut = new(0x78);
    Tap(backend, 0x78);
    Action<Packet> newSender = second.Add;
    router.Send = newSender;
    Check(!router.IsRemote && first.Last().Type == "leave", "Replacing a sender first releases its own remote session.");
    int oldCount = first.Count;
    router.ReleaseSession(oldSender);
    Check(router.CanControl, "Closing an old session cannot revoke a new session.");
    Check(!router.ConfigureSession(oldSender, false, 200, 300) && router.CanControl && router.RemoteWidth == 1920 && router.RemoteHeight == 1080,
        "An old sender cannot change the current session's permission or dimensions.");
    Check(router.ConfigureSession(newSender, false, 2560, 1440) && !router.CanControl && router.RemoteWidth == 2560 && router.RemoteHeight == 1440,
        "The owning sender can atomically configure validated dimensions and permission.");
    Check(!router.ConfigureSession(oldSender, true) && !router.CanControl, "An obsolete permissions grant cannot reactivate capture.");
    Check(router.ConfigureSession(newSender, true), "The authenticated current sender can grant control.");
    bool rejectedBounds = false;
    try { router.ConfigureSession(newSender, true, 1, 70000); } catch (ArgumentOutOfRangeException) { rejectedBounds = true; }
    Check(rejectedBounds && router.RemoteWidth == 2560, "Invalid hello dimensions leave router metadata unchanged.");
    Tap(backend, 0x78);
    Check(second.Count(p => p.Type == "enter") == 1 && first.Count == oldCount, "A new capture sends only through the current callback.");
    backend.Emit(new(PhysicalInputKind.Move, Relative: true, DeltaX: 1));
    backend.Emit(new(PhysicalInputKind.Move, Relative: true, DeltaX: 2));
    router.CanControl = false;
    int sentAtRevoke = second.Count;
    await Task.Delay(30);
    Check(second.Count == sentAtRevoke && !backend.Capturing, "A queued motion timer cannot send after permission revocation.");
    Check(!backend.Emit(Key(0x78)), "Revoked shortcut passes through locally.");
    await router.DisposeAsync();
    Check(!router.ConfigureSession(newSender, true), "A late session callback is harmless after router disposal.");
}

// All exit/entry edges retain their orientation; no artificial dwell or per-move native calls.
foreach (var edge in Enum.GetValues<ScreenEdge>())
{
    var backend = new FakeBackend("Linux/X11");
    await using var router = new PortableInputRouter(backend) { CanControl = true, ExitEdge = edge };
    var packets = new List<Packet>(); router.Send = packets.Add; router.Start();
    var b = backend.Bounds;
    var entry = edge switch
    {
        ScreenEdge.Right => new DesktopPoint(b.X + b.Width - 1, b.Y + b.Height / 2),
        ScreenEdge.Left => new DesktopPoint(b.X, b.Y + b.Height / 2),
        ScreenEdge.Top => new DesktopPoint(b.X + b.Width / 2, b.Y),
        _ => new DesktopPoint(b.X + b.Width / 2, b.Y + b.Height - 1),
    };
    backend.Emit(new(PhysicalInputKind.Move, entry.X, entry.Y));
    Check(router.IsRemote && packets[0].Type == "enter" && packets[0].Name == edge.ToString(), $"{edge}: enters without waiting 0.2 seconds.");
    Check(edge switch
    {
        ScreenEdge.Right => packets[0].X < 200,
        ScreenEdge.Left => packets[0].X > 65300,
        ScreenEdge.Top => packets[0].Y > 65300,
        _ => packets[0].Y < 200,
    }, $"{edge}: remote entry is on the opposite screen edge.");
    backend.Emit(new(PhysicalInputKind.Move, Relative: true,
        DeltaX: edge == ScreenEdge.Right ? -20 : edge == ScreenEdge.Left ? 20 : 0,
        DeltaY: edge == ScreenEdge.Top ? 20 : edge == ScreenEdge.Bottom ? -20 : 0));
    Check(!router.IsRemote && packets.Last().Type == "leave", $"{edge}: crossing back returns control.");
}

// A drag held before switching must release locally at the parked anchor, preserving motion deltas.
{
    var backend = new FakeBackend("Windows");
    await using var router = new PortableInputRouter(backend) { CanControl = true };
    var wire = new List<Packet>(); router.Send = wire.Add; router.Start();
    backend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: true));
    Tap(backend, 0x78);
    await AwaitAsync(backend.FirstButtonRelease.Task);
    var anchor = new DesktopPoint(backend.Bounds.X + backend.Bounds.Width / 2, backend.Bounds.Y + backend.Bounds.Height / 2);
    Check(router.IsRemote && backend.Capturing && backend.Position == anchor,
        "The worker's local button-up must not move A away from its parked capture anchor.");
    Check(wire.Any(p => p.Type == "button" && p.Button == "left" && p.Down), "A held drag resumes at B after entry.");
    backend.Emit(new(PhysicalInputKind.Move, anchor.X + 5, anchor.Y));
    backend.Emit(new(PhysicalInputKind.Button, Button: "left", Down: false));
    Check(router.IsRemote && wire.Last().Type == "button" && !wire.Last().Down,
        "The first post-entry physical move and button-up preserve capture and ordered drag completion.");
}

// Receiver validates packet bounds and rejects synthesized emergency chords and old sessions.
{
    var backend = new FakeBackend("macOS");
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    Action<Packet> sender = _ => { }; router.Send = sender; router.Start();
    await router.ApplyAsync(new() { Type = "enter", X = 32768, Y = 32768 }, sender);
    int before = backend.Injected.Length;
    foreach (Packet invalid in new Packet[]
    {
        new() { Type = "key", Key = 999 }, new() { Type = "key", Key = 0x41, Scan = -1 },
        new() { Type = "key", Key = 0x41, Flags = 2 }, new() { Type = "button", Button = "unknown" },
        new() { Type = "text", Text = "\ud800" }, new() { Type = "text", Text = "a", Dx = 129 },
        new() { Type = "text", Text = new string('a', 257) }, new() { Type = "text", Text = "\0" },
        new() { Type = "wheel", Dy = int.MaxValue }, new() { Type = "unsupported" },
    }) await AwaitAsync(router.ApplyAsync(invalid, sender));
    Check(backend.Injected.Length == before, "Malformed key, Unicode, button and wheel packets never reach a native backend.");
    await router.ApplyAsync(new() { Type = "move", X = -100, Y = int.MaxValue }, sender);
    Check(backend.Injected.Last().X == 0 && backend.Injected.Last().Y == 65535, "Coordinates are normalized before injection.");
    await router.ApplyAsync(new() { Type = "key", Key = 0xA2, Down = true }, sender);
    await router.ApplyAsync(new() { Type = "key", Key = 0xA4, Down = true }, sender);
    await router.ApplyAsync(new() { Type = "key", Key = 0x7B, Down = true }, sender);
    Check(!backend.Injected.Any(p => p.Type == "key" && p.Key == 0x7B), "A remote peer cannot inject Ctrl+Alt+F12.");
    int emergencies = 0; router.EmergencyReleased += () => emergencies++;
    backend.Emit(Key(0xA2)); backend.Emit(Key(0xA4)); backend.Emit(Key(0x7B));
    Check(emergencies == 1 && !router.CanControl && !router.IsRemote, "B's physical emergency chord immediately revokes permission independently of the UI.");
    await router.ApplyAsync(new() { Type = "enter" }, sender);
    Check(!router.IsRemote, "A new enter cannot undo B's emergency revoke.");
    await AwaitAsync(router.ResetTypingAsync(TypingMode.VietnameseVni));
    Check(backend.HeldKeys.Count == 0 && router.TypingMode == TypingMode.VietnameseVni, "Reset returns after outstanding native release cleanup.");
    router.CanControl = true;
    Action<Packet> replacement = _ => { }; router.Send = replacement;
    await router.ApplyAsync(new() { Type = "enter" }, sender);
    Check(!router.IsRemote, "An old connection cannot enter the replacement session.");
}

// Ordered slow injection remains off the caller/hook thread and cannot replay after revocation.
{
    var backend = new FakeBackend("Windows") { BlockNextKey = true };
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    router.Start(); await router.ApplyAsync(new() { Type = "enter" });
    Task first = router.ApplyAsync(new() { Type = "key", Key = 0x41, Down = true });
    Check(backend.KeyEntered.Wait(TimeSpan.FromSeconds(3)), "Fake slow native call entered worker.");
    Task second = router.ApplyAsync(new() { Type = "key", Key = 0x42, Down = true });
    router.CanControl = false;
    backend.ContinueKey.Set();
    await AwaitAsync(Task.WhenAll(first, second));
    await AwaitAsync(router.ResetTypingAsync(TypingMode.VietnameseTelex));
    Check(!backend.Injected.Any(p => p.Type == "key" && p.Key == 0x42), "A queued stale key is dropped after permission revocation.");
    Check(backend.HeldKeys.Count == 0, "An in-flight native down is released after revocation.");
}

// Native APIs may report failure after a down was partially accepted; fail closed and release it.
{
    var backend = new FakeBackend("Linux/X11");
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    router.Start(); await router.ApplyAsync(new() { Type = "enter" });
    backend.FailNextKey = true;
    bool failed = false;
    try { await router.ApplyAsync(new() { Type = "key", Key = 0xA2, Down = true }); }
    catch (InvalidOperationException) { failed = true; }
    Check(failed && !router.CanControl && !router.IsRemote && backend.HeldKeys.Count == 0, "Partial native failure revokes input and releases every potential down.");
    Check(!string.IsNullOrEmpty(router.LastError), "Native failures are visible to the session UI.");
}

// A former receiver's slow native call must finish and release before it can become source A.
{
    var backend = new FakeBackend("Windows") { BlockNextKey = true };
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    Action<Packet> oldSender = _ => { }; router.Send = oldSender; router.Start();
    await router.ApplyAsync(new() { Type = "enter" }, oldSender);
    Task oldInput = router.ApplyAsync(new() { Type = "key", Key = 0xA2, Down = true }, oldSender);
    Check(backend.KeyEntered.Wait(TimeSpan.FromSeconds(3)), "Former receiver is inside a slow native down.");
    router.IsController = true;
    var packets = new List<Packet>(); Action<Packet> sourceSender = packets.Add; router.Send = sourceSender;
    router.ConfigureSession(sourceSender, true);
    Check(!backend.Emit(Key(0x78)) && !router.IsRemote && backend.Warps.Count == 0,
        "Source entry fails open until the former receiver's native call and cleanup drain.");
    backend.Emit(Key(0x78, false));
    backend.ContinueKey.Set();
    await AwaitAsync(oldInput);
    await AwaitAsync(router.ResetTypingAsync(TypingMode.VietnameseTelex));
    Check(backend.HeldKeys.Count == 0, "Role transition releases a down accepted after the generation was invalidated.");
    Tap(backend, 0x78);
    Check(router.IsRemote && packets.Count(p => p.Type == "enter") == 1, "A fresh shortcut enters after the native fence settles.");
    await router.ApplyAsync(new() { Type = "leave" }, oldSender);
    Check(router.IsRemote, "A stale receiver callback cannot release the new source session.");
}

// SetCapture can fail (e.g. an X11 grab already belongs to another app).
{
    var backend = new FakeBackend("Linux/X11") { FailCapture = true };
    await using var router = new PortableInputRouter(backend) { CanControl = true };
    var wire = new List<Packet>(); router.Send = wire.Add; router.Start();
    backend.Emit(Key(0x78));
    Check(!router.IsRemote && !router.CanControl && !backend.Capturing, "Failure to grab input rolls back remote state and opens local input.");
    Check(!wire.Any(p => p.Type == "enter"), "A failed local capture must not leave B active.");
}

// Canceled queued work is never injected after another call completes.
{
    var backend = new FakeBackend("macOS") { BlockNextKey = true };
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    router.Start(); await router.ApplyAsync(new() { Type = "enter" });
    Task first = router.ApplyAsync(new() { Type = "key", Key = 0x41, Down = true });
    Check(backend.KeyEntered.Wait(TimeSpan.FromSeconds(3)), "Blocked fake native call starts.");
    using var cancellation = new CancellationTokenSource();
    Task canceled = router.ApplyAsync(new() { Type = "key", Key = 0x42, Down = true }, cancellation.Token);
    cancellation.Cancel(); backend.ContinueKey.Set();
    await AwaitAsync(first);
    try { await AwaitAsync(canceled); } catch (OperationCanceledException) { }
    await AwaitAsync(router.ResetTypingAsync(TypingMode.Physical));
    Check(!backend.Injected.Any(p => p.Type == "key" && p.Key == 0x42), "Cancellation fences a queued native key.");
}

// A blocked native API cannot make an unlimited queue or replay a long stale key sequence.
{
    var backend = new FakeBackend("Windows") { BlockNextKey = true };
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    router.Start(); await router.ApplyAsync(new() { Type = "enter" });
    var pending = new List<Task> { router.ApplyAsync(new() { Type = "key", Key = 0x41, Down = true }) };
    Check(backend.KeyEntered.Wait(TimeSpan.FromSeconds(3)), "Queue-pressure fixture enters the fake native worker.");
    for (int n = 0; n < 4200; n++) pending.Add(router.ApplyAsync(new() { Type = "key", Key = 0x42, Down = true }));
    Check(!router.CanControl && !router.IsRemote, "Input backlog limit fails closed and revokes the receiver.");
    backend.ContinueKey.Set();
    await AwaitAsync(Task.WhenAll(pending));
    await AwaitAsync(router.ResetTypingAsync(TypingMode.VietnameseTelex));
    Check(!backend.Injected.Any(p => p.Type == "key" && p.Key == 0x42), "Overloaded stale input never replays after unblocking.");
    Check(backend.HeldKeys.Count == 0, "Backlog recovery releases the single in-flight down.");
}

// Physical mode, layout-provided Unicode, VNI, and repeated failed translations preserve semantics.
{
    var backend = new FakeBackend("macOS");
    await using var router = new PortableInputRouter(backend) { CanControl = true };
    var wire = new List<Packet>(); router.Send = wire.Add; router.Start(); Tap(backend, 0x78);
    Type(backend, "a");
    // No platform translation at all, matching an intermittently unavailable layout/IME.
    Tap(backend, 0x53); Tap(backend, 0x20); Tap(backend, 0x41); Tap(backend, 0x53);
    Check(Render(wire) == "á á", "Canonical fallback accents the first Telex tone key after missing translations.");
    await router.ResetTypingAsync(TypingMode.VietnameseVni); wire.Clear(); Tap(backend, 0x78);
    Type(backend, "tie6ng1 Vie6t5");
    Check(Render(wire) == "tiếng Việt", "VNI composes consistently on a simulated macOS source.");
    await router.ResetTypingAsync(TypingMode.VietnameseTelex); wire.Clear(); Tap(backend, 0x78);
    Tap(backend, 0x41, "Ä"); Tap(backend, 0x42, "🙂");
    Check(Render(wire) == "Ä🙂", "A valid non-US layout translation and supplementary Unicode stay intact.");
    wire.Clear();
    backend.Emit(Key(0x14)); backend.Emit(Key(0x14)); backend.Emit(Key(0x14, false));
    Tap(backend, 0x54, "t");
    backend.Emit(Key(0xA0)); Tap(backend, 0x48, "H"); backend.Emit(Key(0xA0, false));
    Check(Render(wire) == "Th", "Captured Caps Lock toggles once despite repeats and inverts backend case including Shift.");
    router.ReleaseRemote(); wire.Clear(); Tap(backend, 0x78); Tap(backend, 0x41, "a");
    Check(Render(wire) == "a", "A later entry re-reads case from the backend instead of retaining a swallowed Caps Lock.");
    await router.ResetTypingAsync(TypingMode.Physical); wire.Clear(); Tap(backend, 0x78);
    Type(backend, "as");
    Check(wire.Count(p => p.Type == "key" && p.Down) == 2 && !wire.Any(p => p.Type == "text"), "Physical mode preserves raw keys for the receiving OS's own IME.");
}

// A sender failure must release source capture even if the socket-enqueue callback throws.
{
    var backend = new FakeBackend("Windows");
    await using var router = new PortableInputRouter(backend) { CanControl = true };
    router.Send = _ => throw new IOException("Fake closed socket."); router.Start();
    backend.Emit(Key(0x78));
    Check(!router.IsRemote && !backend.Capturing && !router.CanControl, "Sender failure restores local pointer and keyboard ownership.");
}

// Permanent device-release failures must not recursively enqueue cleanup and spin forever.
{
    var backend = new FakeBackend("Linux/X11");
    await using var router = new PortableInputRouter(backend) { IsController = false, CanControl = true };
    router.Start(); await router.ApplyAsync(new() { Type = "enter" });
    backend.FailReleaseAll = true;
    router.CanControl = false;
    await Task.Delay(40);
    int count = backend.ReleaseCalls;
    await Task.Delay(40);
    Check(count == backend.ReleaseCalls && count <= 5, "A permanent native cleanup failure settles without an unbounded retry loop.");
    Check(!router.CanControl && !router.IsRemote && router.LastError is not null, "An unreleasable device remains revoked and reports its failure.");
}

Console.WriteLine($"CrossPlatformSmoke passed: {assertions} assertions (nine simulated OS directions; no native input).");

static string Render(IEnumerable<Packet> packets)
{
    string result = "";
    foreach (var packet in packets.Where(p => p.Type == "text")) result = result[..(result.Length - packet.Dx)] + packet.Text;
    return result;
}

sealed class FakeBackend(string name) : IInputBackend
{
    [ThreadStatic] private static int _emitDepth;
    private readonly object _gate = new();
    private readonly List<Packet> _injected = [];
    private readonly HashSet<int> _heldKeys = [];
    private readonly HashSet<string> _heldButtons = [];
    private string _document = "";
    public string Name => name;
    public BackendCapabilities Capabilities => new(true, true, true, "Fake native-free backend");
    public DesktopBounds Bounds => new(-100, -50, 1920, 1080);
    public DesktopPoint Position { get; private set; } = new(200, 300);
    public bool Capturing { get; private set; }
    public bool FailCapture { get; set; }
    public bool FailNextKey { get; set; }
    public bool BlockNextKey { get; set; }
    public bool FailReleaseAll { get; set; }
    private int _releaseCalls;
    public int ReleaseCalls => Volatile.Read(ref _releaseCalls);
    public int LastEmitThreadId { get; private set; }
    public bool InjectedInline { get; private set; }
    public ManualResetEventSlim KeyEntered { get; } = new(false);
    public ManualResetEventSlim ContinueKey { get; } = new(false);
    public TaskCompletionSource FirstButtonRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public List<DesktopPoint> Warps { get; } = [];
    public ConcurrentQueue<int> WorkerThreadIds { get; } = [];
    public Packet[] Injected { get { lock (_gate) return _injected.ToArray(); } }
    public HashSet<int> HeldKeys { get { lock (_gate) return new(_heldKeys); } }
    public HashSet<string> HeldButtons { get { lock (_gate) return new(_heldButtons); } }
    public string Document { get { lock (_gate) return _document; } }
    public event Func<PhysicalInput, bool>? Input;
    public event Action<string>? Error;
    public void Start() { }
    public bool Emit(PhysicalInput input)
    {
        LastEmitThreadId = Environment.CurrentManagedThreadId;
        _emitDepth++;
        try
        {
            bool consumed = Input?.Invoke(input) == true;
            if (input.Kind == PhysicalInputKind.Move && !consumed && !input.Relative) Position = new(input.X, input.Y);
            return consumed;
        }
        finally { _emitDepth--; }
    }
    public void SetCapture(bool active)
    {
        if (active && FailCapture) throw new InvalidOperationException("Fake grab failed.");
        Capturing = active;
    }
    public void Warp(DesktopPoint point) { Position = point; Warps.Add(point); }
    public void Inject(Packet packet)
    {
        if (_emitDepth != 0) InjectedInline = true;
        WorkerThreadIds.Enqueue(Environment.CurrentManagedThreadId);
        if (packet.Type == "key" && BlockNextKey)
        {
            BlockNextKey = false; KeyEntered.Set();
            if (!ContinueKey.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Fake worker was not unblocked.");
        }
        lock (_gate)
        {
            _injected.Add(packet);
            if (packet.Type == "key")
            {
                if (packet.Down) _heldKeys.Add(packet.Key); else _heldKeys.Remove(packet.Key);
                if (FailNextKey) { FailNextKey = false; throw new InvalidOperationException("Fake partial native failure."); }
            }
            if (packet.Type == "button")
            {
                if (packet.Down) _heldButtons.Add(packet.Button!); else _heldButtons.Remove(packet.Button!);
                Position = new(Bounds.X + (int)Math.Round(packet.X * (Bounds.Width - 1d) / 65535),
                    Bounds.Y + (int)Math.Round(packet.Y * (Bounds.Height - 1d) / 65535));
                if (!packet.Down) FirstButtonRelease.TrySetResult();
            }
            if (packet.Type == "text")
            {
                if (packet.Dx > _document.Length) throw new InvalidOperationException("Unexpected fake document deletion.");
                _document = _document[..(_document.Length - packet.Dx)] + packet.Text;
            }
        }
    }
    public void ReleaseAll()
    {
        Interlocked.Increment(ref _releaseCalls);
        if (FailReleaseAll) throw new IOException("Fake device release failed.");
        lock (_gate) { _heldKeys.Clear(); _heldButtons.Clear(); }
    }
    public void Dispose() { Capturing = false; KeyEntered.Dispose(); ContinueKey.Dispose(); }
    public void EmitError(string error) => Error?.Invoke(error);
}
