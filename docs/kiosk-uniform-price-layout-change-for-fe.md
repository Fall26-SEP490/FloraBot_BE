# Kiosk: một giá mỗi ô và yêu cầu đổi cơ cấu ô

Áp dụng sau migration `20261106_uniform_slot_price_zone_requests.sql` và bản publish `FloraBot.Api` mới. Các hồ sơ, giao dịch và hợp đồng cũ giữ nguyên phí đã chốt.

## Thuê kiosk

`GET /api/kiosk-packages` trả `annualSlotPrice`, `version`, `configured`, `termMonths`, `minSlots`, `maxSlots`. Nếu bảng giá cũ từng có hai mức khác nhau, `configured=false` cho đến khi Manager đặt giá chung. Manager dùng `PUT /api/operations/kiosk-pricing` với body `{ "annualSlotPrice": 10000, "expectedVersion": <version từ GET> }`; nếu chưa từng có bảng giá thì version bằng 0. Giá 10.000 chỉ là ví dụ, hệ thống không tự đặt giá.

Form thuê lần đầu `POST /api/member/kiosk-applications` và thuê thêm `POST /api/me/kiosk-applications` vẫn gửi `walkInSlots`, `preorderSlots`, `pricingVersion`. Tổng phí năm hiển thị là `(walkInSlots + preorderSlots) × annualSlotPrice`. Ví dụ 2 Walk-in + 2 Preorder, giá 10.000 mỗi ô: 40.000 VND. BE tính lại và lưu snapshot trên hồ sơ, đơn thanh toán; không nhận giá từ FE.

## Đổi cơ cấu sau khi đã thuê

Seller xem các ô hiện tại bằng API kiosk/slot đang có, rồi gửi yêu cầu:

```http
POST /api/me/kiosks/{kioskId}/layout-requests
Content-Type: application/json

{"walkInSlots":3,"preorderSlots":1,"reason":"Cần thêm ô bán sẵn"}
```

Tổng số ô phải bằng số ô vật lý của kiosk và mỗi kiosk chỉ có một yêu cầu `PENDING`. Seller xem lịch sử tại `GET /api/me/kiosks/{kioskId}/layout-requests`. Manager xem `GET /api/operations/kiosk-layout-requests?status=PENDING`.

Manager chọn **chính xác ô nào sẽ là PREORDER** khi duyệt. Với tủ bốn ô yêu cầu 3 Walk-in + 1 Preorder, `preorderSlotIds` gồm đúng một UUID của ô sẽ giữ thành Preorder. Những ô còn lại thành Walk-in:

```http
POST /api/operations/kiosk-layout-requests/{requestId}/review
Content-Type: application/json

{"preorderSlotIds":["<slotId>"],"note":"Đã kiểm tra các ô cần đổi đang trống"}
```

Từ chối: `{ "preorderSlotIds": null, "note": "Lý do từ chối" }`. Mảng rỗng `[]` nghĩa là duyệt phương án không có ô Preorder. Chỉ những ô **thực sự đổi zone** phải ở trạng thái `RENTED_EMPTY`, không có hoa hoặc đơn đang giữ. Nếu còn hoa/đơn, API trả 409; cần xử lý ô theo quy trình nghiệp vụ hiện có rồi duyệt lại. Toàn bộ việc đổi diễn ra trong một transaction, có audit; phí của kỳ thuê đang chạy vẫn giữ nguyên.

Các ô thuê cũ có `zone=null` cần được Manager đưa vào một yêu cầu phù hợp khi chúng trống; Seller không trực tiếp sửa zone.
