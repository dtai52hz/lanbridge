using System.Runtime.InteropServices;
using LanBridge;
using SkiaSharp;

namespace LanBridge.CrossPlatform;

/// <summary>Created without opening displays or asking for permissions. Capture runs only after an approved request.</summary>
public interface IScreenCapture
{
    Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken);
}

public static class DesktopScreenCapture
{
    public static IScreenCapture Create(Func<DesktopBounds> bounds) => new NativeScreenCapture(bounds);

    internal static (int Width, int Height) Fit(DesktopBounds bounds)
    {
        if (bounds.Width is < 2 or > 65536 || bounds.Height is < 2 or > 65536)
            throw new InvalidOperationException("Kích thước màn hình không hợp lệ.");
        double scale = Math.Min(1, Math.Min(1280d / bounds.Width, 800d / bounds.Height));
        return (Math.Max(2, (int)Math.Round(bounds.Width * scale)), Math.Max(2, (int)Math.Round(bounds.Height * scale)));
    }

    internal static byte Component(ulong pixel, ulong mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        ulong max = mask >> shift, value = (pixel & mask) >> shift;
        return (byte)((value * 255 + max / 2) / max);
    }

    sealed class NativeScreenCapture(Func<DesktopBounds> bounds) : IScreenCapture
    {
        readonly SemaphoreSlim _captureGate = new(1, 1);
        public async Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken)
        {
            // A cancelled macOS permission/screenshot call may still be unwinding. Repeated requests
            // must never run several native captures concurrently or multiply full-desktop buffers.
            await _captureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { return Capture(cancellationToken); }
            finally { _captureGate.Release(); }
        }
        ScreenFrame Capture(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rectangle = bounds();
            var size = Fit(rectangle);
            using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            if (OperatingSystem.IsWindows()) WindowsScreenCapture.Copy(rectangle, bitmap);
            else if (OperatingSystem.IsMacOS()) MacScreenCapture.Copy(rectangle, bitmap);
            else if (OperatingSystem.IsLinux()) X11ScreenCapture.Copy(rectangle, bitmap);
            else throw new PlatformNotSupportedException("Hệ điều hành chưa hỗ trợ xem màn hình.");
            cancellationToken.ThrowIfCancellationRequested();
            using var image = SKImage.FromBitmap(bitmap);
            // Bound the encoded image even on noisy desktops; never retain an unbounded video queue.
            foreach (int quality in new[] { 65, 45, 25, 10 })
            {
                using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                if (encoded != null && encoded.Size <= 512 * 1024)
                    return new ScreenFrame(encoded.ToArray(), size.Width, size.Height);
            }
            throw new IOException("Khung hình vượt giới hạn 512 KiB; đã dừng xem màn hình.");
        }
    }
}

