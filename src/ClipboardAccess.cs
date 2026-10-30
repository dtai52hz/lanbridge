using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace LanBridge;

public sealed record ClipboardContent(string? Text = null, string[]? Files = null, byte[]? ImagePng = null);

/// <summary>STA clipboard access, replaceable in integration tests without changing the user's clipboard.</summary>
public interface IClipboardAccess
{
    uint SequenceNumber { get; }
    ClipboardContent Read();
    void SetText(string text);
    void SetFiles(string[] paths);
    void SetImage(byte[] png) => throw new NotSupportedException("Clipboard images are not supported by this clipboard provider.");
    void Clear();
}

internal sealed class WindowsClipboardAccess : IClipboardAccess
{
    public uint SequenceNumber => GetClipboardSequenceNumber();
    public ClipboardContent Read()
    {
        // Explorer may expose a text representation too. A file selection must win.
        if (Clipboard.ContainsFileDropList()) return new(Files: Clipboard.GetFileDropList().Cast<string>().ToArray());
        // Snipping Tool and some editors publish a lossless registered PNG alongside
        // their Bitmap. Prefer it to keep alpha and avoid a second PNG encoding.
        if (Clipboard.ContainsData("PNG"))
        {
            object? value = Clipboard.TryGetData("PNG", out MemoryStream? pngStream) ? pngStream :
                Clipboard.TryGetData("PNG", out byte[]? pngBytes) ? pngBytes : null;
            try
            {
                byte[]? png = value switch
                {
                    byte[] bytes => bytes,
                    Stream stream => ReadPngStream(stream),
                    _ => null
                };
                if (png is not null)
                {
                    ClipboardImageCodec.Validate(png);
                    return new(ImagePng: png);
                }
            }
            catch (InvalidDataException) { /* Try the native Bitmap representation instead. */ }
            finally { if (value is Stream stream) stream.Dispose(); }
        }
        if (Clipboard.ContainsImage())
        {
            using Image? image = Clipboard.GetImage();
            if (image is not null) return new(ImagePng: ClipboardImageCodec.Encode(image));
        }
        return Clipboard.ContainsText() ? new(Text: Clipboard.GetText()) : new();
    }
    public void SetText(string text) { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); }
    public void SetFiles(string[] paths)
    {
        var files = new StringCollection(); files.AddRange(paths);
        Clipboard.SetFileDropList(files);
    }
    public void SetImage(byte[] png)
    {
        using var bitmap = ClipboardImageCodec.Decode(png);
        // WinForms SetImage uses SetDataObject(copy: true), so Windows owns a
        // persistent copy before the bitmap is disposed. This runs on the UI STA.
        Clipboard.SetImage(bitmap);
    }
    public void Clear() => Clipboard.Clear();

    static byte[] ReadPngStream(Stream stream)
    {
        if (stream.CanSeek)
        {
            if (stream.Length > ClipboardImageCodec.MaxEncodedBytes) throw new InvalidDataException("Clipboard PNG exceeds 32 MiB.");
            stream.Position = 0;
        }
        using var result = new MemoryStream();
        byte[] buffer = new byte[64 * 1024];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            if (result.Length + read > ClipboardImageCodec.MaxEncodedBytes) throw new InvalidDataException("Clipboard PNG exceeds 32 MiB.");
            result.Write(buffer, 0, read);
        }
        return result.ToArray();
    }

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
}
