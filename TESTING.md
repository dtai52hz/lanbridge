# Kiểm thử LanBridge

Chạy tất cả bằng `pwsh -File scripts/build.ps1 -Task Test` với .NET 10 SDK trên Windows.

## Bộ cài Windows 0.7.0

```powershell
pwsh -File scripts/setup-installer-tools.ps1
pwsh -File scripts/build.ps1 -Task Installer
pwsh -File scripts/test-installer.ps1
```

Phép thử installer dùng một thư mục mới bên trong `.tools`, truyền `/PACKAGETEST=1` cho Setup để tắt ghi registry/shortcut và không dùng đường dẫn cài trước đó. So sánh SHA-256 từng file với payload đã Publish, mở EXE đã cài bằng `--preview`, rồi gọi chính uninstaller của bản thử. Chỉ gỡ thư mục cài riêng; giữ log/ảnh/kết quả JSON để xem lại.

Kiểm tra trước/sau bao gồm registry uninstall, shortcut và thiết lập thật, các tiến trình LanBridge đã mở và file dữ liệu nằm ngoài thư mục cài. Không khởi chạy hook, mạng, clipboard thật hoặc thay bản đang dùng. Phép thử này xác minh chép file, chạy preview và gỡ file; đăng ký Installed apps/shortcut của chế độ cài bình thường cần thử thủ công trên Windows.

Script Inno Setup yêu cầu Windows 10 build 17763 trở lên và x64 tương thích, cài vào tài khoản hiện tại, không tự đóng ứng dụng đang chạy. Cả ZIP lẫn Setup có MIT LICENSE và giấy phép/thông báo runtime, NAudio, Inno Setup. Cách đóng gói được giới thiệu ở 0.6.1 và tiếp tục áp dụng cho bản 0.7.0.

## Bộ kiểm tra ứng dụng

