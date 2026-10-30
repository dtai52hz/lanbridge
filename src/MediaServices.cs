using System.Diagnostics;
using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using NAudio.Wave;

namespace LanBridge;

/// <summary>Explicitly enabled system-output audio from B to A; never captures a microphone.</summary>
public sealed class AudioBridge : IDisposable
{
    private readonly object _captureGate = new();
    private readonly object _playbackGate = new();
    private WasapiLoopbackCapture? _capture;
    private Channel<byte[]>? _outgoing;
    private CancellationTokenSource? _sendCancellation;
    private Task? _sendTask;
    private BufferedWaveProvider? _playbackBuffer;
    private WaveOutEvent? _output;
    private bool _disposed;

    public Action<Packet>? Send { get; set; }
    public Action<string>? Error { get; set; }

    public void StartCapture()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        StopCapture();
        lock (_captureGate)
        {
            var capture = new WasapiLoopbackCapture
            {
                // WASAPI's shared-mode conversion keeps the wire format predictable on both PCs.
                WaveFormat = new WaveFormat(48000, 16, 2)
            };
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(4)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true,
                AllowSynchronousContinuations = false
            });
            var cancellation = new CancellationTokenSource();
            capture.DataAvailable += (_, args) =>
            {
                if (cancellation.IsCancellationRequested || args.BytesRecorded == 0) return;
                // Never do network I/O, invoke the UI, or wait for queue space on the WASAPI thread.
                var bytes = new byte[args.BytesRecorded];
                Buffer.BlockCopy(args.Buffer, 0, bytes, 0, bytes.Length);
                queue.Writer.TryWrite(bytes);
            };
            capture.RecordingStopped += (_, args) =>
            {
                queue.Writer.TryComplete();
                if (args.Exception is not null && !cancellation.IsCancellationRequested)
                    ReportError($"Không thể thu âm thanh hệ thống: {args.Exception.Message}");
            };
            _capture = capture;
            _outgoing = queue;
            _sendCancellation = cancellation;
            _sendTask = Task.Run(async () =>
            {
                try
                {
                    if (cancellation.IsCancellationRequested) return;
                    Send?.Invoke(new Packet
                    {
                        Type = "audio-format", Rate = 48000, Channels = 2, Bits = 16,
                        Encoding = (int)WaveFormatEncoding.Pcm
                    });
                    await foreach (var bytes in queue.Reader.ReadAllAsync(cancellation.Token))
                    {
                        if (cancellation.IsCancellationRequested) break;
                        Send?.Invoke(new Packet { Type = "audio", Data = bytes });
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ReportError($"Không thể gửi âm thanh: {ex.Message}"); }
            });
            try { capture.StartRecording(); }
            catch
            {
                cancellation.Cancel();
                queue.Writer.TryComplete();
                capture.Dispose();
                _capture = null;
                throw;
            }
        }
    }

    public void StopCapture()
    {
        WasapiLoopbackCapture? capture;
        CancellationTokenSource? cancellation;
        Channel<byte[]>? queue;
        Task? task;
        lock (_captureGate)
        {
            capture = _capture;
            cancellation = _sendCancellation;
            queue = _outgoing;
            task = _sendTask;
            _capture = null;
            _sendCancellation = null;
            _outgoing = null;
            _sendTask = null;
        }
        if (cancellation is null) return;
        cancellation.Cancel();
        queue?.Writer.TryComplete();
        try { capture?.Dispose(); }
        catch (Exception ex) { ReportError($"Không thể dừng thu âm thanh: {ex.Message}"); }
        // A caller's Send action must enqueue promptly. Bound teardown even if a transport misbehaves.
        if (task is not null)
        {
            try { task.Wait(TimeSpan.FromMilliseconds(500)); }
            catch (AggregateException) { }
            _ = task.ContinueWith(_ => cancellation.Dispose(), TaskScheduler.Default);
        }
        else cancellation.Dispose();
        if (capture is not null)
        {
            try { Send?.Invoke(new Packet { Type = "audio-stop" }); }
            catch (Exception ex) { ReportError($"Không thể gửi trạng thái dừng âm thanh: {ex.Message}"); }
        }
    }

    public void Handle(Packet packet)
    {
        if (_disposed) return;
        try
        {
            lock (_playbackGate)
            {
                if (packet.Type == "audio-stop")
                {
                    StopPlaybackCore();
                    return;
                }
                if (packet.Type == "audio-format")
                {
                    StopPlaybackCore();
                    if (packet.Rate is < 8000 or > 192000 || packet.Channels is < 1 or > 8 ||
                        packet.Bits is not (8 or 16 or 24 or 32) ||
                        (packet.Encoding != (int)WaveFormatEncoding.Pcm && packet.Encoding != (int)WaveFormatEncoding.IeeeFloat) ||
                        (packet.Encoding == (int)WaveFormatEncoding.IeeeFloat && packet.Bits != 32))
                        throw new InvalidDataException("Định dạng âm thanh không hợp lệ.");
                    var format = packet.Encoding == (int)WaveFormatEncoding.IeeeFloat
                        ? WaveFormat.CreateIeeeFloatWaveFormat(packet.Rate, packet.Channels)
                        : new WaveFormat(packet.Rate, packet.Bits, packet.Channels);
                    var buffer = new BufferedWaveProvider(format)
                    {
                        BufferDuration = TimeSpan.FromMilliseconds(500),
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };
                    var output = new WaveOutEvent { DesiredLatency = 120, NumberOfBuffers = 3 };
                    try
                    {
                        output.Init(buffer);
                        output.Play();
                    }
                    catch { output.Dispose(); throw; }
                    _playbackBuffer = buffer;
                    _output = output;
                    return;
                }
                if (packet.Type != "audio" || packet.Data is not { Length: > 0 } data || _playbackBuffer is null)
                    return;
                var blockAlign = _playbackBuffer.WaveFormat.BlockAlign;
                if (data.Length > _playbackBuffer.BufferLength || data.Length % blockAlign != 0)
                    return;
                // Keep live sound close to the current moment after network congestion.
                if (_playbackBuffer.BufferedDuration > TimeSpan.FromMilliseconds(250))
                    _playbackBuffer.ClearBuffer();
                _playbackBuffer.AddSamples(data, 0, data.Length);
            }
        }
        catch (Exception ex)
        {
            StopPlayback();
            ReportError($"Không thể phát âm thanh: {ex.Message}");
        }
    }

    public void StopPlayback()
    {
        lock (_playbackGate) StopPlaybackCore();
    }

    private void StopPlaybackCore()
    {
        var output = _output;
        _output = null;
        _playbackBuffer = null;
        try { output?.Dispose(); }
        catch (Exception ex) { ReportError($"Không thể đóng thiết bị phát: {ex.Message}"); }
    }

    private void ReportError(string message) => MediaErrors.Report(Error, message);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopCapture();
        StopPlayback();
    }
}

