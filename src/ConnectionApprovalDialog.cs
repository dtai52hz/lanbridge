using System.Diagnostics;

namespace LanBridge;

public sealed class ConnectionApprovalDialog : Form
{
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 250 };
    readonly Stopwatch _elapsed = Stopwatch.StartNew();
    readonly Label _countdown = new();
    public ConnectionApprovalDialog(ConnectionRequest request)
    {
        Text = "LanBridge · Yêu cầu kết nối"; ClientSize = new Size(530, 362);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false;
        BackColor = Color.FromArgb(16, 20, 29); ForeColor = Color.White; Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        Controls.Add(new Label
        {
            Text = "YÊU CẦU KẾT NỐI", Font = new Font(Font.FontFamily, 9, FontStyle.Bold),
            ForeColor = Color.FromArgb(47, 208, 177), Location = new Point(26, 22), Size = new Size(475, 24)
        });
        Controls.Add(new Label
        {
            Text = "Cho phép máy này điều khiển?", Font = new Font(Font.FontFamily, 17, FontStyle.Bold),
            Location = new Point(23, 51), Size = new Size(480, 39)
        });
        var device = new Panel { BackColor = Color.FromArgb(25, 33, 46), Bounds = new Rectangle(26, 105, 478, 88) };
        device.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 3, BackColor = Color.FromArgb(47, 208, 177) });
        device.Controls.Add(new Label
        {
            Text = request.Name, Location = new Point(18, 15), Size = new Size(440, 29),
            Font = new Font(Font.FontFamily, 12, FontStyle.Bold), AutoEllipsis = true, UseMnemonic = false
        });
        device.Controls.Add(new Label
        {
            Text = "Địa chỉ IP  ·  " + request.Address, Location = new Point(19, 49), Size = new Size(440, 23),
            ForeColor = Color.FromArgb(151, 166, 187), AutoEllipsis = true, UseMnemonic = false
        });
        Controls.Add(device);
        Controls.Add(new Label
        {
            Text = "Máy này được dùng các quyền bạn đã bật trong LanBridge.\nChỉ đồng ý khi bạn đang muốn ghép nối với máy đó.",
            Location = new Point(26, 209), Size = new Size(478, 49), ForeColor = Color.FromArgb(193, 204, 220)
        });
        _countdown.SetBounds(26, 268, 478, 24); _countdown.ForeColor = Color.FromArgb(151, 166, 187);
        _countdown.Font = new Font(Font.FontFamily, 9); Controls.Add(_countdown);
        var deny = new DarkButton { Text = "Từ chối", DialogResult = DialogResult.No, Bounds = new Rectangle(224, 308, 125, 37), BackColor = Color.FromArgb(39, 51, 69), ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        var allow = new DarkButton { Text = "Đồng ý kết nối", DialogResult = DialogResult.Yes, Bounds = new Rectangle(362, 308, 142, 37), BackColor = Color.FromArgb(47, 208, 177), ForeColor = Color.FromArgb(16, 20, 29), FlatStyle = FlatStyle.Flat };
        Controls.Add(deny); Controls.Add(allow); AcceptButton = deny; CancelButton = deny;
        _countdown.Text = "Tự động từ chối sau 30 giây nếu bạn không trả lời.";
        _timer.Tick += (_, _) => { int left = Math.Max(0, 30 - (int)_elapsed.Elapsed.TotalSeconds); _countdown.Text = $"Tự động từ chối sau {left} giây nếu bạn không trả lời."; if (left == 0) { DialogResult = DialogResult.No; Close(); } };
        Shown += (_, _) => { deny.Focus(); _timer.Start(); }; FormClosed += (_, _) => _timer.Dispose();
    }
}
