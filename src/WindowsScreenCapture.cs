using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LanBridge;

/// <summary>Opt-in capture of the same virtual desktop used by normalized mouse input.</summary>
public static class WindowsScreenCapture
{
    public static Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 134_217_728)
            throw new InvalidOperationException("Không thể chụp kích thước màn hình hiện tại.");
        using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(desktop))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        cancellationToken.ThrowIfCancellationRequested();
        return EncodeFrame(desktop, cancellationToken);
    }, cancellationToken);

    // Kept separate from capture so synthetic-image tests never read the user's display.
    internal static ScreenFrame EncodeFrame(Image desktop, CancellationToken cancellationToken)
    {
        if (desktop.Width < 1 || desktop.Height < 1) throw new ArgumentException("Ảnh màn hình không hợp lệ.", nameof(desktop));
        var scale = Math.Min(1d, Math.Min(1280d / desktop.Width, 800d / desktop.Height));
        int width = Math.Max(1, (int)Math.Floor(desktop.Width * scale));
        int height = Math.Max(1, (int)Math.Floor(desktop.Height * scale));
        using var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(desktop, new Rectangle(0, 0, width, height));
        }
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        foreach (long quality in new[] { 65L, 45L, 25L, 10L })
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var stream = new MemoryStream();
            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            image.Save(stream, encoder, parameters);
            if (stream.Length <= 512 * 1024) return new ScreenFrame(stream.ToArray(), width, height);
        }
        throw new InvalidDataException("Ảnh màn hình vượt giới hạn truyền 512 KiB.");
    }
}
