# Lộ trình chuyển FloraBot sang microservices

## Phạm vi của 7 service

Kiến trúc mục tiêu gồm sáu service nghiệp vụ .NET và service AI Python hiện có.
Gateway tiếp nhận và định tuyến các yêu cầu từ bên ngoài. Việc chuyển đổi được
thực hiện từng phần, có kiểm chứng riêng cho mỗi phần. API hiện tại tiếp tục phục
vụ các nghiệp vụ đang chờ tách.

| Service | Dữ liệu và nghiệp vụ phụ trách |
| --- | --- |
| Identity | Tài khoản, đăng nhập, làm mới phiên, trạng thái tài khoản, vai trò và quan hệ giữa seller với nhân viên |
| Catalog | Hồ sơ công khai của shop, mẫu sản phẩm hoa giả, tag, mô tả, ảnh, giá niêm yết, phụ kiện preorder và đánh giá sản phẩm |
| Ordering | Báo giá, đơn walk-in/preorder, checkout, biên nhận, trạng thái nhận hàng, hủy đơn, khiếu nại chất lượng và điểm thưởng |
| Payment | Kênh thanh toán của seller/nền tảng, link và webhook PayOS, khoản thu, hoàn tiền, sổ giao dịch, chứng từ chuyển khoản thủ công và báo cáo tài chính |
| Kiosk Operations | Đăng ký tủ, kỳ thuê và hạn thuê, phân công, quyền sử dụng ô, bó hoa thực tế, giữ chỗ, nạp–thay–thu hồi, OTP thao tác tủ, MQTT, bằng chứng cảm biến, sự cố và công việc technician |
| Communication | Chat, thông báo, gửi email và theo dõi kết quả gửi |
| AI | Gợi ý và dự báo dựa trên dữ liệu đã được các service nghiệp vụ kiểm tra quyền truy cập và cung cấp |

Identity phụ trách phát hành và xác minh OTP xác thực tài khoản. Kiosk Operations
phụ trách cấp quyền bằng OTP cho thao tác với tủ. Communication gửi email chứa OTP
do service phụ trách nghiệp vụ chuẩn bị.

Catalog quản lý mẫu sản phẩm. Kiosk Operations quản lý bó hoa thực tế và tình
trạng sử dụng từng ô. Ordering điều phối yêu cầu giữ hàng hoặc giữ ô thông qua
Kiosk Operations. Payment quản lý bằng chứng giao dịch tiền; Ordering quản lý
trạng thái đơn tương ứng.

Kiosk Operations quản lý kỳ thuê và quyền sử dụng tủ; Payment quản lý tiền thuê.
Báo cáo tài chính có thể sử dụng dữ liệu tổng hợp thuộc Payment, được cập nhật từ
các sự kiện đơn hàng và kiosk. Báo cáo lịch sử phải giữ đúng chủ sở hữu tại thời
điểm phát sinh giao dịch.

Mỗi service có cấu hình khởi động, build, triển khai và dữ liệu riêng. Một máy chủ
PostgreSQL có thể chứa nhiều database với tài khoản và quyền truy cập riêng cho
từng service. Các service cùng chạy trên một VPS sẽ cùng chịu ảnh hưởng khi máy
chủ đó gặp sự cố.

## Mốc đầu tiên đã triển khai trong local

`FloraBot.Communication` đã có endpoint HTTP nội bộ được xác thực để tiếp nhận
yêu cầu gửi email, cùng database PostgreSQL riêng. Service sử dụng thư viện
`FloraBot.Email`, chứa bộ gửi email, mẫu nội dung và hợp đồng dữ liệu dùng chung.
Service chỉ tham chiếu project `FloraBot.Email`. Các truy vấn trong service tập
trung vào dữ liệu theo dõi gửi email do chính nó quản lý. API phụ trách đọc và
xử lý dữ liệu Identity, Ordering, Payment và kiosk trước khi chuẩn bị yêu cầu
gửi email.

