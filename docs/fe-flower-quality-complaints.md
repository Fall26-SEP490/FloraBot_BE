# Luồng phản ánh chất lượng hoa — hợp đồng BE cho FE

Ngày 16/11/2026. Áp dụng cho phản ánh **sau khi Customer đã nhận hoa**, cho cả khách có tài khoản và khách giữ biên nhận.

## Quy tắc đã chốt

| Phần | BE thực hiện | FE cần thể hiện |
|---|---|---|
| Liên kết phản ánh với công việc | Gắn complaint → order → order item/bouquet → kiosk/slot → Seller → task/staff | Hiển thị đúng đơn, bó hoa và vị trí cũ; không tìm ảnh/công việc theo mỗi kiosk |
| Nhóm việc chất lượng hoa | Task `kind=DELIVERY`, `workType=FLOWER_QUALITY`, có `complaintId` | Nhóm “Xử lý chất lượng hoa”; tải nội dung ở `/api/staff/tasks/{taskId}/quality` |
| Báo cáo và ảnh | Staff gửi báo cáo 10–2000 ký tự; ảnh xử lý không bắt buộc | Cho nhập báo cáo, thêm ảnh tùy chọn; không bắt buộc ảnh ở FE |
| Seller duyệt | Seller duyệt qua complaint, hoặc trả task về `IN_PROGRESS` | Không dùng transition `COMPLETED` chung để duyệt việc chất lượng |
| Kết quả cho Customer | Giải thích, thay hoa miễn phí tại kiosk, hoặc chuyển Admin xem xét hoàn tiền | Hiển thị kết quả và trạng thái thật; `REFUND_REVIEW` chưa phải đã hoàn tiền |

Luồng chính: Customer phản ánh → Seller tiếp nhận → giao staff của chính shop → `ASSIGNED` → `IN_PROGRESS` → `SUBMITTED` → Seller duyệt → task `COMPLETED` và chốt phương án với Customer.

## 1. Customer gửi và theo dõi phản ánh

Giữ API hiện có:

- `POST /api/receipts/{orderId}/evidence`, header `X-Receipt-Token`, body binary ảnh, lấy `reference` của ảnh đã upload.
- `POST /api/receipts/flows/open_dispute`:

```json
{
  "p_order": "<orderId>",
  "p_tracking": "<trackingToken trên biên nhận>",
  "p_reason": "Bó hoa bị dập sau khi nhận.",
  "p_photo": "<reference của ảnh đã upload>"
}
```

Điều kiện cũ vẫn giữ: đơn `COMPLETED`, còn thời hạn phản ánh theo cấu hình, mỗi đơn một COMPLAINT, ảnh Customer đã upload hợp lệ. Quy tắc ảnh không bắt buộc trong task này chỉ áp dụng **báo cáo staff**.

API mới để Customer xem kết quả:

```http
POST /api/receipts/complaints/lookup?evidencePage=1
Content-Type: application/json
```

```json
{ "orderId": "<orderId>", "trackingToken": "<mã biên nhận>" }
```

Response có `id`, `orderCode`, `status`, `handlingState`, `outcome`, `resolution`, `replacement`, `evidence`. Trường `task` là null cho Customer; không công khai hướng dẫn nội bộ hoặc định danh staff. Giữ trackingToken trong phạm vi phiên nhận hàng; không đưa vào URL, log hoặc analytics.

## 2. Seller tiếp nhận và giao việc

Các API Seller dùng Bearer token của Seller, BE suy ra shop từ tài khoản; không nhận sellerId từ body.

| API | Mục đích |
|---|---|
| `GET /api/me/complaints?state=REPORTED&page=1` | Danh sách, 25 hồ sơ/trang, `hasMore` |
| `GET /api/me/complaints/{complaintId}?evidencePage=1` | Chi tiết, bó hoa gốc, công việc hiện tại, ảnh, hoa thay |
| `POST /api/me/complaints/{complaintId}/accept` | Tiếp nhận |
| `POST /api/me/complaints/{complaintId}/assignment` | Giao staff của shop |
| `POST /api/me/complaints/{complaintId}/replacement-plan` | Chỉ định mẫu và ô nạp hoa thay, nếu chọn phương án này |
| `POST /api/me/complaints/{complaintId}/review` | Duyệt báo cáo và kết quả hoặc trả việc |
| `POST /api/me/complaints/{complaintId}/replacement/cancel` | Hủy quyền nhận hoa thay để xử lý tiếp/thu hồi |

