# LanBridge 0.5.1

Ứng dụng Windows cho hai máy tính chung mạng LAN: đưa chuột qua mép màn hình A để dùng chuột và bàn phím của A trên máy B. Mỗi máy tiếp tục dùng màn hình riêng. Đây là bản thử nghiệm có mã nguồn và bản chạy độc lập cho Windows 10/11 x64.

[Tải bản Windows 0.5.1](https://github.com/dtai52hz/lanbridge/releases/tag/v0.5.1). Đăng nhập tài khoản được cấp quyền vào repository private để tải gói ZIP, rồi giải nén trên cả hai máy.

![Giao diện LanBridge 0.3.1 với thiết bị minh họa](docs/screenshots/devices-0.3.1.png)

## Chạy trên hai máy

1. Chép `release/LanBridge-0.5.1-win-x64/LanBridge.exe` sang **cả hai máy** rồi mở. Không cần cài .NET để dùng bản phát hành này.
2. Ứng dụng mở trang **Thiết bị trong mạng**, tự tìm các máy cũng đang mở LanBridge 0.5.1 trong cùng mạng LAN. Nếu Windows hỏi tường lửa, cho phép ứng dụng trên mạng **Riêng tư / Private**.
3. Trên máy có chuột/bàn phím chính (**A**), tìm tên và IP của **B** rồi bấm **Kết nối** ở thẻ máy đó. Không cần chọn vai trò A/B, nhập IP hoặc chép mã trong luồng này.
4. Trên **B**, hộp **Yêu cầu kết nối** hiện tên và IP của A. Bấm **Đồng ý** để nhận điều khiển, hoặc **Từ chối**. Mỗi yêu cầu tìm qua danh sách đều cần xác nhận tại B; yêu cầu hết hạn sau 30 giây.
5. Ứng dụng chuyển sang **Phiên điều khiển**. Trên A chọn B nằm bên phải/trái/trên/dưới A và bộ gõ Telex/VNI/Phím gốc. Trên B bật/tắt quyền cần cấp.
6. Đưa chuột tới mép đã chọn: **chuyển ngay, không chờ**. Ví dụ B nằm bên phải A: chạm mép phải A để sang B; đưa chuột qua mép trái B để quay lại A.
7. **Ctrl + Alt + F12** trên A trả chuột/phím về A. Trên B, tổ hợp này còn bỏ màn che, tắt khóa đầu vào và thu hồi quyền chuột/phím. **Ngắt kết nối** kết thúc phiên và trở về danh sách thiết bị.

**Chỉ các máy đang mở LanBridge mới hiện trong danh sách.** Cài/chạy file một lần rồi đóng ứng dụng không làm máy luôn xuất hiện. Máy đã đóng/mất mạng sẽ được loại khỏi danh sách sau khoảng 7–9 giây. Ứng dụng không tự cài dịch vụ hay tự mở cùng Windows; sau khi khởi động lại B cần mở lại LanBridge.

Tên máy, cổng kết nối, trạng thái bận và dấu vân tay chứng chỉ được thông báo qua UDP trong LAN; mã ghép nối bí mật không được phát qua UDP. Máy nhận xác nhận từng yêu cầu trước khi cho phép phiên TLS. Dấu vân tay được kiểm tra theo thiết bị vừa phát hiện; danh sách LAN không phải danh bạ tài khoản đã xác minh.

## Kết nối bằng mã khi cần

Nếu mạng chặn tìm thiết bị, vẫn có trang **Kết nối bằng mã**:

1. Trên B chọn **Máy B · Nhận điều khiển**, chọn IP mạng đang dùng, bấm **Bật nhận kết nối → Sao chép mã**.
2. Trên A chọn **Máy A · Chuột và bàn phím chính**, dán mã và bấm **Kết nối máy B**.
3. Mã là thông tin cấp quyền trực tiếp, chỉ gửi cho máy A bạn muốn cho kết nối. Nó có hiệu lực tới khi đóng LanBridge trên B. Luồng có mã không hiện lại hộp hỏi như luồng tìm thiết bị.

Ngắt phiên không tắt bộ tìm máy; đóng ứng dụng để máy ngừng quảng bá và nhận kết nối.

## Thu nhỏ cửa sổ

Nút **−** chỉ thu nhỏ cửa sổ; phiên kết nối, chuột/phím và âm thanh vẫn chạy. Mở lại LanBridge từ thanh tác vụ để xem hoặc thay đổi phiên. Nút **×** đóng ứng dụng và kết thúc kết nối.

Từ 0.3.1, chuột/phím chạy trên một luồng xử lý riêng, không chờ thao tác vẽ, kéo hoặc thu nhỏ cửa sổ. Các vị trí chuột liên tiếp được gộp khi có dữ liệu dồn, giữ vị trí mới nhất trước mỗi phím/nút/cuộn hoặc chuyển máy. Mất mạng vẫn tự nhả điều khiển. Nếu đang dùng bản cũ, đóng LanBridge rồi thay bản 0.5.1 trên cả A và B.

## Di chuyển và cuộn chuột cùng lúc

Bản 0.5.1 giảm dồn thao tác khi chuột có tần số lấy mẫu cao: gửi vị trí mới nhất theo nhịp danh nghĩa 8 ms, gộp các vị trí chưa gửi liền nhau trước khi mã hóa và dùng một lần ghi TLS cho mỗi gói. Lần di chuyển đầu tiên, chuyển mép và thao tác phím/nút/cuộn được xử lý ngay; không thêm thời gian chờ ở mép.

Trên B, các gói nhập được xử lý theo nhóm tối đa 32 gói trên luồng riêng. Mỗi nấc cuộn vẫn giữ đủ giá trị, chiều, tọa độ và thứ tự với phím/nút; không cộng dồn cuộn qua vị trí khác. Cập nhật cả A và B để dùng đầy đủ thay đổi. Độ mượt thực tế còn phụ thuộc mạng và tải máy.

## Ctrl+C / Ctrl+V file giữa hai máy

1. Dùng **LanBridge 0.5.1 trên cả A và B**, kết nối như thường. Trên B bật cả **Clipboard chữ/ảnh/file** và **Truyền file**.
2. Trên B, mở File Explorer, chọn một hoặc nhiều file/thư mục rồi nhấn **Ctrl+C**. Có thể làm khi đang điều khiển B bằng chuột/phím của A.
3. LanBridge tự truyền bản sao qua phiên TLS, kiểm tra đủ dữ liệu và SHA-256. Nhật ký hiện **Clipboard đã sẵn sàng** khi toàn bộ lựa chọn đã nhận xong.
4. Chuyển chuột về A, mở thư mục đích trong File Explorer rồi **Ctrl+V**. Không cần mở hộp **Gửi file**. Chiều A → B dùng cùng cách.

File được truyền ngay khi sao chép, trước lúc dán. **File lớn cần chờ nhận xong rồi mới Ctrl+V**; nhấn dán trong lúc đang truyền chưa tạo được file ở thư mục đích. Giữ nguyên tên file, cây thư mục và thư mục trống. Hỗ trợ tối đa 2 GB/file, 8 GB/lần, 10.000 mục, 64 cấp thư mục; không sao chép symlink/junction, mục gốc trùng tên hoặc đường dẫn đặc biệt của Windows.

Bản nhận nằm trong bộ nhớ đệm riêng `%LOCALAPPDATA%\LanBridge\Clipboard`; Ctrl+V để File Explorer chép vào nơi bạn chọn. Bộ nhớ đệm đã nhận đủ được giữ khi ngắt kết nối/đóng ứng dụng để clipboard còn dùng được, và dọn các lô cũ hơn 30 ngày khi mở phiên mới. Lô chưa hoàn tất bị hủy khi mất mạng, thu hồi quyền hoặc sao chép nội dung mới. File nguồn không bị xóa; đây là sao chép, không phải Ctrl+X/di chuyển. File ảo từ ứng dụng như Outlook chưa được hỗ trợ.

## Dùng chung ảnh chụp màn hình

1. Cập nhật **LanBridge 0.5.1 trên cả A và B**, kết nối và bật **Clipboard chữ/ảnh/file** trên B. Chia sẻ ảnh không cần bật quyền Truyền file.
2. Trên B nhấn **Win+Shift+S** và chọn vùng chụp, hoặc dùng **Print Screen** theo cài đặt Windows. Công cụ chụp cần đưa ảnh vào clipboard.
3. Chờ báo **Ảnh chụp đã sẵn sàng**, chuyển về A và **Ctrl+V** trong Paint, Word hoặc ứng dụng chat hỗ trợ dán ảnh. Chiều A → B dùng cùng cách.

Ảnh được chuyển dưới dạng PNG rồi đặt vào clipboard của Windows như một ảnh; không cần lưu ảnh ra file hoặc bấm Gửi file. Ảnh lớn được truyền từng phần, tối đa 32 MiB PNG, 16.384 pixel mỗi chiều và tổng 40 triệu pixel. Bản nhận chỉ được đưa vào clipboard sau khi đủ dữ liệu, đúng SHA-256 và PNG hợp lệ. Nếu sao chép chữ/file/ảnh mới, tác vụ cũ được hủy và không ghi đè nội dung mới.

Ảnh trong clipboard dùng để dán vào ứng dụng hỗ trợ ảnh. File Explorer không tự lưu clipboard ảnh thành file PNG; muốn lưu thành file có thể dán vào Paint rồi lưu. Chức năng này chia sẻ ảnh bạn đã chụp, không tự chụp màn hình B liên tục.

## Gõ tiếng Việt

Trên A, trang **Phiên điều khiển** có ô **Gõ** cạnh vị trí màn hình B:

- **Tiếng Việt Telex** (mặc định): `tieengs Vieetj` → `tiếng Việt`, `ddieeuf khieenr` → `điều khiển`. Các phím dấu `s/f/r/x/j`, `aa/aw/ee/oo/ow/uw/dd` được xử lý ngay trong LanBridge.
- **Tiếng Việt VNI**: `tie6ng1 Vie6t5` → `tiếng Việt`.
- **Phím gốc**: gửi nguyên phím, phù hợp khi gõ mã, mật khẩu hoặc muốn dùng bộ gõ được cấu hình riêng trên B.

Bộ gõ Telex/VNI tích hợp gửi chữ Unicode có dấu trực tiếp, không phụ thuộc clipboard và không cần UniKey/EVKey trên B. Nếu UniKey/EVKey đang can thiệp và chữ bị sai/lặp, chuyển bộ gõ ngoài sang **E** hoặc tắt nó trong lúc thử chế độ tích hợp, nhất là khi vừa mở lại bộ gõ ngoài sau LanBridge. Chế độ tích hợp là bộ gõ cơ bản, chưa có từ điển, tự sửa chính tả hay viết tắt của UniKey.

Ctrl/Alt/Windows và phím điều hướng tiếp tục chuyển theo dạng phím; Shift chọn vùng và Shift+click được giữ. Bấm chuột, chuyển con trỏ văn bản hoặc dùng tổ hợp phím sẽ kết thúc từ đang ghép dấu để tránh sửa nhầm vị trí mới. Backspace xóa ký tự đang hiển thị.

## Quyền và chức năng

Các ô quyền ở trang **Phiên điều khiển** được thay đổi **trên máy B** và có hiệu lực trong phiên. Trên A, các ô này hiển thị quyền B đã cấp; các nút bên dưới thực hiện chức năng tương ứng. Quyền cấp cục bộ được giữ riêng với quyền mà máy khác cấp cho mình.

| Chức năng | Hành vi của bản này |
| --- | --- |
| Chuột / bàn phím | Chuyển ngay qua mép đã chọn; chuột 5 nút, cuộn dọc/ngang, phím và phím mở rộng. Telex/VNI tích hợp hoặc phím gốc. Hỗ trợ tọa độ của nhiều màn hình. |
| Clipboard chữ/ảnh/file | Văn bản hai chiều, tối đa 262.144 ký tự; ảnh chụp/bitmap qua PNG tối đa 32 MiB chỉ cần quyền Clipboard. Ctrl+C file/thư mục trong File Explorer rồi Ctrl+V ở máy kia sau khi nhận xong cần đồng thời quyền Truyền file. Chưa đồng bộ xóa clipboard tùy ý. |
| Âm thanh B | Nút **Nghe B** trên A phát âm thanh hệ thống của B. Không thu microphone; dùng PCM 48 kHz stereo. |
| Truyền file | Nút **Gửi file** trên A hoặc B; tối đa 2 GB/file. Nhận vào `%USERPROFILE%\Downloads\LanBridge`, tên mới không ghi đè. Kiểm tra số byte và SHA-256 trước khi hoàn tất. Không tự mở file nhận được. |
| Khởi động lại B | B phải cấp quyền; A yêu cầu và B xác nhận tại máy. Windows đếm 30 giây; có thể hủy bằng `shutdown /a` trên B. |
| Ghi màn hình B | A bật/tắt; ghi trên B vào `%USERPROFILE%\Videos\LanBridge`. AVI/MJPEG khoảng 8 fps, chiều ngang tối đa 1280 px, không kèm âm thanh, giới hạn 2 GB mỗi bản ghi. |
| Khóa đầu vào B | Trong lúc A điều khiển, bỏ qua chuột/phím vật lý trên B; đầu vào từ A tiếp tục hoạt động. `Ctrl+Alt+F12` vật lý trên B vẫn trả điều khiển. |
| Màn che B | Màn che đen phần mềm trên các màn hình B; cho thao tác chuột xuyên qua và vẫn có dòng chỉ dẫn thoát. Bật màn che sẽ dừng ghi hình. Đây không phải driver tắt màn hình hay chế độ riêng tư tương đương AnyDesk. |
| Chat | Tin nhắn văn bản hai chiều trong phiên. Nhật ký chỉ ở bộ nhớ ứng dụng. |

Chuột/phím, clipboard chữ và truyền file được cho phép mặc định trên B. Các quyền còn lại mặc định tắt. Khi mất kết nối, ứng dụng nhả các phím/nút nó đã giữ, bỏ khóa đầu vào và màn che, dừng âm thanh và hoàn tất bản ghi đang mở.

## Nếu không kết nối được

- Mở LanBridge 0.5.1 trên cả hai máy cùng mạng rồi bấm **Làm mới**. Wi-Fi khách hoặc tính năng AP/client isolation có thể chặn liên lạc giữa máy; danh sách không phát hiện máy ở VLAN khác hoặc chỉ có bản cũ.
- Nếu Windows Defender Firewall hỏi, cho phép LanBridge trên **mạng Riêng tư / Private** ở cả hai máy. Tìm thiết bị dùng **UDP 45832**; phiên dùng **TCP 45831** mặc định. Nếu TCP cổng đó đang bận, ứng dụng chọn một cổng trống và thông báo cổng thực qua danh sách. Cho phép theo file ứng dụng giúp áp dụng đúng cổng. Không cần mở cổng router hoặc dùng máy chủ Internet.
- Nếu đã chặn trước đó, cho phép `LanBridge.exe` tại Windows Security → Firewall & network protection → Allow an app through firewall. Ứng dụng không tự sửa tường lửa.
- Nếu máy hiện nhưng không kết nối được, kiểm tra B đang thấy yêu cầu và bấm Đồng ý trước khi hết 30 giây. Máy đang bận không nhận thêm phiên.
- Nếu vẫn không thấy máy, dùng **Kết nối bằng mã**, chọn đúng IP LAN của B, tránh IP VPN/adapter ảo. Khi đổi IP hoặc mở lại LanBridge, lấy lại mã từ B.
- Không điều khiển được cửa sổ nâng quyền: Windows giới hạn `SendInput` theo mức quyền. Chạy ứng dụng B ở cùng mức quyền với cửa sổ cần thao tác. Bản này không điều khiển màn hình UAC bảo mật, màn hình đăng nhập/khóa hay gửi Ctrl+Alt+Del. Khóa phiên Windows sẽ trả điều khiển.

## Trạng thái kiểm chứng

Đã kiểm tra tự động discovery UDP ở loopback, query/trả lời, danh sách/trạng thái bận/hết hạn, xác nhận TLS, từ chối/timeout/hủy yêu cầu, cùng các kiểm tra TLS cũ, Unicode, file, quyền và kết nối lại. Các kiểm tra giao diện dùng form ẩn/preview, không cài hook hay phát sinh thao tác nhập trên desktop. AVI từ hình tổng hợp đã được FFmpeg giải mã và tua kiểm chứng.

Kiểm tra hồi quy 0.3.1 tái hiện việc ngắt phiên trước bản sửa: hai form ẩn được thu nhỏ, luồng giao diện cố ý tạm dừng trong khi gửi hơn 2.000 gói chuột kèm phím, chữ tiếng Việt và nút chuột qua TLS thật. Sau bản sửa, luồng nhập vẫn xử lý tới gói trả điều khiển khi giao diện còn dừng; phiên và thứ tự các thao tác quan trọng được giữ. Chế độ kiểm tra dùng luồng nhập riêng nhưng tắt hook và `SendInput` thật.

Clipboard file 0.4.0 được kiểm tra cả ở bộ truyền và hai MainForm ẩn qua TLS thật: nhiều file/thư mục tiếng Việt, thư mục rỗng, SHA-256, không gửi vòng lặp, hủy lô dang dở, clipboard bận và nội dung sao chép mới, thu hồi quyền, reconnect và bản cũ không hỗ trợ. Các kiểm tra dùng clipboard giả riêng cho từng form, không thay clipboard đang dùng trên máy. Thao tác dán thật trong File Explorer trên hai PC vẫn cần thử thủ công.

Clipboard ảnh 0.5.0 được kiểm tra bằng ảnh tổng hợp: mã hóa/giải mã pixel và alpha, PNG lớn hơn một gói mạng, giới hạn 8K/40 MP, dữ liệu lỗi và giới hạn giải nén. Kiểm tra hai form ẩn qua TLS thật dùng clipboard giả để xác minh ảnh hai chiều, quyền, không echo, thử lại khi bận và hủy theo nội dung mới. Chưa kiểm chứng Snipping Tool/Print Screen và dán ảnh vào ứng dụng trên hai PC vật lý.

Hồi quy chuột/cuộn 0.5.1 dùng hai form ẩn, TLS loopback và chủ động tạm dừng luồng gửi/nhận để tạo tồn đọng: 1.000 hoặc 8.000 mẫu chuột, 32 lần cuộn cùng phím, kéo chuột và chữ còn 73 gói, xử lý qua 9 lần chuyển sang luồng nhập, giữ đủ thứ tự và tọa độ thao tác. Cùng kịch bản với mã 0.5.0 tạo 1.041 gói rồi ngắt phiên do quá tải. Đây là dữ liệu tổng hợp, tắt hook và `SendInput`; chưa đo độ trễ khi vừa di chuyển vừa cuộn trên hai PC thật.

**Chưa kiểm chứng trên hai PC thật:** broadcast/multicast qua router/tường lửa thực tế, chuyển mép với chuột thực, gõ vào từng ứng dụng trên B, nhiều màn hình/DPI, âm thanh WASAPI, ghi desktop và màn che. Kiểm tra loopback không thay thế việc thử mạng Wi-Fi/Ethernet A/B của bạn.

Con trỏ trên A hiện tại vẫn hiển thị ở điểm căn giữa khi đang điều khiển B. Bản này không có cửa sổ xem trực tiếp màn hình B trên A, kéo thả file giữa hai desktop, mã hóa video thời gian thực, tự kết nối sau reboot hoặc điều khiển game dùng raw input.

## Phát triển và đóng gói

Stack: C# / .NET 10 Windows Forms, Windows hooks + `SendInput`, UDP broadcast/multicast, TLS với certificate pinning, NAudio 2.2.1. Một phiên A/B mỗi máy; không relay. Device ID ngẫu nhiên lưu trong `%LOCALAPPDATA%\LanBridge\device-id.txt`; mã ghép nối không ghi vào log hoặc file cấu hình.

Cài [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) nếu máy phát triển chưa có. Máy hiện tại cũng có SDK cục bộ trong `.tools/dotnet`; thư mục đó không nằm trong gói mã nguồn.

```powershell
pwsh -File .\scripts\build.ps1 -Task Build
pwsh -File .\scripts\build.ps1 -Task Test
pwsh -File .\scripts\build.ps1 -Task Publish
```

Hoặc chạy `dotnet run --project src/LanBridge.csproj`. Bản phát hành ở `release/LanBridge-0.5.1-win-x64/LanBridge.exe`; bản này chưa được ký chứng chỉ phát hành.

`dotnet run --project src/LanBridge.csproj -- --preview` chỉ mở giao diện, không bật hook hay lắng nghe cổng. Có thể thêm `--preview-output C:\duong-dan\preview.png` để xuất hình rồi tự đóng.

Mã nguồn: `DevicesPage.cs` danh sách thiết bị, `Discovery.cs` UDP, `MainForm.Discovery.cs` luồng tìm/kết nối/xác nhận; `MainForm.cs` phiên điều khiển, `NativeInput.cs` đầu vào, `VietnameseInput.cs` bộ gõ, `Transport.cs` TLS/heartbeat, `FileTransfer.cs` truyền file, `MediaServices.cs` media. Chi tiết kiểm thử ở [TESTING.md](TESTING.md).

API Windows: [low-level hooks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw), [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput), [TLS/SslStream](https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslstream), [layered windows](https://learn.microsoft.com/en-us/windows/win32/winmsg/window-features#layered-windows).
