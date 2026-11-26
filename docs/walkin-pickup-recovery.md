# Khôi phục lệnh nhận hoa walk-in

Migration: `20261126_walkin_pickup_command_recovery.sql`.

`flow.request_pickup` là đường tạo lệnh lần đầu hoặc khôi phục kết quả khi mất phản hồi. Hàm vẫn kiểm tra đúng kiosk, mã biên nhận, thanh toán/trạng thái đơn và thời gian giữ ô; preorder tiếp tục dùng OTP email.

- Nếu có command CUSTOMER_PICKUP ISSUED/SENT/ACKED còn hiệu lực, trả cùng commandId. Không REVOKED command đang chờ và không phát command mới.
- Cửa đã mở: từ chối, khách nhận hoa và đóng cửa.
- Đã có lịch sử command nhưng không còn command hiệu lực: từ chối phát mới; giữ biên nhận để xử lý theo trạng thái đơn/sự cố. Không dùng endpoint này để vượt giới hạn retry.
- Thử relay lại dùng `/api/kiosks/{kioskId}/pickup/orders/{orderId}/commands/{cmdId}/retry-unlock`. Giữ commandId và requestId khi mạng lỗi. BE giữ chính sách ACK thực, cooldown, heartbeat, quyền, trạng thái cửa/cảm biến và idempotency hiện có.

Triển khai migration BE trước Kiosk mới. Chưa áp dụng vào database dự án/host trong quá trình sửa.

API member cũ `/api/kiosks/{kioskId}/pickup/orders/{orderId}/open` không còn là hướng tích hợp cho preorder. Không gọi endpoint này từ UI mới; dùng API OTP email trong `preorder-email-pickup-otp.md`.

Kiểm tra tự động mô phỏng sự kiện trong DB tạm, không chứng minh relay hoặc cửa thật đã hoạt động. Khi nghiệm thu, kiểm tra cùng một đơn với ACK chậm, phản hồi mạng bị mất, cửa đã mở, lệnh hết hạn và bấm thử lại liên tiếp.