Tiếp nhận:

```json
{ "version": 1 }
```

Giao việc, dùng `orderItemId` từ `items` của chi tiết phản ánh và staff từ API danh sách staff Seller hiện có:

```json
{
  "version": 2,
  "taskId": "<UUID mới do FE tạo, giữ nguyên khi retry cùng yêu cầu>",
  "assigneeId": "<staff của shop>",
  "orderItemId": "<dòng bó hoa trong đơn bị phản ánh>",
  "instructions": "Kiểm tra chất lượng bó hoa theo phản ánh và gửi báo cáo."
}
```

Response các thao tác Seller: `{ "version": <version mới của hồ sơ> }`. `version` hồ sơ và `task.version` là hai giá trị riêng; tải lại chi tiết sau thao tác để lấy đúng cả hai. Không tự cộng version ở FE. Chỉ có một công việc hiện tại đang mở cho mỗi phản ánh; các công việc trước và ảnh vẫn giữ lịch sử.

Nếu cần đổi người/hủy công việc, dùng API task hiện có của Seller. BE chặn khi còn phiên nạp chưa khôi phục hoặc hoa thay đang giữ. Hủy phương án hoa thay trước khi chuyển phương án khác; nếu cửa đang mở thì phải xử lý đóng cửa trước.

## 3. Staff làm việc và gửi báo cáo

```http
GET /api/staff/tasks
GET /api/staff/tasks/{taskId}/quality
POST /api/staff/tasks/{taskId}/transition
```

Bắt đầu:

```json
{ "version": 1, "status": "IN_PROGRESS", "report": "Đã bắt đầu kiểm tra tình trạng hoa theo phản ánh." }
```

Gửi Seller duyệt:

```json
{ "version": 2, "status": "SUBMITTED", "report": "Đã kiểm tra tình trạng hoa và ghi nhận kết quả xử lý." }
```

Ảnh tùy chọn, dùng luồng task evidence hiện có:

1. `POST /api/staff/tasks/{taskId}/evidence/upload` — binary JPEG/PNG/WebP khi task đang `IN_PROGRESS`.
2. `POST /api/staff/tasks/{taskId}/evidence` — `{ "id": "<attachment UUID>", "version": <taskVersion>, "reference": "<upload reference>" }`.

Upload chưa attach thì chưa phải ảnh trong hồ sơ. Không lấy ảnh của task khác hoặc ảnh lắp đặt cùng kiosk.

## 4. Seller duyệt báo cáo và chốt kết quả

```json
{
  "version": 5,
  "taskVersion": 3,
  "outcome": "EXPLANATION",
  "resolution": "Shop đã kiểm tra và giải thích tình trạng hoa cho khách."
}
```

| outcome | Kết quả |
|---|---|
| `RETURN` | Trả task `SUBMITTED` về `IN_PROGRESS`, staff làm và gửi lại. `resolution` là yêu cầu bổ sung |
| `EXPLANATION` | Task `COMPLETED`, phản ánh `RESOLVED_FIXED`, order trở về `COMPLETED`; không cộng điểm lần nữa |
| `REPLACEMENT` | Chỉ duyệt khi hoa thay đã nạp và giữ riêng; task `COMPLETED`, hồ sơ `REPLACEMENT_READY`, phản ánh còn `OPEN` tới khi khách nhận |
| `REFUND_REVIEW` | Task `COMPLETED`, hồ sơ `REFUND_REVIEW`, phản ánh vẫn `OPEN`; Admin dùng quy trình xét duyệt/chi hoàn hiện có. API này chưa lập/chi tiền hoàn |