| Bộ kiểm tra | Phạm vi |
| --- | --- |
| TransportSmoke | TLS thật trên localhost; certificate pin, sai bí mật; một kết nối; Unicode/binary; backpressure; heartbeat; gói quá lớn/JSON lỗi/cắt cụt; timeout xác thực; kết nối lại. Tạm dừng writer, gửi 80.000 mẫu chuột rồi kiểm tra gộp trước mã hóa, giữ ranh giới cuộn/phím/nút/chữ/quyền, snapshot dữ liệu và JSON input gọn tương thích cả giá trị 0/false. Quyền false vẫn được gửi đầy đủ. |
| DiscoverySmoke | UDP loopback thật: query, trả lời, cập nhật trạng thái, hết hạn, giới hạn/gói lỗi, địa chỉ lấy từ nguồn gửi và không phát bí mật. TLS xác nhận: pin, đồng ý, từ chối, đang bận, hủy từ máy A, dữ liệu trước chấp nhận và hết hạn 30 giây đều dọn trạng thái. Không phát broadcast ra LAN trong kiểm tra. |
| DevicesPageSmoke | Form ẩn: danh sách thiết bị, trạng thái bận/kết nối/làm mới, metadata IP/pin mới, không thay thẻ khi chỉ heartbeat. Giao diện chính mặc định ở Thiết bị; preview không mở mạng/hook; mặc định hộp xác nhận từ chối; quyền máy khác không ghi đè quyền cục bộ. Hộp ghi phím nhận phím đơn/tổ hợp, Esc/Tab/Enter, bỏ gán, từ chối tổ hợp dành riêng và giữ key-up/auto-repeat để không tự kích hoạt nút. |
| PreferencesSmoke | File JSON trong thư mục tạm: mọi vị trí/bộ gõ, tốc độ 50–200%, tùy chọn khay và phím chuyển; giới hạn kích thước/độ sâu/schema, dữ liệu hỏng từng trường và khóa file. Phím chuyển hợp lệ roundtrip, file cũ mặc định chưa gán, shortcut hỏng chỉ reset trường đó. Ghi nguyên file, giữ bản cũ nếu không ghi được, xóa temp và chỉ lưu trường tiện dùng. |
| DesktopSmoke | MainForm ẩn: preview tách khỏi thiết lập thật/khay thật; khôi phục lựa chọn vào UI và input router, 80 lần ghi chồng nhau giữ bản cuối, thu nhỏ tùy chọn/ẩn/mở lại/chuẩn bị hộp xác nhận và đóng sạch. Gán/lưu/mở lại/bỏ gán phím chuyển, từ chối phím sai và lưu thiết lập trong lúc router tạm ngưng phím để ghi lựa chọn mới. |
| FileSmoke | Tên tiếng Việt; file nhị phân, file rỗng; xác minh hash và biên byte; không ghi đè; chặn đường dẫn/tên thiết bị; từ chối khi không có quyền; xóa file dang dở. |
| ClipboardFilesSmoke | Nhiều file/thư mục tiếng Việt, thư mục trống, giữ tên và SHA-256, hai chiều, không echo, lỗi hash, hủy và thay lô, đường dẫn vượt phạm vi, giới hạn, junction thật và giữ cache đã hoàn tất. Điều tiết từng khối: không gửi tiếp trước xác nhận đúng ID/offset của khối đã ghi, hủy khi đang chờ ACK. Không đọc/ghi clipboard hệ điều hành. |
| ClipboardSessionSmoke | Hai form ẩn, TLS thật, clipboard giả riêng: text/file hai chiều, thỏa thuận phiên bản, hai quyền, chat trong lúc truyền 24 MB, clipboard bận và thử lại, không ghi đè lần sao chép mới, hủy file .partial khi ngắt, callback phiên cũ bị bỏ qua. |
| ClipboardImagesSmoke | Bộ truyền ảnh trong bộ nhớ: hai chiều, SHA-256, ACK đúng ID/offset, giới hạn 32 MiB/khối 48 KiB, gói lỗi/thiếu/lặp, hủy và thay ảnh khi đang chờ, quyền, đóng phiên và không giữ ảnh dở. |
| ClipboardImageCodecSmoke | Ảnh tổng hợp: pixel/alpha roundtrip, vòng đời bitmap/stream, PNG 8K/40 MP, giới hạn byte/kích thước, CRC, dữ liệu/trang scanline/DEFLATE hỏng, chặn kích thước giải nén trước GDI+. Không chụp màn hình hoặc truy cập clipboard thật. |
| ClipboardImageSessionSmoke | Hai form ẩn/TLS thật/clipboard giả: ảnh hai chiều, ưu tiên ảnh hơn text, không echo, chỉ quyền Clipboard, PNG lớn hơn 4 MiB, chat trong lúc truyền, retry clipboard bận, chữ/file/ảnh mới thay ảnh cũ, ngắt/reconnect và bản cũ. |
| NativeInputSmoke | Không cài hook hoặc gọi SendInput thật. Tọa độ âm, ánh xạ/clamp, layout native INPUT, chuyển mép trong cùng sự kiện không dwell/cooldown, khóa đầu vào chỉ ở B, thứ tự gói nút/cuộn, Unicode UTF-16/NFC, cặp keydown/up, giới hạn text và giữ phím tắt. Pacing vị trí mới nhất theo 8 ms với đồng hồ giả, vị trí cuối khi dừng, lần đầu/chuyển mép tức thì; sender chậm giữ đủ ranh giới. Cuộn dọc/ngang giữ tọa độ và delta trong một batch native. Luồng STA riêng, nhóm tối đa 32 gói, hủy giữa nhóm, callback lỗi phiên cũ không ảnh hưởng phiên mới và dọn luồng khi đóng. |
| TextInputSmoke | Bộ gõ thuần không dùng desktop: Telex/VNI, câu tiếng Việt, vị trí dấu qu/gi/ươ/uy, chữ hoa, phím lặp, Backspace ký tự hiển thị, reset ở ranh giới con trỏ, giới hạn bộ đệm và gói JSON. |
| MediaSmoke | Không chụp màn hình hoặc dùng thiết bị âm thanh. Tạo hình tổng hợp và kiểm tra AVI RIFF, timing, index, padding, JPEG decode. |
| SessionSmoke | Hai MainForm ẩn ở chế độ preview với TLS localhost thật; hello, quyền hai phía, chat, file/ack/SHA-256, gói sửa chữ Unicode, ngắt và kết nối lại, callback phiên cũ không lọt sang phiên mới. Thu nhỏ/khôi phục, tạm dừng giao diện và gửi hơn 2.000 gói chuột. Tạm dừng sender 100 ms và luồng nhận 120 ms; gửi 1.000/8.000 mẫu chuột cùng 32 lần cuộn, phím sửa đổi, kéo thả, chữ và chuyển máy. So sánh chính xác delta dọc/ngang, tọa độ và thứ tự; kiểm tra xử lý theo nhóm, giới hạn hàng đợi và giữ phiên. Clipboard timer tắt; không cài hook, gọi SendInput, ghi hình, phát âm, màn che hoặc restart. |