internal static class WindowsScreenCapture
{
    internal static void Copy(DesktopBounds bounds, SKBitmap bitmap)
    {
        var current = new DesktopBounds(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        if (current != bounds)
            throw new IOException("Bố trí màn hình Windows đã thay đổi; kết nối lại để đồng bộ ảnh và tọa độ điều khiển.");
        nint screen = GetDC(0), memory = 0, dib = 0, previous = 0;
        if (screen == 0) throw new IOException("Windows không cho phép đọc màn hình hiện tại.");
        try
        {
            memory = CreateCompatibleDC(screen);
            var info = new BitmapInfo { Size = 40, Width = bitmap.Width, Height = -bitmap.Height, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(screen, ref info, 0, out nint pixels, 0, 0);
            if (memory == 0 || dib == 0 || pixels == 0) throw new IOException("Không tạo được bộ đệm màn hình Windows.");
            previous = SelectObject(memory, dib);
            if (previous is 0 or -1) { previous = 0; throw new IOException("Không chọn được bộ đệm màn hình Windows."); }
            SetStretchBltMode(memory, 4);
            if (!StretchBlt(memory, 0, 0, bitmap.Width, bitmap.Height, screen, bounds.X, bounds.Y,
                    bounds.Width, bounds.Height, 0x40CC0020))
                throw new IOException("Windows từ chối đọc màn hình (màn hình khóa hoặc desktop bảo vệ không được chia sẻ).");
            unsafe { Buffer.MemoryCopy((void*)pixels, (void*)bitmap.GetPixels(), bitmap.ByteCount, bitmap.ByteCount); }
        }
        finally
        {
            if (previous != 0 && memory != 0) SelectObject(memory, previous);
            if (dib != 0) DeleteObject(dib);
            if (memory != 0) DeleteDC(memory);
            ReleaseDC(0, screen);
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, SizeImage; public int XPels, YPels; public uint Used, Important;
    }
    [DllImport("user32")] static extern nint GetDC(nint window);
    [DllImport("user32")] static extern int GetSystemMetrics(int index);
    [DllImport("user32")] static extern int ReleaseDC(nint window, nint dc);
    [DllImport("gdi32")] static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32")] static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint pixels, nint section, uint offset);
    [DllImport("gdi32")] static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32")] static extern bool DeleteObject(nint value);
    [DllImport("gdi32")] static extern bool DeleteDC(nint dc);
    [DllImport("gdi32")] static extern int SetStretchBltMode(nint dc, int mode);
    [DllImport("gdi32")] static extern bool StretchBlt(nint target, int x, int y, int width, int height, nint source, int sx, int sy, int sw, int sh, uint operation);
}

internal static class MacScreenCapture
{
    internal static void Copy(DesktopBounds bounds, SKBitmap bitmap)
    {
        // The helper uses ScreenCaptureKit on macOS 14+, not obsolete display-image APIs.
        int status = Capture(bounds.X, bounds.Y, bounds.Width, bounds.Height, bitmap.GetPixels(), bitmap.Width,
            bitmap.Height, bitmap.RowBytes);
        if (status != 0) throw new IOException(status switch
        {
            1 => "macOS chưa cấp quyền Screen Recording. Mở System Settings → Privacy & Security → Screen & System Audio Recording, cho phép LanBridge rồi yêu cầu xem lại.",
            2 => "Không nhận được khung hình macOS trong 10 giây. Hãy kiểm tra quyền Screen Recording rồi thử lại.",
            4 => "Bố trí màn hình macOS đã thay đổi; kết nối lại để đồng bộ ảnh và tọa độ điều khiển.",
            _ => "ScreenCaptureKit không đọc được màn hình hiện tại; đã dừng xem màn hình.",
        });
    }
    [DllImport("LanBridge.ScreenCapture", EntryPoint = "lanbridge_capture_bgra")]
    static extern int Capture(int x, int y, int width, int height, nint pixels, int outputWidth, int outputHeight, int stride);
}

internal static class X11ScreenCapture
{
    // A separate display, owned entirely by the capture worker, never shares the input backend's Xlib handle.
    internal static unsafe void Copy(DesktopBounds bounds, SKBitmap bitmap)
    {
        if (LinuxInputBackend.IsWaylandSession(Environment.GetEnvironmentVariable("XDG_SESSION_TYPE"), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY")))
            throw new PlatformNotSupportedException("Xem màn hình Linux cần phiên X11/Xorg; Wayland chưa hỗ trợ.");
        X11Threading.Initialize();
        nint display = XOpenDisplay(0);
        if (display == 0) throw new IOException("Không mở được màn hình X11 của phiên đăng nhập.");
        nint image = 0;
        try
        {
            int screen = XDefaultScreen(display);
            int width = XDisplayWidth(display, screen), height = XDisplayHeight(display, screen);
            if (bounds.X != 0 || bounds.Y != 0 || bounds.Width != width || bounds.Height != height)
                throw new IOException("Bố trí màn hình X11 đã thay đổi; kết nối lại để cập nhật tọa độ.");
            if ((long)bounds.Width * bounds.Height > 32_000_000)
                throw new IOException("Màn hình X11 vượt giới hạn 32 triệu pixel của bản xem trước.");
            image = XGetImage(display, XRootWindow(display, screen), bounds.X, bounds.Y, (uint)bounds.Width, (uint)bounds.Height, nuint.MaxValue, 2);
            if (image == 0) throw new IOException("X11 không trả về ảnh màn hình.");
            var source = Marshal.PtrToStructure<XImage>(image);
            if (source.Data == 0 || source.BitsPerPixel is not (16 or 24 or 32) || source.BytesPerLine <= 0)
                throw new IOException("Định dạng pixel X11 chưa hỗ trợ.");
            int bytesPerPixel = source.BitsPerPixel / 8;
            byte* output = (byte*)bitmap.GetPixels();
            for (int y = 0; y < bitmap.Height; y++)
            {
                int sy = (int)((long)y * source.Height / bitmap.Height);
                byte* row = (byte*)source.Data + sy * source.BytesPerLine;
                for (int x = 0; x < bitmap.Width; x++)
                {
                    int sx = (int)((long)x * source.Width / bitmap.Width);
                    byte* at = row + sx * bytesPerPixel;
                    ulong pixel = 0;
                    for (int b = 0; b < bytesPerPixel; b++)
                        pixel |= (ulong)at[b] << (8 * (source.ByteOrder == 0 ? b : bytesPerPixel - 1 - b));
                    byte* target = output + y * bitmap.RowBytes + x * 4;
                    target[0] = DesktopScreenCapture.Component(pixel, source.BlueMask);
                    target[1] = DesktopScreenCapture.Component(pixel, source.GreenMask);
                    target[2] = DesktopScreenCapture.Component(pixel, source.RedMask); target[3] = 255;
                }
            }
        }
        finally { if (image != 0) XDestroyImage(image); XCloseDisplay(display); }
    }
    // Xlib uses native unsigned long for masks and the function table; explicit n(u)int keeps ARM64/x64 layouts correct.
    [StructLayout(LayoutKind.Sequential)] struct XImage
    {
        public int Width, Height, XOffset, Format; public nint Data;
        public int ByteOrder, BitmapUnit, BitmapBitOrder, BitmapPad, Depth, BytesPerLine, BitsPerPixel;
        public nuint RedMask, GreenMask, BlueMask; public nint ObData;
        public nint CreateImage, DestroyImage, GetPixel, PutPixel, SubImage, AddPixel;
    }
    [DllImport("libX11.so.6")] static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] static extern int XDefaultScreen(nint display);
    [DllImport("libX11.so.6")] static extern nuint XRootWindow(nint display, int screen);
    [DllImport("libX11.so.6")] static extern int XDisplayWidth(nint display, int screen);
    [DllImport("libX11.so.6")] static extern int XDisplayHeight(nint display, int screen);
    [DllImport("libX11.so.6")] static extern nint XGetImage(nint display, nuint drawable, int x, int y, uint width, uint height, nuint mask, int format);
    [DllImport("libX11.so.6")] static extern int XDestroyImage(nint image);
}

internal static class X11Threading
{
    static readonly Lazy<bool> Ready = new(() => XInitThreads() != 0);
    internal static void Initialize()
    {
        if (!Ready.Value) throw new IOException("Xlib không hỗ trợ nhiều luồng cho đầu vào và xem màn hình.");
    }
    [DllImport("libX11.so.6")] static extern int XInitThreads();
}