public static class DesktopCapture
{
    /// <summary>Captures the interactive virtual desktop only when called.</summary>
    public static byte[] CaptureJpeg(int maxWidth = 1280)
    {
        using var image = CaptureBitmap(maxWidth);
        return EncodeJpeg(image);
    }

    internal static Bitmap CaptureBitmap(int maxWidth = 1280)
    {
        if (maxWidth is < 160 or > 4096) throw new ArgumentOutOfRangeException(nameof(maxWidth));
        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 134_217_728)
            throw new InvalidOperationException("Không thể chụp kích thước màn hình hiện tại.");
        using var desktop = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(desktop))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        var scale = Math.Min(1.0, (double)maxWidth / bounds.Width);
        // AVI's stream rectangle uses signed 16-bit coordinates.
        scale = Math.Min(scale, 16384.0 / bounds.Height);
        var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var image = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(image);
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.DrawImage(desktop, new Rectangle(0, 0, width, height));
            return image;
        }
        catch { image.Dispose(); throw; }
    }

    internal static byte[] EncodeJpeg(Image image)
    {
        using var stream = new MemoryStream();
        var encoder = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 70L);
        image.Save(stream, encoder, parameters);
        return stream.ToArray();
    }
}

/// <summary>Local, opt-in screen recording. The owner must display its recording state and destination.</summary>
public sealed class ScreenRecorder : IDisposable
{
    private const int FramesPerSecond = 8;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancellation;
    private Task? _worker;
    private volatile bool _isRecording;
    private bool _disposed;

    public bool IsRecording => _isRecording;
    public Action<string>? Error { get; set; }

