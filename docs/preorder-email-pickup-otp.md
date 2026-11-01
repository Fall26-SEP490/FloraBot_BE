# Nhận preorder bằng OTP email

## Luồng nghiệp vụ

1. Customer đặt CUSTOM → Seller báo giá → Customer chấp nhận và thanh toán PayOS.
2. Seller/Seller Staff nạp bó thực tế vào ô PREORDER đúng kiosk qua API fulfill hiện có.
3. Seller bấm **Gửi mã nhận hoa**. Hệ thống gửi OTP 8 chữ số tới **email đăng nhập** của Customer, không lấy contactEmail.
4. Customer đến đúng kiosk, chọn **Nhận đơn đặt trước**, nhập OTP rồi xác nhận mở ô. Không nhập UUID, số điện thoại hoặc mã biên nhận.
5. BE kiểm tra thanh toán, bó/ô/đơn, kiosk và heartbeat còn hiệu lực, khung giờ nhận; tạo lệnh CUSTOMER_PICKUP qua hệ thống MQTT hiện có.
6. Kiosk theo dõi lệnh. Chỉ trạng thái COMPLETED mới hiển thị đã nhận hoa; ISSUED/SENT/ACKED chưa có nghĩa cửa đã mở.

**Hoa sẵn sàng** nghĩa là đã thanh toán và đã nạp đúng ô PREORDER; thanh toán thành công một mình chưa đủ. Không có bước khách duyệt ảnh.

OTP được gửi trước giờ nhận nếu hoa đã nạp, nhưng chỉ dùng từ pickupAt đến trước pickupBefore. OTP hết hạn tại pickupBefore. Mỗi mã dùng một lần; gửi lại vô hiệu mã cũ. Mã của kiosk khác không mở được ô. Lệnh phần cứng cũng bị giới hạn bởi cuối khung giờ nhận.

## API cho FE Seller

```http
POST /api/me/preorders/{requestId}/pickup-otp
```

- Seller đăng nhập; requestId là ID yêu cầu preorder, không phải orderId/checkoutId.
- Không có body, không truyền email hoặc sellerId. BE tự lấy chủ shop và email đăng nhập của khách.
- Cookie auth phải gửi Origin hợp lệ theo cơ chế CSRF hiện có; không tắt kiểm tra này.
- Seller khác shop, Customer, Seller Staff, Operations, Admin không được gửi mã bằng API này.

Kết quả 200, ví dụ (không có OTP trong response):

```json
{
  "requestId": "<uuid>",
  "orderId": "<uuid>",
  "recipientMasked": "c***@example.com",
  "expiresAt": "<pickupBefore UTC>",
  "resendAfterSeconds": 60
}
```

FE hiển thị nút khi đơn có `ready=true` và orderStatus PAID, hoặc DISPENSING khi cần hỗ trợ sau lệnh lỗi/hết hạn. BE vẫn kiểm tra lại. Khóa gửi lại 60 giây; tối đa 5 lần/giờ/đơn. 200 nghĩa dịch vụ email đã chấp nhận gửi, không bảo đảm thư vào inbox.

| HTTP | FE xử lý |
| --- | --- |
| 401/403 | Kiểm tra đăng nhập Seller, cookie/Origin và quyền |
| 404 | Không có đơn thuộc shop |
| 409 | Chưa thanh toán/nạp hoa, hết giờ hoặc đang có lệnh mở/cửa chưa đóng |
| 429 | Chờ hết cooldown/giới hạn gửi |
| 503 | Chưa xác nhận gửi/kích hoạt mã; không tự động gửi lại, đọc thông báo và kiểm tra trước |

Website FE chưa được sửa trong thay đổi này. Customer cần hướng dẫn **xem OTP trong email**, không trình bày trackingToken như OTP mở preorder. TrackingToken vẫn phục vụ tra cứu/hỗ trợ theo các API hiện có.

## API cho Kiosk

Thiết bị phải có `X-Kiosk-Key` của đúng kiosk; không dùng token Customer/Seller để mở preorder.