Kiểm chứng bổ sung đã thực hiện trong lần tạo dự án: FFmpeg giải mã 8 frame và tua tới 0,5 giây của AVI tổng hợp; ảnh giao diện được xuất từ chính form ở chế độ preview. FFmpeg không phải phụ thuộc của ứng dụng phát hành.

Hồi quy di chuyển/cuộn ở 0.5.1 đã được chạy đối chiếu mã 0.5.0 trong thư mục riêng: trường hợp 1.000 mẫu tạo 1.041 gói và đóng cả hai đầu TLS khi luồng nhập đang tạm dừng. Bản sửa giữ kết nối, còn 73 gói và 9 lần chuyển sang luồng nhập cho cả trường hợp 1.000 và 8.000 mẫu. Các số này mô tả tải tổng hợp và cách gộp, không phải số đo độ trễ trên LAN thật.

Hồi quy tương tác 0.5.2 đi qua phương thức nhận mẫu chuột của InputRouter, xen 8.000 mẫu với click/phím/cuộn: không yêu cầu lệnh native nào trong giai đoạn rê chuột, nhưng vẫn yêu cầu đúng một lần đặt con trỏ khi vào/ra máy B. Kiểm tra `Stopwatch` cho nhịp gửi dưới bước đồng hồ Windows cũ. Hai form ẩn gửi liên tục move/click/phím qua TLS, chèn clipboard trong lúc UI nhận dừng 900 ms: thao tác nhập phải hoàn tất trước khi UI chạy lại, không mất ranh giới quan trọng. Mã 0.5.1 tái hiện p95 khoảng 923 ms trong kịch bản clipboard/UI này; thời gian chỉ là phép đo của một lần chạy tổng hợp.

Cùng kịch bản tương tác được thử khi khóa nhận file đang bị giữ để mô phỏng I/O chậm. Có kiểm tra bỏ clipboard cũ đã xếp hàng trước khi B thu hồi rồi cấp lại quyền, và chấp nhận dữ liệu A nhận ngay sau gói cấp quyền mới của B. Các dịch vụ file được giữ theo phiên riêng; thư mục nhận trong kiểm tra là thư mục tạm. Hàng đợi chính giới hạn 512 gói/32 MiB, hàng đợi nền 128 gói/16 MiB; giới hạn byte được kiểm tra cả khi thay vị trí chuột liền nhau.

Lệnh tắt ghi hình đang chờ sau file I/O phải còn hiệu lực sau khi một quyền khác bị thu hồi; kiểm tra chỉ đặt cờ trạng thái giả, không khởi tạo recorder. Cả bật và tắt Màn che/Khóa đầu vào vẫn đi qua ranh giới có thứ tự với input: kiểm tra phân luồng Màn che bằng helper dùng trong production, và gửi lệnh khóa cùng phím qua TLS khi UI dừng để xác minh phím không vượt lệnh khóa. Không bật màn che thật hoặc chặn chuột/phím vật lý.

Đo bổ sung phần truyền độc lập (không hook/SendInput/clipboard thật):