    public void Start(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Stop();
        lock (_gate)
        {
            // The first capture and file creation are part of this explicit Start operation.
            using var firstImage = DesktopCapture.CaptureBitmap();
            var firstFrame = DesktopCapture.EncodeJpeg(firstImage);
            var writer = new MjpegAviWriter(Path.GetFullPath(path), firstImage.Width, firstImage.Height, FramesPerSecond);
            try { writer.AddFrame(firstFrame); }
            catch { writer.Dispose(); throw; }
            var cancellation = new CancellationTokenSource();
            _cancellation = cancellation;
            _isRecording = true;
            _worker = Task.Run(async () =>
            {
                try
                {
                    var clock = Stopwatch.StartNew();
                    long nextFrame = 1;
                    while (!cancellation.IsCancellationRequested)
                    {
                        var delay = TimeSpan.FromSeconds((double)nextFrame / FramesPerSecond) - clock.Elapsed;
                        if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        using var image = DesktopCapture.CaptureBitmap();
                        if (image.Width != writer.Width || image.Height != writer.Height)
                            throw new InvalidOperationException("Độ phân giải màn hình đã thay đổi. Hãy bắt đầu bản ghi mới.");
                        var frame = DesktopCapture.EncodeJpeg(image);
                        // Duplicate a previous sample when a tick is missed so playback preserves wall time.
                        var dueFrame = (long)(clock.Elapsed.TotalSeconds * FramesPerSecond);
                        if (dueFrame - nextFrame > FramesPerSecond * 10)
                            throw new InvalidOperationException("Máy tạm dừng quá lâu. Bản ghi đã được lưu và dừng.");
                        while (nextFrame < dueFrame)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            writer.AddFrame(firstFrame);
                            nextFrame++;
                        }
                        writer.AddFrame(frame);
                        firstFrame = frame;
                        nextFrame++;
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { MediaErrors.Report(Error, $"Đã dừng ghi màn hình: {ex.Message}"); }
                finally
                {
                    try { writer.Dispose(); }
                    catch (Exception ex) { MediaErrors.Report(Error, $"Không thể hoàn tất tệp AVI: {ex.Message}"); }
                    _isRecording = false;
                }
            });
        }
    }

    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? worker;
        lock (_gate)
        {
            cancellation = _cancellation;
            worker = _worker;
            _cancellation = null;
            _worker = null;
        }
        if (cancellation is null) return;
        cancellation.Cancel();
        try { worker?.GetAwaiter().GetResult(); }
        finally
        {
            cancellation.Dispose();
            _isRecording = false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}

/// <summary>A visible software cover; not a display driver or protection against physical observation.</summary>
public sealed class PrivacyCurtain : IDisposable
{
    private readonly Control _dispatcher = new();
    private readonly List<Form> _windows = [];
    private volatile bool _isVisible;
    private bool _disposed;

    public PrivacyCurtain() => _ = _dispatcher.Handle;
    public bool IsVisible => _isVisible;

    public void Show() => OnUi(() =>
    {
        HideCore();
        try
        {
            foreach (var screen in Screen.AllScreens)
            {
                var window = new CurtainWindow
                {
                    Bounds = screen.Bounds,
                    Text = "LanBridge · Màn che riêng tư",
                    BackColor = Color.Black,
                    ForeColor = Color.White,
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition = FormStartPosition.Manual,
                    TopMost = true,
                    ShowInTaskbar = false
                };
                var message = new Label
                {
                    Dock = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 19),
                    Text = "LanBridge · Màn che riêng tư đang bật\n\n" +
                           "Máy B đang trong phiên chia sẻ được cho phép.\n" +
                           "Ctrl + Alt + F12 để thu hồi điều khiển và hiện lại màn hình.\n\n" +
                           "Màn che phần mềm · Không thay thế trình điều khiển riêng tư"
                };
                window.Controls.Add(message);
                _windows.Add(window);
                window.Show();
            }
            _isVisible = _windows.Count > 0;
        }
        catch { HideCore(); throw; }
    });

    public void Hide() => OnUi(HideCore);

    private void HideCore()
    {
        foreach (var window in _windows)
        {
            window.Close();
            window.Dispose();
        }
        _windows.Clear();
        _isVisible = false;
    }

    private void OnUi(Action action)
    {
        if (_disposed || _dispatcher.IsDisposed) return;
        if (_dispatcher.InvokeRequired) _dispatcher.Invoke(action);
        else action();
    }

    public void Dispose()
    {
        if (_disposed) return;
        OnUi(() => { HideCore(); _dispatcher.Dispose(); });
        _disposed = true;
    }

    private sealed class CurtainWindow : Form
    {
        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

        protected override bool ShowWithoutActivation => true;
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            // Keep the cover opaque. WS_EX_TRANSPARENT on a layered window passes hit testing
            // through to underlying apps, including clicks injected by the paired controller.
            if (!SetLayeredWindowAttributes(Handle, 0, 255, 0x00000002 /* LWA_ALPHA */))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Không thể bật màn che phần mềm.");
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000 | 0x00000020;
                // NOACTIVATE | TOOLWINDOW | LAYERED | TRANSPARENT
                return parameters;
            }
        }
    }
}

internal static class MediaErrors
{
    internal static void Report(Action<string>? handler, string message)
    {
        if (handler is null) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { handler(message); }
            catch { /* Reporting must not terminate an audio/capture worker. */ }
        });
    }
}

/// <summary>Standard RIFF AVI with independent JPEG frames, a video stream, and an idx1 index.</summary>
internal sealed class MjpegAviWriter : IDisposable
{
    private readonly FileStream _stream;
    private readonly BinaryWriter _writer;
    private readonly List<(uint Offset, uint Length)> _index = [];
    private readonly long _riffSizePosition;
    private readonly long _totalFramesPosition;
    private readonly long _streamFramesPosition;
    private readonly long _moviSizePosition;
    private readonly long _moviFourCcPosition;
    private bool _disposed;
    internal int Width { get; }
    internal int Height { get; }

