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
            if (!ownsInstance)
            {
                if (!DesktopActivation.RequestShowExisting())
                    MessageBox.Show("LanBridge đang mở. Bấm đúp biểu tượng LanBridge ở khay hệ thống để mở lại.", "LanBridge", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
        }
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "LanBridge", MessageBoxButtons.OK, MessageBoxIcon.Error);
        var form = new MainForm(preview);
        if (preview && args.Contains("--preview-minimum")) form.Size = form.MinimumSize;
        if (preview && args.Contains("--preview-session")) form.SelectSessionPreview();
        var previewOutput = Array.IndexOf(args, "--preview-output");
        if (args.Contains("--preview") && previewOutput >= 0 && previewOutput + 1 < args.Length)
        {
            form.Shown += async (_, _) =>
            {
                await Task.Delay(600);
                Form target = args.Contains("--preview-latency") ? form.ShowLatencyDiagnostics(false) : form;
                if (target != form) await Task.Delay(100);
                using var bitmap = new Bitmap(target.Width, target.Height);
                target.DrawToBitmap(bitmap, new Rectangle(Point.Empty, target.Size));
                bitmap.Save(Path.GetFullPath(args[previewOutput + 1]), System.Drawing.Imaging.ImageFormat.Png);
                if (target != form) target.Dispose();
                form.Close();
            };
        }
        try { Application.Run(form); }
        finally { if (ownsInstance) instance.ReleaseMutex(); }
    }
}
