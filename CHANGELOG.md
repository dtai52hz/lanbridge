# 0.3.0

- Trang Thiết bị trong mạng: tự tìm các máy đang mở LanBridge, tên/IP, trạng thái bận, làm mới và xóa máy hết hạn.
- Bấm thẻ để kết nối; máy nhận xác nhận từng yêu cầu trước khi phiên TLS bắt đầu. Không phát mã ghép nối bí mật qua UDP.
- Tách trang Phiên điều khiển và Kết nối bằng mã; tự chọn vai trò khi kết nối từ danh sách.
- Giữ quyền nhận điều khiển của máy này riêng với quyền do máy khác cấp khi đổi vai trò.
- Giữ chuyển mép ngay và Telex/VNI; bổ sung kiểm tra UDP, xác nhận TLS và giao diện danh sách.

# 0.2.0

- Chuyển chuột ngay ở mép, bỏ dwell 180 ms và cooldown 500/650 ms. Vị trí trở về A nằm phía trong viền để không bật ngược ngoài ý muốn.
- Bộ gõ tiếng Việt Telex (mặc định) và VNI trên A; gửi các chỉnh sửa Unicode NFC tới B. Giữ chế độ Phím gốc.
- Backspace theo chữ đang hiển thị; reset từ khi đổi vị trí con trỏ. Giữ phím tắt và Shift chọn vùng.
- Thêm kiểm tra bộ gõ, Unicode/scancode, giới hạn text, thứ tự phím, chuyển mép và phiên TLS.
- Cần dùng bản 0.2.0 trên cả A và B; bản cũ được từ chối bằng thông báo cập nhật.
