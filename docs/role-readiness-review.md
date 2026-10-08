> Cập nhật 08/10/2026: user đã chọn cả hai kiểu preorder. Phần mô tả thiếu membership/preorder dưới đây là mốc điều tra trước triển khai. Xem [bàn giao thành viên và đặt trước](member-preorder-handoff.md) cho hành vi hiện tại.

# Đánh giá mức sẵn sàng theo vai trò — 08/10/2026

## Cập nhật sau yêu cầu sửa

Phát hiện P1 bên dưới đã được sửa: `confirm_refund` và `pay_withdrawal` dùng
Cloudinary authenticated, reference gắn với ADMIN/hồ sơ/nghiệp vụ, metadata của
bytes thật và private reader. Portal có upload và xem lại chứng từ. Quy tắc
hai ADMIN khác nhau được giữ nguyên. Các phần bên dưới ghi lại đánh giá trước
khi sửa; giới hạn provider thật, WCAG thủ công và độ phủ kiểm thử vẫn áp dụng.
Kết quả xác minh bản sửa được lưu trong `artifacts/financial-proof-*.log` của BE/FE.

Bản cuối: 201 API và 6 Gateway đạt; một theory MQTT bỏ qua theo cấu hình mặc định
(không chạy lại MQTT riêng trong đợt sửa này). 13 bài backend tập trung đạt;
11 bài browser chứng từ đạt; lint và typecheck đạt. Hồi quy lần đầu phát hiện
Notify truy vấn trực tiếp bảng Payment; đã chuyển truy vấn vào adapter thuộc
Payment, kiểm tra ranh giới + private evidence đạt 6 bài và chạy lại toàn bộ
backend đạt. Fixture mới từng gọi sai thứ tự tham số SQL, đã sửa và chạy lại.
Không thay SQL nguồn, không thêm dependency, không commit/push.

Điều tra preorder, địa chỉ chạy `/admin` và tình trạng tài khoản demo được ghi
riêng tại [customer-preorder-admin-investigation.md](customer-preorder-admin-investigation.md).

## Kết luận

Các bộ kiểm thử chạy trong đợt này không có bài thất bại. SELLER, CUSTOMER,
KIOSK và SYSTEM đạt các kịch bản đã kiểm thử ở môi trường local/mô phỏng.
ADMIN còn thiếu cơ chế chứng từ tài chính riêng tư và xác minh nội dung file.
Chưa đủ cơ sở kết luận mọi vai trò xử lý được mọi tình huống.

Phạm vi hiểu từ yêu cầu: đánh giá vai trò phần mềm ADMIN, SELLER, CUSTOMER,
KIOSK và tác vụ SYSTEM; loại trừ thiết bị vật lý và dịch vụ bên ngoài bằng tài
khoản thật. KIOSK và SYSTEM được liệt kê như tác nhân kỹ thuật.

## Phát hiện cần xử lý

### P1 — Chứng từ hoàn tiền/chi trả chưa theo cơ chế Cloudinary riêng tư

- `FE/apps/portal/src/AdminRefunds.tsx:17` và
  `FE/apps/portal/src/AdminWithdrawals.tsx:18` chỉ kiểm tra URL HTTPS ở giao diện.
- `BE/src/FloraBot.Api/Infrastructure/FlowExecutor.cs:67` chỉ xác minh private
  reference cho `return_to_seller`, `report_device_fault`, `open_dispute`.
- `BE/db/migrations/005_two_admin_payouts.sql:46` và `:73` chỉ yêu cầu chứng từ
  không rỗng; `confirm_refund`/`pay_withdrawal` vẫn chuyển URL vào `flow.attach`.
- `BE/db/FloraBot_DB_v3/03_flows.sql:38` tạo size giả lập và SHA-256 của chuỗi
  URL, không phải nội dung file. Hai luồng tài chính chưa ghi đè metadata này.

Hệ quả: có thể ghi nhận đã chi tiền với một liên kết không chứa chứng từ hợp lệ;
file được thay đổi tại URL vẫn không có bằng chứng toàn vẹn tương ứng. Việc
chọn URL công khai cũng không bảo đảm riêng tư cho chứng từ ngân hàng.
Quy tắc hai ADMIN khác nhau và chống ghi sổ trùng có test đạt, nhưng không
khắc phục được thiếu sót về file minh chứng.

Hướng sửa: áp dụng upload riêng tư có reference ràng buộc ADMIN, nghiệp vụ và
refund/withdrawal ID; xác minh bytes, metadata và đọc có phân quyền. Giữ nguyên
SQL nguồn và quy tắc tách người duyệt/người chi. Đây là phát hiện review,
chưa triển khai sửa trong đợt chạy UT này.

### Khoảng trống kiểm chứng

- FE `pnpm test` chỉ có ba bài trong `tests/registration.test.ts`. 99 bài
  Playwright bổ sung bằng chứng hành vi nhưng không thay thế UT cho mọi module.
