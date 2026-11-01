using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using LanBridge;

internal static partial class Program
{
    private static async Task CheckKeyboardHookPrioritySessionAsync(string root)
    {
        using var a = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "hook-priority-a"));
        using var b = new MainForm(preview: true, clipboard: new MemoryClipboard(), receivedDirectory: Path.Combine(root, "hook-priority-b"));
        _ = a.Handle; _ = b.Handle;
        Get<System.Windows.Forms.Timer>(a, "_clipboardTimer").Stop();
        Get<System.Windows.Forms.Timer>(b, "_clipboardTimer").Stop();
        Get<ComboBox>(b, "_role").SelectedIndex = 1;
        Set(a, "_permissions", new Permissions { Input = false, Files = false, Clipboard = false });
        Set(b, "_permissions", new Permissions { Input = true, Files = false, Clipboard = false });
        Call(a, "ApplyPermissions"); Call(b, "ApplyPermissions");
        var source = Get<InputRouter>(a, "_input");
        var receiver = Get<InputRouter>(b, "_input");
        var chain = new FakeKeyboardHookChain();
        Type apiType = typeof(InputRouter).Assembly.GetType("LanBridge.KeyboardHookTestApi")
            ?? throw new MissingMemberException(nameof(InputRouter), "KeyboardHookTestApi");
        object api = Activator.CreateInstance(apiType,
            (Func<Func<int, IntPtr, IntPtr, IntPtr>, IntPtr>)chain.InstallCapture,
            (Func<IntPtr, bool>)chain.Uninstall,
            (Func<int, IntPtr, IntPtr, IntPtr>)chain.Next,
            (Func<long>)(() => chain.Now))!;
        typeof(InputRouter).GetProperty("KeyboardHooksForTesting", Private)!.SetValue(source, api);
        // Both StartForTesting calls prohibit native installation and input. The only hooks
        // below live in the in-memory fake chain, using the production owner's callback.
        Call(source, "StartForTesting"); Call(receiver, "StartForTesting");
        source.TypingMode = TypingMode.VietnameseTelex;
        Call(a, "ApplySwitchShortcut", new SwitchShortcut(0x78));
        typeof(InputRouter).GetProperty("PrintableTranslationForTesting", Private)!.SetValue(source,
            (Func<int, int, (int Length, string Text)>)((key, _) =>
                (1, ((char)(key is >= 0x41 and <= 0x5A ? key + 32 : key)).ToString())));
        var document = new StringBuilder();
        var documentGate = new object();
        var applied = new ConcurrentQueue<Packet>();
        string Document() { lock (documentGate) return document.ToString(); }
        object Field(object value, string name) => value.GetType().GetField(name)!.GetValue(value)!;
        typeof(InputRouter).GetProperty("PacketAppliedForTesting", Private)!.SetValue(receiver, (Action<Packet>)applied.Enqueue);
        typeof(InputRouter).GetProperty("NativeInputBatchForTesting", Private)!.SetValue(receiver, (Action<Array>)(entries =>
        {
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
                        if (document.Length == 0) throw new InvalidDataException("Hook-priority test erased before its document.");
                        document.Length--;
                    }
                }
        }));
        async Task Owner(Action action) => await (Task)Call(source, "InvokeForTestingAsync", action)!;
        async Task Key(int key, bool down) => await Owner(() => chain.Dispatch(key, down));
        async Task Type(string text)
        {
            foreach (char c in text) { int key = char.ToUpperInvariant(c); await Key(key, true); await Key(key, false); }
        }
        async Task Maintain(long elapsed)
        {
            await Owner(() => chain.Now += elapsed);
            await (Task)Call(source, "MaintainKeyboardHookForTestingAsync")!;
        }

        using var session = await ConnectAsync(a, b);
        await WaitUntilAsync(() => source.CanControl && receiver.CanControl, "The hook-priority test did not receive approved TLS permissions.");
        var tokenA = Get<CancellationTokenSource>(a, "_session");
        var tokenB = Get<CancellationTokenSource>(b, "_session");
        try
        {
            await Key(0x78, true); await Key(0x78, false);
            await Owner(() => Set(source, "_textCapsLock", false));
            await WaitUntilAsync(() => receiver.IsRemote, "The fake capture hook did not enter B over TLS.");
            // This deliberately configured consumer models documented hook-chain suppression.
            // It does not claim to reproduce EVKey's private implementation.
            await Owner(() => chain.InstallPrecedingToneConsumer());
            await Type("mootjj");
            await WaitUntilAsync(() => Document() == "môt", "The preceding fake hook did not swallow two tone presses.");
            Check(chain.ConsumedTonePresses == 2 && Document() == "môt",
                "A preceding hook can consume physical tone commands before LanBridge sees them.");
            await Type("j ");
            await WaitUntilAsync(() => Document() == "một ", "The third tone command did not reach B.");
            Check(Document() == "một ", "The three-press symptom must reproduce without dropping a receiver Unicode edit.");

            int installedBefore = chain.CaptureInstallations;
            await Owner(() => chain.ResetToneConsumer());
            await Maintain(250);
            Check(chain.CaptureInstallations == installedBefore && !chain.HeadIsCapture,
                "A newly preceding hook must not trigger repeated renewal before the bounded periodic interval.");
            await Maintain(10000);
            Check(chain.CaptureInstallations > installedBefore && chain.HeadIsCapture,
                "Production maintenance must renew capture priority after a later hook takes over and the periodic interval elapses.");
            await Type("mootj ");
            await WaitUntilAsync(() => Document() == "một một ", "Hook renewal did not preserve the first tone command over TLS.");
            Check(chain.ConsumedTonePresses == 0 && Document() == "một một " && source.IsRemote && receiver.IsRemote,
                "Renewed capture must accept one-pass Vietnamese and consume physical input before the preceding consumer.");

            IntPtr beforeHeld = chain.ActiveCaptureHandle;
            await Key(0xA2, true);
            await Maintain(11000);
            Check(chain.ActiveCaptureHandle == beforeHeld,
                "Hook maintenance must defer while Ctrl is physically held rather than splitting a shortcut.");
            await Key(0x43, true); await Key(0x43, false); await Key(0xA2, false);
            await Maintain(250);
            Check(chain.ActiveCaptureHandle != beforeHeld,
                "Deferred hook maintenance must resume when the whole shortcut is released and quiet.");
            await Type("moot");
            await WaitUntilAsync(() => Document() == "một một môt", "An unfinished word did not reach B before renewal.");
            await Maintain(11000);
            await Type("j ");
            await WaitUntilAsync(() => Document() == "một một một ", "Hook maintenance discarded an unfinished Telex word.");
            Check(Document() == "một một một ",
                "Refreshing hook priority must preserve the current composer word and its suffix corrections.");

            await Key(0x78, true); await Key(0x78, false);
            await WaitUntilAsync(() => !receiver.IsRemote && applied.LastOrDefault()?.Type == "leave",
                "Hook-priority test did not complete its native-free leave barrier.");
            Check(Get<IDictionary>(receiver, "_receivedKeys").Count == 0
                && ReferenceEquals(Get<CancellationTokenSource>(a, "_session"), tokenA)
                && ReferenceEquals(Get<CancellationTokenSource>(b, "_session"), tokenB)
                && !tokenA.IsCancellationRequested && !tokenB.IsCancellationRequested
                && !session.A.IsClosed && !session.B.IsClosed,
                "Priority recovery must release every shortcut while preserving the same authenticated TLS session.");
            Check(!Get<bool>(source, "_nativeInputEnabled") && !Get<bool>(receiver, "_nativeInputEnabled")
                && Get<IntPtr>(source, "_mouseHook") == IntPtr.Zero && Get<IntPtr>(receiver, "_mouseHook") == IntPtr.Zero
                && !a.Visible && !b.Visible,
                "The hook-priority integration must only use fake hooks, fake native input and hidden preview forms.");
            Console.WriteLine("Keyboard-hook priority: fake preceding consumer reproduces third-press tone; production renewal restores first-press tone, preserves partial words/chords and retains TLS.");
        }
        finally
        {
            foreach (int key in new[] { 0x43, 0xA2, 0x78 }) await Key(key, false);
            source.ReleaseRemote();
            await Owner(() => { });
            if (!session.A.IsClosed && !session.B.IsClosed)
                await WaitUntilAsync(() => !receiver.IsRemote, "Hook-priority cleanup did not release B.");
            Call(a, "Disconnect"); Call(b, "Disconnect");
        }
    }

    private sealed class FakeKeyboardHookChain
    {
        private readonly List<(IntPtr Handle, Func<int, IntPtr, IntPtr, IntPtr> Callback)> _hooks = [];
        private readonly HashSet<IntPtr> _captureHandles = [];
        private (IntPtr Handle, Func<int, IntPtr, IntPtr, IntPtr> Callback)[] _dispatch = [];
        private int _nextHandle = 1, _index = -1;
        private bool _swallowToneUp;
        internal long Now;
        internal int CaptureInstallations { get; private set; }
        internal int ConsumedTonePresses { get; private set; }
        internal IntPtr Head => _hooks.Count > 0 ? _hooks[0].Handle : IntPtr.Zero;
        internal bool HeadIsCapture => _captureHandles.Contains(Head);
        internal IntPtr ActiveCaptureHandle => _hooks.FirstOrDefault(item => _captureHandles.Contains(item.Handle)).Handle;
        internal IntPtr InstallCapture(Func<int, IntPtr, IntPtr, IntPtr> callback)
        {
            CaptureInstallations++;
            IntPtr handle = Install(callback);
            _captureHandles.Add(handle);
            return handle;
        }
        private IntPtr Install(Func<int, IntPtr, IntPtr, IntPtr> callback)
        {
            var handle = new IntPtr(_nextHandle++);
            _hooks.Insert(0, (handle, callback));
            return handle;
        }
        internal bool Uninstall(IntPtr handle) => _hooks.RemoveAll(item => item.Handle == handle) > 0;
        internal void ResetToneConsumer() { ConsumedTonePresses = 0; _swallowToneUp = false; }
        internal void InstallPrecedingToneConsumer() => Install((code, message, data) =>
        {
            var key = Marshal.PtrToStructure<FakeKeyboardHookData>(data);
            bool down = message.ToInt32() == 0x100;
            if (key.VkCode == 0x4A)
            {
                if (down && ConsumedTonePresses < 2) { ConsumedTonePresses++; _swallowToneUp = true; return new IntPtr(1); }
                if (!down && _swallowToneUp) { _swallowToneUp = false; return new IntPtr(1); }
            }
            return Next(code, message, data);
        });
        internal IntPtr Next(int code, IntPtr message, IntPtr data) => Invoke(_index + 1, code, message, data);
        private IntPtr Invoke(int index, int code, IntPtr message, IntPtr data)
        {
            if (index >= _dispatch.Length) return IntPtr.Zero;
            int previous = _index;
            _index = index;
            try { return _dispatch[index].Callback(code, message, data); }
            finally { _index = previous; }
        }
        internal void Dispatch(int key, bool down)
        {
            IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<FakeKeyboardHookData>());
            try
            {
                Marshal.StructureToPtr(new FakeKeyboardHookData { VkCode = (uint)key, Time = unchecked((uint)Now) }, data, false);
                _dispatch = _hooks.ToArray();
                Invoke(0, 0, new IntPtr(down ? 0x100 : 0x101), data);
            }
            finally { _dispatch = []; Marshal.FreeHGlobal(data); }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FakeKeyboardHookData
    {
        public uint VkCode, ScanCode, Flags, Time;
        public UIntPtr ExtraInfo;
    }
}
