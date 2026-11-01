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
            await _input.RecoverTypingAsync(mode);
            if (!_closing && !IsDisposed)
            {
                var hook = _input.GetKeyboardHookDiagnosticsSnapshot();
                Log(hook.RefreshRequested && hook.FailedRenewals > 0
                    ? "Đã trả điều khiển về A và nhả trạng thái phím cũ. Kênh nhận phím đang chờ thử lại; xem Đo độ trễ để kiểm tra."
                    : "Đã trả điều khiển về A, nhả trạng thái phím cũ và khôi phục " + _typing.Text + ". Nhả các phím đang giữ rồi chuyển sang B để gõ tiếp.");
            }
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
