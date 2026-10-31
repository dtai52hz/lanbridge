using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using LanBridge;

internal static class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        using var context = new ApplicationContext();
        bool running = false;
        int result = 1;
        Application.Idle += async (_, _) =>
        {
            if (running) return;
            running = true;
            try
            {
                await RunAsync();
                Console.WriteLine($"Desktop smoke passed: {_assertions} assertions; hidden preview forms, isolated preferences, no tray icons, hooks, physical input or OS clipboard.");
                result = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { context.ExitThread(); }
        };
        Application.Run(context);
        return result;
    }

    private static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "LanBridge-DesktopSmoke-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await CheckPreviewIsolationAsync();
            await CheckRestoredPreferencesAsync(Path.Combine(root, "restore", "preferences.json"));
            await CheckLatestPreferencesAsync(Path.Combine(root, "latest", "preferences.json"));
            CheckTrayLifecycle();
            CheckCloseCleanup();
        }
        finally
        {
            string absolute = Path.GetFullPath(root);
            if (!absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(absolute).StartsWith("LanBridge-DesktopSmoke-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing desktop test cleanup outside its temporary directory.");
            Directory.Delete(absolute, recursive: true);
        }
    }

    private static async Task CheckPreviewIsolationAsync()
    {
        using var form = CreateForm();
        try
        {
            Check(Get<UserPreferencesStore?>(form, "_preferencesStore") is null,
                "A default preview must not load or save the user's real preferences.");
            Check(Get<NotifyIcon?>(form, "_trayIcon") is null && Get<ContextMenuStrip?>(form, "_trayMenu") is null,
                "A preview must not register a real notification-area icon or menu.");
            var input = Get<InputRouter>(form, "_input");
            Check(input.MouseSpeedPercent == 100 && input.TypingMode == TypingMode.VietnameseTelex
                && Get<ComboBox>(form, "_edge").SelectedIndex == (int)ScreenEdge.Right,
                "A default preview inherited preferences from outside its test.");
            Get<NumericUpDown>(form, "_pointerSpeed").Value = 175;
            Get<CheckBox>(form, "_minimizeToTray").Checked = true;
            await (Task)Call(form, "FlushPreferencesAsync")!;
            Check(Get<UserPreferencesStore?>(form, "_preferencesStore") is null && input.MouseSpeedPercent == 175,
                "Preview convenience controls should update their router without enabling disk persistence.");
            AssertInert(form);
        }
        finally { form.Close(); }
    }

    private static async Task CheckRestoredPreferencesAsync(string path)
    {
        var expected = new UserPreferences
        {
            Edge = ScreenEdge.Top, TypingMode = TypingMode.VietnameseVni,
            MouseSpeedPercent = 175, MinimizeToTray = true
        };
        Check(new UserPreferencesStore(path).TrySave(expected), "Could not prepare isolated startup preferences.");
        using var form = CreateForm(path);
        try
        {
            var input = Get<InputRouter>(form, "_input");
            Check(Get<ComboBox>(form, "_edge").SelectedIndex == (int)expected.Edge
                && Get<ScreenLayoutPicker>(form, "_screenLayout").SelectedEdge == expected.Edge
                && Get<ComboBox>(form, "_typing").SelectedIndex == (int)expected.TypingMode
                && Get<NumericUpDown>(form, "_pointerSpeed").Value == expected.MouseSpeedPercent
                && Get<CheckBox>(form, "_minimizeToTray").Checked,
                "Startup did not restore the position picker, typing, pointer speed and tray preference together.");
            Call(form, "ApplyPermissions");
            Check(input.ExitEdge == expected.Edge && input.TypingMode == expected.TypingMode
                && input.MouseSpeedPercent == expected.MouseSpeedPercent && !input.CanControl,
                "Restored convenience preferences did not reach the disabled input router.");

            Get<ComboBox>(form, "_role").SelectedIndex = 1;
            Get<TextBox>(form, "_pairing").Text = "LB1|private-secret-must-not-persist";
            foreach (var permission in Get<Dictionary<string, CheckBox>>(form, "_checks").Values) permission.Checked = true;
            await (Task)Call(form, "FlushPreferencesAsync")!;
            using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
            var allowed = new HashSet<string> { "schemaVersion", "edge", "typingMode", "mouseSpeedPercent", "minimizeToTray" };
            Check(document.RootElement.EnumerateObject().All(property => allowed.Remove(property.Name)) && allowed.Count == 0,
                "Form preferences persisted connection, permission, role or other state outside the convenience allowlist.");
            Check(!File.ReadAllText(path).Contains("private-secret", StringComparison.Ordinal)
                && (UserPreferences)Call(form, "CapturePreferences")! == expected,
                "Capturing convenience preferences included a pairing secret or unrelated session state.");
            AssertInert(form);
        }
        finally { form.Close(); }

        using var reopened = CreateForm(path);
        try
        {
            var permissions = Get<Permissions>(reopened, "_permissions");
            Check(Get<ComboBox>(reopened, "_role").SelectedIndex == 0 && Get<TextBox>(reopened, "_pairing").TextLength == 0
                && !permissions.Audio && !permissions.Restart && !permissions.Recording && !permissions.BlockInput && !permissions.Privacy,
                "Reopening convenience preferences restored a role, pairing credential or elevated permission.");
            AssertInert(reopened);
        }
        finally { reopened.Close(); }
    }

    private static async Task CheckLatestPreferencesAsync(string path)
    {
        using var form = CreateForm(path);
        try
        {
            var writes = new List<Task>();
            for (int index = 0; index < 80; index++)
            {
                Get<ComboBox>(form, "_edge").SelectedIndex = index % 4;
                Get<ComboBox>(form, "_typing").SelectedIndex = index % 3;
                Get<NumericUpDown>(form, "_pointerSpeed").Value = 50 + index % 151;
                Get<CheckBox>(form, "_minimizeToTray").Checked = index % 2 == 0;
                writes.Add((Task)Call(form, "FlushPreferencesAsync")!);
                if (index % 4 == 0) await Task.Delay(1);
            }
            var final = new UserPreferences { Edge = ScreenEdge.Left, TypingMode = TypingMode.Physical, MouseSpeedPercent = 165, MinimizeToTray = true };
            Get<ComboBox>(form, "_edge").SelectedIndex = (int)final.Edge;
            Get<ComboBox>(form, "_typing").SelectedIndex = (int)final.TypingMode;
            Get<NumericUpDown>(form, "_pointerSpeed").Value = final.MouseSpeedPercent;
            Get<CheckBox>(form, "_minimizeToTray").Checked = final.MinimizeToTray;
            writes.Add((Task)Call(form, "FlushPreferencesAsync")!);
            await Task.WhenAll(writes).WaitAsync(TimeSpan.FromSeconds(8));
            Check(new UserPreferencesStore(path).Load() == final,
                "Serialized asynchronous writes lost the latest complete UI preference snapshot.");
            Check(Get<InputRouter>(form, "_input").MouseSpeedPercent == final.MouseSpeedPercent,
                "Saving preferences changed the active input router's pointer speed.");
            AssertInert(form);
        }
        finally { form.Close(); }
    }

    private static void CheckTrayLifecycle()
    {
        using var form = CreateForm();
        try
        {
            var clipboardTimer = Get<System.Windows.Forms.Timer>(form, "_clipboardTimer");
            bool clipboardRunning = clipboardTimer.Enabled;
            form.WindowState = FormWindowState.Minimized;
            Call(form, "HandleWindowMinimized");
            Check(!Get<bool>(form, "_hiddenInTray"), "Default minimization must preserve existing taskbar behavior.");
            form.WindowState = FormWindowState.Normal;
            Get<CheckBox>(form, "_minimizeToTray").Checked = true;
            form.WindowState = FormWindowState.Minimized;
            Call(form, "HandleWindowMinimized");
            Check(Get<bool>(form, "_hiddenInTray") && !form.ShowInTaskbar && !form.Visible
                && clipboardTimer.Enabled == clipboardRunning && !Get<bool>(form, "_closing"),
                "Opt-in tray minimization must hide the window while retaining its clipboard timer and lifetime.");
            Call(form, "RestoreFromTray");
            Check(!Get<bool>(form, "_hiddenInTray") && form.ShowInTaskbar && !form.Visible,
                "The preview restore path must clear tray state without showing or activating a real window.");
            form.WindowState = FormWindowState.Normal;
            Get<CheckBox>(form, "_minimizeToTray").Checked = false;
            Call(form, "HideToTray");
            Check(Get<bool>(form, "_hiddenInTray"), "The explicit hide action must work without enabling minimize-to-tray.");
            Call(form, "PrepareForLocalPrompt");
            Check(!Get<bool>(form, "_hiddenInTray") && form.ShowInTaskbar && !form.Visible,
                "Preparing B's local consent prompt must restore tray state without bypassing the prompt.");
            Call(form, "RefreshDesktopStatus");
            Check(Get<Label>(form, "_connectionSummary").Text.Contains("Sẵn sàng", StringComparison.Ordinal),
                "Disconnected convenience status did not describe the ready state.");
            AssertInert(form);
        }
        finally { form.Close(); }
    }

    private static void CheckCloseCleanup()
    {
        using var form = CreateForm();
        var input = Get<InputRouter>(form, "_input");
        Get<CheckBox>(form, "_minimizeToTray").Checked = true;
        Call(form, "HideToTray");
        form.Close();
        Check(form.IsDisposed && Get<bool>(form, "_closing") && Get<bool>(input, "_disposed")
            && !Get<bool>(form, "_desktopInitialized") && Get<NotifyIcon?>(form, "_trayIcon") is null,
            "Close must still exit and clean up input/desktop resources even when minimize-to-tray is enabled.");
        Check(!input.CanControl && !input.IsRemote && input.Send is null,
            "Closing a hidden window left remote input authority or a sender attached.");
    }

    private static MainForm CreateForm(string? path = null)
    {
        var form = new MainForm(preview: true, clipboard: new MemoryClipboard(), preferencesPath: path);
        _ = form.Handle;
        return form;
    }

    private static void AssertInert(MainForm form)
    {
        var input = Get<InputRouter>(form, "_input");
        Check(!form.Visible && Get<NotifyIcon?>(form, "_trayIcon") is null
            && Get<PeerConnection?>(form, "_peer") is null && Get<HostServer?>(form, "_autoServer") is null
            && Get<LanDiscoveryService?>(form, "_discovery") is null && !Get<bool>(form, "_servicesStarted"),
            "A desktop convenience test opened a window, tray icon, session or network listener.");
        Check(!Get<bool>(input, "_started") && Get<IntPtr>(input, "_mouseHook") == IntPtr.Zero
            && Get<IntPtr>(input, "_keyboardHook") == IntPtr.Zero && !input.CanControl,
            "A desktop convenience test enabled native input or installed global hooks.");
    }

    private static void Check(bool result, string message) { _assertions++; if (!result) throw new InvalidOperationException(message); }
    private static T Get<T>(object instance, string name) => (T)instance.GetType().GetField(name, Private)!.GetValue(instance)!;
    private static object? Call(object instance, string name, params object?[] args)
    {
        try { return instance.GetType().GetMethod(name, Private)!.Invoke(instance, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    private sealed class MemoryClipboard : IClipboardAccess
    {
        public uint SequenceNumber => 0;
        public ClipboardContent Read() => new();
        public void SetText(string text) => throw new InvalidOperationException("Desktop-only tests must not publish clipboard data.");
        public void SetFiles(string[] paths) => throw new InvalidOperationException("Desktop-only tests must not publish clipboard data.");
        public void Clear() => throw new InvalidOperationException("Desktop-only tests must not change the clipboard.");
    }
}