```powershell
& .\.tools\dotnet\dotnet.exe run --project tests\TransportSmoke -c Release -- --latency-only
& .\.tools\dotnet\dotnet.exe run --project tests\TransportSmoke -c Release -- --latency-shaped
```

`--latency-only` in p50/p95/p99 từ enqueue tới callback nhận TLS localhost, khi chỉ có input, kèm move và kèm bulk 48 KiB có ACK. `--latency-shaped` đặt relay TCP loopback giới hạn tốc độ để so sánh bulk có/không ACK. Hai chế độ không thuộc bộ Test mặc định và không đo độ trễ vật lý giữa hai PC. Truyền file thủ công hiện chưa có ACK từng khối như clipboard; khi mạng chậm, dữ liệu trên cùng TCP có thể làm tăng thời gian chờ input.

Số đo 0.5.3 trong bộ Test mặc định:

- TransportSmoke: RTT với nonce/monotonic clock, pong sai/lặp, observer lỗi, peer cũ không echo vẫn giữ phiên và RTT rỗng. Relay TLS trễ 300 ms mỗi chiều phải làm RTT tăng rồi hồi phục sau khi bỏ trễ. Gói input thường không thêm metadata; Id/Length được giữ khi có mẫu, kể cả qua gộp move.
- NativeInputSmoke: chủ động giữ dispatcher, sender và native observer trong 120 ms để xác minh từng bộ đếm; snapshot vẫn đọc được khi owner bận. Kiểm tra callback lồng nhau, lỗi native, dấu thời gian DWORD vòng và phân biệt physical/own-injected/foreign-injected. Không cài hook hoặc gọi SendInput thật.
- SessionSmoke: gói mẫu đi qua sender thật của InputRouter và TLS; B giữ pump không được ACK sớm, thả pump khi UI vẫn dừng phải trả ACK. Mẫu sai/lặp/phiên đã đóng bị bỏ, gộp move giữ tọa độ mới nhất và mã đo, báo cáo không chứa marker nội dung phím.

Hồi quy 0.5.4 cho khâu nhận đầu vào:

- NativeInputSmoke: hook/state owner vẫn xử lý công việc khi worker native bị giữ; một nhóm nhận được ghép thành một mảng native theo thứ tự. Kiểm tra hủy, thu hồi quyền, nhả phím/nút, lỗi native và chuyển vai trò; không gọi SendInput thật.
- SessionSmoke: gửi liên tục move/phím/click/cuộn/chữ qua TLS trong lúc mỗi lời gọi native giả mất 35 ms. Giữ vị trí cuối, từng ranh giới quan trọng và phiên kết nối. Trong lần đối chiếu, mã 0.5.3 tạo 47 lời gọi, vị trí cuối chờ 1.239,7 ms; mã sửa tạo 19 lời gọi, chờ 84,2 ms với khoảng 761 ms phát liên tục. Các số này phụ thuộc lịch chạy của máy kiểm tra và không phải độ trễ mạng LAN thật.
- Giữ native call trong lúc kiểm tra ACK: hook owner và heartbeat vẫn chạy, chưa được ACK trước khi thả native. Thu hồi quyền lúc đang giữ phải cập nhật trạng thái ngay, sau đó nhả phím qua worker, không ACK công việc cũ; cấp quyền lại phải nhận được phím mới trên cùng phiên.

Toàn bộ 13 bộ kiểm tra đạt cho 0.5.4, gồm 1.608 assertions NativeInputSmoke và 380 assertions SessionSmoke. Lần chạy tổng ghi nhận 19 native jobs và tuổi vị trí cuối 75,8 ms trong kịch bản 35 ms giả lập; đây vẫn là phép thử tổng hợp.

Hồi quy tiện dùng 0.6.0:

