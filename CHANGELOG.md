# 0.3.1

- Sửa lỗi khựng/ngắt phiên khi giao diện bận hoặc thu nhỏ: đưa hook và xử lý chuột/phím sang luồng STA có message loop riêng, bỏ vòng chờ giao diện trên đường xử lý đầu vào.
- Gộp các vị trí chuột liên tiếp trong hàng đợi nhận; giữ nguyên phím, sửa chữ Unicode, nút, cuộn và ranh giới chuyển máy. Âm thanh nhận cũng được xử lý ngoài luồng giao diện.
- Nhả đầu vào ngay trên luồng nhập khi mất kết nối dù cửa sổ đang bận; báo nguyên nhân cụ thể nếu hàng đợi thao tác quan trọng thực sự quá tải.
- Làm mới giao diện tối, thẻ thiết bị và hộp xác nhận; giảm layout lặp khi resize, điều hướng và bố cục phiên dễ theo dõi hơn.
- Bổ sung kiểm tra hồi quy thu nhỏ/tạm dừng giao diện qua TLS thật, thứ tự gói sau gộp chuột, độc lập luồng nhập và hủy thao tác của phiên cũ.
- Script Publish đọc phiên bản từ project, tạo ZIP Windows và file SHA-256 tự động.

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
