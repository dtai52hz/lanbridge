using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using LanBridge;

internal static class KeyboardRouteChecks
{
    internal static void Run(Action<bool, string> check)
    {
        // These cases call only the packet-routing helpers, never hooks, Start or injection.
        // Modifiers, navigation and Backspace also avoid consulting the desktop keyboard layout.
        using (var input = new RoutedKeys())
        {
            input.Composer.Process('a');
            input.Down(0xA0);
            check(input.Sent.Count == 0 && input.Take().Length == 0, "Shift alone must stay deferred while typing Unicode.");
            input.Down(0xA2);
            check(input.Composer.CurrentWord == "", "Starting a Ctrl shortcut must end the current Vietnamese word.");
            input.Up(0xA2);
            input.Up(0xA0);
            check(Sequence(input.Take(), (0xA0, true), (0xA2, true), (0xA2, false), (0xA0, false)),
                "A deferred Shift must precede Ctrl and both modifiers must be released exactly once.");
            check(input.Sent.Count == 0 && input.TextKeys.Count == 0, "A completed modifier chord must leave no held remote or text keys.");
        }

        using (var input = new RoutedKeys())
        {
            input.Composer.Process('a');
            input.Down(0xA0);
            input.Down(0x25);
            check(input.Composer.CurrentWord == "", "Navigation must reset composition before moving the remote caret.");
            input.Up(0x25);
            input.Up(0xA0);
            check(Sequence(input.Take(), (0xA0, true), (0x25, true), (0x25, false), (0xA0, false)),
                "Shift+Left must select on B and release its deferred Shift after navigation ends.");
            check(input.Sent.Count == 0, "Shift selection must not leave a remote modifier held.");
        }

        using (var input = new RoutedKeys())
        {
            input.Down(0x25);
            input.Down(0xA0);
            input.Up(0xA0);
            input.Up(0x25);
            check(Sequence(input.Take(), (0x25, true), (0xA0, true), (0xA0, false), (0x25, false)),
                "Shift pressed while a navigation key is already held must take effect immediately.");
            check(input.Sent.Count == 0, "Releasing Shift before its navigation key must leave no held keys.");
        }

        using (var input = new RoutedKeys())
        {
            input.Down(0xA2);
            input.Down(0xA0);
            input.Up(0xA2);
            input.Up(0xA0);
            check(Sequence(input.Take(), (0xA2, true), (0xA0, true), (0xA2, false), (0xA0, false)),
                "Shift pressed after Ctrl must preserve the shortcut's physical modifier order.");
        }

        foreach (int[] modifiers in new[] { new[] { 0xA2, 0xA4 }, new[] { 0x5B }, new[] { 0x5C } })
        {
            using var input = new RoutedKeys();
            input.Composer.Process('a');
            foreach (int modifier in modifiers) input.Down(modifier);
            input.Down(0x08);
            input.Up(0x08);
            foreach (int modifier in modifiers.Reverse()) input.Up(modifier);
            var expected = modifiers.Select(key => (key, true)).Concat([(0x08, true), (0x08, false)])
                .Concat(modifiers.Reverse().Select(key => (key, false))).ToArray();
            check(Sequence(input.Take(), expected), "Ctrl+Alt and Windows shortcuts must forward raw Backspace, without a Unicode edit.");
            check(input.Composer.CurrentWord == "" && input.TextKeys.Count == 0 && input.Sent.Count == 0,
                "A shortcut must reset the word and clear all routing state after release.");
        }

        using (var input = new RoutedKeys())
        {
            input.Down(0xA0);
            input.Call("SendPhysicalShifts");
            input.Call("SendButton", "left", true);
            input.Call("ReleaseUnneededShift");
            check(input.Sent.Contains(0xA0), "A Shift+mouse operation must retain Shift while its button is held.");
            input.Call("SendButton", "left", false);
            input.Call("ReleaseUnneededShift");
            input.Up(0xA0);
            var packets = input.Take();
            check(packets.Length == 4 && IsKey(packets[0], 0xA0, true) && packets[1].Type == "button" && packets[1].Down
                && packets[2].Type == "button" && !packets[2].Down && IsKey(packets[3], 0xA0, false),
                "Shift+mouse must send Shift before the click and release it after the button, without duplicate releases.");
            check(input.Sent.Count == 0, "A completed Shift+mouse operation must not leave Shift held remotely.");
        }

        using (var input = new RoutedKeys())
        {
            input.Call("SendButton", "left", true);
            input.Down(0xA0);
            check(input.Sent.Contains(0xA0), "Pressing Shift during an active drag must forward the modifier immediately.");
            input.Call("SendButton", "left", false);
            input.Call("ReleaseUnneededShift");
            input.Up(0xA0);
            var packets = input.Take();
            check(packets.Length == 4 && packets[0].Type == "button" && packets[0].Down && IsKey(packets[1], 0xA0, true)
                && packets[2].Type == "button" && !packets[2].Down && IsKey(packets[3], 0xA0, false),
                "A drag modified by a later Shift press must preserve event order and release Shift when the drag ends.");
            check(input.Sent.Count == 0, "Shift pressed during a drag must not remain held remotely after release.");
        }

        using (var input = new RoutedKeys())
        {
            input.Router.TypingMode = TypingMode.Physical;
            input.Down(0xA0);
            input.Call("SendButton", "left", true);
            input.Call("SendButton", "left", false);
            input.Call("ReleaseUnneededShift");
            check(input.Sent.Contains(0xA0), "Ending a mouse click in physical mode must retain a physically held Shift.");
            input.Call("SendWheel", 0, 120);
            input.Call("ReleaseUnneededShift");
            check(input.Sent.Contains(0xA0), "Ending a wheel event in physical mode must retain a physically held Shift.");
            input.Up(0xA0);
            var packets = input.Take();
            check(packets.Length == 5 && IsKey(packets[0], 0xA0, true) && packets[1].Type == "button" && packets[1].Down
                && packets[2].Type == "button" && !packets[2].Down && packets[3].Type == "wheel" && packets[3].Dy == 120
                && IsKey(packets[4], 0xA0, false), "Physical Shift must stay held across clicks and wheel events until its real key up.");
            check(input.Sent.Count == 0, "Physical Shift must clear after its actual release.");
        }

        using (var input = new RoutedKeys())
        {
            input.Down(0x08);
            check(input.TextKeys.Contains(0x08) && input.Sent.Count == 0, "Text Backspace must be tracked as an edit rather than a held raw key.");
            input.Up(0x08);
            var packets = input.Take();
            check(packets.Length == 1 && packets[0].Type == "text" && packets[0].Dx == 1 && string.IsNullOrEmpty(packets[0].Text),
                "Text Backspace must emit one deletion and no raw key up.");
            check(input.TextKeys.Count == 0 && input.Sent.Count == 0, "Releasing text Backspace must clear its text routing state.");
            input.Down(0x14);
            input.Up(0x14);
            check(input.CapsLock && input.Take().Length == 0 && input.TextKeys.Count == 0, "Text Caps Lock must toggle locally without leaving a remote raw key.");
            input.Down(0x14);
            input.Up(0x14);
            check(!input.CapsLock && input.Take().Length == 0, "A second text Caps Lock press must restore the original state.");
        }

        using (var input = new RoutedKeys())
        {
            input.Composer.Process('a');
            input.Down(0x0D);
            input.Up(0x0D);
            check(Sequence(input.Take(), (0x0D, true), (0x0D, false)) && input.Composer.CurrentWord == "",
                "Enter must end composition and preserve its physical down/up semantics.");
            input.Composer.Process('a');
            input.Router.TypingMode = TypingMode.Physical;
            check(input.Composer.CurrentWord == "", "Changing typing mode must discard the prior composition context.");
            input.Down(0xA0);
            input.Down(0x41);
            input.Up(0x41);
            input.Up(0xA0);
            check(Sequence(input.Take(), (0xA0, true), (0x41, true), (0x41, false), (0xA0, false)),
                "Physical typing mode must transmit raw printable keys and modifiers with balanced down/up events.");
            check(input.Sent.Count == 0 && input.TextKeys.Count == 0 && input.Composer.CurrentWord == "",
                "Physical typing must leave no Unicode composition or held-key residue.");
        }
    }

