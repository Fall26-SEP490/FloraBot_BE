# Thuê kiosk theo số ô và bảng giá từng loại

**Tài liệu lịch sử cho bản ngày 04/11/2026.** Quy tắc mới dùng một giá chung/ô và hỗ trợ Seller gửi yêu cầu đổi cơ cấu ô; xem [hợp đồng mới](kiosk-uniform-price-layout-change-for-fe.md).

Quyết định nghiệp vụ ngày 04/11/2026. Áp dụng cho Customer thuê lần đầu và Seller thuê thêm kiosk. Tất cả số tiền là VND, đơn giá tính cho một năm; kỳ thuê bắt đầu khi bàn giao.

## 1. Manager cấu hình bảng giá

Đọc bảng giá bằng API hiện có:

```http
GET /api/kiosk-packages
```

**Response đã đổi từ danh sách gói sang một object bảng giá.** FE cần cập nhật contract trước khi sử dụng bản BE này.

```json
{
  "walkInAnnualPrice": 100000,
  "preorderAnnualPrice": 120000,
  "version": 1,
  "configured": true,
  "updatedAt": "2026-11-04T03:00:00Z",
  "termMonths": 12,
  "minSlots": 1,
  "maxSlots": 50
}
```

Các giá trên chỉ là ví dụ. Chưa cấu hình thì hai đơn giá và `updatedAt` là `null`, `configured=false`, `version=0`; FE hiển thị “Chưa có bảng giá” và chưa cho gửi hồ sơ/thanh toán.

Chỉ Operations Manager được cập nhật:

```http
PUT /api/operations/kiosk-pricing
Content-Type: application/json
```

```json
{
  "walkInAnnualPrice": 100000,
  "preorderAnnualPrice": 120000,
  "expectedVersion": 0
}
```

Lần đầu dùng `expectedVersion=0`; lần sau dùng `version` vừa đọc. Hai giá là số nguyên dương. Nếu Manager khác đã sửa, API trả 409: tải lại bảng giá và cho người dùng xem trước khi gửi lại. Không tự ghi đè bằng phiên bản cũ.

## 2. Người thuê nhập số ô và thấy tổng phí

FE cho nhập `walkInSlots` và `preorderSlots`; từng số không âm, tổng từ 1 đến 50. Có thể chọn toàn Walk-in hoặc toàn Pre-order.

```text
Tổng ô = walkInSlots + preorderSlots
Phí một năm = walkInSlots × walkInAnnualPrice
             + preorderSlots × preorderAnnualPrice
```

Ví dụ 2 ô Walk-in giá 100.000 và 2 ô Pre-order giá 120.000: tổng 4 ô, phí 440.000 VND/năm. FE hiển thị đơn giá, số lượng từng loại, tổng và kỳ thuê trước nút tiếp tục. BE tự tính lại, không nhận tổng tiền từ FE.

Customer thuê lần đầu dùng API cũ:

```http
POST /api/member/kiosk-applications
```

```json
{
  "shopName": "Shop Hoa Dĩ An",
  "phone": "0901234567",
  "contactEmail": "shop@example.com",
  "installationAddress": "Địa chỉ lắp đặt kiosk",
  "latitude": 10.889361,
  "longitude": 106.773372,
  "bankName": "MB Bank",
  "bankHolder": "NGUYEN VAN A",
  "bankAccountNumber": "0123456789",
  "walkInSlots": 2,
  "preorderSlots": 2,
  "pricingVersion": 1
}
```

Seller thuê thêm dùng API cũ; shop, liên hệ và ngân hàng lấy từ Seller đang đăng nhập:

```http
POST /api/me/kiosk-applications
```

```json
{
  "installationAddress": "Địa chỉ kiosk thứ hai",
  "latitude": 10.889361,
  "longitude": 106.773372,
  "walkInSlots": 2,
  "preorderSlots": 2,
  "pricingVersion": 1
}
```

Không gửi `sellerId`, `annualFee` hoặc đơn giá trong body. `packageId` cũ đã được thay bằng nhu cầu số ô; trường nullable cũ chỉ được giữ để tương thích đầu vào và không quyết định giá. ID được BE sinh.

Response hồ sơ có `totalSlots`, `walkInSlots`, `preorderSlots`, `annualFee`, `walkInAnnualPrice`, `preorderAnnualPrice`, `pricingVersion`, `pricingMode="SLOT_PRICING"`, `paymentId`, `status="AWAITING_PAYMENT"`. Dùng **giá trong hồ sơ** để hiển thị lại hồ sơ, không lấy bảng giá mới để tính lại phí của hồ sơ cũ.

Lỗi cần xử lý:

| HTTP | Xử lý FE |
|---|---|
| 400 | Kiểm tra số ô, phiên bản hoặc thông tin hồ sơ thiếu/sai. |
| 403 | Tài khoản không có quyền thực hiện thao tác. |
| 409 | Bảng giá đã đổi hoặc có hồ sơ đang xử lý; tải lại dữ liệu và hiển thị nguyên nhân. |
| 503 | Manager chưa cấu hình bảng giá hoặc dịch vụ thanh toán chưa sẵn sàng. |