    internal MjpegAviWriter(string path, int width, int height, int framesPerSecond)
    {
        if (width is < 1 or > 32767 || height is < 1 or > 32767 || framesPerSecond is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        _stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 65536);
        _writer = new BinaryWriter(_stream, Encoding.ASCII, leaveOpen: true);
        try
        {
            FourCc("RIFF"); _riffSizePosition = _stream.Position; _writer.Write(0u); FourCc("AVI ");
            FourCc("LIST"); var headerSizePosition = _stream.Position; _writer.Write(0u); FourCc("hdrl");
            FourCc("avih"); _writer.Write(56u);
            _writer.Write((uint)(1_000_000 / framesPerSecond));
            _writer.Write((uint)(width * height * 3L * framesPerSecond));
            _writer.Write(0u); _writer.Write(0x10u); // AVIF_HASINDEX
            _totalFramesPosition = _stream.Position; _writer.Write(0u);
            _writer.Write(0u); _writer.Write(1u); _writer.Write((uint)(width * height * 3));
            _writer.Write((uint)width); _writer.Write((uint)height);
            for (var i = 0; i < 4; i++) _writer.Write(0u);
            FourCc("LIST"); var streamListSizePosition = _stream.Position; _writer.Write(0u); FourCc("strl");
            FourCc("strh"); _writer.Write(56u); FourCc("vids"); FourCc("MJPG");
            _writer.Write(0u); _writer.Write((ushort)0); _writer.Write((ushort)0); _writer.Write(0u);
            _writer.Write(1u); _writer.Write((uint)framesPerSecond); _writer.Write(0u);
            _streamFramesPosition = _stream.Position; _writer.Write(0u);
            _writer.Write((uint)(width * height * 3)); _writer.Write(uint.MaxValue); _writer.Write(0u);
            _writer.Write((short)0); _writer.Write((short)0); _writer.Write((short)width); _writer.Write((short)height);
            FourCc("strf"); _writer.Write(40u); _writer.Write(40u);
            _writer.Write(width); _writer.Write(height); _writer.Write((ushort)1); _writer.Write((ushort)24);
            FourCc("MJPG"); _writer.Write((uint)(width * height * 3));
            _writer.Write(0); _writer.Write(0); _writer.Write(0u); _writer.Write(0u);
            PatchUInt32(streamListSizePosition, checked((uint)(_stream.Position - streamListSizePosition - 4)));
            PatchUInt32(headerSizePosition, checked((uint)(_stream.Position - headerSizePosition - 4)));
            FourCc("LIST"); _moviSizePosition = _stream.Position; _writer.Write(0u);
            _moviFourCcPosition = _stream.Position; FourCc("movi");
        }
        catch { _writer.Dispose(); _stream.Dispose(); throw; }
    }

    internal void AddFrame(byte[] jpeg)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            throw new InvalidDataException("Khung hình JPEG không hợp lệ.");
        // AVI 1.0 is kept below 2 GiB for compatibility; always retain room for the final index.
        if (_stream.Position + jpeg.Length + (_index.Count + 1L) * 16 + 32 >= int.MaxValue)
            throw new IOException("Bản ghi đã đạt giới hạn 2 GB của AVI. Hãy bắt đầu tệp mới.");
        var offset = checked((uint)(_stream.Position - _moviFourCcPosition));
        FourCc("00dc"); _writer.Write((uint)jpeg.Length); _writer.Write(jpeg);
        if ((jpeg.Length & 1) != 0) _writer.Write((byte)0);
        _index.Add((offset, (uint)jpeg.Length));
    }

    private void FourCc(string value) => _writer.Write(Encoding.ASCII.GetBytes(value));

    private void PatchUInt32(long position, uint value)
    {
        var previous = _stream.Position;
        _stream.Position = position;
        _writer.Write(value);
        _stream.Position = previous;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            PatchUInt32(_moviSizePosition, checked((uint)(_stream.Position - _moviSizePosition - 4)));
            FourCc("idx1"); _writer.Write(checked((uint)(_index.Count * 16L)));
            foreach (var entry in _index)
            {
                FourCc("00dc"); _writer.Write(0x10u); _writer.Write(entry.Offset); _writer.Write(entry.Length);
            }
            PatchUInt32(_totalFramesPosition, checked((uint)_index.Count));
            PatchUInt32(_streamFramesPosition, checked((uint)_index.Count));
            PatchUInt32(_riffSizePosition, checked((uint)(_stream.Length - 8)));
            _writer.Flush();
            _stream.Flush(flushToDisk: true);
        }
        finally { _writer.Dispose(); _stream.Dispose(); }
    }
}
