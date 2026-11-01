# LanBridge Desktop 0.8.0-preview.2

Bản desktop đa nền tảng cho **Windows, macOS và Linux X11**: mỗi máy giữ màn hình riêng, dùng chung chuột/bàn phím trong mạng LAN. Máy nhận phải đồng ý kết nối. Phiên dùng TLS và chứng chỉ được ghim theo thiết bị/mã ghép nối, cùng giao thức của LanBridge Windows 0.7.x.

Đây là **preview riêng**, không thay bản Windows 0.7.3. Có thể nhận điều khiển và chia sẻ màn hình cho [ứng dụng Android](../android/README.md); chưa điều khiển điện thoại từ PC. Chưa hỗ trợ Linux Wayland, iOS hoặc mọi biến thể/hệ điều hành khác. Biên dịch được không đồng nghĩa đã kiểm chứng chuột/phím trên tất cả máy thật.

## Tải và chọn bản

[Trang Releases chung cho Windows, macOS, Linux và Android](https://github.com/dtai52hz/lanbridge/releases/tag/v0.7.3). Các gói Desktop mang phiên bản `0.8.0-preview.2`; [mã nguồn của đợt phát hành](https://github.com/dtai52hz/lanbridge/tree/v0.7.3). Cơ chế bảo trì hook bàn phím của ứng dụng Windows 0.7.2 chưa được chuyển sang Desktop preview.

| Máy | Gói |
| --- | --- |
| Windows 10/11 x64 | `LanBridge-Desktop-0.8.0-preview.2-win-x64.zip` |
| Mac Apple Silicon (M1/M2/M3/M4…) | `LanBridge-Desktop-0.8.0-preview.2-osx-arm64.zip` |
| Mac Intel | `LanBridge-Desktop-0.8.0-preview.2-osx-x64.zip` |
| Linux X11 x64 | `LanBridge-Desktop-0.8.0-preview.2-linux-x64.tar.gz` |
| Linux X11 ARM64 | `LanBridge-Desktop-0.8.0-preview.2-linux-arm64.tar.gz` |

Các gói chứa sẵn .NET; không cần cài runtime riêng. Giữ toàn bộ thư mục đã giải nén, gồm thư viện native. Mỗi gói có file SHA-256 bên cạnh.

**Windows:** đóng LanBridge cũ trước, giải nén ZIP rồi mở `LanBridge.Desktop.exe`. Bản Windows 0.7.3 có [bộ cài EXE tại cùng trang tải](https://github.com/dtai52hz/lanbridge/releases/tag/v0.7.3) và đầy đủ các tính năng Windows trước đây.

**macOS 14 trở lên:** giải nén rồi kéo `.app` vào Applications. Gói dùng chữ ký ad-hoc, chưa có chữ ký Developer ID/notarization. Nếu macOS chặn lần mở đầu, mở **System Settings → Privacy & Security** và kiểm tra mục cho phép mở ứng dụng vừa tải. Cấp quyền cho LanBridge trong **Privacy & Security → Accessibility** và **Input Monitoring** nếu hệ điều hành yêu cầu, rồi mở lại/kết nối lại. Ứng dụng không tự cấp hoặc vượt các quyền này.

**Linux:** cần phiên X11/Xorg 64-bit, XInput 2.1, XTEST và thư viện X11. Ví dụ trên Ubuntu 24.04:

```bash
sudo apt-get install libx11-6 libxi6 libxtst6 libxt6t64 libxkbcommon0 libxinerama1 libxrandr2 libice6 libsm6 libfontconfig1
tar -xzf LanBridge-Desktop-0.8.0-preview.2-linux-x64.tar.gz
cd LanBridge-Desktop-0.8.0-preview.2-linux-x64
./LanBridge.Desktop
```

Không chạy bằng root và không cần cấp quyền `/dev/input` hoặc `/dev/uinput`. Kiểm tra phiên bằng `echo "$XDG_SESSION_TYPE"`. Nếu là Wayland, cần dùng phiên X11 mà bản phân phối có cung cấp. XWayland không đủ để điều khiển toàn bộ desktop Wayland; ứng dụng báo rõ và từ chối đầu vào trong trường hợp này.

## Dùng hai máy

1. Mở LanBridge trên hai máy cùng mạng LAN. Cho phép ứng dụng trên mạng riêng nếu tường lửa hỏi.
2. Trên máy có chuột/bàn phím chính **A**, chọn máy **B** trong **Thiết bị** rồi bấm **Kết nối máy đã chọn**.
3. Trên **B**, kiểm tra tên/IP và chọn **Đồng ý**. B có thể bỏ quyền **Chuột / bàn phím** bất kỳ lúc nào.
4. Trong **Phiên điều khiển** trên A, chọn vị trí B, bộ gõ và phím chuyển. Mặc định **F9**: bấm để sang B, bấm lại để về A. Cách chuyển qua mép vẫn dùng được.
5. **Ctrl + Alt + F12** tại A trả điều khiển về A; tại B còn thu hồi quyền nhập ngay. Mất mạng/đóng phiên nhả các phím/nút mà LanBridge đã đưa xuống.

Nếu bàn phím Mac dùng F9 cho chức năng hệ thống, giữ **Fn + F9** hoặc chọn tổ hợp khác trong LanBridge. Fn là phím của máy A, không được gửi như một phím Windows.

Có thể chọn máy theo cả hai chiều: Windows → Mac, Mac → Windows, Linux X11 → Windows/Mac, Windows/Mac → Linux X11 và các cặp cùng hệ điều hành. Đây là các đường giao thức đã triển khai, không phải tuyên bố đã kiểm tra vật lý đủ mọi cặp thiết bị.

Nếu discovery bị chặn, **Chép mã máy này** trên B rồi dán vào **Kết nối bằng mã** ở A. Mã LB1 là quyền kết nối trực tiếp; chỉ gửi cho máy tin cậy. Luồng mã không hiện lại hộp hỏi tại B.

## Tính năng của preview

| Tính năng | Trạng thái |
| --- | --- |
| Chuột 5 nút, cuộn dọc/ngang, phím, Shift/Ctrl/Alt/Meta | Đã có backend Windows/macOS/Linux X11 |
| F9/chọn phím chuyển trong danh sách, chuyển mép, thoát khẩn cấp | Đã có; Linux có thể nhận phím kích hoạt đầu tiên ở A trước khi grab có hiệu lực |
| Telex/VNI tích hợp và gõ Unicode | Đã có; Linux X11 có giới hạn về tốc độ/ứng dụng bên dưới |
| Chat | Đã có |
| Nhận điều khiển từ Android, chia sẻ màn hình cho Android | Đã có; bật quyền Xem màn hình riêng tại máy tính, mặc định tắt |
| Clipboard chữ | Bật trên cả hai máy để chia sẻ, mặc định tắt |
| Gửi file thủ công, SHA-256, không ghi đè | Đã có; lưu tại `~/Downloads/LanBridge` |
| Clipboard ảnh/file tự động, âm thanh, ghi hình, màn che, restart, khay hệ thống, nhớ thiết lập | Chưa chuyển sang desktop preview; vẫn có các tính năng tương ứng trong bản Windows ổn định |
| Linux Wayland | Chưa hỗ trợ; không dùng XWayland để giả lập hỗ trợ toàn desktop |

Trên Mac, Command được gửi như Win/Super; Control vẫn là Control. Preview giữ phím vật lý, chưa tự đổi mọi phím tắt giữa quy ước macOS và Windows. Ví dụ điều khiển Windows từ Mac, dùng Control+C cho lệnh Ctrl+C của Windows.

Linux X11 không có API nhập Unicode trực tiếp tương đương Windows/macOS. SharpHook ánh xạ keycode rồi gửi phím; độ trễ mặc định khoảng 50 ms mỗi ký tự và phụ thuộc ứng dụng nhận. Vì vậy gõ tiếng Việt nhanh có thể chậm trên B Linux. Chưa đánh đồng chất lượng này với đường nhập Windows 0.7.3. Đây là giới hạn đã biết của preview.

## Chia sẻ màn hình cho Android

Kết nối điện thoại rồi bật **Xem màn hình** tại máy nhận. Điện thoại cần chọn chế độ **Xem màn hình** để bắt đầu; cấp quyền đơn thuần không tự chụp. Tắt quyền, ngắt phiên hoặc điện thoại về nền sẽ dừng chia sẻ. macOS 14+ dùng ScreenCaptureKit và hỏi quyền **Screen & System Audio Recording / Screen Recording** trong Privacy & Security khi nhận yêu cầu xem hợp lệ; nếu chưa được cấp, ứng dụng báo lỗi và dừng.

Ảnh JPEG được thu nhỏ đúng tỉ lệ, tối đa 1280×800 và 512 KiB, tối đa 5 khung/giây. Tọa độ chạm áp dụng cho toàn vùng desktop mà backend điều khiển. Linux X11 giới hạn vùng nguồn 32 triệu pixel; Wayland chưa hỗ trợ. Windows/Linux chưa ghép hình con trỏ hệ điều hành; điện thoại có dấu trỏ riêng. Desktop hiện chỉ cung cấp hình cho Android; giao diện Desktop chưa có trình xem màn hình của đối tác.

## Kiểm tra và giới hạn

- Các bài kiểm tra backend giả bao phủ chín cặp nhãn Windows/macOS/Linux qua giao thức, TLS localhost thật, xác nhận, quyền, Unicode/Telex, chuột, clipboard giả, file SHA-256 và ngắt phiên. Chúng không chạy hook native trên máy người dùng.
- Các bộ kiểm tra ánh xạ phím, ABI X11 và giao diện headless không cấp quyền Accessibility hoặc truy cập đầu vào thật.
- Workflow `Desktop platforms` build/test/publish riêng trên Windows x64, macOS Intel/ARM64 và Linux x64/ARM64. Kết quả thực tế xem tại [GitHub Actions](https://github.com/dtai52hz/lanbridge/actions/workflows/desktop.yml).
- Bài native Linux chỉ chạy trong Xvfb `:99` cô lập ở GitHub Actions, thử thư viện native, Unicode, cuộn và grab/nhả. Kết quả đó không thay cho kiểm chứng trên desktop GNOME/KDE hoặc phần cứng thật.
- Native macOS cần thử trên máy Mac đã được cấp quyền; Windows/macOS có thể từ chối đầu vào tại desktop bảo mật hoặc ứng dụng khác mức quyền.

Build/test từ nguồn bằng .NET 10 SDK:

```powershell
pwsh -File scripts/build-desktop.ps1 -Task Build
pwsh -File scripts/build-desktop.ps1 -Task Test
pwsh -File scripts/build-desktop.ps1 -Task Publish -Runtime win-x64
```

Tạo gói `.app` trên macOS và `.tar.gz` trên Linux để giữ quyền thực thi. Thư viện native để rời, có thể thay thế; nguồn libuiohook tương ứng và giấy phép bên thứ ba nằm trong `licenses`.

Tài liệu nền tảng: [Avalonia](https://docs.avaloniaui.net/docs/supported-platforms), [giới hạn SharpHook](https://sharphook.tolik.io/articles/os-constraints.html), [Wayland InputCapture](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.InputCapture.html), [Wayland RemoteDesktop](https://flatpak.github.io/xdg-desktop-portal/docs/doc-org.freedesktop.portal.RemoteDesktop.html).
