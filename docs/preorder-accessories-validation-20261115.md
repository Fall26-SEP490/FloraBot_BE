# Kết quả kiểm thử Pre-order phụ kiện — 2026-11-15

Kiểm thử trên PostgreSQL 16, Valkey và RabbitMQ trong Docker local. Database mới dùng riêng cho lượt này là `florabot_preorder_accessories_20261115_tests`, được tạo từ schema/seed/flows nền rồi áp dụng toàn bộ migration theo tên, gồm `20261115_preorder_accessories.sql`. Database `florabot` và các database test cũ được giữ nguyên.

## Kết quả cuối

| Nhóm | Đạt | Lỗi |
|---|---:|---:|
| PreorderAccessoryTests | 10 | 0 |
| WebPreorderTests | 8 | 0 |
| PaymentLinkTests: kiểm tra phản hồi gateway và chữ ký | 5 | 0 |
| CustomEndpointAccessTests | 3 | 1 |
| Tổng lượt cuối | 26 | 1 |

Artifact: `artifacts/test-results/preorder-accessories-final.trx`.

Các ca phụ kiện kiểm tra bằng API và database thật:

- Tạo yêu cầu/báo giá chưa trừ tồn; giá phụ kiện do BE lấy từ kho. Client gửi thêm đơn giá bị từ chối theo hợp đồng JSON.
- Accept đúng quote version mới trừ tồn; gửi lại không tạo thêm đơn hoặc trừ thêm tồn.
- Sửa giá/tên danh mục sau báo giá không thay snapshot mà khách đã xác nhận.
- QR và webhook gateway giả lập sử dụng tổng giá hoa cộng phụ kiện, tiền gắn kênh `SELLER_DIRECT`.
- Hủy/hết hạn chưa trả tiền trả tồn đúng một lần.
- Đơn đã trả tiền bị Seller hủy hoặc hết hạn trước lúc nạp được hoàn cả hoa và phụ kiện, trả tồn đúng một lần; khách không tự hủy đơn đã trả tiền.
- Báo giá cũ hoặc hết tồn bị từ chối; rollback không để lại đơn hoặc phần tồn đã trừ dở.
- Hai accept cạnh tranh lượng tồn cuối chỉ một đơn thành công.
- Danh mục, lựa chọn và accept kiểm tra role, kiosk, seller và chủ đơn.
- Staff đọc được danh sách phụ kiện; luồng OTP nạp yêu cầu xác nhận ở kiosk và tín hiệu cảm biến mới trước khi fulfill. Hoa/phụ kiện vào cùng slot; nhận hàng hoàn tất không trả tồn hàng đã bán.
- Biên nhận và lịch sử giữ thông tin bó hoa ngay trước lúc hoa được nạp, đồng thời liệt kê phụ kiện.

WebPreorderTests được cập nhật để gửi `quoteVersion` và dùng luồng loading-access/OTP/cảm biến hiện tại thay cho gọi fulfill không có phiên nạp. Fixture gateway dùng được số tiền khác nhau và vẫn kiểm tra chính xác số tiền BE gửi.

## Lỗi còn lại ngoài nhóm phụ kiện

`CustomEndpointAccessTests.EveryCustomHttpEndpointHasExactlyTheExpectedPublicOrRoleAccess` vẫn lỗi: ma trận đang kỳ vọng `GET /api/packages` không còn được map và thiếu nhiều route hiện có, gồm mobile auth, quản lý ảnh, shop, bố trí kiosk, ESP32, staff tasks và maintenance recovery. Route danh mục phụ kiện mới đã được thêm với quyền CUSTOMER. Không sửa toàn bộ ma trận hoặc thay phân quyền runtime trong task này.

Lượt hồi quy rộng đầu tiên có 29 ca: 14 đạt, 15 lỗi (`artifacts/test-results/preorder-accessories-regression-initial.trx`). Bốn ca WebPreorder đã được cập nhật và đều đạt trong lượt cuối. Các nhóm còn lỗi trong lượt rộng đầu gồm:

- 5 ca KioskPayment: fixture nạp lại bó hoa sau khi đã dùng hết tồn shop.
- 2 ca subscription payment link: fixture gọi luồng gói tháng đã bị loại bỏ.
- 1 ca SellerAccessory: fixture chưa có kỳ thuê kiosk hợp lệ.
- 1 ca AccessoryCheckout: fixture danh mục không có kỳ thuê kiosk hợp lệ nên API trả rỗng.
- 2 ca ModuleBoundary: phát hiện nhiều tham chiếu schema ngoài module trong các phần hiện có.

Các lỗi này chưa được sửa hoặc chạy lại toàn bộ trong task phụ kiện. Kết quả 26/27 không có nghĩa toàn bộ test suite xanh.

## OpenAPI và giới hạn

Xuất OpenAPI thành công vào `artifacts/preorder-accessories-openapi.json`. Đã kiểm tra route danh mục member và các field accessories/quoteVersion của request, accept và danh sách việc nạp staff.

Gateway payOS và tín hiệu MQTT/cảm biến trong test là giả lập. Kết quả chứng minh xử lý backend với database thật, chưa chứng nhận giao dịch payOS hoặc thiết bị kiosk thật. Build còn các cảnh báo NU1902/NU1903 hiện có của `System.Security.Cryptography.Pkcs` 9.0.0.

## Chạy lại

Trỏ `TEST_DATABASE_URL` tới database test đã migrate và `TEST_VALKEY_URL` tới Valkey test. Không dùng database runtime. Chạy bộ nhóm cuối bằng:

```powershell
dotnet test tests/FloraBot.Api.Tests/FloraBot.Api.Tests.csproj --no-restore `
  --filter 'FullyQualifiedName~PreorderAccessoryTests|FullyQualifiedName~WebPreorderTests|FullyQualifiedName~CustomEndpointAccessTests|FullyQualifiedName~PaymentLinkTests.UntrustedGatewayResponsesAreRejected|FullyQualifiedName~PaymentLinkTests.TransactionArraysHaveCanonicalSortedJsonForGatewaySignatures' `
  --logger 'trx;LogFileName=preorder-accessories-final.trx' `
  --results-directory artifacts/test-results
```
