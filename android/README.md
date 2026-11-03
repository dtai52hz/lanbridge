# LanBridge Android 0.1.0-preview.2

Điện thoại **Android 8 trở lên** điều khiển máy tính Windows/macOS/Linux X11 cùng mạng LAN. Có hai chế độ: **Bàn rê chuột** và **Xem màn hình** để chạm/kéo trực tiếp. Không cần root, Accessibility hay quyền chụp màn hình trên điện thoại vì điện thoại chỉ là máy điều khiển.

## Cài và kết nối

1. Tải `LanBridge-Android-0.1.0-preview.2.apk` từ [Releases chung](https://github.com/dtai52hz/lanbridge/releases/tag/v0.7.4), mở APK để cập nhật bản cũ, không cần gỡ ứng dụng trước. Nếu Android hỏi, cho phép trình duyệt/trình quản lý file cài ứng dụng từ nguồn này.
2. Trên máy tính, dùng Windows **0.7.3 trở lên** (khuyến nghị 0.7.4) hoặc Desktop **0.8.0-preview.2**. Mac cần macOS 14+, Linux cần X11/Xorg. Mở LanBridge và cho phép mạng riêng trong tường lửa.
3. Nối điện thoại vào Wi-Fi cùng LAN với máy tính. Chọn máy tính trong danh sách trên điện thoại, rồi **Đồng ý** tại máy tính. Danh sách nghe thông báo LAN mỗi 2 giây; điện thoại không tự quảng bá thành máy nhận điều khiển.
4. Nếu không thấy máy, lấy **mã LB1** trong trang kết nối bằng mã của máy tính, dán vào điện thoại rồi bấm **Kết nối bằng mã**. Mã là bí mật cấp quyền trực tiếp; chỉ chia sẻ cho người điều khiển được phép.

Danh sách dùng UDP 45832. Phiên dùng TCP/TLS trên cổng máy tính quảng bá, mặc định 45831. Chứng thư được đối chiếu dấu vân tay từ thông báo LAN/mã ghép nối trước khi gửi bí mật. Thông báo LAN không phải danh bạ tài khoản xác minh; kiểm tra tên/IP tại hộp xác nhận.

## Dùng chuột và bàn phím

- **Bàn rê chuột:** vuốt một ngón để rê, chạm nhanh để bấm trái, **nhấn giữ tại chỗ để bấm phải**, vuốt hai ngón để cuộn. Nhấn giữ chỉ bấm phải một lần, nhấc tay không bấm trái thêm. Di chuyển quá ngưỡng rê, thêm ngón tay hoặc bỏ chế độ sẽ hủy lượt giữ đang chờ. Có nút giữ/nhả chuột trái để kéo thả.
- **Xem màn hình:** trên máy tính bật quyền **Xem màn hình**, trên điện thoại chọn **Xem màn hình**. Chạm và kéo trực tiếp trong ảnh, hai ngón để cuộn. Viền đen ngoài ảnh không tạo nhấp chuột.
- **Bàn phím:** bấm **Bàn phím** để mở ô nhập và bàn phím điện thoại. Nhập tiếng Việt rồi bấm **Gửi**; có thể xóa nháp hoặc ẩn bảng để bàn rê rộng hơn. Chữ Unicode đã ghép dấu được gửi nguyên dạng, không chạy Telex lần nữa. Nút **Phím** mở phím đặc biệt và phím tắt, gồm Ctrl+C/V và Command+C/V cho Mac. Các phím tắt thao tác clipboard của máy tính; chưa đồng bộ clipboard điện thoại.
- **Dừng:** bấm **Ngắt**, hoặc chủ máy thu hồi quyền. Khi điện thoại về nền, ứng dụng dừng yêu cầu ảnh và nhả phím/chuột. Phiên có thể còn kết nối trong thời gian hệ điều hành cho chạy; không có dịch vụ điều khiển nền.

Quyền **Chuột/phím** và **Xem màn hình** tách riêng. Xem màn hình mặc định tắt; máy tính có trạng thái đang chia sẻ. Mac còn cần Screen Recording trong System Settings → Privacy & Security. Linux cần X11; Wayland chưa hỗ trợ. Máy tính cũ Windows 0.7.2/Desktop preview.1 vẫn nhận thao tác nhưng không cung cấp hình.

## Gửi và nhận file

Máy tính cần bật quyền **Truyền file**. Mở **Tệp** trên điện thoại:

1. **Điện thoại → máy tính:** bấm gửi file, chọn một tài liệu/ảnh/video bằng trình chọn file Android. File được đọc theo từng khối, có tiến trình và nút hủy. Máy tính lưu trong thư mục nhận LanBridge, thông thường `Downloads/LanBridge`; ứng dụng máy tính tự kiểm tra tên, dung lượng và SHA-256.
2. **Máy tính → điện thoại:** bật nhận file trong bảng Tệp. Trên máy tính bấm **Gửi file**, rồi trên điện thoại chọn **Nhận** tại thông báo. Cần trả lời trước khi yêu cầu hết hạn (trong khoảng 18 giây).
3. Nhận xong, ứng dụng kiểm tra đủ dung lượng và SHA-256 rồi đưa vào danh sách file đã nhận. Bấm **Lưu thành…** để chọn nơi lưu bằng trình chọn file Android. Có thể xóa bản lưu riêng trong ứng dụng sau đó.

Không cấp quyền truy cập toàn bộ bộ nhớ. Ứng dụng chỉ đọc file bạn chọn hoặc ghi vào nơi bạn chọn khi xuất file. File nhận ban đầu nằm trong vùng riêng của ứng dụng, chưa xuất sẽ mất nếu gỡ ứng dụng. Tối đa **2 GiB mỗi file**, một file gửi và một file nhận cùng lúc; tối đa **8 file đã nhận** được giữ riêng, cần lưu/xóa bớt khi đầy. File chưa đủ, sai SHA-256, bị hủy hoặc mất quyền không được xuất như file hoàn chỉnh. Không ghi đè file đã có.

Bộ đệm nhận file giới hạn 3 MiB để giữ phiên điều khiển phản hồi được. Với giao thức máy tính hiện tại, nếu bộ nhớ điện thoại ghi quá chậm và bộ đệm đầy, lượt truyền đó sẽ dừng kèm thông báo để thử lại; chuột, bàn phím và kết nối vẫn hoạt động.

Mở trình chọn file sẽ nhả điều khiển và dừng hình khi ứng dụng về nền; phiên truyền có thể tiếp tục trong thời gian Android cho ứng dụng chạy. Giữ ứng dụng phía trước khi truyền lâu; chưa có dịch vụ truyền file nền. Không cần đổi ứng dụng máy tính chỉ để dùng truyền file vì giao thức tương thích các bản nêu trên.

## Giới hạn preview

Ảnh tối đa **1280×800, 512 KiB, 5 khung/giây**, toàn vùng desktop theo tọa độ máy tính; ưu tiên thao tác cơ bản. Có gửi file thủ công hai chiều; chưa đồng bộ clipboard, chưa có âm thanh, điều khiển điện thoại từ PC hay kết nối qua Internet. Chưa gửi cả thư mục. Màn hình khóa/desktop bảo vệ và ứng dụng khác mức quyền có thể không nhận thao tác. Windows/Linux chưa ghép con trỏ hệ điều hành vào ảnh; điện thoại vẽ dấu trỏ riêng.

Các bài thử dùng giao diện Android mô phỏng, tài liệu/file giả và kết nối TLS loopback thật giữa Java/.NET, không thay cho thử trên điện thoại và máy tính vật lý đủ mọi cặp hệ điều hành. macOS cần người dùng cấp quyền khi thử thật. [Mã nguồn và phát hành của bản APK này](https://github.com/dtai52hz/lanbridge/releases/tag/android-v0.1.0-preview.2).

## Build từ nguồn

Cần JDK 17, Android SDK platform 36/build-tools 36.0.0. Trong thư mục gốc dự án:

```powershell
pwsh -File scripts/build-android.ps1 -Task Test
pwsh -File scripts/test-android-interop.ps1
pwsh -File scripts/build-android.ps1 -Task Publish
```

Gradle wrapper 8.13 có SHA-256 cố định. `Publish` ký APK bằng khóa riêng trong `.tools/android-signing` (bị loại khỏi Git), tái sử dụng khóa đó cho các lần cập nhật; cần sao lưu thư mục này ở nơi riêng. CI build APK chưa ký để kiểm tra, không chứa khóa phát hành. APK không cần .NET trên điện thoại. Ứng dụng dùng Android API/Java chuẩn; JUnit, JSON và Robolectric chỉ là phụ thuộc kiểm thử. Mã nguồn dự án theo [MIT](../LICENSE).
