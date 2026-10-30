using System.Diagnostics;

namespace LanBridge;

public sealed class ConnectionApprovalDialog : Form
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    readonly Label _countdown = new();
    public ConnectionApprovalDialog(ConnectionRequest request)
    {
        Text = "LanBridge · Yêu cầu kết nối"; ClientSize = new Size(490, 285);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        BackColor = Color.FromArgb(25, 31, 43); ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        Controls.Add(new Label { Text = "Cho phép máy này điều khiển?", Font = new Font(Font.FontFamily, 17, FontStyle.Bold), Location = new Point(22, 18), Size = new Size(450, 40) });
        Controls.Add(new Label { Text = request.Name + "\nIP: " + request.Address, Location = new Point(24, 70), Size = new Size(440, 58), Font = new Font(Font, FontStyle.Bold), AutoEllipsis = true });
        Controls.Add(new Label { Text = "Máy này có thể dùng các quyền bạn đã bật trong LanBridge.\nChỉ đồng ý khi bạn đang muốn ghép nối với máy đó.", Location = new Point(24, 136), Size = new Size(440, 46) });
        _countdown.SetBounds(24, 190, 440, 25); _countdown.ForeColor = Color.FromArgb(151, 166, 187); Controls.Add(_countdown);
        var deny = new DarkButton { Text = "Từ chối", DialogResult = DialogResult.No, Bounds = new Rectangle(214, 229, 114, 35), BackColor = Color.FromArgb(49, 62, 83), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var allow = new DarkButton { Text = "Đồng ý", DialogResult = DialogResult.Yes, Bounds = new Rectangle(342, 229, 124, 35), BackColor = Color.FromArgb(47, 208, 177), ForeColor = Color.Black, FlatStyle = FlatStyle.Flat };
        Controls.Add(deny); Controls.Add(allow); AcceptButton = deny; CancelButton = deny;
        _timer.Tick += (_, _) => { int left = Math.Max(0, 30 - (int)_elapsed.Elapsed.TotalSeconds); _countdown.Text = $"Yêu cầu hết hạn sau {left} giây."; if (left == 0) { DialogResult = DialogResult.No; Close(); } };
        Shown += (_, _) => { deny.Focus(); _timer.Start(); }; FormClosed += (_, _) => _timer.Dispose();
    }
}
