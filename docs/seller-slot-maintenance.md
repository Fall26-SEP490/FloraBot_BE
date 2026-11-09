# Seller slot maintenance (Bảo trì ô WALK_IN tự phục vụ)

Quy trình tự phục vụ dành cho Seller sở hữu kiosk để nạp hoa mới (`LOAD`), thu hồi hoa (`REMOVE`) hoặc đổi hoa (`REPLACE`) cho các ô bán sẵn (`WALK_IN`).

## 1. Yêu cầu & Điều kiện tiên quyết

- **Quyền hạn**: Seller sở hữu kiosk và ô tủ, có tài khoản hoạt động (`role = 'SELLER'`, `status = 'ACTIVE'`). Đối với thao tác thu hồi hoa (`REMOVE`), cho phép shop ở trạng thái `ACTIVE` hoặc `PAST_DUE` thực hiện miễn là người thực hiện (`actor`) có tài khoản hoạt động (`ACTIVE`) và ô vẫn đang được phân công hoạt động cho shop (`assignment_active = true`). Các thao tác `LOAD` và `REPLACE` (bao gồm mở nạp `open-next`) chỉ cho phép khi shop có trạng thái `ACTIVE` và còn hạn thuê ô.
- **Phân loại vùng**: Chỉ áp dụng cho ô vùng `WALK_IN`.
- **Cảm biến giám sát**: Ô phải được kích hoạt đầy đủ năng lực cảm biến cửa và cảm biến hoa LM393 trong `kiosk_ops.slot_sensor_capabilities` (`enabled = true`, `door_verified = true`, `presence_verified = true`). Khi đã bật giám sát, các luồng nạp/thu hồi trực tiếp cũ bị chặn (409 Conflict) và bắt buộc dùng phiên bảo trì.
- **Thử lại lệnh mở cửa (Manual Retry)**: API chỉ cho phép gửi lệnh thử lại qua MQTT sau khi Admin xác minh phần cứng và bật tính năng cho ô. Mặc định `supports_manual_retry = false`, API trả `retryAfterSeconds: null`.
- **Bảo vệ đơn hàng & Giữ chỗ**: Chặn khi ô đang có đơn hàng chưa hoàn tất (`AWAITING_PAYMENT`, `PAID`, `DISPENSING`, `COMPLETED`, `DISPUTED`), có `hold_order_id`, hoặc có lệnh mở cửa đang hiệu lực. Các đơn hàng COMPLETED trong quá khứ của các bó hoa cũ không chặn nạp hoa mới.
- **Thứ tự khóa (Lock ordering)**: Mọi thao tác đều thực hiện theo thứ tự khóa thống nhất: `kiosk SHARE -> slot UPDATE -> session FOR UPDATE -> actor/seller/assignment SHARE` với `lock_timeout = 5s` để chống deadlock.
- **Trạng thái ô**:
  - `LOAD`: Ô phải ở trạng thái `RENTED_EMPTY`, chưa có bó hoa, hợp đồng thuê ô còn hạn thanh toán, và sản phẩm hoa hoạt động thuộc shop. Yêu cầu dữ liệu cảm biến hoa gần nhất xác nhận ô đang trống và ổn định (`states[relay] == 0`) thông qua kiểm tra toàn diện `VerifyTelemetryFreshness`.
  - `REMOVE`: Ô ở trạng thái `STOCKED` hoặc `PENDING_REMOVAL`, có hoa thuộc shop. Cho phép thực hiện khi hợp đồng thuê ô đã hết hạn hoặc shop ở trạng thái `PAST_DUE`, miễn là ô vẫn được phân công hoạt động cho shop và tài khoản người thực hiện đang hoạt động (`ACTIVE`).
  - `REPLACE`: Ô ở trạng thái `STOCKED` hoặc `PENDING_REMOVAL`, hợp đồng thuê còn hạn, sản phẩm thay thế hợp lệ thuộc shop.

## 2. API Endpoints

Tất cả các endpoint seller yêu cầu xác thực Bearer token với role `SELLER` và header `Cache-Control: no-store`.

### 2.1 Bắt đầu phiên bảo trì
```http
POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions
Content-Type: application/json
```
```json
{
  "id": "3fa85f64-5717-4562-b3fc-2c963f66afa6",
  "action": "LOAD", // hoặc "REMOVE", "REPLACE"
  "expectedBouquetId": "00000000-0000-0000-0000-000000000000",
  "productId": "00000000-0000-0000-0000-000000000000",
  "reason": "Ly do thuc hien bao tri"
}
```
- **Hành vi**:
  - Khóa ô chuyển sang `LOCKED` (giữ hoa cũ ra khỏi catalog/checkout bán hàng của khách).
  - Khởi tạo phiên trong `kiosk_ops.slot_maintenance_sessions` (thời hạn 15 phút).
  - Phát lệnh mở cửa kỹ thuật `SELLER_ACCESS` đầu tiên với `cmd_id`.
  - Idempotent: Gửi lại cùng `id` và cùng nội dung bởi cùng shop trả kết quả 200 OK với phiên hiện tại; khác nội dung trả 409 Conflict. Không rò rỉ thông tin phiên giữa các shop (cross-tenant trả 404).

### 2.2 Xem chi tiết phiên
```http
GET /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}
```

