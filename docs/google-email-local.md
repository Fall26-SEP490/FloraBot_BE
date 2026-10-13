# Kiểm thử Google và email OTP thật

Thiết lập các biến môi trường backend trước khi khởi chạy:

```dotenv
GOOGLE_CLIENT_ID=your-web-client-id.apps.googleusercontent.com
EMAIL_PROVIDER=GoogleSmtp
EMAIL_SENDER=your-sender@gmail.com
EMAIL_SENDER_NAME=FloraBot
GOOGLE_SMTP_APP_PASSWORD=your-app-password
MEMBER_WEB_URL=http://localhost:8088
```

Biến `EMAIL_SENDER_NAME` dùng cấu hình tên hiển thị người gửi email (From display name), mặc định là `FloraBot`. Tên này được xác thực độ dài tối đa 100 ký tự và không chứa ký tự điều khiển (CRLF).

Giữ credential trong file local ignored hoặc secret manager; không commit và không đưa SMTP password vào FE. GoogleSmtp chỉ hỗ trợ Development. Google Client ID công khai; FE lấy từ `/api/auth/config`. Luồng Google Identity Services hiện tại không cần Client Secret.

Trong Google Auth Platform, cấu hình OAuth client Web application, thêm đúng Authorized JavaScript origin của FE, ví dụ `http://localhost:8088`. Không thêm `/dang-nhap`; `127.0.0.1` là origin khác. Google có thể mất 5 phút đến vài giờ để áp dụng thay đổi. Các thành viên dùng chung client cần cấu hình origin tương ứng cổng thực tế của họ.

Docker Compose app chuyển các biến trên vào API. Truyền file cấu hình local qua `--env-file`, cùng các biến runtime bắt buộc như JWT_SIGNING_KEY và AI_SERVICE_TOKEN; file provider riêng không đủ để chạy toàn bộ stack.

Với database mới, dùng quy trình khởi tạo trong README. Với database đã chạy, sao lưu trước rồi chỉ áp dụng forward migrations chưa có (021/022 cho increment auth này). Không replay db-init trên volume hiện hữu: migration replay cũ còn lỗi đổi tên tham số hàm. Không dùng `down -v` để cập nhật code. Sau migration, rebuild và thay các ứng dụng bằng `up -d --no-deps api gateway next-web web` với đúng tên project và compose files đang dùng.

## Thử trên FE

1. Mở `/dang-ky`, đăng ký email thật. Phải nhận OTP email; trước xác thực chưa có tài khoản hoặc cookie đăng nhập.
2. Nhập mã: tạo CUSTOMER và đăng nhập. Mã hết hạn sau 10 phút; gửi lại chờ 60 giây; mã đã sử dụng bị từ chối.
3. Thử đăng nhập mật khẩu và Quên mật khẩu. Đặt mật khẩu mới phải vô hiệu phiên cũ.
4. Thử Google bằng email mới: backend kiểm tra chữ ký/audience/issuer, tạo CUSTOMER; không nhập token hoặc email giả để đăng nhập.
5. Email đã có tài khoản mật khẩu: đăng nhập trước, vào trang tài khoản để liên kết Google. Không tự ghép bằng email; không nâng role khi liên kết.

Đã kiểm thử gửi và xác thực OTP email thật trên máy phát triển. Google origin đã lưu nhưng đăng nhập Google cá nhân vẫn cần thử. Tests dùng validator giả qua DI không chứng minh consent/picker Google thật đã chạy.
