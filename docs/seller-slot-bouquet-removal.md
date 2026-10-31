# Seller thu hồi hoa khỏi ô

Chỉ Seller đang sở hữu kiosk được gọi:

```http
DELETE /api/me/kiosks/{kioskId}/slots/{slotId}/bouquet
Content-Type: application/json
```

```json
{
  "bouquetId": "<bouquetId hiện tại từ GET danh sách ô>",
  "reason": "Thu hồi bó hoa cũ để nạp hoa mới",
  "confirmedRemoved": true
}
```

Seller ID và người thực hiện lấy từ phiên đăng nhập. Thân JSON bắt buộc với DELETE; Swagger mô tả request body và hai path parameters từ endpoint thực tế.

Chỉ gọi sau khi Seller đã lấy hoa thật ra khỏi ô. Đây là xác nhận vận hành, không phải xác minh bằng cảm biến: API không phát lệnh MQTT, không tạo unlock token hoặc sự kiện mở/đóng cửa. Không yêu cầu ảnh cho API này. Luồng trả hoa có ảnh hiện tại vẫn là nghiệp vụ riêng.

Kết quả 200 có `status: RENTED_EMPTY`, `bouquetId: null`, `removedBouquetId` là bó vừa thu hồi, `zone` là zone hiện tại. Bó cũ được giữ với trạng thái `RETURNED` (nếu đã `DAMAGED` thì giữ `DAMAGED`), kèm inventory log `RETURN_SELLER` và audit `SELLER_SLOT_BOUQUET_REMOVED` ghi người thực hiện/lý do. Không xóa mẫu hoa, đơn hàng, chi tiết đơn, khoản thu, khoản hoàn hoặc sổ cái.

- Chấp nhận ô `STOCKED` chưa có đơn đang sử dụng hoặc `PENDING_REMOVAL`; ô phải vẫn thuộc kiosk/Seller và có phân công ACTIVE.
- Chặn ô HELD/FAULT/LOCKED/PENDING_RELEASE; chặn hold và mọi liên kết đơn ngoài EXPIRED/CANCELLED/DISPENSE_FAILED/REFUNDED. Chặn lệnh mở cửa đang có hiệu lực hoặc cửa đã mở chưa đóng.
- Lease hết hạn không cấm thu hồi khi ô vẫn được gán cho Seller; không vì thu hồi mà gia hạn lease hoặc cấp lại quyền bán/nạp.
- `bouquetId` bắt buộc giúp chống yêu cầu dùng dữ liệu cũ. Gọi lại cho đúng bó đã thu hồi bởi cùng Seller trả thành công khi ô vẫn trống, không ghi log lần hai. Nếu đã nạp bó mới, yêu cầu cũ bị 409.
- Không tự đổi/xóa zone. Nếu `zone` đang null, sau khi thu hồi gọi `PUT /api/me/kiosks/{kioskId}/slots/{slotId}/zone` với `{"zone":"WALK_IN"}` hoặc `{"zone":"PREORDER"}` trước khi nạp.

Phản hồi: 400 nếu thiếu bouquetId/xác nhận/lý do hợp lệ (5–500 ký tự); 401/403 nếu phiên/quyền không hợp lệ; 404 nếu kiosk/ô ngoài phạm vi quản lý; 409 nếu trạng thái hoặc bouquetId thay đổi/ô đang được xử lý.

Triển khai: áp dụng `db/migrations/20261031_seller_remove_slot_bouquet.sql` rồi publish `FloraBot.Api`. Không cần thay Gateway hay khởi tạo lại database. Migration chỉ thêm hàm SQL và không dọn ô đang có hoa khi triển khai.

Kiểm thử tích hợp dùng PostgreSQL trong database riêng `florabot_slot_removal_tests`, dịch vụ Valkey/RabbitMQ thử nghiệm. Không gửi lệnh thiết bị hay giao dịch thanh toán thật. Test kiểm tra quyền/tenant, STOCKED/PENDING_REMOVAL, giữ zone null/PREORDER/WALK_IN, chặn đơn/cửa đang hoạt động, gửi đồng thời/gửi lại và bảo toàn đơn/hoàn tiền.

Nhóm test có thể chạy riêng bằng `dotnet test tests/FloraBot.SlotRemoval.Tests/FloraBot.SlotRemoval.Tests.csproj`, dùng `TEST_DATABASE_URL`, `TEST_VALKEY_URL` và cấu hình RabbitMQ trỏ vào dịch vụ thử nghiệm. Project này liên kết cùng fixture/API test gốc; không sửa logic production để làm test chạy. Project test chung hiện có lỗi biên dịch trong KioskOnboardingTests vì còn dùng DTO chia số ô cũ, nên không thể coi kết quả nhóm này là toàn bộ suite đã đạt.

Để xuất OpenAPI thật, đặt `EXPORT_OPENAPI_PATH` thành đường dẫn file mong muốn rồi chạy test `DeleteContractExportsJsonBodyAndPathParameters`. Không sửa thủ công file contract generated của FE.