Admin lọc hồ sơ chuyển xem xét hoàn tiền bằng `GET /api/admin/quality-complaints?state=REFUND_REVIEW&page=1`, xem tổng hợp ảnh và trạng thái xử lý ở `GET /api/admin/quality-complaints/{complaintId}`. API quyết định hoàn tiền vẫn là `POST /api/admin/flows/resolve_dispute`; không cấp API này cho Seller/staff. Danh sách Admin complaints hiện có chấp nhận thêm bộ lọc `RESOLVED_FIXED` và `CANCELLED`.

## 5. Nạp và nhận hoa thay tại kiosk

Seller chọn mẫu còn tồn của shop và ô **WALK_IN hoặc PREORDER trống, không giữ đơn, đã xác minh cảm biến** tại kiosk của đơn gốc. Dùng API sản phẩm/ô hiện có để lấy lựa chọn. FE không được cho staff tự đổi mẫu hoặc ô đã giao. Đây là luồng hoa thay có công việc riêng; nạp bán sẵn thông thường vẫn chỉ dùng WALK_IN. Khách nhận hoa thay bằng biên nhận đơn gốc cho cả hai loại ô.

Nếu task đang `SUBMITTED`, Seller trả việc bằng `RETURN` trước; khi task `ASSIGNED` hoặc `IN_PROGRESS`, gửi:

```http
POST /api/me/complaints/{complaintId}/replacement-plan
```

```json
{ "version": 6, "taskVersion": 4, "productId": "<mẫu hoa của shop>", "slotId": "<ô Walk-in trống của kiosk gốc>" }
```

Staff dùng đúng luồng nạp có giám sát hiện có:

1. `GET /api/staff/tasks/{taskId}/inventory-maintenance` lấy mẫu/ô đã giao.
2. `POST /api/staff/tasks/{taskId}/inventory-maintenance/access`, `{ "operation": "START" }`, lấy mã mở ô của staff.
3. Nhập mã tại kiosk: `POST /api/kiosks/{kioskId}/maintenance-access/{id}/confirm`, `{ "code": "<mã>" }`, header `X-Kiosk-Key`.
4. Staff bỏ hoa mới vào ô, đóng cửa; BE kiểm tra chu kỳ cửa và cảm biến. Không dùng `/stock` hoặc `/preorders/.../fulfill` của task này.
5. Khi phiên nạp `Completed`, hoa được giữ riêng trong cùng transaction và trừ tồn shop đúng một lần. Không hiện trong catalog bán hàng. Staff gửi `SUBMITTED`, Seller duyệt `REPLACEMENT`.

Customer dùng **biên nhận gốc**, không dùng OTP nạp của staff. FE kiosk cần màn hình nhận hoa thay: quét biên nhận hoặc chuyển thông tin biên nhận an toàn vào phiên nhận; lấy complaint qua `/api/receipts/complaints/lookup`, kiểm tra `REPLACEMENT_READY`, rồi gọi:

```http
POST /api/kiosks/{kioskId}/quality-replacements/open
X-Kiosk-Key: <key của chính kiosk>
Content-Type: application/json
```

```json
{
  "complaintId": "<complaintId>",
  "orderId": "<orderId gốc>",
  "trackingToken": "<mã biên nhận gốc>",
  "attemptId": "<UUID của lần yêu cầu mở; giữ nguyên khi retry không rõ kết quả>"
}
```

Response:

```json
{ "complaintId": "<id>", "commandId": "<MQTT commandId>", "commandStatus": "ISSUED" }
```

Theo dõi bằng `POST /api/kiosks/{kioskId}/quality-replacements/status`, cùng body và header. Kiosk khác, biên nhận sai hoặc hồ sơ khác bị chặn. `ISSUED`/`ACKED`/`OPENED` chưa phải nhận xong. Chỉ thông báo hoàn tất khi `replacement.state=COLLECTED`, phản ánh `RESOLVED_FIXED`. Cần có cửa đã đóng và quan sát cảm biến trống mới; nếu telemetry đến sau CLOSED, job định kỳ sẽ đối chiếu. Không tạo checkout, QR thanh toán hoặc thu thêm tiền cho bó hoa thay.

Nếu request timeout/mất mạng, kiểm tra status trước; không tự tạo attemptId mới. Cùng attemptId trả lại cùng command. Một command hết hạn không được hồi sinh; chỉ cho lượt mới khi BE xác nhận hoa còn trong ô, không còn lệnh/cửa đang mở và quyền nhận còn hiệu lực.

