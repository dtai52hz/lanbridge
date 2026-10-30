# 0.5.3

- Thêm **Phiên điều khiển → Đo độ trễ** và chép báo cáo tổng hợp trên A/B để tìm nguyên nhân khựng theo đợt trong phiên thật.
- Đo RTT heartbeat bằng nonce và đồng hồ cục bộ; thêm biên nhận input sau khi đi qua luồng nhập B. Giới hạn số mẫu chờ, bỏ mẫu sai/lặp/phiên cũ và thỏa thuận khả năng với peer cũ.
- Đo riêng hàng đợi gửi, hàng đợi luồng nhập, thời gian gọi SendInput, callback hook và tuổi sự kiện vật lý/input của LanBridge. Chỉ lưu số liệu tổng hợp; không ghi nội dung phím, clipboard hoặc tọa độ.
- Giữ mã đo khi gộp move vào vị trí mới nhất; thêm kiểm tra TLS với relay trễ, tương thích peer cũ, hủy phiên, gộp mẫu và các khâu chờ giả lập. Các số đo chưa xác nhận ứng dụng B đã vẽ xong.

# 0.5.2

- Sửa đường nhận chuột trên A: không gọi WarpLocal/SendInput cho từng mẫu vật lý đã bị hook giữ lại; giữ một lần đặt con trỏ khi vào/ra B. Tránh tạo thêm hàng nghìn lượt xử lý hook lúc vừa rê chuột vừa click/gõ.
- Dùng Stopwatch cho nhịp gửi vị trí, tránh bước thời gian 10–16 ms của đồng hồ TickCount64; không thay đổi độ phân giải timer hệ thống.
- Tách clipboard, file, media và thông báo giao diện sang FIFO nền có giới hạn để input không chờ thao tác clipboard/ổ đĩa/ACK. Giữ thứ tự hello/quyền, Màn che và Khóa đầu vào với các thao tác nhập tiếp theo; hủy phiên và bỏ tác vụ cũ khi thu hồi quyền.
- Thêm hồi quy đường capture 8.000 mẫu xen 240 thao tác và tương tác liên tục qua TLS khi clipboard/UI bận; thêm công cụ đo riêng latency transport và mô phỏng TCP chậm.

# 0.5.1

- Giảm lag khi vừa di chuyển vừa cuộn: điều tiết vị trí mới nhất theo nhịp danh nghĩa 8 ms và gộp move chưa gửi liền nhau trước mã hóa, tránh dồn mẫu từ chuột có polling cao. Lần đầu/chuyển mép vẫn tức thì.
- Gói input JSON gọn, giữ giao thức hiện tại và ghi cả frame TLS một lần; không bỏ các quyền false hoặc gộp qua ranh giới thao tác.
- Máy B xử lý theo nhóm tối đa 32 gói trên luồng nhập; click/cuộn cùng tọa độ được đưa vào một batch SendInput. Giữ đủ từng delta cuộn, chiều, vị trí và thứ tự với phím/chữ/kéo thả.
- Thêm kiểm tra sender chậm, hủy giữa nhóm, lỗi phiên cũ và hồi quy hai form/TLS với 1.000/8.000 mẫu chuột xen cuộn. Kịch bản từng làm 0.5.0 ngắt phiên nay giữ kết nối; chưa đo trên hai PC vật lý.

# 0.5.0

- Thêm chia sẻ ảnh chụp/bitmap trong clipboard hai chiều: Win+Shift+S/Print Screen trên B rồi Ctrl+V vào ứng dụng hỗ trợ ảnh trên A, không cần lưu file hay bấm Gửi file.
- Ảnh dùng PNG truyền từng khối có ACK và SHA-256; giới hạn 32 MiB, 16.384 pixel mỗi chiều, tổng 40 MP; kiểm tra PNG và giải nén có giới hạn trước khi giải mã.
- Ảnh chỉ cần quyền Clipboard, không phụ thuộc quyền Truyền file. Bổ sung thỏa thuận khả năng với máy kia và nhắc cập nhật bản cũ.
- Giữ nguyên nội dung mới khi ảnh/file/chữ thay nhau, thử lại clipboard bận và chặn callback phiên cũ; giữ tính năng file, gõ tiếng Việt và thu nhỏ từ các bản trước.
- Bổ sung kiểm tra bộ truyền ảnh, codec PNG và hai form/TLS với clipboard giả; không tác động clipboard thật khi kiểm tra.

# 0.4.0

- Thêm Ctrl+C file/thư mục trong File Explorer rồi Ctrl+V trên máy kia, cả hai chiều; tự truyền từ clipboard, không cần chọn Gửi file.
- Giữ tên, cây thư mục, thư mục trống và lựa chọn nhiều mục; chỉ đưa đường dẫn bản nhận vào clipboard sau khi toàn bộ lô đã xác minh SHA-256. File lớn cần chờ truyền xong trước khi dán.
- Yêu cầu cả quyền Clipboard và Truyền file, thỏa thuận khả năng với máy kia; hủy lô cũ khi sao chép mới, ngắt hoặc thu hồi quyền, không gửi lặp bản nhận về nguồn.
- Dùng thông báo clipboard của Windows và kiểm tra sequence; thử lại khi clipboard bận, không ghi đè nội dung người dùng vừa sao chép. Không chặn chuột/phím trong lúc truyền.
- Thêm kiểm tra clipboard file riêng và tích hợp hai MainForm/TLS với clipboard giả; giữ bộ kiểm tra ngắt/thu nhỏ trước đó.

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