Phần đọc dữ liệu nghiệp vụ và các CAP subscriber trong API tiếp tục chọn người
nhận hợp lệ, kiểm tra trạng thái nghiệp vụ hiện tại và chuẩn bị nội dung. Cấu hình
`EMAIL_PROVIDER=Communication` chuyển việc gửi các email này qua service mới.
API vẫn hỗ trợ cấu hình mặc định và chế độ gọi trực tiếp nhà cung cấp email.

Mốc này hoàn thành phần giao email. Chat, lưu thông báo trong ứng dụng, việc
Communication trực tiếp nhận sự kiện từ broker và năm service nghiệp vụ .NET còn
lại thuộc các mốc tiếp theo. Các sự kiện seller hiện tại trong CAP outbox tiếp
tục sử dụng payload chỉ chứa ID; người nhận và nội dung được xác định khi xử lý.

Xem [hướng dẫn cấu hình và kiểm tra Communication](communication.md).

## Các mốc tiếp theo

**Cập nhật 20/11/2026:** AI và Communication email đang chạy thành tiến trình
riêng trên VPS; Gateway cũng chạy riêng. Communication có DB/login riêng, SMTP,
CAP broker và callback đã được kiểm chứng, OTP đã nhận ở hộp thư khác. Chat và
thông báo trong ứng dụng vẫn ở API chung; năm phạm vi Identity, Catalog, Ordering,
Payment, Kiosk Operations chưa tách thành tiến trình độc lập. Không coi hai service
đang chạy riêng là đã hoàn tất toàn bộ phạm vi của hai service mục tiêu.

1. **Hoàn thiện Communication.** Chuyển quyền quản lý chat và thông báo sang
   service, định nghĩa hợp đồng sự kiện có phiên bản. Chuyển phần nhận và xử lý
   sự kiện sau khi đã làm rõ quy tắc kiểm tra điều kiện gửi theo trạng thái hiện
   tại và bảo vệ dữ liệu cá nhân.
2. **Tách Catalog.** Giữ tương thích đường dẫn API, cấu trúc phản hồi, kiểm tra
   quyền theo shop, quyền xem ảnh, đánh giá dành cho preorder và phân biệt hoa
   walk-in với preorder. Thay các phép JOIN xuyên phạm vi service bằng dữ liệu
   đã được công bố hoặc cơ chế tổng hợp qua API có giới hạn phạm vi.
3. **Tách Identity.** Giữ tương thích access token, refresh token và cách làm mới
   phiên. Các service phải kiểm tra quyền ở cấp nghiệp vụ. Thay đổi vai trò hoặc
   trạng thái tài khoản phải tiếp tục làm mất hiệu lực quyền truy cập tương ứng.
4. **Tách Payment.** Bổ sung inbox/outbox bền vững để theo dõi sự kiện nhận/gửi,
   xử lý yêu cầu và sự kiện trùng, giữ đúng phân bổ tiền khi một checkout gộp
   nhiều đơn. Duy trì quy trình duyệt hoàn tiền và chứng từ chuyển khoản thủ công.
5. **Tách Ordering và Kiosk Operations.** Lưu bền vững trạng thái điều phối và
   định nghĩa phiên bản cho các sự kiện giữ chỗ, thanh toán và thiết bị. Phải theo
   dõi riêng việc xác nhận thanh toán, phát lệnh, nhận phản hồi rơ-le và xác nhận
   khách thực sự nhận hàng.

Với mỗi mốc, kiểm tra khả năng khởi động độc lập, quyền truy cập database theo
service sở hữu, xác thực, xử lý sự kiện lặp và yêu cầu đồng thời, timeout và cách
ứng xử khi service gặp sự cố. Kiểm tra lại các luồng hiện tại bị ảnh hưởng trước
khi chuyển route tương ứng trên Gateway.

VPS hiện có 4 GB RAM và đang dùng chung cho nhiều ứng dụng. Cần đo mức sử dụng tài
nguyên mỗi khi bổ sung một tiến trình để quyết định bước triển khai tiếp theo.
