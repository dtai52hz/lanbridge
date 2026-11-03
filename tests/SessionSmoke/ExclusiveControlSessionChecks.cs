using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckExclusiveControlSessionAsync(string root)
    {
        // Hidden forms use actual session routing and authenticated loopback TLS. The
        // callbacks below receive only explicitly synthesized structures, and both
        // input routers prohibit native hooks, cursor movement and SendInput.
        using var a = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "exclusive-a"));
        using var b = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "exclusive-b"));
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
        source.TypingMode = TypingMode.VietnameseTelex;
        Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x23)); // Same End shortcut as the reported session.
        typeof(InputRouter).GetProperty("PrintableTranslationForTesting", Private)!.SetValue(source,
            (Func<int, int, (int Length, string Text)>)((key, _) =>
                (1, ((char)(key is >= 0x41 and <= 0x5A ? key + 32 : key)).ToString())));

        uint ownTag = (uint)typeof(InputRouter).GetField("InjectionTag", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var localKeyboard = new ConcurrentQueue<(uint Key, int Message, ulong Tag)>();
        var localMouse = new ConcurrentQueue<(int Message, ulong Tag)>();
        int receiverSyntheticKeyboard = 0, receiverSyntheticMouse = 0;
        void SetNext(InputRouter router, string name, Func<int, IntPtr, IntPtr, IntPtr> next) =>
            typeof(InputRouter).GetProperty(name, Private)!.SetValue(router, next);
        SetNext(source, "KeyboardHookNextForTesting", (_, message, data) =>
        {
            var input = Marshal.PtrToStructure<ExclusiveKeyboardPayload>(data);
            localKeyboard.Enqueue((input.Key, message.ToInt32(), input.ExtraInfo.ToUInt64()));
            return IntPtr.Zero;
        });
        SetNext(source, "MouseHookNextForTesting", (_, message, data) =>
        {
            var input = Marshal.PtrToStructure<ExclusiveMousePayload>(data);
            localMouse.Enqueue((message.ToInt32(), input.ExtraInfo.ToUInt64()));
            return IntPtr.Zero;
        });
        SetNext(receiver, "KeyboardHookNextForTesting", (_, _, _) => { Interlocked.Increment(ref receiverSyntheticKeyboard); return IntPtr.Zero; });
        SetNext(receiver, "MouseHookNextForTesting", (_, _, _) => { Interlocked.Increment(ref receiverSyntheticMouse); return IntPtr.Zero; });

        var applied = new ConcurrentQueue<Packet>();
        var document = new StringBuilder();
        var documentGate = new object();
        typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!.SetValue(receiver, (Action<Packet>)applied.Enqueue);
        typeof(InputRouter).GetProperty("NativeInputBatchForTesting", Private)!.SetValue(receiver, (Action<Array>)(entries =>
        {
            object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
            lock (documentGate)
                foreach (object entry in entries)
                {
                    if ((uint)Field(entry, "Type") != 1) continue;
                    object key = Field(Field(entry, "Union"), "Keyboard");
                    uint flags = (uint)Field(key, "Flags");
                    if ((flags & 2) != 0) continue;
                    if ((flags & 4) != 0) document.Append((char)(ushort)Field(key, "Scan"));
                    else if ((ushort)Field(key, "Vk") == 8)
                    {
                        if (document.Length == 0) throw new InvalidDataException("Exclusive-control test erased before its document.");
                        document.Length--;
                    }
                }
        }));
        string Document() { lock (documentGate) return document.ToString(); }
        int LocalKeyboardActions() => localKeyboard.Count(item => item.Tag != ownTag);
        int LocalMouseActions() => localMouse.Count(item => item.Tag != ownTag);
        Task<bool> KeyOn(InputRouter router, int key, bool down, uint flags = 0, ulong tag = 0, int scan = 0) =>
            (Task<bool>)Call(router, "RouteKeyboardHookForTestingAsync", key, down, flags, tag, scan, 0)!;
        Task<bool> Key(int key, bool down, uint flags = 0, ulong tag = 0, int scan = 0) => KeyOn(source, key, down, flags, tag, scan);
        Task<bool> MouseOn(InputRouter router, int message, Point point, uint mouseData = 0, uint flags = 0, ulong tag = 0) =>
            (Task<bool>)Call(router, "RouteMouseHookForTestingAsync", message, point.X, point.Y, mouseData, flags, tag, 0)!;
        Task<bool> Mouse(int message, Point point, uint mouseData = 0, uint flags = 0, ulong tag = 0) =>
            MouseOn(source, message, point, mouseData, flags, tag);
        async Task Enter()
        {
            Check(await Key(0x23, true) && await Key(0x23, false), "End must be owned by the machine-switch action, including its up.");
            await (Task)Call(source, "InvokeForTestingAsync", (Action)(() => Set(source, "_textCapsLock", false)))!;
            await WaitUntilAsync(() => source.IsRemote && receiver.IsRemote, "Exclusive source callbacks did not enter B through the actual TLS session.");
        }
        async Task InputBarrier()
        {
            int expected = applied.Count(packet => packet.Type == "key" && packet.Key == 0x70 && !packet.Down) + 1;
            Check(await Key(0x70, true) && await Key(0x70, false), "The native-free input barrier must also be exclusive to B.");
            await WaitUntilAsync(() => applied.Count(packet => packet.Type == "key" && packet.Key == 0x70 && !packet.Down) >= expected,
                "Input callbacks did not cross the TLS/native-worker FIFO barrier.");
        }

        using var session = await ConnectAsync(a, b);
        try
        {
            await WaitUntilAsync(() => source.CanControl && receiver.CanControl, "Exclusive-control test did not receive the host input grant.");
            AssertNoHooks(source, receiver);
            Check(!await Key(0x5A, true) && !await Key(0x5A, false) && LocalKeyboardActions() == 2,
                "Before switching to B, fresh physical keys must still reach local A.");
            await Enter();
            int aKeysBefore = LocalKeyboardActions(), aMouseBefore = LocalMouseActions();
            foreach (char letter in "mootj")
            {
                int vk = char.ToUpperInvariant(letter);
                Check(await Key(vk, true) && await Key(vk, false), "A physical Telex key must be swallowed locally while routing to B.");
            }
            Point anchor = Get<Point>(source, "_anchor");
            Check(await Mouse(0x0200, new Point(anchor.X + 12, anchor.Y + 8)), "A remote pointer move must not reach A's next mouse consumer.");
            Check(await Mouse(0x0201, anchor) && await Mouse(0x0202, anchor), "A remote click must consume both button transitions on A.");
            Check(await Mouse(0x020A, anchor, 120u << 16), "A remote wheel must be consumed locally on A.");
            await InputBarrier();
            await WaitUntilAsync(() => Document() == "một", "Exclusive physical Telex did not produce the exact Vietnamese document on B.");
            Check(LocalKeyboardActions() == aKeysBefore && LocalMouseActions() == aMouseBefore,
                "An A foreground consumer received a physical action already delivered to B.");
            Check(applied.Count(packet => packet.Type == "move") == 1
                && applied.Count(packet => packet.Type == "button" && packet.Button == "left" && packet.Down) == 1
                && applied.Count(packet => packet.Type == "button" && packet.Button == "left" && !packet.Down) == 1
                && applied.Count(packet => packet.Type == "wheel" && packet.Dy == 120) == 1,
                "One physical move/click/wheel must reach B once without callback duplication.");

            int textBeforeForeign = applied.Count(packet => packet.Type == "text");
            int mouseBeforeForeign = applied.Count(packet => packet.Type is "move" or "button" or "wheel");
            int rawBeforeForeign = applied.Count(packet => packet.Type == "key" && packet.Key != 0x70);
            foreach (var input in new[] { (Key: 0x41, Scan: 0), (Key: 0xE7, Scan: 0x1ED9), (Key: 0x08, Scan: 0), (Key: 0x5B, Scan: 0) })
                Check(await Key(input.Key, true, 0x10, 0, input.Scan) && await Key(input.Key, false, 0x10, 0, input.Scan),
                    "A foreign injected ASCII/Unicode/edit/modifier must not reach local A during remote capture.");
            Check(await Mouse(0x0200, anchor, flags: 1)
                && await Mouse(0x0201, anchor, flags: 1)
                && await Mouse(0x0202, anchor, flags: 1)
                && await Mouse(0x020A, anchor, 120u << 16, 1),
                "Foreign injected move/click/wheel actions must not leak into A.");
            Check(!await Key(0xA2, false, 0x10, ownTag)
                && !await Mouse(0x0200, anchor, flags: 1, tag: ownTag),
                "LanBridge's own tagged cleanup/anchor operations must keep their native pass-through path.");
            await InputBarrier();
            Check(LocalKeyboardActions() == aKeysBefore && LocalMouseActions() == aMouseBefore
                && localKeyboard.Any(item => item.Tag == ownTag) && localMouse.Any(item => item.Tag == ownTag),
                "Remote A must allow only its tagged cleanup, never unrelated injected local app actions.");
            Check(applied.Count(packet => packet.Type == "text") == textBeforeForeign
                && applied.Count(packet => packet.Type is "move" or "button" or "wheel") == mouseBeforeForeign
                && applied.Count(packet => packet.Type == "key" && packet.Key != 0x70) == rawBeforeForeign && Document() == "một",
                "Foreign injection must not be re-forwarded as duplicate text, edit keys, clicks or wheel on B.");
            Check(!await KeyOn(receiver, 0xE7, true, 0x10, ownTag, 0x1ED9)
                && !await MouseOn(receiver, 0x0200, anchor, flags: 1, tag: ownTag)
                && receiverSyntheticKeyboard == 1 && receiverSyntheticMouse == 1,
                "Receiver B must still accept its own injected text and pointer instead of swallowing the output.");

            source.TypingMode = TypingMode.Physical;
            Check(await Key(0x57, true) && await Mouse(0x0201, anchor), "Held remote key/button must initially be owned by B.");
            await InputBarrier();
            Check(await Key(0x23, true) && await Key(0x23, false), "End return and its trailing up must not become local key actions.");
            await WaitUntilAsync(() => !source.IsRemote && !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "Returning to A did not release B's input FIFO.");
            Check(Get<IDictionary>(receiver, "_receivedKeys").Count == 0 && Get<HashSet<string>>(receiver, "_receivedButtons").Count == 0,
                "Returning to A left a remote key or button pressed on B.");
            Check(await Key(0x57, true) && await Key(0x57, false) && await Mouse(0x0202, anchor),
                "Remote-held key repeat/up and button up must remain quarantined after returning to A.");
            Check(LocalKeyboardActions() == aKeysBefore && LocalMouseActions() == aMouseBefore,
                "A key/button that started only on B leaked an orphan transition into local A after return.");
            Check(!await Key(0x57, true) && !await Key(0x57, false)
                && !await Mouse(0x0201, anchor) && !await Mouse(0x0202, anchor),
                "After genuine ups, fresh local A presses and clicks must work immediately.");
            Check(LocalKeyboardActions() == aKeysBefore + 2 && LocalMouseActions() == aMouseBefore + 2,
                "The return quarantine did not restore normal fresh local keyboard/mouse actions.");

            await Enter();
            anchor = Get<Point>(source, "_anchor");
            aKeysBefore = LocalKeyboardActions(); aMouseBefore = LocalMouseActions();
            Check(await Key(0x58, true) && await Mouse(0x0204, anchor),
                "Mouse-edge return must also begin with a key/button owned only by B.");
            await InputBarrier();
            Check(await Mouse(0x0200, new Point(anchor.X - Math.Clamp(source.RemoteWidth, 2, 65536) - 10, anchor.Y)) && !source.IsRemote,
                "The physical crossing that returns to A must itself remain consumed after ending remote capture.");
            await WaitUntilAsync(() => !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "Mouse-edge return did not release B through the actual session FIFO.");
            Check(await Key(0x58, true) && await Key(0x58, false) && await Mouse(0x0205, anchor)
                && LocalKeyboardActions() == aKeysBefore && LocalMouseActions() == aMouseBefore,
                "Mouse-edge return leaked remote-owned repeats/ups into A.");
            Check(!await Key(0x58, true) && !await Key(0x58, false)
                && !await Mouse(0x0204, anchor) && !await Mouse(0x0205, anchor),
                "Fresh A keyboard/mouse actions must resume after the edge-return quarantine is released.");

            await Enter();
            aKeysBefore = LocalKeyboardActions();
            Check(await Key(0xA2, true) && await Key(0xA4, true), "Remote Ctrl/Alt downs must not leak to A before emergency return.");
            Check(await Key(0x7B, true), "The remote-origin emergency trigger must be consumed after releasing B.");
            Check(await Key(0x7B, false) && await Key(0xA4, false) && await Key(0xA2, false),
                "Emergency chord trailing ups must not execute an orphan local A shortcut.");
            await WaitUntilAsync(() => !source.IsRemote && !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "Ctrl+Alt+F12 did not return exclusive control through the TLS session.");
            Check(LocalKeyboardActions() == aKeysBefore && Get<IDictionary>(receiver, "_receivedKeys").Count == 0,
                "Emergency return duplicated the chord on A or left B modifiers held.");
            Check(!await Key(0x51, true) && !await Key(0x51, false), "A fresh local press must work after all emergency components are released.");

            await Enter();
            anchor = Get<Point>(source, "_anchor");
            Check(await Key(0x44, true) && await Mouse(0x0204, anchor), "Disconnect regression must begin with a held remote key/right button.");
            await InputBarrier();
            Call(a, "Disconnect");
            await WaitUntilAsync(() => Get<PeerConnection?>(b, "_peer") is null && !receiver.IsRemote,
                "Disconnect did not release the exclusive receiver and TLS peer.");
            Check(Get<IDictionary>(receiver, "_receivedKeys").Count == 0 && Get<HashSet<string>>(receiver, "_receivedButtons").Count == 0,
                "Disconnect left a synthetic B key/button held.");
            Check(await Key(0x44, true) && await Key(0x44, false) && await Mouse(0x0205, anchor),
                "Disconnect must quarantine held remote repeats/ups before normal local use resumes.");
            Check(!await Key(0x44, true) && !await Key(0x44, false), "A must accept a fresh physical press after disconnect and release.");
            Check(session.A.IsClosed && session.B.IsClosed && !a.Visible && !b.Visible,
                "Exclusive session tests must close their isolated TLS pair and never display test forms.");
            AssertNoHooks(source, receiver);
            Console.WriteLine("Exclusive control: fake hook callbacks suppress duplicate A actions, deliver one-pass Vietnamese/mouse to B through TLS, quarantine held return inputs and preserve emergency/disconnect recovery.");
        }
        finally { Call(a, "Disconnect"); Call(b, "Disconnect"); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExclusiveKeyboardPayload
    {
        public uint Key, Scan, Flags, Time;
        public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ExclusiveMousePayload
    {
        public Point Point;
        public uint MouseData, Flags, Time;
        public UIntPtr ExtraInfo;
    }
}
