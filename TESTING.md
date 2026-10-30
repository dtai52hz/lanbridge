# Kiểm thử LanBridge

Chạy tất cả bằng `pwsh -File scripts/build.ps1 -Task Test` với .NET 10 SDK trên Windows.

| Bộ kiểm tra | Phạm vi |
| --- | --- |
| TransportSmoke | TLS thật trên localhost; certificate pin, sai bí mật; một kết nối; Unicode/binary; backpressure; heartbeat; gói quá lớn/JSON lỗi/cắt cụt; timeout xác thực; kết nối lại. |
| DiscoverySmoke | UDP loopback thật: query, trả lời, cập nhật trạng thái, hết hạn, giới hạn/gói lỗi, địa chỉ lấy từ nguồn gửi và không phát bí mật. TLS xác nhận: pin, đồng ý, từ chối, đang bận, hủy từ máy A, dữ liệu trước chấp nhận và hết hạn 30 giây đều dọn trạng thái. Không phát broadcast ra LAN trong kiểm tra. |
| DevicesPageSmoke | Form ẩn: danh sách thiết bị, trạng thái bận/kết nối/làm mới, metadata IP/pin mới, không thay thẻ khi chỉ heartbeat. Giao diện chính mặc định ở Thiết bị; preview không mở mạng/hook; mặc định hộp xác nhận từ chối; quyền máy khác không ghi đè quyền cục bộ. |
| FileSmoke | Tên tiếng Việt; file nhị phân, file rỗng; xác minh hash và biên byte; không ghi đè; chặn đường dẫn/tên thiết bị; từ chối khi không có quyền; xóa file dang dở. |
| ClipboardFilesSmoke | Nhiều file/thư mục tiếng Việt, thư mục trống, giữ tên và SHA-256, hai chiều, không echo, lỗi hash, hủy và thay lô, đường dẫn vượt phạm vi, giới hạn, junction thật và giữ cache đã hoàn tất. Điều tiết từng khối: không gửi tiếp trước xác nhận đúng ID/offset của khối đã ghi, hủy khi đang chờ ACK. Không đọc/ghi clipboard hệ điều hành. |
| ClipboardSessionSmoke | Hai form ẩn, TLS thật, clipboard giả riêng: text/file hai chiều, thỏa thuận phiên bản, hai quyền, chat trong lúc truyền 24 MB, clipboard bận và thử lại, không ghi đè lần sao chép mới, hủy file .partial khi ngắt, callback phiên cũ bị bỏ qua. |
| ClipboardImagesSmoke | Bộ truyền ảnh trong bộ nhớ: hai chiều, SHA-256, ACK đúng ID/offset, giới hạn 32 MiB/khối 48 KiB, gói lỗi/thiếu/lặp, hủy và thay ảnh khi đang chờ, quyền, đóng phiên và không giữ ảnh dở. |
| ClipboardImageCodecSmoke | Ảnh tổng hợp: pixel/alpha roundtrip, vòng đời bitmap/stream, PNG 8K/40 MP, giới hạn byte/kích thước, CRC, dữ liệu/trang scanline/DEFLATE hỏng, chặn kích thước giải nén trước GDI+. Không chụp màn hình hoặc truy cập clipboard thật. |
| ClipboardImageSessionSmoke | Hai form ẩn/TLS thật/clipboard giả: ảnh hai chiều, ưu tiên ảnh hơn text, không echo, chỉ quyền Clipboard, PNG lớn hơn 4 MiB, chat trong lúc truyền, retry clipboard bận, chữ/file/ảnh mới thay ảnh cũ, ngắt/reconnect và bản cũ. |
| NativeInputSmoke | Không cài hook. Tọa độ âm, ánh xạ/clamp, layout native INPUT, chuyển mép trong cùng sự kiện không dwell/cooldown, khóa đầu vào chỉ ở B, thứ tự gói nút/cuộn, Unicode UTF-16/NFC, cặp keydown/up, giới hạn gói text và giữ trạng thái phím tắt. Luồng STA riêng tiếp tục xử lý 1.500 gói khi context giao diện không chạy, hủy gói phiên cũ đang chờ và dọn luồng đúng khi đóng. |
| TextInputSmoke | Bộ gõ thuần không dùng desktop: Telex/VNI, câu tiếng Việt, vị trí dấu qu/gi/ươ/uy, chữ hoa, phím lặp, Backspace ký tự hiển thị, reset ở ranh giới con trỏ, giới hạn bộ đệm và gói JSON. |
| MediaSmoke | Không chụp màn hình hoặc dùng thiết bị âm thanh. Tạo hình tổng hợp và kiểm tra AVI RIFF, timing, index, padding, JPEG decode. |
| SessionSmoke | Hai MainForm ẩn ở chế độ preview với TLS localhost thật; hello, quyền hai phía, chat, file/ack/SHA-256, gói sửa chữ Unicode, ngắt và kết nối lại, callback phiên cũ không lọt sang phiên mới. Thu nhỏ/khôi phục lặp lại, tạm dừng giao diện và gửi hơn 2.000 gói chuột cùng ranh giới phím/chữ/nút: luồng nhập vẫn chạy, phiên giữ kết nối. Kiểm tra riêng gộp vị trí chuột nhưng giữ nguyên thứ tự ranh giới và giới hạn hàng đợi. Clipboard timer tắt; không cài hook, ghi hình, phát âm, màn che hoặc restart. |

