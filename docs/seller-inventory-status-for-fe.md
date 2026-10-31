# Hướng dẫn FE: trạng thái hoa tại tủ sau thanh toán

API hiện tại được mở rộng, không tạo route mới:

```http
GET /api/me/kiosks/{kioskId}/slots
```

Quyền: Seller đăng nhập sở hữu kiosk. Response giữ các trường cũ và thêm 5 trường nullable:

| Trường | Ý nghĩa |
|---|---|
| `status` (cũ) | Trạng thái ô tủ; không phải trạng thái mẫu hoa hoặc khoản tiền |
| `bouquetStatus` | Trạng thái bó thực tế đang gắn với ô: STOCKED, HELD, SOLD…; null khi ô trống |
| `orderId` | UUID đơn đang giữ bó; nếu ô chờ thu hồi thì là đơn gần nhất gắn với chính bó và ô này |
| `orderCode` | Mã đơn để hiển thị/đối chiếu |
| `orderStatus` | Trạng thái đơn: AWAITING_PAYMENT, PAID, DISPENSING, DISPENSE_FAILED… |
| `paymentStatus` | Trạng thái khoản thu CHARGE của checkout đó; không lấy trạng thái khoản REFUND |

Ví dụ phần dữ liệu liên quan sau xác nhận thanh toán, khách chưa lấy hoa:

```json
{
  "id": "40000000-0000-0000-0001-000000000001",
  "slotCode": "A01",
  "zone": "WALK_IN",
  "status": "HELD",
  "bouquetId": "01a14b27-95bd-7953-9efa-df504c078f12",
  "bouquetStatus": "SOLD",
  "orderId": "01a16876-b200-722d-b4d1-5cad0af74943",
  "orderCode": "FB261031-ABC123",
  "orderStatus": "PAID",
  "paymentStatus": "SUCCEEDED"
}
```

Các ID trên là ví dụ, không phải dữ liệu hiện tại của VPS. Ví dụ chỉ trích các trường cần giải thích; response thật vẫn có thông tin kiosk, mẫu hoa, QR, giá snapshot… như trước.

## Quy tắc hiển thị

Nên tách nhãn **ô tủ** và nhãn **giao dịch/bó hoa**. Nhãn FAULT/LOCKED/PENDING_REMOVAL phải luôn hiện nếu có; không để nhãn đã bán che mất sự cố.

| Điều kiện từ API | Nhãn bán hàng/hướng dẫn |
|---|---|
| `status=RENTED_EMPTY` và `bouquetId=null` | Ô trống — có thể nạp theo zone và quyền hiện tại |
| `status=STOCKED`, `bouquetStatus=STOCKED`, `orderId=null` | Đang bán / Có hoa sẵn |
| `status=HELD`, `bouquetStatus=HELD`, `orderStatus=AWAITING_PAYMENT`, `paymentStatus=PENDING` | Khách đang thanh toán |
| `bouquetStatus=SOLD`, `orderStatus=PAID`, `paymentStatus=SUCCEEDED` | Đã bán — chờ khách nhận |
| `bouquetStatus=SOLD`, `orderStatus=DISPENSING`, `paymentStatus=SUCCEEDED` | Đã bán — đang xử lý nhận hoa |
| `orderStatus=DISPENSE_FAILED` | Nhận hoa thất bại — kiểm tra hoàn tiền/thu hồi hoa; khoản thu SUCCEEDED không có nghĩa đơn đã giao thành công |
| `status=PENDING_REMOVAL` | Chờ thu hồi hoa; nếu có đơn thất bại/hủy/hoàn thì kèm thông tin đó, không mặc định là hoa tươi hết hạn |
| `status=PENDING_RELEASE` | Chờ thu hồi hoa để trả ô |
| `status=FAULT` / `LOCKED` | Có sự cố / Đang khóa; kèm nhãn giao dịch nếu có |
| Thiếu trường mới hoặc trạng thái chưa đối chiếu được | Đang kiểm tra trạng thái — tải lại; không tự suy ra đã thanh toán chỉ từ HELD hoặc SOLD |

Không đổi `Product.status` từ ACTIVE sau khi bán một bó: Product là mẫu hoa còn dùng để làm/nạp các bó khác. Mẫu ACTIVE vẫn được FE danh mục hiển thị là đang kinh doanh. Nhãn đã bán thuộc màn hình quản lý bó hoa/ô tủ.

Sau khi khách lấy hoa và quy trình thiết bị xác nhận hoàn tất, ô về RENTED_EMPTY, bouquetId/bouquetStatus/orderId/orderCode/orderStatus/paymentStatus của ô đều null. Lịch sử đơn vẫn ở màn hình đơn hàng, không để nhãn đã bán tồn tại trên ô đã trống hoặc đã nạp bó mới.

## Tải lại và các trường hợp đặc biệt

- FE cần tải lại GET này khi nhận refresh SignalR, sau thao tác nạp/thu hồi và khi quay lại màn hình. Có thể polling 5 giây khi có ô HELD, 30 giây khi đang mở màn hình bình thường. HTTP `Cache-Control: no-store` không tự xóa cache TanStack Query.
- Chỉ đếm đang bán bằng trạng thái ô/bó đang có sẵn; không đếm Product ACTIVE thành số bó tồn kho. Nếu đếm đã bán trong màn hình ô tủ, đó chỉ là các bó đã bán còn nằm trong ô; doanh số/lịch sử bán phải lấy từ đơn/báo cáo.
- Khi ô có holdOrderId, BE chọn đúng đơn đó, không lấy ngẫu nhiên đơn mới nhất. Nếu đang bán lại sau lần thanh toán hết hạn, các trường đơn cũ sẽ null.
- Với ô chờ thu hồi không còn holdOrderId, BE chọn đơn mới nhất theo created_at, rồi id, có liên kết đúng bouquetId/slotId/kiosk/Seller hiện tại. Không dùng orderCode PayOS để đoán trạng thái.
- Các trường nullable có thể null khi không có khoản thu/đơn hợp lệ; FE không đổi null thành PENDING/SUCCEEDED.
- Không trả trackingToken, danh tính khách hoặc thông tin ngân hàng vào response kho.

## Triển khai và kiểm thử

Phần mở rộng response chỉ cần publish FloraBot.Api; không thêm migration. Migration của chức năng DELETE thu hồi ô là thay đổi riêng (`20261031_seller_remove_slot_bouquet.sql`).

OpenAPI sinh từ endpoint thật đã chứa các trường mới; FE xuất lại schema và chạy quy trình generate contracts của FE, không sửa tay generated types. Quy trình kiểm thử dùng chung bài `DeleteContractExportsJsonBodyAndPathParameters` để xuất toàn bộ OpenAPI qua biến EXPORT_OPENAPI_PATH; bài `OpenApiContainsNullableSaleFieldsAndReadDoesNotLeakPickupOrBankSecrets` kiểm tra riêng các trường kho và dữ liệu nhạy cảm không được trả.

Nhóm kiểm thử trong `tests/FloraBot.SlotRemoval.Tests` liên kết cả SellerInventoryReadTests và SellerSlotRemovalTests. Dùng PostgreSQL/database thử nghiệm riêng, kiểm tra trước/sau `flow.checkout_paid`, đơn thất bại/charge/refund, lịch sử cũ, nạp bó mới, tenant và hợp đồng OpenAPI. Sự kiện cửa trong test chỉ là đầu vào giả lập cho hàm chuyển trạng thái SQL; không chứng minh cửa thật hoặc PayOS thật đã hoạt động.
