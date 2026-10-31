namespace LanBridge;

/// <summary>Records a shortcut only inside this dialog. It never installs an input hook.</summary>
public sealed class SwitchShortcutDialog : Form
{
    static readonly Color BackgroundColor = Color.FromArgb(16, 20, 29);
    static readonly Color CardColor = Color.FromArgb(25, 33, 46);
    static readonly Color AccentColor = Color.FromArgb(47, 208, 177);
    static readonly Color MutedColor = Color.FromArgb(151, 166, 187);
    readonly Label _value = new();
    readonly Label _hint = new();
    readonly DarkButton _record = new();
    readonly DarkButton _save = new();
    readonly DarkButton _cancel = new();
    readonly HashSet<int> _releaseKeys = new();
    readonly HashSet<int> _windowsKeys = new();
    bool _recording;

    public SwitchShortcut? SelectedShortcut { get; private set; }

    public SwitchShortcutDialog(SwitchShortcut? current)
    {
        SelectedShortcut = current is { IsValid: true } ? current : null;
        Text = "LanBridge · Phím chuyển A ⇄ B";
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        ClientSize = new Size(590, 370);
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; KeyPreview = true;
        BackColor = BackgroundColor; ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6,
            Padding = new Padding(24, 20, 24, 20), Margin = Padding.Empty,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (int height in new[] { 34, 43, 64, 44, 101, 40 })
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        Controls.Add(layout);
        layout.Controls.Add(Label("Chuyển máy bằng một phím", 18, Color.White, true), 0, 0);
        layout.Controls.Add(Label("Gán phím đơn hoặc tổ hợp để chuyển chuột và bàn phím\nqua B, rồi bấm lại để trở về A.", 9.5f, MutedColor), 0, 1);
        var valueCard = new UiCard
        {
            Dock = DockStyle.Fill, BackColor = CardColor, Padding = new Padding(14, 10, 14, 10),
            Margin = new Padding(0, 0, 0, 9),
        };
        _value.Dock = DockStyle.Fill; _value.ForeColor = Color.White; _value.Font = new Font("Segoe UI", 16, FontStyle.Bold);
        _value.TextAlign = ContentAlignment.MiddleCenter; _value.AutoEllipsis = true; _value.UseMnemonic = false;
        valueCard.Controls.Add(_value); layout.Controls.Add(valueCard, 0, 2);
        Style(_record, "Bấm để gán phím", CardColor, Color.White);
        _record.Dock = DockStyle.Fill; _record.Margin = new Padding(0, 0, 0, 6);
        _record.AccessibleName = "Ghi phím chuyển máy";
        _record.Click += (_, _) => SetRecording(!_recording);
        layout.Controls.Add(_record, 0, 3);
        _hint.Dock = DockStyle.Fill; _hint.ForeColor = MutedColor; _hint.Font = new Font("Segoe UI", 9);
        _hint.Margin = new Padding(0, 5, 0, 4); _hint.UseMnemonic = false;
        layout.Controls.Add(_hint, 0, 4);
        var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, Margin = Padding.Empty };
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 109));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 99));
        actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 107));
        var clear = new DarkButton(); Style(clear, "Bỏ gán", CardColor, Color.White);
        clear.Dock = DockStyle.Fill; clear.Margin = new Padding(0, 0, 0, 3);
        clear.Click += (_, _) => { SelectedShortcut = null; SetRecording(false); RefreshValue(); };
        actions.Controls.Add(clear, 0, 0);
        Style(_cancel, "Hủy", CardColor, Color.White); _cancel.DialogResult = DialogResult.Cancel;
        _cancel.Dock = DockStyle.Fill; _cancel.Margin = new Padding(0, 0, 8, 3); actions.Controls.Add(_cancel, 2, 0);
        Style(_save, "Lưu", AccentColor, BackgroundColor); _save.DialogResult = DialogResult.OK;
        _save.Dock = DockStyle.Fill; _save.Margin = new Padding(0, 0, 0, 3); actions.Controls.Add(_save, 3, 0);
        layout.Controls.Add(actions, 0, 5);
        AcceptButton = _save; CancelButton = _cancel;
        Deactivate += (_, _) => { _windowsKeys.Clear(); _releaseKeys.Clear(); if (_recording) SetRecording(false); };
        RefreshValue(); RefreshHint();
    }

    void SetRecording(bool recording)
    {
        _recording = recording;
        _record.Text = recording ? "Đang chờ phím… · Bấm lại để dừng" : "Bấm để gán phím";
        _record.BackColor = recording ? Color.FromArgb(24, 66, 63) : CardColor;
        _record.ForeColor = recording ? AccentColor : Color.White;
        _save.Enabled = !recording; AcceptButton = recording ? null : _save; CancelButton = recording ? null : _cancel;
        RefreshHint();
    }

    void RefreshValue() => _value.Text = SelectedShortcut?.DisplayName ?? "Chưa gán phím";

    void RefreshHint()
    {
        _hint.ForeColor = MutedColor;
        _hint.Text = _recording
            ? "Nhấn phím muốn dùng; có thể giữ Ctrl, Alt, Shift hoặc Win trước.\nEsc cũng có thể được gán. Bấm Hủy để đóng mà không lưu.\nCác tổ hợp Windows dành riêng có thể không ghi được."
            : "Ctrl + Alt + F12 luôn trả về A.\nPhím đã gán được dành riêng trong phiên có quyền điều khiển.\n" + (SelectedShortcut?.Modifiers == ShortcutModifiers.None
                ? "Nếu chọn phím chữ đơn, phím đó sẽ không dùng để gõ trong phiên."
                : "Gợi ý: chọn F9 hoặc Ctrl + Alt + Space để tránh trùng phím gõ.");
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (HandleRecordedKey(keyData, keyDown: true)) return true;
        return base.ProcessCmdKey(ref message, keyData);
    }

    protected override bool ProcessKeyPreview(ref Message message)
    {
        const int KeyDown = 0x100, KeyUp = 0x101, SysKeyDown = 0x104, SysKeyUp = 0x105;
        if (message.Msg is KeyDown or SysKeyDown or KeyUp or SysKeyUp)
        {
            Keys keys = (Keys)message.WParam.ToInt32() | ModifierKeys;
            if (HandleRecordedKey(keys, message.Msg is KeyDown or SysKeyDown)) return true;
        }
        return base.ProcessKeyPreview(ref message);
    }

    bool HandleRecordedKey(Keys keyData, bool keyDown)
    {
        int key = (int)(keyData & Keys.KeyCode);
        if (key is 0x5B or 0x5C)
        {
            if (keyDown) _windowsKeys.Add(key); else _windowsKeys.Remove(key);
        }
        if (!keyDown) return _releaseKeys.Remove(key) || _recording;
        // A recorded Enter/Space must stay consumed through auto-repeat and key-up,
        // so it cannot immediately activate Save or start another recording.
        if (!_recording) return _releaseKeys.Contains(key);
        _releaseKeys.Add(key);
        var modifiers = ShortcutModifiers.None;
        if ((keyData & Keys.Control) != 0) modifiers |= ShortcutModifiers.Control;
        if ((keyData & Keys.Alt) != 0) modifiers |= ShortcutModifiers.Alt;
        if ((keyData & Keys.Shift) != 0) modifiers |= ShortcutModifiers.Shift;
        if (_windowsKeys.Count > 0) modifiers |= ShortcutModifiers.Windows;
        if (key is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5)
        {
            _hint.Text = "Giữ phím bổ trợ rồi nhấn thêm phím muốn dùng.\nBấm Hủy để đóng mà không lưu.";
            return true;
        }
        if (!SwitchShortcut.TryCreate(key, modifiers, out var shortcut))
        {
            _hint.ForeColor = Color.FromArgb(255, 179, 113);
            _hint.Text = "Tổ hợp này dành riêng cho thoát nhanh hoặc Windows.\nHãy nhấn một phím hoặc tổ hợp khác.";
            return true;
        }
        SelectedShortcut = shortcut; SetRecording(false); RefreshValue();
        return true;
    }

    static Label Label(string text, float size, Color color, bool bold = false) => new()
    {
        Text = text, Dock = DockStyle.Fill, ForeColor = color, UseMnemonic = false, Margin = Padding.Empty,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
    };

    static void Style(Button button, string text, Color background, Color foreground)
    {
        button.Text = text; button.BackColor = background; button.ForeColor = foreground;
        button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0;
        button.Font = new Font("Segoe UI", 9, FontStyle.Bold); button.Cursor = Cursors.Hand;
    }
}