Kiểm chứng bổ sung đã thực hiện trong lần tạo dự án: FFmpeg giải mã 8 frame và tua tới 0,5 giây của AVI tổng hợp; ảnh giao diện được xuất từ chính form ở chế độ preview. FFmpeg không phải phụ thuộc của ứng dụng phát hành.

## Thử trên hai máy thật

1. Mở bản 0.5.0 trên A/B cùng LAN, cho phép mạng Riêng tư qua tường lửa; trang Thiết bị phải tự hiện máy kia. Bấm Kết nối trên A, thử Từ chối rồi yêu cầu lại và Đồng ý trên B. Thử đóng A trong lúc chờ và để yêu cầu hết hạn; B phải trở về sẵn sàng. Thử đóng B và xác minh thẻ biến mất sau khoảng 7–9 giây, mở lại và kết nối lại. Thử Ethernet và Wi-Fi; mạng chặn discovery dùng trang Kết nối bằng mã.
2. Chọn đúng vị trí B; thử chuyển ngay sang B, trở lại A, sang B tiếp mà không đợi và hotkey Ctrl+Alt+F12. Thử trái/phải, cuộn, middle/X buttons và gõ trong Notepad trên B. Trên A chọn Telex gõ `tieengs Vieetj`, VNI gõ `tie6ng1 Vie6t5`; cả hai cần ra `tiếng Việt`. Thử Backspace, chữ hoa, Ctrl+C/V/Z, Shift+mũi tên, Shift+click và chuyển sang Phím gốc. Tắt bộ gõ ngoài nếu can thiệp; không cần UniKey B cho chế độ tích hợp.
3. Trong lúc giữ Shift hoặc nút chuột trên B, rút mạng hoặc ngắt kết nối. Kiểm tra A lấy lại chuột và B không còn phím/nút bị giữ.
4. Trên B tắt quyền Chuột/phím khi A đang điều khiển. Xác minh A nhận lại chuột và không tiếp tục thao tác được B cho tới khi B bật quyền lại.
5. Thử clipboard chữ hai chiều. Trong File Explorer trên B chọn nhiều file/thư mục có tên tiếng Việt, Ctrl+C, chờ Clipboard đã sẵn sàng rồi về A mở thư mục đích và Ctrl+V; thử chiều ngược lại, thư mục trống và file lớn. Kiểm tra không tự gửi vòng lặp, giữ nguyên tên/nội dung và không xóa nguồn. Tắt riêng từng quyền Clipboard/Truyền file và thử sao chép lại; thử sao chép chữ mới khi đang nhận, clipboard bị ứng dụng khác giữ và rút mạng giữa file. Thử Gửi file thủ công mỗi chiều để kiểm tra chức năng cũ vẫn độc lập.
6. Bật quyền âm thanh B rồi bấm Nghe B trên A; phát âm trên B, kiểm tra bật/tắt và mất mạng. Tai nghe trên A giúp tránh vọng âm qua microphone của phần mềm khác.
7. Cấp quyền ghi hình; ghi thử, dừng và mở AVI trên B. Kiểm tra chỉ báo đang ghi và đường dẫn. Bật màn che phải dừng ghi.
8. Cấp khóa đầu vào: khi A điều khiển, chuột/phím vật lý trên B bị bỏ qua, Ctrl+Alt+F12 trên B vẫn mở khóa/thu hồi quyền.
9. Cấp màn che: màn B có lớp đen, A vẫn thao tác được ứng dụng phía dưới; hotkey trên B và ngắt mạng đều bỏ lớp che. Thử từng màn hình/DPI thực tế.
10. Nếu muốn thử restart, lưu công việc B trước; cấp quyền, yêu cầu từ A rồi xác nhận hoặc từ chối trên B. Dùng `shutdown /a` nếu cần hủy đếm ngược đã xác nhận.
11. Bấm nút **−** trên A và B, tiếp tục di chuyển chuột/gõ tiếng Việt, rồi mở lại từ thanh tác vụ. Lặp lại khi đang gửi file/nghe âm thanh; phiên phải giữ kết nối và không bị dồn thao tác cũ khi mở lại. Kéo và thay đổi kích thước cửa sổ trong khi thao tác trên B. Sau đó rút mạng để kiểm tra cơ chế nhả điều khiển vẫn hoạt động.
12. Bật Clipboard, tắt Truyền file: trên B chụp vùng bằng Win+Shift+S rồi về A Ctrl+V trong Paint/Word/ứng dụng chat. Thử chiều ngược, ảnh 4K, Print Screen theo cài đặt Windows, sao chép ảnh trong trình duyệt, clipboard bị ứng dụng khác giữ, và chụp ảnh mới/sao chép chữ/file trong lúc ảnh cũ đang nhận. Tắt Clipboard phải chặn ảnh nhưng không làm ngắt phiên chuột/phím.

Các bước trên là kế hoạch xác minh thủ công; chưa được đánh dấu đạt cho hai máy thật.
