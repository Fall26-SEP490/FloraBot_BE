# FloraBot — Annual Kiosk Rental APIs for FE

Tài liệu này mô tả các API thuê kiosk theo năm trong BE. Các request cần đăng nhập gửi cookie/token theo cơ chế xác thực hiện tại của dự án.

## Quy tắc nghiệp vụ

- Kỳ thuê là **12 tháng**.
- Walk-in và Pre-order dùng cùng đơn giá mỗi ô.
- Customer đăng ký kiosk lần đầu; Seller đang hoạt động có thể gửi yêu cầu thuê thêm kiosk.
- Seller ID lấy từ token, FE không truyền Seller ID khi tạo yêu cầu kiosk thứ hai.
- Tổng số ô từ 1 đến 50; hồ sơ lưu số ô Walk-in và Pre-order theo nhu cầu.
- Giá/kỳ thuê được chụp vào hồ sơ lúc tạo. Thay đổi bảng giá sau đó không đổi hồ sơ đã gửi.
- Hợp đồng thuê năm gắn với kiosk. Gia hạn chỉ gia hạn kiosk được chọn.

## 1. Đọc bảng giá

```http
GET /api/kiosk-packages
```

Không cần đăng nhập. Response có các trường chính:

```json
{
  "annualSlotPrice": 10000,
  "version": 1,
  "configured": true,
  "updatedAt": "2026-11-07T10:00:00Z",
  "termMonths": 12,
  "minSlots": 1,
  "maxSlots": 50
}
```

FE tính số tiền xem trước:

```text
annualFee = annualSlotPrice * (walkInSlots + preorderSlots)
```

Gửi lại `version` dưới tên `pricingVersion` khi tạo hồ sơ. Nếu giá đổi giữa lúc xem và gửi, BE trả lỗi xung đột; FE cần tải lại bảng giá và cho người dùng xác nhận lại tổng tiền.

Operations Manager cập nhật giá bằng:

```http
PUT /api/operations/kiosk-pricing
```

```json
{
  "annualSlotPrice": 10000,
  "expectedVersion": 1
}
```

## 2. Tạo hồ sơ thuê

### Customer thuê kiosk lần đầu

```http
POST /api/member/kiosk-applications
```

Chỉ tài khoản CUSTOMER. `packageId` hiện có thể gửi `null`; bảng giá và số lượng ô được xác định bởi `pricingVersion`, `walkInSlots`, `preorderSlots`.

```json
{
  "packageId": null,
  "shopName": "Shop Hoa",
  "phone": "0901234567",
  "contactEmail": "shop@example.com",
  "installationAddress": "Địa chỉ lắp đặt",
  "latitude": 10.78,
  "longitude": 106.69,
  "bankName": "MB Bank",
  "bankHolder": "NGUYEN VAN A",
  "bankAccountNumber": "0123456789",
  "walkInSlots": 2,
  "preorderSlots": 2,
  "pricingVersion": 1
}
```

### Seller thuê thêm kiosk

```http
POST /api/me/kiosk-applications
```

Chỉ tài khoản SELLER đang hoạt động. Seller ID, tên shop, điện thoại, email và thông tin ngân hàng được lấy từ hồ sơ/token hiện tại; body chỉ gửi địa điểm và nhu cầu ô.

```json
{
  "packageId": null,
  "installationAddress": "Địa chỉ kiosk mới",
  "latitude": 10.78,
  "longitude": 106.69,
  "walkInSlots": 2,
  "preorderSlots": 2,
  "pricingVersion": 1
}
```

Cả hai API trả về hồ sơ tạo thành công với `id`, `annualFee`, `totalSlots`, số ô từng zone, `paymentId` và `status` (thường bắt đầu ở `AWAITING_PAYMENT`). Lưu `id` làm `applicationId` cho các bước sau.

## 3. Thanh toán PayOS và đọc trạng thái

### Tạo liên kết thanh toán

```http
POST /api/member/kiosk-applications/{applicationId}/payment-link
```

Customer hoặc Seller là chủ hồ sơ đều dùng route này; không cần body. Response:

```json
{
  "orderCode": 123,
  "amount": 40000,
  "checkoutUrl": "https://pay.payos.vn/web/...",
  "qrCode": "..."
}
```

FE mở `checkoutUrl` hoặc hiển thị QR nếu có `qrCode`. Trạng thái thanh toán cuối cùng được xác nhận từ webhook PayOS; không đánh dấu thành công chỉ vì người dùng quay lại từ trang thanh toán.

### Lấy hồ sơ gần nhất của tài khoản

```http
GET /api/member/kiosk-applications/current
```

Yêu cầu đăng nhập CUSTOMER hoặc SELLER. Trả về hồ sơ mới nhất của tài khoản, có `paymentStatus`, tiến độ xác nhận/lắp đặt và kiosk được gán nếu đã có.

### Đối soát trạng thái thanh toán khi webhook cập nhật chậm

```http
POST /api/member/kiosk-applications/{applicationId}/reconcile
```

Chỉ gọi khi cần đồng bộ lại trạng thái PayOS. API không thay thế webhook và không dùng để xác nhận thanh toán giả lập.

### Hủy hồ sơ khi trạng thái còn cho phép

```http
POST /api/member/kiosk-applications/{applicationId}/cancel
```

```json
{ "reason": "Lý do hủy" }
```

## 4. Operations Manager xử lý hồ sơ