Thời hạn mặc định giữ hoa thay: 24 giờ từ lúc **nạp**, không vượt hạn bán của bó hoa. Response `expiresAt` là mốc chính xác. Hết hạn/hủy: hoa chuyển chờ thu hồi, hồ sơ `ATTENTION_REQUIRED`, không tự hoàn tiền hoặc cộng lại tồn shop. Seller dùng quy trình thu hồi có giám sát hiện có, kiểm tra thực tế rồi quyết định bước xử lý tiếp. Nếu muốn giao công việc mới, phải kết thúc/hủy công việc đang mở trước.

Hủy phương án:

```json
{ "version": 8, "reason": "Khách đổi phương án xử lý, cần thu hồi hoa thay đã nạp." }
```

## 6. Ảnh và lỗi FE cần xử lý

`evidence.items` có `id`, `createdAt`, `source` (`COMPLAINT`/`STAFF_TASK`), `taskId` và `url`. 20 ảnh/trang, sắp xếp `(createdAt,id)`; dùng `evidencePage` để tải tiếp, xem `hasMore`.

- Seller/Admin/staff: GET đúng URL API trong response bằng phiên/Bearer phù hợp.
- Customer: URL ảnh là POST `/api/receipts/complaints/{complaintId}/evidence/{attachmentId}`, body `{ orderId, trackingToken }`; lấy Blob để hiển thị. Không gọi URL bằng `<img src>` trực tiếp vì thiếu body capability.
- FE proxy chỉ cho phép các route evidence cụ thể ở tài liệu này, không mở allowlist URL tùy ý. Không đổi ảnh private sang public.
- `404`: sai/ngoài quyền/không tồn tại. `409`: phiên bản cũ, task/ô chưa đủ điều kiện hoặc có thao tác đang mở — tải lại trạng thái. `400`: body/lựa chọn/báo cáo không hợp lệ. `503` ảnh: chưa tải được ảnh, hiển thị lỗi/thử lại; không coi là “chưa có ảnh”.

Không log Bearer, X-Kiosk-Key, trackingToken hoặc mã mở ô. Các API đọc thông tin riêng tư trả `Cache-Control: no-store`.

## 7. Triển khai và nghiệm thu

Migration mới: `db/migrations/20261116_z_flower_quality_complaints.sql`, chạy **một lần** sau các migration hiện có đến `20261116_seller_preorder_accessory_inventory.sql`. Backup DB trước, `PGCLIENTENCODING=UTF8`, `psql -v ON_ERROR_STOP=1 -f ...`; chỉ publish API mới sau khi migration thành công. Không cần thay firmware cho purpose mở cửa.

SQL kiểm tra sau migration (ASCII, tránh lỗi encoding console):

```sql
SELECT
  to_regclass('ordering.complaint_handling') IS NOT NULL AS handling_table,
  to_regclass('kiosk_ops.staff_quality_tasks') IS NOT NULL AS quality_tasks,
  to_regclass('kiosk_ops.quality_replacements') IS NOT NULL AS replacement_table,
  to_regprocedure('flow.review_quality_complaint(uuid,uuid,integer,integer,text,text)') IS NOT NULL AS review_function,
  to_regprocedure('flow.expire_quality_replacements()') IS NOT NULL AS expiry_function;
```

Xuất OpenAPI từ API mới bằng workflow export của repo, rồi FE regenerate contracts; không tự sửa file types đã sinh.

Nghiệm thu: phản ánh sau nhận → tiếp nhận/giao đúng staff → báo cáo không ảnh vẫn được → trả việc/duyệt; hoa thay nạp đúng ô và giữ riêng → Seller duyệt → đúng Customer/đúng kiosk mở nhận → hết quyền nhận; sai shop/staff/biên nhận bị chặn; ảnh liên kết đúng và lỗi storage không thành rỗng; hủy/hết hạn giữ lịch sử, yêu cầu thu hồi; hoàn tiền vẫn qua quy trình xét duyệt/chi hiện có.
