using System.Reflection;
using System.Collections.Concurrent;
using LanBridge;

internal static class UnicodeChecks
{
    private const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly MethodInfo Builder = typeof(InputRouter).GetMethod("BuildTextInputs", PrivateStatic)
        ?? throw new MissingMethodException("InputRouter.BuildTextInputs");

    internal static void Run(Action<bool, string> check)
    {
        // BuildTextInputs only constructs values. Do not call Start, ApplyRemote or InjectText.
        Array Build(Packet packet) => (Array)Builder.Invoke(null, [packet])!;
        object Field(object target, string name) => target.GetType().GetField(name)!.GetValue(target)!;
        (ushort Vk, ushort Scan, uint Flags, ulong Tag) Read(object input)
        {
            check((uint)Field(input, "Type") == 1, "A text edit must contain only keyboard INPUT entries.");
            object keyboard = Field(Field(input, "Union"), "Keyboard");
            return ((ushort)Field(keyboard, "Vk"), (ushort)Field(keyboard, "Scan"),
                (uint)Field(keyboard, "Flags"), ((UIntPtr)Field(keyboard, "ExtraInfo")).ToUInt64());
        }

        string text = "ếng Việt 😀\t\r\n";
        var entries = Build(new Packet { Type = "text", Dx = 2, Text = text });
        check(entries.Length == (2 + text.Length) * 2, "Unicode UTF-16 units and Backspaces must each have a down/up pair.");
        for (int index = 0; index < entries.Length; index += 2)
        {
            var down = Read(entries.GetValue(index)!);
            var up = Read(entries.GetValue(index + 1)!);
            check(down.Tag == 0x4C424447 && up.Tag == down.Tag, "Text input must use the router's injection tag on both events.");
            if (index < 4)
            {
                check(down.Vk == 8 && up.Vk == 8 && down.Scan == 0 && up.Scan == 0,
                    "A text correction must erase with VK_BACK, without a Unicode or scan-code flag.");
                check(down.Flags == 0 && up.Flags == 2, "Backspace must have one key down and one key up.");
            }
            else
            {
                char expected = text[(index - 4) / 2];
                check(down.Vk == 0 && up.Vk == 0 && down.Scan == expected && up.Scan == expected,
                    "Unicode text must preserve UTF-16 units, including surrogate pairs, in wScan with wVk=0.");
                check(down.Flags == 4 && up.Flags == 6, "Unicode pairs must use KEYEVENTF_UNICODE and KEYEVENTF_KEYUP.");
            }
        }

        var normalized = Build(new Packet { Type = "text", Text = "a\u0301 e\u0302\u0301" });
        string expectedNormalized = "á ế";
        check(normalized.Length == expectedNormalized.Length * 2, "Unicode builder must normalize decomposed Vietnamese text to NFC.");
        for (int index = 0; index < expectedNormalized.Length; index++)
            check(Read(normalized.GetValue(index * 2)!).Scan == expectedNormalized[index], "NFC conversion changed the Vietnamese text.");
        check(Build(new Packet { Type = "text", Text = "" }).Length == 0, "An empty edit must create no native input.");
        check(Build(new Packet { Type = "text", Dx = 128, Text = new string('a', 256) }).Length == 768,
            "Maximum permitted Unicode edit should be accepted exactly.");

        foreach (var invalid in new[]
        {
            new Packet { Type = "text", Dx = -1, Text = "x" },
            new Packet { Type = "text", Dx = 129, Text = "x" },
            new Packet { Type = "text", Text = null },
            new Packet { Type = "text", Text = new string('a', 257) },
            new Packet { Type = "text", Text = "\ud800" },
            new Packet { Type = "text", Text = "\udfff" },
            new Packet { Type = "text", Text = "a\0b" },
            new Packet { Type = "text", Text = "\u001b" },
            new Packet { Type = "text", Text = "\u007f" },
        })
        {
            bool rejected = false;
            try { Build(invalid); }
            catch (TargetInvocationException error) when (error.InnerException is InvalidDataException) { rejected = true; }
            check(rejected, "Invalid or oversized Unicode edits must be rejected before native input construction.");
        }

        // A key can start in text mode and then become part of a shortcut while held.
        // Its eventual release must still balance the physical key sent to B.
        using (var router = new InputRouter())
        using (var delivered = new ManualResetEventSlim())
        {
            var packets = new ConcurrentQueue<Packet>();
            router.Send = packet => { packets.Enqueue(packet); if (packets.Count == 2) delivered.Set(); };
            const BindingFlags privateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
            Type strokeType = typeof(InputRouter).GetNestedType("KeyStroke", BindingFlags.NonPublic)!;
            object stroke = Activator.CreateInstance(strokeType, [0x41, 0x1E, false])!;
            typeof(InputRouter).GetMethod("SendKey", privateInstance)!.Invoke(router, [stroke, true]);
            var textKeys = (HashSet<int>)typeof(InputRouter).GetField("_textKeys", privateInstance)!.GetValue(router)!;
            textKeys.Add(0x41);
            typeof(InputRouter).GetMethod("RouteControllerKey", privateInstance)!.Invoke(router, [stroke, false]);
            check(delivered.Wait(TimeSpan.FromSeconds(3)), "Text-to-shortcut transition swallowed the remote key release.");
            Packet[] result = packets.ToArray();
            check(result.Length == 2 && result[0].Down && !result[1].Down && result.All(p => p.Type == "key" && p.Key == 0x41),
                "A held key transitioning from text to a shortcut must have balanced down/up packets.");
            check(textKeys.Count == 0, "A released text key remained in the composition-key set.");
        }
    }
}