## 3. Thanh toán và xử lý hồ sơ giữ các bước hiện có

Sau khi chấp nhận tổng phí và tạo hồ sơ, người sở hữu dùng:

```http
POST /api/member/kiosk-applications/{applicationId}/payment-link
```

Không cần body chấp nhận báo giá. Đây cũng là route thanh toán cho hồ sơ thuê thêm của Seller, BE kiểm tra chủ hồ sơ. FE dùng response thanh toán hiện có và tải lại trạng thái từ BE; trang callback của PayOS không phải bằng chứng đã thanh toán.

```text
AWAITING_PAYMENT → PAID_REVIEW → CONFIRMED → SCHEDULED
→ INSTALLING → AWAITING_HANDOVER → ACTIVE
```

Sau thanh toán thành công, Manager xác nhận liên hệ bằng API cũ:

```http
POST /api/operations/kiosk-applications/{applicationId}/confirm
```

```json
{ "note": "Đã xác nhận thông tin liên hệ và nhu cầu lắp đặt" }
```

Không có bước Manager báo giá riêng từng hồ sơ. Không thêm `AWAITING_QUOTE` hoặc `QUOTED` vào bộ lọc trạng thái.

## 4. Manager tạo kiosk đúng hồ sơ

Chỉ tạo kiosk cho hồ sơ đã thanh toán và `CONFIRMED`:

```http
POST /api/operations/kiosks/enroll
```

```json
{
  "applicationId": "<ID hồ sơ>",
  "code": "K-DIAN-01",
  "name": "Kiosk Shop Hoa Dĩ An",
  "hardwareId": "<ID phần cứng>",
  "mqttClientId": "<MQTT client ID>",
  "region": "HCM"
}
```

BE tự tạo số ô và zone theo hồ sơ. Không nhập lại phân bổ Walk-in/Pre-order ở form này. Nếu vẫn gửi `slotCount`, số đó phải bằng tổng ô trên hồ sơ. Response trả tổng và số ô từng loại; `apiKey` chỉ được chuyển cho người cấu hình thiết bị, không đưa vào log hay màn hình công khai.

Đọc kiosk đủ điều kiện cho đúng hồ sơ:

```http
GET /api/operations/eligible-kiosks?applicationId=<ID hồ sơ>
```

Kiosk phải trống, chưa cho thuê, đúng tổng và phân bổ zone; kiosk được tạo dành cho hồ sơ khác không được chọn. Tham số `packageId` cũ chỉ còn phục vụ hồ sơ cũ. Phân công, lắp đặt, bằng chứng, báo cáo và bàn giao dùng các API hiện có.

## 5. Phân bổ loại ô cố định

Số ô Walk-in và Pre-order được chốt ngay khi gửi hồ sơ thuê. Manager tạo kiosk đúng phân bổ đã đăng ký; không nhập lại hoặc điều chỉnh phân bổ tại bước tạo kiosk, xếp lịch hay bàn giao.

Sau khi tạo ô, cả Seller và Operations Manager đều không được đổi `zone`, kể cả khi ô trống. Không hiển thị nút đổi loại ô cho bất kỳ vai trò nào. Route Seller cũ `PUT /api/me/kiosks/{kioskId}/slots/{slotId}/zone` đã bỏ; không có route thay thế cho Manager.

BE còn chặn đổi zone và phân bổ hồ sơ bằng trigger database. Việc nạp hoa, bán hoa và làm trống ô vẫn cập nhật trạng thái sử dụng bình thường, nhưng giữ nguyên loại ô.

Migration giữ nguyên zone lịch sử, gồm cả `null`; không tự phân loại hoặc cho phép Manager sửa ô cũ. Ô chưa phân loại không được dùng để nạp Walk-in/Pre-order theo các kiểm tra hiện có.

Phí và kỳ thuê đã chốt giữ nguyên. Bảng giá mới chỉ dùng cho hồ sơ thuê mới; thay đổi này chưa cung cấp API gia hạn.

## 6. Cập nhật contract và triển khai

1. Sao lưu database VPS rồi chạy migration `db/migrations/20261104_requested_kiosk_layout_pricing.sql` đã sao chép lên VPS; không chạy lại bộ khởi tạo database.
2. Publish và triển khai `FloraBot.Api`, recycle App Pool `FloraBot-API`. Phần thay đổi này không yêu cầu publish Gateway.
3. Operations Manager nhập bảng giá qua API mới; không có giá mặc định được tự thêm. Kiểm tra `GET /api/kiosk-packages` trả `configured=true`.
4. Xuất OpenAPI từ BE mới. FE tải lại schema và chạy quy trình generate contracts của dự án; không sửa tay generated types. Cập nhật đồng bộ form thuê, bảng giá Manager, bộ lọc kiosk và quy định zone cố định.
5. Kiểm tra tạo hồ sơ cả hai vai trò, tổng phí/phiên bản, thanh toán, tạo đúng kiosk, chặn đổi loại ô ngay cả khi ô trống. Kết quả test giả lập thanh toán hoặc thiết bị không thay cho kiểm chứng PayOS và kiosk thật.