- Kiểm tra tốc độ 100% giữ chuyển động cũ, 50% cộng dồn phần lẻ, 200% giữ chiều/clamp, đổi 175% trong lúc giữ Ctrl/kéo chuột không nhả phím hoặc thêm warp. Các mép vẫn chuyển tức thì; delta cuộn giữ nguyên.
- Sơ đồ thử đủ bốn vị trí bằng chuột/phím, đồng bộ với danh sách, bỏ thao tác khi bị vô hiệu hóa và xuất bitmap ở nhiều kích thước. Kiểm tra vai trò B không sửa cấu hình chuột của A.
- SessionSmoke ẩn cả hai form rồi gửi input, chat, clipboard chữ giả hai chiều và file kiểm SHA-256; phiên vẫn hoạt động, mở lại chỉ đổi trạng thái cửa sổ. Không tạo NotifyIcon thật hoặc thay clipboard hệ điều hành.

Toàn bộ 15 bộ kiểm tra đạt cho 0.6.0: DevicesPageSmoke 81, PreferencesSmoke 305, DesktopSmoke 30, NativeInputSmoke 1.636 và SessionSmoke 398 assertions. Hồi quy native giả chậm 35 ms vẫn giữ kết nối và luồng hook rảnh; lần chạy tổng ghi nhận 20 native jobs, vị trí cuối 60,2 ms. Đã xuất và xem bố cục phiên ở 1120×860 và 1060×780; DrawToBitmap có hạn chế với phần native của ComboBox/RichTextBox, nên hiển thị thực trên desktop vẫn cần kiểm tra thủ công.

Hồi quy phím chuyển 0.7.0:

Toàn bộ 15 bộ kiểm tra đạt; DesktopSmoke được chạy lại sau bản sửa cuối của hộp gán phím.

- NativeInputSmoke: 1.773 assertions; phím đơn/tổ hợp chuyển từ con trỏ bất kỳ sang giữa B rồi về vị trí A trước đó; chống auto-repeat, giữ đủ thứ tự nhả phím và modifier, không gửi phím chuyển vào ứng dụng B. Kiểm tra quyền/vai trò, mất phiên, phím thoát Ctrl+Alt+F12, chặn chuyển qua mép trong lúc ghi phím và khôi phục khi hủy/bỏ gán, cùng các hồi quy chuyển mép và bộ gõ cũ. Lời gọi native được thay bằng observer; không cài hook hoặc gọi SendInput thật.
- PreferencesSmoke: 487 assertions, trong đó phím đơn/tổ hợp được lưu bằng key/modifiers có kiểm tra; phím không hợp lệ hoặc JSON hỏng không làm mất các lựa chọn khác. File schema 1 trước 0.7.0 vẫn đọc được và mặc định không gán phím.
- DesktopSmoke: 70 assertions với form ẩn, kiểm tra F8/Ctrl+Alt+S, nhãn phím, lưu/mở lại, bỏ gán và từ chối giá trị sai. Tạm ngưng router để ghi phím rồi chụp/lưu thiết lập phải giữ giá trị đã cấu hình. Không tạo biểu tượng khay thật hoặc ghi AppData thật.
- DevicesPageSmoke: 118 assertions, gồm ghi phím bên trong hộp thoại mà không mở cửa sổ tương tác thật; giữ Enter/Space qua key-up để tránh tự nhấn Lưu sau khi ghi, cho phép gán Esc/Tab và bảo vệ tổ hợp dành riêng.
- SessionSmoke: 443 assertions; phím F9 và Ctrl+Alt+Space chạy qua sender thật, TLS localhost và worker B khi hai form ẩn trong trạng thái khay. Xác minh tọa độ giữa B, không chuyển lặp, không rò phím kích hoạt, nhả đủ phím/modifier trước leave, thu hồi/cấp lại quyền và tạm ngưng để ghi phím mà giữ nguyên phiên/cấu hình. Không cài hook hoặc gọi SendInput thật.
- Render ẩn ở kích thước mặc định/tối thiểu và hộp gán phím đã được xem trực tiếp. Tùy chọn `LANBRIDGE_UI_PREVIEW_DIR` trỏ tới thư mục con trong `.tools` xuất bốn ảnh và thêm bốn assertions (DevicesPageSmoke 122); kiểm tra thanh cuộn xuất hiện và cuộn tới đủ ô chat ở kích thước tối thiểu. Không Show/kích hoạt cửa sổ; phần native ComboBox/RichTextBox vẫn có hạn chế trong DrawToBitmap.

