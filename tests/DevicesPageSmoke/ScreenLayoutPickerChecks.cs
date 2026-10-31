using LanBridge;

internal static class ScreenLayoutPickerChecks
{
    internal static void Run(Action<bool, string> check)
    {
        using var window = new Form { ClientSize = new Size(500, 260) };
        using var picker = new TestPicker { Size = new Size(310, 88) };
        window.Controls.Add(picker); _ = window.Handle; _ = picker.Handle;
        int changes = 0; picker.SelectedEdgeChanged += (_, _) => changes++;
        check(picker.SelectedEdge == ScreenEdge.Right && picker.TabStop,
            "The position diagram must start at right and be keyboard reachable.");
        picker.SelectedEdge = ScreenEdge.Left; picker.SelectedEdge = ScreenEdge.Left;
        check(changes == 1, "Assigning the same screen position twice must not raise another change event.");
        check(picker.AccessibleDescription!.Contains("bên trái", StringComparison.Ordinal),
            "The position diagram must describe its current selection to assistive technology.");
        foreach (var (key, edge) in new[]
        {
            (Keys.Right, ScreenEdge.Right), (Keys.Up, ScreenEdge.Top),
            (Keys.Down, ScreenEdge.Bottom), (Keys.Left, ScreenEdge.Left)
        })
        {
            var args = picker.Press(key);
            check(picker.SelectedEdge == edge && args.Handled && args.SuppressKeyPress && picker.AcceptsKey(key),
                $"The position diagram did not select {edge} using {key}.");
        }
        int before = changes;
        var modified = picker.Press(Keys.Control | Keys.Right);
        check(picker.SelectedEdge == ScreenEdge.Left && changes == before && !modified.Handled,
            "Modified arrow shortcuts must not unexpectedly move the receiving computer.");
        foreach (var (point, edge) in new[]
        {
            (new Point(255, 44), ScreenEdge.Right), (new Point(55, 44), ScreenEdge.Left),
            (new Point(155, 10), ScreenEdge.Top), (new Point(155, 78), ScreenEdge.Bottom)
        })
        {
            picker.ClickAt(point);
            check(picker.SelectedEdge == edge, $"Clicking the {edge} screen tile chose another position.");
        }
        before = changes;
        picker.ClickAt(new Point(155, 44)); picker.ClickAt(new Point(255, 44), MouseButtons.Right);
        check(picker.SelectedEdge == ScreenEdge.Bottom && changes == before,
            "The local computer tile and right mouse button must not change the receiving position.");
        picker.Enabled = false; picker.ClickAt(new Point(255, 44)); picker.Press(Keys.Right);
        check(picker.SelectedEdge == ScreenEdge.Bottom && changes == before,
            "A disabled position diagram must ignore mouse and keyboard selection.");
        picker.Enabled = true; picker.Press(Keys.Right);
        check(picker.SelectedEdge == ScreenEdge.Right, "Re-enabling the diagram must restore keyboard selection.");
        before = changes;
        bool rejected = false;
        try { picker.SelectedEdge = (ScreenEdge)999; }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        check(rejected && picker.SelectedEdge == ScreenEdge.Right && changes == before,
            "An invalid enum must not corrupt the diagram selection or raise a change event.");
        foreach (var size in new[] { new Size(210, 76), new Size(310, 88), new Size(420, 120) })
        {
            picker.Size = size;
            using var bitmap = new Bitmap(picker.Width, picker.Height);
            picker.DrawToBitmap(bitmap, picker.ClientRectangle);
            check(picker.Size == size && changes == before,
                "Drawing or resizing the position diagram must not resize its owner or change its selection.");
        }
        check(!window.Visible && !picker.Visible, "Position diagram checks must never show a desktop window.");
    }

    sealed class TestPicker : ScreenLayoutPicker
    {
        internal KeyEventArgs Press(Keys keys) { var args = new KeyEventArgs(keys); OnKeyDown(args); return args; }
        internal bool AcceptsKey(Keys key) => IsInputKey(key);
        internal void ClickAt(Point point, MouseButtons button = MouseButtons.Left)
            => OnMouseDown(new MouseEventArgs(button, 1, point.X, point.Y, 0));
    }
}
