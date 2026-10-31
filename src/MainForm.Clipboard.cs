using System.Runtime.InteropServices;

namespace LanBridge;

public sealed partial class MainForm
{
    readonly IClipboardAccess _clipboard;
    readonly string _clipboardCacheDirectory;
    ClipboardFiles? _clipboardFiles;
    ClipboardImages? _clipboardImages;
    volatile bool _clipboardFilesCapable;
    volatile bool _clipboardImagesCapable;
    uint? _observedClipboardSequence;
    long _clipboardGeneration;
    long _clipboardFileEpoch, _clipboardImageEpoch;
    long _incomingClipboardSerial;
    PendingClipboard? _pendingClipboard;
    bool _clipboardListener;
    bool _writingClipboard;

    sealed record PendingClipboard(PeerConnection Peer, CancellationToken Token, uint Sequence,
        long Generation, string? Text = null, string[]? Files = null, byte[]? ImagePng = null, bool Clear = false, bool RequiresFiles = false, long IncomingSerial = 0);

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
        long incomingFileEpoch = _clipboardFileEpoch;
        long incomingSerial = 0;
        files.ReceivingStarted += () =>
        {
            long offeredGeneration = Interlocked.Read(ref _clipboardGeneration);
            long fileEpoch = Interlocked.Read(ref _clipboardFileEpoch);
            long serial = Interlocked.Increment(ref _incomingClipboardSerial);
            Interlocked.Exchange(ref incomingGeneration, offeredGeneration);
            Interlocked.Exchange(ref incomingFileEpoch, fileEpoch);
            Interlocked.Exchange(ref incomingSerial, serial);
            Ui(() =>
            {
                if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || !_permissions.Files) return;
                if (_clipboardGeneration != offeredGeneration || _incomingClipboardSerial != serial) return;
                // Notice a local copy that happened before the clipboard timer got to read it.
                _pendingClipboard = null;
                SyncClipboard();
                if (_clipboardGeneration != offeredGeneration) return;
                CancelClipboardImages();
                _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, offeredGeneration, Clear: true, RequiresFiles: true, IncomingSerial: serial);
                TryPublishClipboard();
                Log("Đang nhận file đã sao chép. Khi nhận xong, nhấn Ctrl+V trong thư mục đích.");
            });
        };
        files.FilesReady += paths =>
        {
            long generation = Interlocked.Read(ref incomingGeneration), fileEpoch = Interlocked.Read(ref incomingFileEpoch), serial = Interlocked.Read(ref incomingSerial);
            Ui(() =>
            {
                if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || !_permissions.Files
                    || serial != _incomingClipboardSerial || fileEpoch != _clipboardFileEpoch || _clipboardGeneration != generation || _clipboard.SequenceNumber != _observedClipboardSequence) return;
                _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, generation, Files: paths, RequiresFiles: true, IncomingSerial: serial);
                TryPublishClipboard();
            });
        };
    }

    void CancelClipboardTransfers()
    {
        _clipboardGeneration++;
        Interlocked.Increment(ref _incomingClipboardSerial);
        _pendingClipboard = null;
        CancelClipboardFilesOnly();
        CancelClipboardImages();
    }

    void CancelClipboardFilesOnly()
    {
        _clipboardFileEpoch++;
        if (_pendingClipboard?.RequiresFiles == true) _pendingClipboard = null;
        _clipboardFiles?.CancelTransfers();
    }

    void CancelClipboardImages()
    {
        _clipboardImageEpoch++;
        if (_pendingClipboard?.ImagePng != null) _pendingClipboard = null;
        _clipboardImages?.CancelTransfers();
    }

    void AttachClipboardImages(PeerConnection peer, CancellationToken token)
    {
        _clipboardImagesCapable = false;
        var images = new ClipboardImages(packet => peer.SendAsync(packet, token),
            message => Ui(() => { if (_peer == peer && !token.IsCancellationRequested) Log("Clipboard ảnh: " + message); }));
        _clipboardImages = images;
        long incomingGeneration = _clipboardGeneration, incomingImageEpoch = _clipboardImageEpoch, incomingSerial = 0;
        images.ReceivingStarted += () =>
        {
            long offeredGeneration = Interlocked.Read(ref _clipboardGeneration);
            long imageEpoch = Interlocked.Read(ref _clipboardImageEpoch);
            long serial = Interlocked.Increment(ref _incomingClipboardSerial);
            Interlocked.Exchange(ref incomingGeneration, offeredGeneration);
            Interlocked.Exchange(ref incomingImageEpoch, imageEpoch);
            Interlocked.Exchange(ref incomingSerial, serial);
            Ui(() =>
            {
                if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || _clipboardGeneration != offeredGeneration || _incomingClipboardSerial != serial) return;
                _pendingClipboard = null;
                SyncClipboard();
                if (_clipboardGeneration != offeredGeneration) return;
                CancelClipboardFilesOnly();
                _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, offeredGeneration, Clear: true, IncomingSerial: serial);
                TryPublishClipboard();
                Log("Đang nhận ảnh chụp. Khi sẵn sàng, nhấn Ctrl+V trong ứng dụng hỗ trợ ảnh.");
            });
        };
        images.ImageReady += png =>
        {
            long generation = Interlocked.Read(ref incomingGeneration), imageEpoch = Interlocked.Read(ref incomingImageEpoch), serial = Interlocked.Read(ref incomingSerial);
            _ = Task.Run(() =>
            {
                try
                {
                    if (token.IsCancellationRequested) return;
                    ClipboardImageCodec.Validate(png);
                    Ui(() =>
                    {
                        if (_peer != peer || token.IsCancellationRequested || !_permissions.Clipboard || generation != _clipboardGeneration
                            || serial != _incomingClipboardSerial || imageEpoch != _clipboardImageEpoch || _clipboard.SequenceNumber != _observedClipboardSequence) return;
                        _pendingClipboard = new(peer, token, _clipboard.SequenceNumber, generation, ImagePng: png, IncomingSerial: serial);
                        TryPublishClipboard();
                    });
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException or OutOfMemoryException or ExternalException)
                { Ui(() => { if (_peer == peer && !token.IsCancellationRequested) Log("Không nhận ảnh clipboard: " + error.Message); }); }
            });
        };
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
                CancelClipboardTransfers();
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
                else if (content.ImagePng is { Length: > 0 } png)
                {
                    if (_clipboardImagesCapable && _clipboardImages is { } images)
                        _ = SendClipboardImageAsync(images, png, _session!.Token);
                    else Log("Cập nhật LanBridge trên cả hai máy để chia sẻ ảnh chụp qua clipboard.");
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
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OutOfMemoryException)
        { _observedClipboardSequence = _clipboard.SequenceNumber; CancelClipboardTransfers(); Log("Clipboard: " + error.Message); }
    }

    async Task SendClipboardFilesAsync(ClipboardFiles files, string[] paths, CancellationToken token)
    {
        try { await files.SetLocalFilesAsync(paths, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Ui(() => { if (!token.IsCancellationRequested && ReferenceEquals(_clipboardFiles, files)) Log("Sao chép file: " + error.Message); }); }
    }

    async Task SendClipboardImageAsync(ClipboardImages images, byte[] png, CancellationToken token)
    {
        try { await images.SetLocalImageAsync(png, token); }
        catch (OperationCanceledException) { }
        catch (Exception error) { Ui(() => { if (!token.IsCancellationRequested && ReferenceEquals(_clipboardImages, images)) Log("Sao chép ảnh: " + error.Message); }); }
    }

    void ReceiveClipboardText(Packet packet, CancellationToken token)
    {
        if (packet.Text is not { Length: <= 262144 } text || !_permissions.Clipboard) return;
        Interlocked.Increment(ref _incomingClipboardSerial);
        _pendingClipboard = null;
        SyncClipboard();
        CancelClipboardFilesOnly(); CancelClipboardImages();
        _pendingClipboard = new(_peer!, token, _clipboard.SequenceNumber, _clipboardGeneration, Text: text, IncomingSerial: _incomingClipboardSerial);
        TryPublishClipboard();
    }

    void TryPublishClipboard()
    {
        var pending = _pendingClipboard;
        if (pending == null) return;
        if (_peer != pending.Peer || pending.Token.IsCancellationRequested || !_permissions.Clipboard
            || pending.IncomingSerial != Interlocked.Read(ref _incomingClipboardSerial)
            || (pending.RequiresFiles && !_permissions.Files) || pending.Generation != _clipboardGeneration
            || pending.Sequence != _clipboard.SequenceNumber)
        { _pendingClipboard = null; return; }
        try
        {
            _writingClipboard = true;
            if (pending.Files != null) _clipboard.SetFiles(pending.Files);
            else if (pending.ImagePng != null) _clipboard.SetImage(pending.ImagePng);
            else if (pending.Clear) _clipboard.Clear();
            else _clipboard.SetText(pending.Text!);
            _observedClipboardSequence = _clipboard.SequenceNumber;
            if (pending.Text != null) _lastClipboard = pending.Text;
            _pendingClipboard = null;
            if (pending.Files != null) Log($"Clipboard đã sẵn sàng: {pending.Files.Length} mục. Nhấn Ctrl+V trong thư mục đích.");
            if (pending.ImagePng != null) Log("Ảnh chụp đã sẵn sàng. Nhấn Ctrl+V trong Paint, Word hoặc ứng dụng chat hỗ trợ ảnh.");
        }
        catch (ExternalException) { /* Keep the pending write and retry, unless the user makes a new copy. */ }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or OutOfMemoryException or NotSupportedException)
        { _pendingClipboard = null; Log("Không đặt được clipboard: " + error.Message); }
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
        if (DesktopActivation.IsShowMessage(message.Msg) && !_closing) { RestoreFromTray(); return; }
        base.WndProc(ref message);
        if (message.Msg == 0x031D && !_closing) SyncClipboard(); // WM_CLIPBOARDUPDATE
    }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AddClipboardFormatListener(IntPtr window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RemoveClipboardFormatListener(IntPtr window);
}
