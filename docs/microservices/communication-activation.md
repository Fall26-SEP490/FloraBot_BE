# Bật Communication trên VPS v53

Phạm vi: email transport và hàng đợi email Seller. Chat/in-app notifications vẫn
ở API. Không thay FE, PayOS, AI, MQTT, bộ dữ liệu nghiệp vụ hoặc artifacts.
OTP vẫn gọi đồng bộ nhưng transport được chuyển qua Communication. Bước này
không phải hoàn thành cả bảy microservices.

## Chạy gói đã build

Chép hai file ZIP và PS1 trong gói vào `C:\FloraBotMigration` trên VPS.
Chạy PowerShell Administrator:

```powershell
& 'C:\FloraBotMigration\FloraBot-Enable-Communication.ps1'
```

Script hỏi mật khẩu PostgreSQL của **postgres**, không phải tài khoản Admin
FloraBot hay mật khẩu Windows. Nếu dùng một login quản trị PostgreSQL khác:

```powershell
& 'C:\FloraBotMigration\FloraBot-Enable-Communication.ps1' -ProvisionUser 'ten_login'
```

Login này cần CREATE ROLE và CREATE DATABASE hoặc superuser. Mật khẩu chỉ dùng
trong tiến trình để backup và provision, không lưu vào gói. Không gửi mật khẩu
vào chat. Login mới `florabot_communication` là NOSUPERUSER/NOCREATEDB/NOCREATEROLE,
có mật khẩu ngẫu nhiên riêng và sở hữu DB cùng tên. PUBLIC không được CONNECT
vào DB mới; script kiểm tra login mới không đọc được hai bảng nghiệp vụ nhạy cảm.
Đây không phải thiết lập deny CONNECT vào mọi DB khác của cluster; không thay
pg_hba hoặc quyền PUBLIC của các ứng dụng khác.

## Trình tự và kiểm tra

- Chỉ nhận API v53 build `747a766`, SMTP và broker giống preflight. Cổng 5090
  phải trống, service/role/DB/thư mục mới phải chưa tồn tại.
- Dừng nếu outbox/inbox email Seller cũ còn bản chưa Succeeded; không tự đánh dấu
  thành công hay gửi lại email cũ để qua điều kiện này.
- Backup DB API và IIS, bảo vệ thư mục backup bằng ACL Administrator/SYSTEM.
- Tạo DB/login mới; đặt service tại `C:\FloraBot\staging\communication-20261120`.
- Tạo AppPool riêng, binding `127.0.0.1:5090`. Chỉ copy cấu hình SMTP/RabbitMQ
  cần thiết; không copy JWT, PayOS, AI, MQTT hoặc DB nghiệp vụ vào service.
- Tạo token nội bộ và thư mục khóa Data Protection riêng tại
  `C:\FloraBot\secrets\communication-20261120`. Sao lưu cả DB service và khóa này
  trong vận hành về sau. Khóa được bảo vệ bằng ACL, không tự khẳng định mã hóa
  khóa trên đĩa. Credential XML backup dùng DPAPI theo tài khoản chạy script.
- Sau HTTP readiness, dừng API để chốt hàng đợi cũ đã cạn. Chỉ đổi bốn biến:
  EMAIL_PROVIDER, COMMUNICATION_SERVICE_URL, COMMUNICATION_SERVICE_TOKEN,
  COMMUNICATION_BROKER_ENABLED. Giữ nguyên các biến khác.
- Trước khi dừng API, công cụ kết nối RabbitMQ có xác thực và kiểm tra consumer
  Communication đã đăng ký thật. HTTP readiness đơn thuần không thay thế bước này.
- Khởi động API, chạy canary qua RabbitMQ và callback eligibility thật. Canary
  có ID ngẫu nhiên không gắn nghiệp vụ; phải SKIPPED và không có provider receipt.
  Không gửi email thử. Canary phải thành công trước khi báo activation PASS.

## Khi lỗi và khôi phục

Nếu lỗi sau khi chạm API config, script khôi phục web.config từ backup và thử
khởi động lại API. Service mới được dừng nếu đã tạo website. Không xóa DB, role,
queue, khóa hoặc backup: nếu còn yêu cầu đang chờ, phải kiểm tra và đối soát trước
khi tiếp tục; không khẳng định đã hủy hay đã gửi các yêu cầu đó. Không chạy lại
script khi nó báo tài nguyên đã tồn tại. Gửi phase thất bại để xử lý đúng tài nguyên.
Nếu rollback cũng thất bại, kết quả PowerShell là lỗi; kiểm tra trạng thái pool/API
qua console, không coi rollback đã hoàn tất.

Sau activation thành công, rollback vận hành cần kiểm tra email đang chờ/SENDING/
UNCERTAIN trước, dừng API, phục hồi web.config đã backup, khởi động và xác minh
API, sau đó mới dừng Communication. Đối soát yêu cầu còn treo; không replay email
Succeeded hoặc xóa hàng đợi để làm sạch. Cửa sổ chuyển đổi không phải transaction
phân tán với SMTP và không đảm bảo exactly-once ở provider.

Chưa kiểm chứng IIS/provisioning end-to-end trên VPS trước khi người vận hành chạy.
Local đã kiểm tra broker/callback canary, fail-closed khi token sai, bảo toàn biến
XML không liên quan, build và bộ integration Communication. Sau chạy cần kiểm tra
RAM, log, SMTP trong một nghiệp vụ được phép, cùng khả năng đăng nhập/OTP thực tế.

## Build lại

```powershell
& '.\scripts\package-communication-vps.ps1' -OutputDirectory 'D:\log\communication-new'
```

Đường dẫn output phải mới. Không chạy trực tiếp file enable trong source vì hash
gói còn là placeholder. Gói được build từ feature branch; publication cần thao tác
merge/push riêng theo yêu cầu của chủ dự án.
