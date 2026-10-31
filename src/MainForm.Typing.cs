namespace LanBridge;

public sealed partial class MainForm
{
    bool _resettingTyping;

    async Task ResetTypingFromUiAsync()
    {
        if (_closing || IsDisposed || IsHost || _resettingTyping) return;
        _resettingTyping = true;
        _resetTypingButton.Enabled = false;
        try
        {
            var mode = _typing.SelectedIndex is >= 0 and <= 2
                ? (TypingMode)_typing.SelectedIndex : TypingMode.VietnameseTelex;
            await _input.ResetTypingAsync(mode);
            if (!_closing && !IsDisposed)
                Log("Đã trả điều khiển về A và đặt lại " + _typing.Text + ". Chuyển sang B để gõ tiếp.");
        }
        catch (OperationCanceledException) when (_closing || IsDisposed) { }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException)
        {
            if (!_closing && !IsDisposed) Log("Chưa đặt lại được bộ gõ: " + error.Message);
        }
        finally
        {
            _resettingTyping = false;
            if (!_closing && !IsDisposed) _resetTypingButton.Enabled = !IsHost;
        }
    }
}
