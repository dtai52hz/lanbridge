using System.Reflection;
using LanBridge;

internal static class HiddenShortcutPreview
{
    internal static void Run(Action<bool, string> check)
    {
        string? requestedDirectory = Environment.GetEnvironmentVariable("LANBRIDGE_UI_PREVIEW_DIR");
        if (string.IsNullOrWhiteSpace(requestedDirectory)) return;
        var project = new DirectoryInfo(AppContext.BaseDirectory);
        while (project != null && !File.Exists(Path.Combine(project.FullName, "src", "LanBridge.csproj")))
            project = project.Parent;
        if (project is null) throw new InvalidOperationException("Cannot find the project for hidden UI previews.");
        string toolsDirectory = Path.GetFullPath(Path.Combine(project.FullName, ".tools")) + Path.DirectorySeparatorChar;
        string outputDirectory = Path.GetFullPath(requestedDirectory);
        if (!outputDirectory.StartsWith(toolsDirectory, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Hidden UI previews must be written inside this project's .tools directory.");
        Directory.CreateDirectory(outputDirectory);

        using (var form = new MainForm(preview: true))
        {
            try
            {
                Get<System.Windows.Forms.Timer>(form, "_clipboardTimer").Stop();
                typeof(MainForm).GetMethod("ApplySwitchShortcut", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(form, new object[] { new SwitchShortcut((int)Keys.F9) });
                PrepareControls(form);
                var tabs = Get<TabControl>(form, "_tabs");
                tabs.SelectedTab = Get<TabPage>(form, "_sessionTab");
                Capture(form, Path.Combine(outputDirectory, "session-shortcut-default.png"));
                form.Size = form.MinimumSize;
                Capture(form, Path.Combine(outputDirectory, "session-shortcut-minimum.png"));
                var scrollArea = (ScrollableControl)Get<TabPage>(form, "_sessionTab").Controls[0];
                var chat = Get<TextBox>(form, "_chat");
                check(scrollArea.VerticalScroll.Visible, "Minimum session size must expose a vertical scrollbar to reach chat.");
                scrollArea.ScrollControlIntoView(chat);
                Capture(form, Path.Combine(outputDirectory, "session-shortcut-minimum-scrolled.png"));
                var chatBounds = scrollArea.RectangleToClient(chat.RectangleToScreen(chat.ClientRectangle));
                check(scrollArea.AutoScrollPosition.Y < 0 && scrollArea.ClientRectangle.Contains(chatBounds),
                    "The chat composer must be fully reachable by scrolling at minimum window size.");
                check(!form.Visible && !Get<bool>(Get<InputRouter>(form, "_input"), "_started"),
                    "Hidden session previews must not show a window or start input hooks.");
            }
            finally { form.Close(); }
        }
        using (var dialog = new SwitchShortcutDialog(new SwitchShortcut((int)Keys.F9)))
        {
            Capture(dialog, Path.Combine(outputDirectory, "switch-shortcut-dialog.png"));
            check(!dialog.Visible, "Hidden shortcut previews must not show or activate a dialog.");
        }
        Console.WriteLine("Saved four hidden shortcut UI previews under the project's .tools directory.");
    }

    static void Capture(Form form, string path)
    {
        _ = form.Handle;
        PrepareControls(form);
        form.PerformLayout();
        PrepareControls(form);
        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    static void PrepareControls(Control parent)
    {
        _ = parent.Handle;
        parent.PerformLayout();
        foreach (Control child in parent.Controls) PrepareControls(child);
    }

    static T Get<T>(object instance, string field) => (T)instance.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;
}
