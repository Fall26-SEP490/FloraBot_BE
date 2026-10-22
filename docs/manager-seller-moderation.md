# Manager quản lý trạng thái Seller

Bốn API BE, policy `OperationsManager`:

| Method | Route | Chức năng |
| --- | --- | --- |
| GET | `/api/operations/sellers?status=ACTIVE&search=shop&page=1&pageSize=25` | Danh sách shop; `items`, `page`, `hasMore` |
| GET | `/api/operations/sellers/{sellerId}` | Chi tiết shop và gói; không trả ngân hàng/password/device key |
| PATCH | `/api/operations/sellers/{sellerId}/status` | Khóa/mở shop; body bên dưới |
| GET | `/api/operations/sellers/{sellerId}/status-history?page=1&pageSize=20` | Lịch sử actor/from/to/reason/time; phân trang |

```json
{"status":"SUSPENDED","reason":"Vi phạm quy chuẩn đóng gói đã được xác minh"}
```

Mở lại dùng `status: ACTIVE` và lý do khắc phục. Lý do sau trim phải dài 5–1000 ký tự.

Chỉ cho `ACTIVE -> SUSPENDED` hoặc `SUSPENDED -> ACTIVE`; gửi trùng trả 409, không ghi audit trùng. Mở lại cần gói hiện tại ACTIVE và còn hạn theo `flow.package_valid` (ưu tiên timestamp chính xác). API không duyệt onboarding, gia hạn gói, đổi vai trò, đóng/xóa shop, đổi ngân hàng, chuyển tiền hoặc sửa đơn cũ. Admin vẫn dùng các API Admin riêng.

Shop bị khóa: Seller vẫn xác thực được tài khoản; tạo/sửa sản phẩm bị chặn. Không tự giải phóng kiosk hoặc xóa dữ liệu. Cơ chế refresh gói hiện có giữ nguyên SUSPENDED.

Trạng thái và audit được ghi trong một transaction có row lock. History do module Notify đọc, bao gồm bản ghi cũ thiếu reason. Không cần migration mới.

## Kiểm thử local

Cần PostgreSQL test đã khởi tạo đủ migration. Đặt `TEST_DATABASE_URL` trỏ CSDL test riêng, không dùng production. Chạy:

```powershell
dotnet test tests/FloraBot.Api.Tests/FloraBot.Api.Tests.csproj --filter 'FullyQualifiedName~ManagerSellerModerationTests'
```

22/10/2026: 11 test mới PASS, bộ gộp 38/38 PASS cùng CatalogSubscription, GoogleAuthAndHierarchy, ProductCatalog, ProductPhotos, SellerAccessories. Kiểm tra DTO/schema metadata, phân trang, role/anonymous, validation, trạng thái cấm, expiry timestamp, package inactive, concurrent duplicate, audit actor/reason, business guard và giữ nguyên hạn gói. Format touched files PASS.

Giới hạn: đây là HTTP TestServer + PostgreSQL riêng, chưa nghiệm thu VPS/FE/IoT thật. Kiểm thử kiến trúc toàn module có một test lỗi trên baseline ở các module Catalog/chat cũ; không có OperationsSellerEndpoints hoặc SellerStatusHistory trong danh sách lỗi. Chưa kiểm thử cưỡng bức lỗi ghi audit; không đưa test hook vào API production.