```http
POST /api/kiosks/{kioskId}/preorders/pickup-otp/open
Content-Type: application/json
X-Kiosk-Key: <device secret>

{"code":"01234567","attemptId":"<UUID sinh mới cho một lượt xác nhận>"}
```

200 trả `attemptId, orderId, orderCode, productName, slotCode, orderStatus, commandId, watchToken, watchExpiresAt`.

- Giữ attemptId và OTP trong bộ nhớ cho đến khi biết kết quả. Khi phản hồi bị mất, chỉ người dùng chủ động kiểm tra lại cùng code + attemptId: BE trả lại cùng commandId, không tạo lệnh mới.
- Không tự retry lệnh mở, không đổi attemptId khi chưa biết cửa đã mở hay chưa.
- 401 mã sai/hết hạn/đã dùng; 409 chưa đúng giờ/điều kiện; 429 nhập sai nhiều lần; 503 kết quả chưa xác định. Sau 5 mã sai tại một kiosk, khóa thử mã 5 phút; ngoài ra giới hạn 10 POST/phút/kiosk.
- Nhập đúng mã trước giờ nhận không tiêu thụ mã.
- Không lưu OTP/watchToken vào storage, URL, log, analytics hoặc QR.

```http
GET /api/kiosks/{kioskId}/preorders/pickup-otp/attempts/{attemptId}
X-Kiosk-Key: <device secret>
X-Pickup-Token: <watchToken>
```

Trả `orderId, orderCode, productName, slotCode, orderStatus, commandStatus`. Poll 3 giây; dừng khi hoàn tất/lệnh thất bại/hết hạn hoặc người dùng rời màn hình. Watch token hết hạn sau tối đa 10 phút, không vượt pickupBefore; nó không có quyền mở cửa. Home, ẩn tab và 3 phút không tương tác xóa thông tin lượt nhận khỏi màn hình.

## CSDL, bảo mật và triển khai

- Migration: `db/migrations/20261031_preorder_email_pickup_otp.sql`, sau các migration hiện có. Tạo bảng OTP và lượt nhận, giữ dữ liệu đơn/tiền/lịch sử; không sửa đơn thành đã thanh toán.
- OTP lưu dạng HMAC có khóa, gắn kiosk và email đăng nhập; không lưu OTP thô. Khóa dùng JWT_SIGNING_KEY với domain riêng; xoay khóa này cũng vô hiệu OTP/watchToken đang có.
- Gửi email ngoài transaction DB; chỉ kích hoạt mã nếu dịch vụ gửi thành công và đúng generation. Không bật mã nếu gửi thất bại hoặc kết quả không xác định.
- Tiêu thụ mã, tạo command, chuyển đơn và ghi audit/outbox trong cùng transaction. Khóa đồng thời theo attempt/order chống mở trùng.
- Migration chặn đường receipt `flow.request_pickup` cho preorder; walk-in receipt recovery giữ nguyên. Phải cập nhật Kiosk và nút gửi mã Seller trong cùng đợt triển khai để khách preorder có đường nhận.
- Chỉ publish lại **FloraBot.Api**, chạy migration và build/deploy **FloraBot_Kios**. Gateway không thay đổi. FE website cần nối nút gửi mã nêu trên.
- Dùng cấu hình email BE hiện có: EMAIL_PROVIDER/EMAIL_SENDER và key tương ứng. Với GoogleSmtp Production, cần GOOGLE_SMTP_ALLOWED_IN_PRODUCTION=true. Không thêm key vào FE/Kiosk.
- Đã dùng email giả, DB kiểm thử riêng, Playwright giả lập BE; chưa kiểm thử email thật/MQTT/tủ vật lý hoặc triển khai VPS.

Sao lưu DB trước migration theo quy trình VPS hiện có. Sau chạy có thể kiểm tra:

```sql
SELECT to_regclass('ordering.preorder_pickup_otps'),
       to_regclass('ordering.preorder_pickup_attempts');
```

Kiểm thử tại môi trường thật bằng một đơn test đã trả tiền, nạp ô, Seller gửi mã, nhập OTP tại đúng kiosk/đúng giờ và quan sát mở–lấy hoa–đóng cửa. Không giả lập trạng thái cửa trên VPS để đánh dấu đơn hoàn tất.