### 2.3 Đồng bộ và kiểm tra tiến trình
```http
POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/check
```
- Kiểm tra trạng thái đóng cửa thực tế (`CLOSED`) và ảnh chụp cảm biến hoa mới nhất (chụp sau thời điểm đóng cửa và bắt đầu pha).
- Khi cửa vừa đóng nhưng telemetry mới chưa kịp gửi tới (quan sát trước lúc đóng cửa), phiên giữ nguyên trạng thái chờ mà không chuyển nhầm sang `NeedsInspection`.
- Đối với `REMOVE`: Cửa đóng + cảm biến xác nhận trống (0) -> hoàn tất, cập nhật `RENTED_EMPTY`.
- Đối với `LOAD`: Cửa đóng + cảm biến xác nhận có hoa (1) -> hoàn tất, nạp hoa mới `STOCKED`.
- Đối với `REPLACE`:
  - Pha 1: Cửa đóng + xác nhận trống (0) -> xác nhận thu hồi thành công, chuyển sang `AwaitLoading` với `substate: RemovalClosedEmptyConfirmed`. Ô vẫn được giữ `LOCKED`.
  - Pha 2: Chờ gọi `open-next` để mở cửa nạp hoa mới.

### 2.4 Mở cửa nạp hoa thay thế (Pha 2 của REPLACE)
```http
POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/open-next
```
- Chỉ hợp lệ khi phiên là `REPLACE`, đang ở `AwaitLoading` và đã xác nhận ô trống (`RemovalClosedEmptyConfirmed`).
- Recheck hợp đồng thuê ô và sản phẩm còn hiệu lực trước khi phát lệnh mở cửa mới. Lệnh mở cửa mới sinh ra `cmd_id` độc lập và đặt lại mốc thời gian pha mới.

### 2.5 Mở cửa kiểm tra ô khi gặp sự cố (NeedsInspection)
```http
POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/open-for-check
Content-Type: application/json
```
```json
{
  "reason": "Mo lai cua de lay bo hoa bi ket / xu ly vat can"
}
```
- Chỉ hợp lệ khi phiên ở trạng thái `NeedsInspection`, lệnh mở cửa trước đó đã đóng hoàn tất (`CLOSED`), snapshot cảm biến hiện tại ổn định (`0` hoặc `1`), kiosk online với heartbeat mới, năng lực cảm biến ô hợp lệ và khớp mapping kiosk+relay.
- Giới hạn tối đa 3 lần mở kiểm tra cho mỗi phiên bảo trì và thời gian giữa các lần mở kiểm tra tối thiểu 30 giây.
- Sinh lệnh `SELLER_ACCESS` mới với `cmd_id` độc lập, đặt lại mốc thời gian pha mới và giữ nguyên `intended_stage` để sau khi đóng cửa có thể phục hồi và hoàn tất phiên mà không cần hủy bỏ.

### 2.6 Báo lỗi / Sự cố
```http
POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/report-issue
Content-Type: application/json
```
```json
{
  "reason": "Cua bi ket hoac cam bien khong phan hoi dung"
}
```
- Chuyển phiên sang `NeedsInspection` để nhân viên hoặc kỹ thuật viên kiểm tra xử lý, ô tiếp tục được giữ `LOCKED`.

### 2.7 Quản trị: Cấu hình năng lực cảm biến (Admin)
```http
PUT /api/admin/kiosks/{kioskId}/slots/{slotId}/sensor-capabilities
Content-Type: application/json
```
```json
{
  "enabled": true,
  "doorVerified": true,
  "presenceVerified": true
}
```
- Admin xác nhận kênh relay (0..3) đã gắn cảm biến cửa và cảm biến hoa LM393.

## Thử lại khi khóa chưa nhả

Admin bật riêng từng ô qua `PUT /api/admin/kiosks/{kioskId}/slots/{slotId}/manual-unlock-retry` với `{"enabled":true,"firmwareVerified":true}` sau khi xác nhận phiên bản firmware, cảm biến cửa và đúng bộ điều khiển. Gửi `{"enabled":false}` để tắt. Quyền mặc định tắt và được gắn với mã phần cứng đã xác minh.

- Seller gọi `POST /api/me/kiosks/{kioskId}/slots/{slotId}/maintenance-sessions/{sessionId}/retry-unlock` (hỗ trợ cả shop `PAST_DUE` khi ở pha `AwaitRemoval` của phiên `REMOVE` còn hiệu lực phân công ô).
- Khách tại kiosk gọi `POST /api/kiosks/{kioskId}/pickup/orders/{orderId}/commands/{cmdId}/retry-unlock`. Thành viên cần phiên đăng nhập tại đúng kiosk và sở hữu đơn. Khách vãng lai cần thông tin xác thực kiosk và mã biên nhận walk-in.
- Gửi `{"requestId":"<uuid>"}` và thêm `trackingToken` cho khách vãng lai. Dùng lại cùng UUID khi gửi lại yêu cầu HTTP. API trả `PENDING` khi đã xếp hàng. Kết quả này xác nhận yêu cầu đã lưu. Sự kiện `OPENED` mới xác nhận cửa đã mở.
- BE cho phép một yêu cầu thử lại sau ít nhất 31 giây tính từ ACK đã xác thực. Đây là khoảng đệm cho chu kỳ chờ 30 giây của ESP32. Nếu còn phải chờ, API trả `409` kèm `Retry-After`. Lệnh hết hạn theo cửa sổ của lệnh gốc, phiên bảo trì hoặc giữ hàng, lấy mốc sớm nhất.
- Tính năng dùng cảm biến cửa đã xác minh. Seller nạp hoặc thay hoa cần thêm cảm biến vật thể. Khi cửa đã mở và đóng nhưng cảm biến vật thể vẫn báo sai kỳ vọng, seller dùng `open-for-check` để bắt đầu chu kỳ kiểm tra riêng.