- Portal/kiosk browser suites sử dụng HTTP/WebSocket mô phỏng. Log portal có
  `ECONNREFUSED` khi kết nối SignalR local; suite vẫn đạt với các kịch bản mock
  và fallback. Không xem đây là bằng chứng realtime nhiều người dùng trên stack thật.
- WCAG chưa nghiệm thu đầy đủ bằng screen reader; chưa xác nhận tuân thủ toàn bộ.
- Chưa chạy provider thật, phần cứng thật, kiểm thử tải dài hạn hay ma trận
  lỗi toàn diện. Đây là giới hạn bằng chứng, không phải lỗi được xác nhận.

## Kết quả chạy kiểm thử

| Nhóm | Kết quả | Bằng chứng trong BE hoặc FE |
| --- | --- | --- |
| FE unit / Vitest | 3 đạt | FE/artifacts/role-review-unit.log |
| .NET API, gồm integration | 199 đạt; 1 theory MQTT bỏ qua mặc định | BE/artifacts/role-review-tests.log; BE/artifacts/role-review/*.trx |
| .NET Gateway | 6 đạt | BE/artifacts/role-review-tests.log |
| AI / pytest | 31 đạt; 1 cảnh báo deprecation Starlette/AnyIO | BE/artifacts/role-review-ai.log |
| Portal / Playwright | 69 đạt | FE/artifacts/role-review-portal.log |
| Kiosk / Playwright | 30 đạt, có build kiosk | FE/artifacts/role-review-kiosk.log |
| MQTT/TLS broker + simulator riêng biệt | 2 đạt, 0 bỏ qua | BE/artifacts/role-review-mqtt.log |
| Firmware native | 13 core đạt với OpenSSL, cùng 13 bài đạt với mbedTLS; 4 transport đạt | BE/artifacts/role-review-firmware.log |

Theory MQTT bị bỏ qua ở lệnh mặc định đã được chạy riêng cho cả hai case
một/hai bó hoa. payOS HTTP được mô phỏng; MQTT/TLS dùng broker local thật.
Firmware native chạy với AddressSanitizer/UndefinedBehaviorSanitizer, chưa phải
kiểm thử relay/cảm biến trên ESP32 thật. Không cộng hai lần 13 core thành 26
kịch bản nghiệp vụ khác nhau.

Lệnh đã chạy: `pnpm test`, `dotnet test --no-restore --logger trx
--results-directory artifacts/role-review`, `pnpm test:portal`, `pnpm test:kiosk`,
AI test Docker + `python -m pytest -q`, `scripts/test-paid-mqtt-cycle.ps1`,
Docker target `native-test` và chạy lại ba binary native trong container.

## Đánh giá từng vai trò

| Vai trò | Bằng chứng chính | Đánh giá trong phạm vi mô phỏng |
| --- | --- | --- |
| ADMIN | Duyệt seller, phân slot, hoàn tiền, rút tiền, khiếu nại/sự cố; hai người duyệt/chi khác nhau; chống ghi sổ lặp | Chưa hoàn chỉnh: phải sửa chứng từ tài chính nêu trên |
| SELLER | Cách ly tenant, giữ khóa ownership, nhập kho/chống trùng, gói hết hạn, ngân hàng/ví; upload minh chứng trả hoa/báo lỗi | Các kịch bản đã kiểm thử đạt; Cloudinary thật chưa nghiệm thu |
| CUSTOMER | OTP/session, lịch sử riêng tư, xóa dữ liệu thu hồi session, receipt capability/rate limit, điểm thưởng và khiếu nại | Các kịch bản đã kiểm thử đạt; email/thanh toán thật chưa nghiệm thu |
| KIOSK | Checkout, mất phản hồi không tự retry, offline không mua, pickup, bảo vệ kiosk/customer; chu trình một/hai bó qua MQTT | Đạt phạm vi simulator; không suy ra hoạt động relay/cảm biến thật |
| SYSTEM | Job hết hạn, khóa chống chạy chồng, outbox rollback/khôi phục, webhook signature/replay, retry lệnh thiết bị, reconciliation | Các kịch bản đã kiểm thử đạt; chưa chứng minh tải dài hạn và mọi tổ hợp sự cố |

Các test đối chiếu tiêu biểu: `PayoutApprovalTests`, `CustomEndpointAccessTests`,
`ResourceAccessTests`, `StockTests`, `CustomerPrivacyTests`, `SessionTests`,
`ReceiptSecurityTests`, `PrivateEvidenceTests`, `KioskPaymentTests`,
`PaidMqttCycleTests`, `JobTests`, `OutboxTests`, `WebhookTests`,
`DeviceDispatchTests`, `DeviceProtocolTests`.

Đợt này chỉ chạy kiểm thử, đọc code và cập nhật báo cáo; không sửa logic sản phẩm,
không commit/push. Kết quả test xanh xác nhận các assertion hiện có, không xác
nhận độ phủ tất cả yêu cầu hay bảo đảm không còn lỗi.
