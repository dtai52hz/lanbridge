# LanBridge Android 0.1.0-preview.1

Điện thoại **Android 8 trở lên** điều khiển máy tính Windows/macOS/Linux X11 cùng mạng LAN. Có hai chế độ: **Bàn rê chuột** và **Xem màn hình** để chạm/kéo trực tiếp. Không cần root, Accessibility hay quyền chụp màn hình trên điện thoại vì điện thoại chỉ là máy điều khiển.

## Cài và kết nối

1. Tải `LanBridge-Android-0.1.0-preview.1.apk` từ [Releases chung](https://github.com/dtai52hz/lanbridge/releases/tag/v0.7.3), mở APK và cho phép trình duyệt/trình quản lý file cài ứng dụng từ nguồn này nếu Android hỏi.
2. Trên máy tính, dùng Windows **0.7.3** hoặc Desktop **0.8.0-preview.2**. Mac cần macOS 14+, Linux cần X11/Xorg. Mở LanBridge và cho phép mạng riêng trong tường lửa.
3. Nối điện thoại vào Wi-Fi cùng LAN với máy tính. Chọn máy tính trong danh sách trên điện thoại, rồi **Đồng ý** tại máy tính. Danh sách nghe thông báo LAN mỗi 2 giây; điện thoại không tự quảng bá thành máy nhận điều khiển.
4. Nếu không thấy máy, lấy **mã LB1** trong trang kết nối bằng mã của máy tính, dán vào điện thoại rồi bấm **Kết nối bằng mã**. Mã là bí mật cấp quyền trực tiếp; chỉ chia sẻ cho người điều khiển được phép.

Danh sách dùng UDP 45832. Phiên dùng TCP/TLS trên cổng máy tính quảng bá, mặc định 45831. Chứng thư được đối chiếu dấu vân tay từ thông báo LAN/mã ghép nối trước khi gửi bí mật. Thông báo LAN không phải danh bạ tài khoản xác minh; kiểm tra tên/IP tại hộp xác nhận.

## Dùng chuột và bàn phím

- **Bàn rê chuột:** vuốt một ngón để rê, chạm để bấm trái, vuốt hai ngón để cuộn. Có nút bấm phải và giữ/nhả chuột trái để kéo thả.
- **Xem màn hình:** trên máy tính bật quyền **Xem màn hình**, trên điện thoại chọn **Xem màn hình**. Chạm và kéo trực tiếp trong ảnh, hai ngón để cuộn. Viền đen ngoài ảnh không tạo nhấp chuột.
- **Bàn phím:** nhập tiếng Việt bằng Gboard/bàn phím điện thoại vào ô nhập rồi bấm **Gửi**. Chữ Unicode đã ghép dấu được gửi nguyên dạng, không chạy Telex lần nữa. Có Esc, Tab, Backspace, Enter, Ctrl+C/V và Command+C/V cho Mac. Các phím tắt thao tác clipboard của máy tính; chưa đồng bộ clipboard điện thoại.
- **Dừng:** bấm **Ngắt**, hoặc chủ máy thu hồi quyền. Khi điện thoại về nền, ứng dụng dừng yêu cầu ảnh và nhả phím/chuột. Phiên có thể còn kết nối trong thời gian hệ điều hành cho chạy; không có dịch vụ điều khiển nền.

Quyền **Chuột/phím** và **Xem màn hình** tách riêng. Xem màn hình mặc định tắt; máy tính có trạng thái đang chia sẻ. Mac còn cần Screen Recording trong System Settings → Privacy & Security. Linux cần X11; Wayland chưa hỗ trợ. Máy tính cũ Windows 0.7.2/Desktop preview.1 vẫn nhận thao tác nhưng không cung cấp hình.

## Giới hạn preview

Ảnh tối đa **1280×800, 512 KiB, 5 khung/giây**, toàn vùng desktop theo tọa độ máy tính; ưu tiên thao tác cơ bản. Chưa có âm thanh, clipboard/file trên Android, điều khiển điện thoại từ PC hay kết nối qua Internet. Màn hình khóa/desktop bảo vệ và ứng dụng khác mức quyền có thể không nhận thao tác. Windows/Linux chưa ghép con trỏ hệ điều hành vào ảnh; điện thoại vẽ dấu trỏ riêng.

Các bài thử dùng giao diện Android mô phỏng và kết nối TLS loopback thật giữa Java/.NET, không thay cho thử trên điện thoại và máy tính vật lý đủ mọi cặp hệ điều hành. macOS cần người dùng cấp quyền khi thử thật.

## Build từ nguồn

Cần JDK 17, Android SDK platform 36/build-tools 36.0.0. Trong thư mục gốc dự án:

```powershell
pwsh -File scripts/build-android.ps1 -Task Test
pwsh -File scripts/test-android-interop.ps1
pwsh -File scripts/build-android.ps1 -Task Publish
```

Gradle wrapper 8.13 có SHA-256 cố định. `Publish` ký APK bằng khóa riêng trong `.tools/android-signing` (bị loại khỏi Git), tái sử dụng khóa đó cho các lần cập nhật; cần sao lưu thư mục này ở nơi riêng. CI build APK chưa ký để kiểm tra, không chứa khóa phát hành. APK không cần .NET trên điện thoại. Ứng dụng dùng Android API/Java chuẩn; JUnit, JSON và Robolectric chỉ là phụ thuộc kiểm thử. Mã nguồn dự án theo [MIT](../LICENSE).
