using System.Reflection;
using LanBridge;

internal static class SwitchShortcutDialogChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var current = new SwitchShortcut((int)Keys.F8, ShortcutModifiers.Control);
        using var dialog = new SwitchShortcutDialog(current);
        _ = dialog.Handle;
        check(dialog.SelectedShortcut == current && dialog.DialogResult == DialogResult.None && !dialog.Visible,
            "Opening shortcut settings must preserve the current selection without displaying a test window.");
        var save = Button(dialog, "Lưu");
        var cancel = Button(dialog, "Hủy");
        check(dialog.AcceptButton == save && dialog.CancelButton == cancel,
            "Shortcut settings must offer normal keyboard Save and Cancel outside recording.");

        Begin(dialog);
        check(!save.Enabled && dialog.AcceptButton is null && dialog.CancelButton is null,
            "Recording must reserve Enter and Escape rather than accepting or cancelling the dialog.");
        check(Press(dialog, Keys.ControlKey | Keys.Control) && dialog.SelectedShortcut == current && Recording(dialog),
            "A modifier by itself must keep recording without replacing the current shortcut.");
        check(Press(dialog, Keys.Control | Keys.Alt | Keys.F12) && dialog.SelectedShortcut == current && Recording(dialog),
            "The emergency shortcut must be rejected without losing the previous selection.");
        check(Text(dialog).Contains("dành riêng", StringComparison.Ordinal),
            "A reserved shortcut must explain why recording continues.");
        check(Press(dialog, Keys.Control | Keys.Alt | Keys.Delete) && Recording(dialog),
            "Windows secure attention keys must not become a switch shortcut.");
        Release(dialog, Keys.ControlKey); Release(dialog, Keys.F12); Release(dialog, Keys.Delete);
        check(Press(dialog, Keys.Control | Keys.Alt | Keys.Space), "A valid shortcut was not consumed while recording.");
        check(dialog.SelectedShortcut == new SwitchShortcut((int)Keys.Space, ShortcutModifiers.Control | ShortcutModifiers.Alt)
            && !Recording(dialog) && save.Enabled && dialog.CancelButton == cancel,
            "Recording must capture modifiers and return to an explicit save step.");
        check(Press(dialog, Keys.Control | Keys.Alt | Keys.Space) && dialog.DialogResult == DialogResult.None,
            "The captured key must stay consumed during auto-repeat after recording ends.");
        check(Release(dialog, Keys.Space), "The captured key-up must not leak to a focused dialog button.");

        foreach (var key in new[] { Keys.Tab, Keys.Escape, Keys.Enter, Keys.F9, Keys.A })
        {
            Begin(dialog);
            check(Press(dialog, key) && dialog.SelectedShortcut == new SwitchShortcut((int)key)
                && dialog.DialogResult == DialogResult.None && !Recording(dialog),
                $"Recording {key} must capture a bare key without navigating or closing the dialog.");
            check(Press(dialog, key) && dialog.DialogResult == DialogResult.None,
                $"Holding captured {key} must not activate Save or another control.");
            check(Release(dialog, key), $"Captured {key} release was not consumed.");
        }
        check(Text(dialog).Contains("không dùng để gõ", StringComparison.Ordinal),
            "A bare key selection must explain that the key is reserved while controlling.");
        Begin(dialog);
        check(Press(dialog, Keys.Alt | Keys.F4) && dialog.SelectedShortcut == new SwitchShortcut((int)Keys.F4, ShortcutModifiers.Alt)
            && dialog.DialogResult == DialogResult.None,
            "Alt+F4 must be recordable without closing shortcut settings.");
        Release(dialog, Keys.F4);

        Begin(dialog);
        check(Press(dialog, Keys.LWin) && Recording(dialog), "Win by itself must wait for an ordinary key.");
        check(Press(dialog, Keys.L) && Recording(dialog), "Win+L must be rejected while the Windows modifier is held.");
        Release(dialog, Keys.L);
        check(Press(dialog, Keys.F10)
            && dialog.SelectedShortcut == new SwitchShortcut((int)Keys.F10, ShortcutModifiers.Windows),
            "A locally observed Windows modifier must be included in the recorded chord.");
        Release(dialog, Keys.F10); Release(dialog, Keys.LWin);
        Begin(dialog);
        Press(dialog, Keys.F9); Release(dialog, Keys.F9);
        check(dialog.SelectedShortcut == new SwitchShortcut((int)Keys.F9),
            "Releasing Win must remove it from the following recording.");

        Click(Button(dialog, "Bỏ gán"));
        check(dialog.SelectedShortcut is null && !Recording(dialog) && save.Enabled,
            "Clearing the assignment must require explicit Save and stop an ongoing recording.");
        check(Text(dialog).Contains("Chưa gán phím", StringComparison.Ordinal),
            "Clearing the shortcut must update its visible selection.");
        check(!dialog.Visible, "Shortcut recording tests must leave the desktop untouched.");

        using var invalid = new SwitchShortcutDialog(new SwitchShortcut((int)Keys.F12, ShortcutModifiers.Control | ShortcutModifiers.Alt));
        check(invalid.SelectedShortcut is null, "An invalid saved shortcut must not be offered for activation by the dialog.");
        using var cancelled = new SwitchShortcutDialog(current);
        Begin(cancelled); Press(cancelled, Keys.F7); Release(cancelled, Keys.F7);
        Click(Button(cancelled, "Hủy"));
        check(cancelled.DialogResult == DialogResult.Cancel && current == new SwitchShortcut((int)Keys.F8, ShortcutModifiers.Control),
            "Cancelling a changed shortcut must not commit or mutate the caller's original record.");
    }

    static void Begin(SwitchShortcutDialog dialog) => Click(Button(dialog, "Bấm để gán phím"));
    static bool Recording(SwitchShortcutDialog dialog) => (bool)typeof(SwitchShortcutDialog)
        .GetField("_recording", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
    static bool Press(SwitchShortcutDialog dialog, Keys keys)
    {
        var message = Message.Create(dialog.Handle, (keys & Keys.Alt) != 0 ? 0x104 : 0x100,
            new IntPtr((int)(keys & Keys.KeyCode)), IntPtr.Zero);
        return (bool)typeof(SwitchShortcutDialog).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { message, keys })!;
    }
    static bool Release(SwitchShortcutDialog dialog, Keys key)
    {
        var message = Message.Create(dialog.Handle, 0x101, new IntPtr((int)(key & Keys.KeyCode)), IntPtr.Zero);
        return (bool)typeof(SwitchShortcutDialog).GetMethod("ProcessKeyPreview", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(dialog, new object[] { message })!;
    }
    static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }
    static Button Button(Control parent, string text) => Descendants(parent).OfType<Button>().Single(button => button.Text == text);
    static string Text(Control parent) => string.Join("\n", Descendants(parent).Select(control => control.Text));
    static void Click(Button button) => typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(button, new object[] { EventArgs.Empty });
}