## Thử trên hai máy thật

1. Mở bản 0.7.0 trên A/B cùng LAN, cho phép mạng Riêng tư qua tường lửa; trang Thiết bị phải tự hiện máy kia. Bấm Kết nối trên A, thử Từ chối rồi yêu cầu lại và Đồng ý trên B. Thử đóng A trong lúc chờ và để yêu cầu hết hạn; B phải trở về sẵn sàng. Thử đóng B và xác minh thẻ biến mất sau khoảng 7–9 giây, mở lại và kết nối lại. Thử Ethernet và Wi-Fi; mạng chặn discovery dùng trang Kết nối bằng mã.
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
13. Mở trang dài trên B, vừa rê chuột qua các vùng khác nhau vừa cuộn dọc/ngang, đổi chiều, giữ Ctrl/Shift và kéo thanh cuộn. Thử các mức polling chuột đang có (125/500/1.000/8.000 Hz), lúc gửi file/ảnh và thu nhỏ ứng dụng. Kiểm tra không mất nấc, cuộn sai vị trí, dồn thao tác cũ hoặc tự ngắt; so sánh Ethernet/Wi-Fi và ghi lại tải máy nếu còn giật.
14. Trên A/B mở Đo độ trễ, thao tác 15–20 giây tới khi tái hiện khựng, tạm dừng hiển thị rồi chép hai báo cáo. So sánh RTT với chờ gửi trên A, chờ pump/SendInput/hook-age trên B; giữ cả tuổi mẫu và số mẫu. Thử lại cả hai bằng dây LAN nếu có sẵn để đối chiếu đường Wi-Fi. RTT thấp hoặc SendInput gọi nhanh chưa xác nhận target app trên B đã nhận/vẽ xong.
15. Chọn vị trí B bằng sơ đồ và danh sách, đổi tốc độ 50/100/200% khi đang rê/kéo/cuộn, đổi bộ gõ rồi đóng/mở lại. Các lựa chọn phải được nhớ; ghép nối và quyền vẫn đi theo phiên. Bật/tắt Thu nhỏ xuống khay, thử nút −, nút Ẩn xuống khay, bấm đúp/chuột phải biểu tượng, mở lại EXE khi đang ẩn và lệnh Thoát. Khi B đang ẩn, A yêu cầu kết nối: cửa sổ B phải hiện lại để người dùng Đồng ý/Từ chối; thử cả timeout. Nút × vẫn thoát dù tùy chọn khay đã bật.
16. Trên A mở **Phiên điều khiển → Gán phím chuyển A ⇄ B… → Bấm để gán phím**, nhấn F9 rồi **Lưu**. Đặt con trỏ giữa A, nhấn F9: phải sang giữa B; nhấn lại phải về vị trí trước đó trên A. Giữ F9 không được chuyển lặp. Thử Ctrl+Alt+Space, Ctrl/Alt trái/phải, phím đơn, Esc/Tab/Enter, đang Telex và đang giữ nút chuột. Kiểm tra không kẹt modifier hoặc gõ phím chuyển vào B. Thử Hủy và Bỏ gán/Lưu, đóng/mở lại, chạy ở khay và chọn lại vai trò. Ctrl+Alt+F12 vẫn phải thoát khẩn cấp; B thu hồi quyền thì phím chuyển không được tiếp tục điều khiển.
17. Thử A 0.7.0 với B 0.6.1: discovery, B Đồng ý, gán và bấm phím chuyển hai chiều, chuyển mép, thu hồi quyền và ngắt phiên. Đây là thay đổi xử lý ở A, không thêm loại gói giao thức; cập nhật B không phải điều kiện để dùng phím chuyển.

Các bước trên là kế hoạch xác minh thủ công; chưa được đánh dấu đạt cho hai máy thật.
