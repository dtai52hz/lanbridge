using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace LanBridge;

public sealed record ClipboardContent(string? Text = null, string[]? Files = null);

/// <summary>STA clipboard access, replaceable in integration tests without changing the user's clipboard.</summary>
public interface IClipboardAccess
{
    uint SequenceNumber { get; }
    ClipboardContent Read();
    void SetText(string text);
    void SetFiles(string[] paths);
    void Clear();
}

internal sealed class WindowsClipboardAccess : IClipboardAccess
{
    public uint SequenceNumber => GetClipboardSequenceNumber();
    public ClipboardContent Read()
    {
        // Explorer may expose a text representation too. A file selection must win.
        if (Clipboard.ContainsFileDropList()) return new(Files: Clipboard.GetFileDropList().Cast<string>().ToArray());
        return Clipboard.ContainsText() ? new(Text: Clipboard.GetText()) : new();
    }
    public void SetText(string text) { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); }
    public void SetFiles(string[] paths)
    {
        var files = new StringCollection(); files.AddRange(paths);
        Clipboard.SetFileDropList(files);
    }
    public void Clear() => Clipboard.Clear();

    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
}
