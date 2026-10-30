namespace LanBridge;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        bool preview = args.Contains("--preview");
        using var instance = new Mutex(false, @"Local\LanBridgeDesktop");
        bool ownsInstance = false;
        if (!preview)
        {
            try { ownsInstance = instance.WaitOne(0); }
            catch (AbandonedMutexException) { ownsInstance = true; }
            if (!ownsInstance) { MessageBox.Show("LanBridge đang mở. Hãy dùng cửa sổ LanBridge hiện tại.", "LanBridge", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "LanBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        var form = new MainForm(preview);
        var previewOutput = Array.IndexOf(args, "--preview-output");
        if (args.Contains("--preview") && previewOutput >= 0 && previewOutput + 1 < args.Length)
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(600);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                bitmap.Save(Path.GetFullPath(args[previewOutput + 1]), System.Drawing.Imaging.ImageFormat.Png);
                form.Close();
            };
        }
        try { Application.Run(form); }
        finally { if (ownsInstance) instance.ReleaseMutex(); }
    }
}
