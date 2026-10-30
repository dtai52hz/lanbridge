using System.Runtime.InteropServices;

namespace LanBridge;

public sealed partial class MainForm
{
    readonly IClipboardAccess _clipboard;
    readonly string _clipboardCacheDirectory;
    ClipboardFiles? _clipboardFiles;
    volatile bool _clipboardFilesCapable;
    uint? _observedClipboardSequence;
    long _clipboardGeneration;
    PendingClipboard? _pendingClipboard;
    bool _clipboardListener;
    bool _writingClipboard;

    sealed record PendingClipboard(PeerConnection Peer, CancellationToken Token, uint Sequence,
        long Generation, string? Text = null, string[]? Files = null, bool Clear = false);

    void AttachClipboardFiles(PeerConnection peer, CancellationToken token)
    {
        _clipboardFilesCapable = false;
        _pendingClipboard = null;
        _clipboardGeneration++;
        _observedClipboardSequence = _clipboard.SequenceNumber;
        var files = new ClipboardFiles(_clipboardCacheDirectory, packet => peer.SendAsync(packet, token),
            message => Ui(() => { if (_peer == peer && !token.IsCancellationRequested) Log("Clipboard file: " + message); }));
        _clipboardFiles = files;
        long incomingGeneration = _clipboardGeneration;
        files.ReceivingStarted += () =>
        {
            long offeredGeneration = Interlocked.Read(ref _clipboardGeneration);
            Ui(() =>
            {
                if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || !_permissions.Files) return;
                if (_clipboardGeneration != offeredGeneration) return;
                // Notice a local copy that happened before the clipboard timer got to read it.
                SyncClipboard();
                if (_clipboardGeneration != offeredGeneration) return;
                incomingGeneration = _clipboardGeneration;
                _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, incomingGeneration, Clear: true);
                TryPublishClipboard();
                Log("Đang nhận file đã sao chép. Khi nhận xong, nhấn Ctrl+V trong thư mục đích.");
            });
        };
        files.FilesReady += paths => Ui(() =>
        {
            if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || !_permissions.Files
                || _clipboardGeneration != incomingGeneration || _clipboard.SequenceNumber != _observedClipboardSequence) return;
            _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, incomingGeneration, Files: paths);
            TryPublishClipboard();
        });
    }

    void CancelClipboardFiles()
    {
        _clipboardGeneration++;
        _pendingClipboard = null;
        _clipboardFiles?.CancelTransfers();
    }

    void SyncClipboard()
    {
        if (_writingClipboard || _peer == null || !_helloReceived || !_permissions.Clipboard) return;
        try
        {
            uint sequence = _clipboard.SequenceNumber;
            if (_observedClipboardSequence != sequence)
            {
                var content = _clipboard.Read();
                if (sequence != _clipboard.SequenceNumber) return;
                _observedClipboardSequence = sequence;
                CancelClipboardFiles();
                if (content.Files is { Length: > 0 } paths)
                {
                    var files = _clipboardFiles;
                    if (_permissions.Files && _clipboardFilesCapable && files != null && !paths.Any(files.IsCachePath))
                    {
                        var token = _session!.Token;
                        _ = SendClipboardFilesAsync(files, paths, token);
                    }
                    else if (!_clipboardFilesCapable) Log("Cập nhật LanBridge trên cả hai máy để sao chép file bằng Ctrl+C / Ctrl+V.");
                }
                else if (content.Text is { Length: <= 262144 } text)
                {
                    _lastClipboard = text;
                    Send(new Packet { Type = "clipboard", Text = text });
                }
            }
            TryPublishClipboard();
        }
        catch (ExternalException) { /* Another application owns the clipboard. Retry on the next tick. */ }
    }

    async Task SendClipboardFilesAsync(ClipboardFiles files, string[] paths, CancellationToken token)
    {
        try { await files.SetLocalFilesAsync(paths, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Ui(() => { if (!token.IsCancellationRequested && ReferenceEquals(_clipboardFiles, files)) Log("Sao chép file: " + error.Message); }); }
    }

    void ReceiveClipboardText(Packet packet, CancellationToken token)
    {
        if (packet.Text is not { Length: <= 262144 } text || !_permissions.Clipboard) return;
        SyncClipboard();
        _clipboardFiles?.CancelTransfers();
        _pendingClipboard = new(_peer!, token, _clipboard.SequenceNumber, _clipboardGeneration, Text: text);
        TryPublishClipboard();
    }

    void TryPublishClipboard()
    {
        var pending = _pendingClipboard;
        if (pending == null) return;
        if (_peer != pending.Peer || pending.Token.IsCancellationRequested || !_permissions.Clipboard
            || (pending.Files != null && !_permissions.Files) || pending.Generation != _clipboardGeneration
            || pending.Sequence != _clipboard.SequenceNumber)
        { _pendingClipboard = null; return; }
        try
        {
            _writingClipboard = true;
            if (pending.Files != null) _clipboard.SetFiles(pending.Files);
            else if (pending.Clear) _clipboard.Clear();
            else _clipboard.SetText(pending.Text!);
            _observedClipboardSequence = _clipboard.SequenceNumber;
            if (pending.Text != null) _lastClipboard = pending.Text;
            _pendingClipboard = null;
            if (pending.Files != null) Log($"Clipboard đã sẵn sàng: {pending.Files.Length} mục. Nhấn Ctrl+V trong thư mục đích.");
        }
        catch (ExternalException) { /* Keep the pending write and retry, unless the user makes a new copy. */ }
        finally { _writingClipboard = false; }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!_preview && _clipboard is WindowsClipboardAccess) _clipboardListener = AddClipboardFormatListener(Handle);
    }
    protected override void OnHandleDestroyed(EventArgs e)
    {
        if (_clipboardListener) { RemoveClipboardFormatListener(Handle); _clipboardListener = false; }
        base.OnHandleDestroyed(e);
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x031D && !_closing) SyncClipboard(); // WM_CLIPBOARDUPDATE
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveClipboardFormatListener(IntPtr window);
}
