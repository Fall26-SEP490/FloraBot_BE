# Tồn bó tại shop: hướng dẫn FE và triển khai

## Nghiệp vụ đã chốt

`quantity` là số bó thực tế còn ở shop của một mẫu hoa. Đây là số nguyên không âm,
không phải số ô trong kiosk hoặc tổng số bó đã bán.

- Tạo/sửa mẫu: Seller nhập số tồn thực tế.
- Nạp thành công một bó Walk-in vào kiosk: trừ 1.
- Fulfill một đơn CUSTOM đã thanh toán vào ô PREORDER: trừ 1, kể cả nhân viên nạp.
- Chấp nhận báo giá, thanh toán hoặc bán bó đã nằm trong kiosk: không trừ thêm.
- Thu hồi, hoàn tiền, hết hạn hoặc hủy: không tự cộng lại. Seller tự chốt lại tồn
  sau khi nhận hoa về shop.
- Không đủ tồn: HTTP 409; thao tác nạp không được ghi nhận dở dang. Với bảo trì ô
  có cảm biến, phiên nạp vẫn giữ để thử lại sau khi cập nhật tồn.

## API hiện có được bổ sung

Không tạo route riêng cho quantity. Các route và quyền hiện tại được giữ nguyên.

### Seller tạo mẫu

`POST /api/me/products`

```json
{
  "kioskId": "<kiosk UUID của Seller>",
  "name": "Hoa hồng đỏ",
  "description": "Mẫu hoa hồng đỏ",
  "price": 350000,
  "tags": ["HONG"],
  "lengthCm": 30,
  "widthCm": 20,
  "heightCm": 40,
  "quantity": 5
}
```

Response vẫn là HTTP 201 với `{ "id": "<product UUID>" }`.
Bỏ qua `quantity` khi tạo thì mặc định bằng 0. Trạng thái mẫu và thao tác kích
hoạt mẫu tiếp tục theo luồng hiện có.

### Seller sửa mẫu

`PUT /api/sellers/{sellerId}/products/{productId}`

Body giữ các trường tên, mô tả, giá, tags, kích thước hiện tại và thêm `quantity`.
Không gửi `quantity`, hoặc gửi `null`, thì giữ nguyên tồn hiện có.
Gửi `quantity: 8` là đặt tồn thành 8, không phải cộng thêm 8.
Response vẫn là HTTP 200 với `{ "id": "<product UUID>" }`.

Số âm, số thập phân hoặc giá trị vượt giới hạn integer không hợp lệ (HTTP 400).
FE dùng input số nguyên, min=0, và chỉ gửi trường này khi Seller thực sự sửa tồn.

### Seller đọc danh sách mẫu

`GET /api/me/products` trả thêm `quantity` trên từng mẫu, ví dụ:

```json
{
  "id": "<product UUID>",
  "name": "Hoa hồng đỏ",
  "price": 350000,
  "status": "ACTIVE",
  "quantity": 5
}
```

FE đặt nhãn **Tồn tại shop**, tách khỏi **Tồn trong kiosk/Đang bán/Đã bán**.
Sau khi nạp thành công, tải lại danh sách để nhận tồn mới. HTTP 409 thì hiển thị
thông báo từ BE và tải lại tồn; không tự giả định thao tác nạp đã thành công.
Quantity không thay thế các điều kiện khác như quyền sở hữu, gói thuê, zone,
ô trống, trạng thái sản phẩm và thanh toán preorder.

## Triển khai VPS

Publish lại **FloraBot.Api**. Không cần publish Gateway, FE hoặc firmware chỉ vì
thay đổi quantity và API tải cấu hình ESP32. FE cần cập nhật form và lấy lại
OpenAPI để sinh contract theo quy trình của FE.

Migration mới duy nhất cho quantity:

`db/migrations/20261109_product_shop_quantity.sql`

Áp dụng migration trước khi chuyển IIS sang API mới. Không chạy lại toàn bộ các
migration cũ. Phần ESP32 không có migration riêng.

Sau khi đã chép file vào `C:\FloraBotMigration`, chạy trên PowerShell Administrator
của VPS. Không chép các dòng `PS C:\...>` vào lệnh:

```powershell
$ErrorActionPreference = 'Stop'
$migrationPath = 'C:\FloraBotMigration\20261109_product_shop_quantity.sql'
$psql = "$env:ProgramFiles\PostgreSQL\16\bin\psql.exe"
$pgDump = "$env:ProgramFiles\PostgreSQL\16\bin\pg_dump.exe"
if (-not (Test-Path -LiteralPath $migrationPath)) { throw 'Khong thay file migration.' }

$backupFile = "C:\FloraBotMigration\florabot-before-quantity-$(Get-Date -Format 'yyyyMMdd-HHmmss').dump"
& $pgDump -h 127.0.0.1 -p 5432 -U florabot -d florabot -Fc -f $backupFile
if ($LASTEXITCODE -ne 0) { throw 'Backup that bai; dung tai day.' }

& $psql -h 127.0.0.1 -p 5432 -U florabot -d florabot -v ON_ERROR_STOP=1 -f $migrationPath
if ($LASTEXITCODE -ne 0) { throw 'Migration that bai; dung va gui nguyen loi.' }

& $psql -h 127.0.0.1 -p 5432 -U florabot -d florabot -v ON_ERROR_STOP=1 -c "
SELECT
  EXISTS (SELECT 1 FROM information_schema.columns
    WHERE table_schema='catalog' AND table_name='flower_products' AND column_name='quantity') AS quantity_column,
  to_regclass('kiosk_ops.product_quantity_deductions') IS NOT NULL AS deduction_ledger,
  EXISTS (SELECT 1 FROM pg_trigger
    WHERE tgrelid='kiosk_ops.inventory_logs'::regclass
      AND tgname='inventory_logs_deduct_shop_product_quantity' AND NOT tgisinternal) AS deduction_trigger;"
if ($LASTEXITCODE -ne 0) { throw 'Khong xac minh duoc migration.' }
```

Kết quả xác minh phải có ba giá trị `t`. Sau khi chuyển bản API mới và recycle
App Pool `FloraBot-API`, kiểm tra `/health`, `/openapi/v1.json` và Seller
`GET /api/me/products`.

**Mẫu đã tồn tại có quantity = 0.** Seller cần nhập tồn thực tế trước lượt nạp
mới. Không tự cộng các bó đang trong kiosk vào kho shop.

Thiết lập và API ESP32 được hướng dẫn riêng trong
`docs/esp32-configuration-for-fe.md`. Chỉ publish API sẽ chưa đủ để tải file nếu
server chưa có broker công khai, CA và bí mật thiết bị đã được cấp hợp lệ.
