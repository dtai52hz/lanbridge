namespace LanBridge;

public sealed partial class MainForm
{
    SwitchShortcut? _configuredSwitchShortcut;
    bool _configuringSwitchShortcut;

    void ApplySwitchShortcut(SwitchShortcut? shortcut)
    {
        if (shortcut is { IsValid: false }) throw new ArgumentException("Phím chuyển không hợp lệ hoặc đang được Windows/LanBridge dành riêng.", nameof(shortcut));
        _configuredSwitchShortcut = shortcut;
        _input.SwitchShortcut = shortcut;
        RefreshSwitchShortcutControls();
        QueuePreferenceSave();
    }

    void RefreshSwitchShortcutControls()
    {
        _switchShortcutButton.Enabled = !IsHost && !_configuringSwitchShortcut;
        _switchShortcutButton.Text = _configuringSwitchShortcut ? "Đang chuẩn bị gán phím…"
            : _configuredSwitchShortcut is { } shortcut ? "Phím chuyển: " + shortcut.DisplayName : "Gán phím chuyển A ⇄ B…";
        _switchShortcutButton.AccessibleDescription = _configuredSwitchShortcut is { } configured
            ? $"Sửa phím {configured.DisplayName}. Nhấn để chuyển sang B, nhấn lại để về A." : "Gán một phím hoặc tổ hợp phím để chuyển điều khiển giữa A và B.";
        _switchShortcutHint.Text = IsHost ? "Gán phím trên máy A. Ctrl + Alt + F12 luôn là phím thoát khẩn cấp."
            : "Nhấn để sang B, nhấn lại để về A. Có hiệu lực khi đã kết nối và B cấp quyền.";
    }

    async void ConfigureSwitchShortcut()
    {
        if (_closing || IsDisposed || IsHost || _configuringSwitchShortcut) return;
        _configuringSwitchShortcut = true;
        RefreshSwitchShortcutControls();
        try
        {
            // Return to local input and suspend the old binding before displaying the
            // recorder, so even reassigning the current shortcut cannot switch away.
            await _input.SuspendSwitchShortcutAsync();
            if (_closing || IsDisposed || IsHost) return;
            using var dialog = new SwitchShortcutDialog(_configuredSwitchShortcut);
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                ApplySwitchShortcut(dialog.SelectedShortcut);
                Log(_configuredSwitchShortcut is { } selected
                    ? $"Phím chuyển A ⇄ B: {selected.DisplayName}. Nhấn một lần để chuyển, nhấn lại để trở về."
                    : "Đã bỏ gán phím chuyển A ⇄ B.");
            }
        }
        catch (OperationCanceledException) when (_closing || IsDisposed)
        {
            // Closing the form can cancel a queued callback on the input owner.
        }
        catch (Exception error) when (error is InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            if (!_closing && !IsDisposed) Log("Chưa gán được phím chuyển: " + error.Message);
        }
        finally
        {
            _configuringSwitchShortcut = false;
            if (!_closing && !IsDisposed)
            {
                _input.SwitchShortcut = _configuredSwitchShortcut;
                RefreshSwitchShortcutControls();
            }
        }
    }
}
