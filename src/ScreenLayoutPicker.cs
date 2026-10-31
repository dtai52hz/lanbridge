using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace LanBridge;

/// <summary>A local, keyboard-accessible choice of the receiving computer's position.</summary>
public class ScreenLayoutPicker : Control
{
    static readonly Color Surface = Color.FromArgb(16, 20, 29), Accent = Color.FromArgb(47, 208, 177);
    static readonly Color Muted = Color.FromArgb(151, 166, 187), Border = Color.FromArgb(51, 63, 82);
    ScreenEdge _selectedEdge = ScreenEdge.Right;
    ScreenEdge? _hover;

    public ScreenLayoutPicker()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        Size = new Size(310, 88); MinimumSize = new Size(210, 76);
        BackColor = Surface; ForeColor = Muted; TabStop = true;
        AccessibleRole = AccessibleRole.Diagram;
        AccessibleName = "Vị trí máy B quanh máy A";
        UpdateAccessibleDescription();
    }

    [DefaultValue(ScreenEdge.Right)]
    public ScreenEdge SelectedEdge
    {
        get => _selectedEdge;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
            if (_selectedEdge == value) return;
            _selectedEdge = value; UpdateAccessibleDescription(); Invalidate();
            SelectedEdgeChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? SelectedEdgeChanged;

    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (Enabled && e.Modifiers == Keys.None)
        {
            ScreenEdge? edge = e.KeyCode switch
            {
                Keys.Right => ScreenEdge.Right, Keys.Left => ScreenEdge.Left,
                Keys.Up => ScreenEdge.Top, Keys.Down => ScreenEdge.Bottom, _ => null
            };
            if (edge is { } selected) { SelectedEdge = selected; e.Handled = true; e.SuppressKeyPress = true; }
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (Enabled && e.Button == MouseButtons.Left && HitTest(e.Location) is { } edge)
        { Focus(); SelectedEdge = edge; }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var hover = Enabled ? HitTest(e.Location) : null;
        if (_hover != hover) { _hover = hover; Cursor = hover != null ? Cursors.Hand : Cursors.Default; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = null; Cursor = Cursors.Default; Invalidate(); base.OnMouseLeave(e);
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        _hover = null; Cursor = Cursors.Default; Invalidate(); base.OnEnabledChanged(e);
    }

    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (ClientSize.Width < 12 || ClientSize.Height < 12) return;
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var center = CenterBounds();
        var target = PositionBounds(_selectedEdge);
        using (var line = new Pen(Enabled ? Accent : Border, Scale(2)))
            e.Graphics.DrawLine(line, Center(center), Center(target));
        foreach (var edge in Enum.GetValues<ScreenEdge>())
        {
            bool selected = edge == _selectedEdge;
            var fill = selected && Enabled ? Color.FromArgb(22, 61, 58) : Color.FromArgb(25, 31, 43);
            if (Enabled && _hover == edge && !selected) fill = Color.FromArgb(33, 44, 60);
            var text = edge switch
            {
                ScreenEdge.Right => "B →", ScreenEdge.Left => "← B", ScreenEdge.Top => "B ↑", _ => "B ↓"
            };
            DrawScreen(e.Graphics, PositionBounds(edge), text, fill,
                selected && Enabled ? Accent : Border, selected && Enabled ? Color.FromArgb(104, 235, 211) : Muted);
        }
        DrawScreen(e.Graphics, center, "Máy A", Color.FromArgb(39, 51, 69), Border, Enabled ? Color.White : Muted);
        if (Focused && Enabled && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -Scale(2), -Scale(2)));
    }

    void DrawScreen(Graphics graphics, Rectangle bounds, string text, Color fill, Color border, Color foreground)
    {
        if (bounds.Width < 2 || bounds.Height < 2) return;
        using var path = UiGeometry.Rounded(new RectangleF(bounds.X + .5f, bounds.Y + .5f, bounds.Width - 1, bounds.Height - 1), Scale(5));
        using var brush = new SolidBrush(fill); using var pen = new Pen(border);
        graphics.FillPath(brush, path); graphics.DrawPath(pen, path);
        TextRenderer.DrawText(graphics, text, Font, bounds, foreground,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    Rectangle CenterBounds()
    {
        int width = Math.Max(2, Math.Min(Scale(72), (ClientSize.Width - Scale(36)) / 3));
        int height = Math.Max(2, Math.Min(Scale(24), (ClientSize.Height - Scale(12)) / 3));
        return new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
    }

    Rectangle PositionBounds(ScreenEdge edge)
    {
        var bounds = CenterBounds();
        int horizontal = bounds.Width + Scale(14), vertical = bounds.Height + Scale(3);
        bounds.Offset(edge == ScreenEdge.Right ? horizontal : edge == ScreenEdge.Left ? -horizontal : 0,
            edge == ScreenEdge.Bottom ? vertical : edge == ScreenEdge.Top ? -vertical : 0);
        return bounds;
    }

    ScreenEdge? HitTest(Point point)
    {
        foreach (var edge in Enum.GetValues<ScreenEdge>())
            if (PositionBounds(edge).Contains(point)) return edge;
        return null;
    }

    int Scale(int value) => Math.Max(1, (int)Math.Round(value * DeviceDpi / 96f));
    static Point Center(Rectangle bounds) => new(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
    void UpdateAccessibleDescription() => AccessibleDescription = $"B ở {_selectedEdge switch
    {
        ScreenEdge.Right => "bên phải", ScreenEdge.Left => "bên trái", ScreenEdge.Top => "phía trên", _ => "phía dưới"
    }} A. Chọn bằng chuột hoặc bốn phím mũi tên.";
}