    private static bool IsKey(Packet packet, int key, bool down) => packet.Type == "key" && packet.Key == key && packet.Down == down;
    private static bool Sequence(Packet[] actual, params (int Key, bool Down)[] expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair => IsKey(pair.First, pair.Second.Key, pair.Second.Down));

    private sealed class RoutedKeys : IDisposable
    {
        private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
        private const string Barrier = "keyboard-route-test-barrier";
        private static readonly Type StrokeType = typeof(InputRouter).GetNestedType("KeyStroke", BindingFlags.NonPublic)!;
        private readonly ConcurrentQueue<Packet> _packets = new();
        private readonly ManualResetEventSlim _drained = new();
        internal InputRouter Router { get; } = new();
        internal IDictionary Sent => (IDictionary)Field("_sentKeys");
        internal HashSet<int> TextKeys => (HashSet<int>)Field("_textKeys");
        internal VietnameseComposer Composer => (VietnameseComposer)Field("_composer");
        internal bool CapsLock => (bool)Field("_textCapsLock");
        private IDictionary Physical => (IDictionary)Field("_physicalKeys");

        internal RoutedKeys() => Router.Send = packet =>
        {
            if (packet.Type == Barrier) _drained.Set();
            else _packets.Enqueue(packet);
        };
        private object Field(string name) => typeof(InputRouter).GetField(name, Private)!.GetValue(Router)!;
        internal void Call(string method, params object[] args) => typeof(InputRouter).GetMethod(method, Private)!.Invoke(Router, args);
        private static object Stroke(int key) => Activator.CreateInstance(StrokeType, [key, 0, key == 0x25])!;
        internal void Down(int key)
        {
            object stroke = Stroke(key);
            Physical[key] = stroke;
            Call("RouteControllerKey", stroke, true);
        }
        internal void Up(int key)
        {
            Physical.Remove(key);
            Call("RouteControllerKey", Stroke(key), false);
        }
        internal Packet[] Take()
        {
            _drained.Reset();
            Call("Queue", new Packet { Type = Barrier });
            if (!_drained.Wait(TimeSpan.FromSeconds(3))) throw new InvalidOperationException("Keyboard packet routing did not flush.");
            var result = new List<Packet>();
            while (_packets.TryDequeue(out var packet)) result.Add(packet);
            return result.ToArray();
        }
        public void Dispose()
        {
            Router.Dispose();
            Take();
            _drained.Dispose();
        }
    }
}
