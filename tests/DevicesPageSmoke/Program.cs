using System.Net;
using System.Reflection;
using LanBridge;

internal static class Program
{
    private static int _assertions;

    [STAThread]
    private static int Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try
        {
            Run();
            RunMainFormContract();
            Console.WriteLine($"Devices page smoke passed: {_assertions} assertions; hidden controls only, no networking or input hooks.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Run()
    {
        using var window = new Form { ClientSize = new Size(1130, 780) };
        using var page = new DevicesPage { Dock = DockStyle.Fill };
        window.Controls.Add(page);
        _ = window.Handle;
        _ = page.Handle;
        page.PerformLayout();
        Check(!window.Visible && !page.Visible, "UI smoke must never show a desktop window.");

        int refreshes = 0;
        var connections = new List<DiscoveredDevice>();
        page.RefreshRequested += () => refreshes++;
        page.ConnectRequested += connections.Add;
        page.SetLocalState("Máy A tiếng Việt", "192.168.1.5", receiving: true);
        page.SetDevices(Array.Empty<DiscoveredDevice>());
        Check(Buttons(page).Count(button => button.Text == "Kết nối") == 0, "An empty device list must not offer a stale connection.");
        Check(AllText(page).Contains("Máy A tiếng Việt", StringComparison.Ordinal), "Local machine's Unicode name is missing.");
        Check(AllText(page).Contains("192.168.1.5", StringComparison.Ordinal), "Local LAN address is missing.");
        Click(Buttons(page).Single(button => button.Text == "Làm mới"));
        Check(refreshes == 1 && connections.Count == 0, "Refresh must request discovery without connecting.");

        var available = new DiscoveredDevice("device-b", "Máy B – làm việc", IPAddress.Parse("192.168.1.8"),
            45831, new string('A', 64), Busy: false, DateTimeOffset.UtcNow);
        var busy = new DiscoveredDevice("device-c", "Máy C", IPAddress.Parse("192.168.1.9"),
            45831, new string('B', 64), Busy: true, DateTimeOffset.UtcNow);
        page.SetDevices(new[] { available, busy });
        var connect = Buttons(page).Single(button => button.Text == "Kết nối");
        var busyButton = Buttons(page).Single(button => button.Text == "Đang bận");
        Check(connect.Enabled && !busyButton.Enabled, "Only an available device may offer a connection.");
        Check(AllText(page).Contains(available.Name, StringComparison.Ordinal)
            && AllText(page).Contains(available.Address.ToString(), StringComparison.Ordinal), "Available card lost its Unicode name or address.");
        Click(connect);
        Check(connections.Count == 1 && connections[0] == available, "Card click must select the advertised device, including its certificate pin.");
        Check(refreshes == 1, "Connecting must not raise a refresh event.");
        var changed = available with { Name = "Máy B đổi IP", Address = IPAddress.Parse("192.168.1.18"), Fingerprint = new string('C', 64) };
        page.SetDevices(new[] { changed, busy });
        Check(ReferenceEquals(connect, Buttons(page).Single(button => button.Text == "Kết nối")), "A metadata update should preserve the existing card and keyboard focus target.");
        Click(connect);
        Check(connections.Count == 2 && connections[1] == changed, "An updated card must connect using its latest address and certificate pin.");
        page.SetDevices(new[] { changed with { LastSeen = DateTimeOffset.UtcNow.AddSeconds(1) }, busy });
        Check(ReferenceEquals(connect, Buttons(page).Single(button => button.Text == "Kết nối")), "A discovery heartbeat must not replace the focused connection button.");

        page.Connecting = true;
        Check(page.Connecting, "Connecting state must be readable by its owner.");
        Check(!Buttons(page).Any(button => button.Text == "Kết nối" && button.Enabled), "Connection buttons must be disabled while a request is pending.");
        page.Connecting = false;
        Check(Buttons(page).Any(button => button.Text == "Kết nối" && button.Enabled), "Available device must be selectable again after a failed or cancelled request.");

        page.SetSessionState(connected: true, peerName: available.Name);
        Check(!Buttons(page).Any(button => button.Text == "Kết nối" && button.Enabled), "A second device must not be selectable while a session is active.");
        page.SetSessionState(connected: false);
        Check(Buttons(page).Any(button => button.Text == "Kết nối" && button.Enabled), "Disconnect must make available devices selectable again.");

        page.SetStatus("Không thể tìm thiết bị thử nghiệm", error: true);
        Check(AllText(page).Contains("Không thể tìm thiết bị thử nghiệm", StringComparison.Ordinal), "Discovery failure must be visible on the page.");
        page.SetStatus("Đang tìm thiết bị trong mạng LAN");
        Check(AllText(page).Contains("Đang tìm thiết bị trong mạng LAN", StringComparison.Ordinal), "Recovery status did not replace the error.");
        page.SetDevices(new[] { available with { Busy = true } });
        Check(!Buttons(page).Any(button => button.Text == "Kết nối" && button.Enabled), "Busy refresh must revoke an earlier available card's connect action.");
        Check(!AllText(page).Contains("192.168.1.9", StringComparison.Ordinal), "An expired device card survived list replacement.");
        page.SetDevices(Array.Empty<DiscoveredDevice>());
        Check(Buttons(page).All(button => button.Text is not ("Kết nối" or "Đang bận")), "Clearing discovered devices must remove all old card actions.");
        Check(connections.Count == 2, "Rendering or state changes must never initiate a connection.");
        Check(!window.Visible, "A test operation unexpectedly showed its window.");
    }

    private static void RunMainFormContract()
    {
        using var form = new MainForm(preview: true);
        try
        {
            _ = form.Handle;
            Get<System.Windows.Forms.Timer>(form, "_clipboardTimer").Stop();
            var tabs = Get<TabControl>(form, "_tabs");
            _ = tabs.Handle;
            var devices = Get<DevicesPage>(form, "_devicesPage");
            var sessionTab = Get<TabPage>(form, "_sessionTab");
            var manualTab = Get<TabPage>(form, "_manualTab");
            var input = Get<InputRouter>(form, "_input");
            Check(tabs.SelectedTab == Get<TabPage>(form, "_deviceTab") && tabs.TabPages.Count == 3,
                "MainForm must open on devices while retaining session and code connection pages.");
            Check(Descendants(sessionTab).Contains(Get<ComboBox>(form, "_edge"))
                && Descendants(sessionTab).Contains(Get<ComboBox>(form, "_typing")),
                "Position and Vietnamese typing settings must be in the session page.");
            Check(Descendants(manualTab).Contains(Get<TextBox>(form, "_pairing")), "Manual code pairing fallback is missing.");
            Get<ComboBox>(form, "_typing").SelectedIndex = 1;
            Check(input.TypingMode == TypingMode.VietnameseVni, "VNI selection did not reach the input router.");
            Get<ComboBox>(form, "_typing").SelectedIndex = 2;
            Check(input.TypingMode == TypingMode.Physical, "Physical typing selection did not reach the input router.");
            Get<ComboBox>(form, "_typing").SelectedIndex = 0;
            Check(input.TypingMode == TypingMode.VietnameseTelex && input.EdgeDwellMilliseconds == 0,
                "Device picker integration must preserve Telex and immediate edge transfer.");
            for (int index = 0; index < 4; index++)
            {
                Get<ComboBox>(form, "_edge").SelectedIndex = index;
                Get<ComboBox>(form, "_role").SelectedIndex = 1;
                Check(!input.IsController && Get<TextBox>(form, "_pairing").ReadOnly,
                    "Manual receiver role did not update input direction and code display.");
                Get<ComboBox>(form, "_role").SelectedIndex = 0;
                Check(input.IsController && input.ExitEdge == (ScreenEdge)index && !input.CanControl,
                    "The selected physical edge did not map to its controller enum without enabling control.");
            }
            Get<ComboBox>(form, "_role").SelectedIndex = 1;
            var permissionChecks = Get<Dictionary<string, CheckBox>>(form, "_checks");
            permissionChecks["Input"].Checked = false;
            permissionChecks["Files"].Checked = false;
            permissionChecks["Clipboard"].Checked = true;
            permissionChecks["Privacy"].Checked = true;
            permissionChecks["Audio"].Checked = false;
            permissionChecks["Recording"].Checked = false;
            permissionChecks["Restart"].Checked = false;
            Get<ComboBox>(form, "_role").SelectedIndex = 0;
            var foreignGrants = new Permissions { Input = true, Files = true, Clipboard = false, Privacy = false,
                Audio = true, Recording = true, Restart = true, BlockInput = true };
            typeof(MainForm).GetField("_permissions", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, foreignGrants);
            typeof(MainForm).GetMethod("ApplyPermissions", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Get<ComboBox>(form, "_role").SelectedIndex = 1;
            var localGrants = Get<Permissions>(form, "_permissions");
            Check(!localGrants.Input && !localGrants.Files && localGrants.Clipboard && localGrants.Privacy,
                "Switching back to receive control must preserve this PC's chosen local grants.");
            Check(!localGrants.Audio && !localGrants.Recording && !localGrants.Restart && !localGrants.BlockInput,
                "A previous remote PC's elevated grants must never become this PC's incoming control permissions.");
            Get<ComboBox>(form, "_role").SelectedIndex = 0;
            var sample = new DiscoveredDevice("preview-b", "Máy B mẫu", IPAddress.Loopback, 45831,
                new string('D', 64), false, DateTimeOffset.UtcNow);
            devices.SetDevices(new[] { sample });
            Click(Buttons(devices).Single(button => button.Text == "Kết nối"));
            Click(Buttons(devices).Single(button => button.Text == "Làm mới"));
            typeof(MainForm).GetMethod("StartNetworkServices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            Check(GetNullable<HostServer>(form, "_autoServer") is null
                && GetNullable<HostServer>(form, "_server") is null
                && GetNullable<LanDiscoveryService>(form, "_discovery") is null
                && GetNullable<PeerConnection>(form, "_peer") is null
                && !Get<bool>(form, "_servicesStarted"), "Preview actions must not create TCP/UDP listeners or connect to a device.");
            Check(!Get<bool>(input, "_started") && Get<IntPtr>(input, "_mouseHook") == IntPtr.Zero
                && Get<IntPtr>(input, "_keyboardHook") == IntPtr.Zero && !form.Visible,
                "Preview UI contract must leave the desktop and OS input hooks untouched.");
        }
        finally { form.Close(); }

        using var approval = new ConnectionApprovalDialog(new ConnectionRequest("Máy yêu cầu", IPAddress.Parse("192.168.1.8")));
        Check(approval.AcceptButton is Button accept && accept.DialogResult == DialogResult.No
            && approval.CancelButton is Button cancel && cancel.DialogResult == DialogResult.No,
            "Consent dialog Enter and Escape must default to refusing control.");
        Check(AllText(approval).Contains("Máy yêu cầu", StringComparison.Ordinal)
            && AllText(approval).Contains("192.168.1.8", StringComparison.Ordinal),
            "Consent dialog must show requesting machine name and actual peer IP.");
        Check(!approval.Visible, "Consent contract test must not display a prompt.");
        approval.Close();
    }

    private static T Get<T>(object instance, string field) => (T)(instance.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!);
    private static T? GetNullable<T>(object instance, string field) where T : class => (T?)instance.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance);

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    private static IEnumerable<Button> Buttons(Control parent) => Descendants(parent).OfType<Button>();
    private static string AllText(Control parent) => string.Join("\n", Descendants(parent).Select(control => control.Text));
    private static void Click(Button button)
    {
        Check(button.Enabled, "The test must not synthesize clicks on disabled controls.");
        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, new object[] { EventArgs.Empty });
    }
    private static void Check(bool condition, string message)
    {
        _assertions++;
        if (!condition) throw new InvalidOperationException(message);
    }
}