### Danh sách và chi tiết

```http
GET /api/operations/kiosk-applications?status=PAID_REVIEW&page=1&limit=25
GET /api/operations/kiosk-applications/{applicationId}
```

`status`, `page`, `limit` là query tùy chọn; giới hạn trang tối đa là 100.

### Xác nhận thông tin hồ sơ

```http
POST /api/operations/kiosk-applications/{applicationId}/confirm
```

```json
{ "note": "Đã xác nhận thông tin liên hệ" }
```

### Tìm kiosk trống đúng layout

```http
GET /api/operations/eligible-kiosks?applicationId={applicationId}
```

Trả về kiosk chưa được gán/đặt trước và có số ô, zone phù hợp với hồ sơ.

### Tạo kiosk mới gắn với hồ sơ (nếu cần)

```http
POST /api/operations/kiosks/enroll
```

```json
{
  "code": "K-NEW-01",
  "name": "Kiosk mới",
  "hardwareId": "HW-NEW-01",
  "mqttClientId": "kiosk-new-01",
  "region": "HCM",
  "slotCount": 4,
  "applicationId": "ID_HO_SO"
}
```

`applicationId` bắt buộc; `slotCount` nếu được gửi phải khớp tổng số ô hồ sơ. BE tạo ô và gán Walk-in/Pre-order theo yêu cầu đã duyệt.

### Xếp lịch kỹ thuật viên và kiosk

```http
POST /api/operations/kiosk-applications/{applicationId}/schedule
```

```json
{
  "technicianId": "ID_TECHNICIAN",
  "scheduledDate": "2026-11-10",
  "timeWindow": "09:00 - 12:00",
  "kioskId": "ID_KIOSK"
}
```

Các thao tác hồ sơ khác:

```http
POST /api/operations/kiosk-applications/{applicationId}/rework
POST /api/operations/kiosk-applications/{applicationId}/reject
POST /api/operations/kiosk-applications/{applicationId}/handover
```

Body lần lượt dùng `{ "reason": "..." }`, `{ "reason": "..." }` và `{}`. Handover hoàn tất việc bàn giao kiosk cho Seller.

## 5. Technician lắp đặt

```http
GET  /api/staff/kiosk-installations
GET  /api/staff/kiosk-installations/{applicationId}
POST /api/staff/kiosk-installations/{applicationId}/start
POST /api/staff/kiosk-installations/{applicationId}/evidence/upload
POST /api/staff/kiosk-installations/{applicationId}/submit
```

Các route yêu cầu role TECHNICIAN. Upload ảnh nghiệm thu trước khi gửi báo cáo lắp đặt.

## 6. Seller xem kiosk và hợp đồng năm

```http
GET /api/me/kiosks
GET /api/me/kiosks/{kioskId}/slots
GET /api/sellers/{sellerId}/subscriptions
```

`/api/me/kiosks` và `/slots` lấy Seller từ token. Lịch sử subscription hiện vẫn dùng `sellerId` trong URL; lấy ID này từ hồ sơ tài khoản. Danh sách subscription chỉ gồm hợp đồng gắn với kiosk, có `periodFrom`, `periodTo`, `price`, `status`, `kioskId`.

## 7. Gia hạn kiosk theo năm

### Tạo yêu cầu gia hạn cho kiosk cụ thể

```http
POST /api/me/kiosks/{kioskId}/renewals
```

Seller đang đăng nhập; không cần body. Response có `subscriptionId`, `kioskId`, `amount`, `periodFrom`, `periodTo`, `status` và `paymentLinkPath`.

### Tạo link PayOS cho gia hạn

```http
POST /api/sellers/{sellerId}/subscriptions/{subscriptionId}/payment-link
```

Không cần body. Dùng `paymentLinkPath` trả về từ API gia hạn; response có `checkoutUrl` và có thể có `qrCode`.

## Thứ tự tích hợp ngắn gọn

1. `GET /api/kiosk-packages` để lấy giá, giới hạn ô và `version`.
2. Customer gọi `POST /api/member/kiosk-applications`, hoặc Seller gọi `POST /api/me/kiosk-applications`.
3. Dùng `applicationId` gọi `POST /api/member/kiosk-applications/{id}/payment-link`; mở link/QR PayOS.
4. Đọc tiến độ bằng `GET /api/member/kiosk-applications/current`.
5. Operations Manager xác nhận hồ sơ, chọn/tạo kiosk, rồi xếp lịch lắp đặt.
6. Technician lắp đặt, gửi bằng chứng và báo cáo; Manager gọi handover.
7. Seller xem kiosk/hợp đồng qua `/api/me/kiosks` và `/api/sellers/{sellerId}/subscriptions`.
8. Khi đến hạn, Seller gọi API renewals cho kiosk cần gia hạn rồi tạo link PayOS theo `paymentLinkPath`.

## API thuê tháng đã nghỉ

FE không gọi các API cũ sau:

```text
GET  /api/packages
POST /api/sellers/{sellerId}/flows/subscribe
```

Gói dữ liệu lịch sử trong database vẫn được giữ để đối soát; API tạo kỳ thuê mới là theo năm/kiosk.

> Lưu ý triển khai: thay đổi annual-only và migration `20261108_annual_only_seller_leases.sql` phải được đưa lên môi trường tương ứng trước khi FE kiểm tra việc gỡ API tháng trên VPS.
