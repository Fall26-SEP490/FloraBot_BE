# Kiểm chứng luồng chất lượng hoa — 16/11/2026

## Kết quả

- Build API thành công; `git diff --check` sạch.
- Migration từ schema nền + toàn bộ migrations chạy thành công trên PostgreSQL 16 trong Docker, DB test riêng. Những chỉnh sửa cuối ở hàm đổi staff/hủy phương án được nạp lại bằng CREATE OR REPLACE trong DB test; không chạy migration vào VPS.
- **72/72** kiểm thử chọn theo phạm vi thay đổi đã qua: `QualityComplaintTests`, `AdminComplaintsTests`, `PrivateEvidenceTests`, `SlotMaintenancePolicyTests`, `SellerSlotMaintenanceIntegrationTests`, `ModuleBoundaryTests.QualityComplaintContractsKeepSchemaOwnership`.
- Trong đó **10 kiểm thử chất lượng mới**: giải thích/trả việc; chuyển đề nghị hoàn tiền; nạp và nhận hoa thay, không cộng thưởng lần hai; hết hạn; Admin giải quyết khi có cửa mở; hủy idempotent; chặn phản ánh trước nhận và staff khác shop; đổi staff/mất quyền mã cũ; liên kết đúng ảnh; đọc bytes ảnh private và phân trang.
- Jobs: 7/8 qua, bao gồm `EveryScheduledFlowRunsAndDailyReportIsDurable`, chạy cả job `expire_quality_replacements`. Một lỗi test cũ về gói tháng, mô tả dưới.
- Khi mở rộng sang `PreorderPickupOtpTests`: 19/22 qua; 3 lỗi cũ mô tả dưới. Luồng OTP gửi/mở/lặp yêu cầu và nhận hàng thông thường vẫn qua.
- OpenAPI đã export từ API build cuối, kiểm tra đủ **17 route mới**, `QualityComplaintDetail` và `StaffTaskRow.workType`. Artifact: `artifacts/quality-complaints-openapi.json`.

Các API/SQL được kiểm tra thật trên DB local; telemetry cửa/cảm biến, email và bytes storage được mô phỏng trong test. Chưa kiểm tra trên ESP32 thực, chưa gọi thanh toán thật hoặc triển khai VPS.

## Các test cũ vẫn lỗi

Đã tạo DB đối chứng chỉ có các migration cũ, **không có** `20261116_z_flower_quality_complaints.sql`, chạy lại chính những case này và tái hiện cùng lỗi:

| Test | Kết quả trên schema đối chứng |
|---|---|
| `JobTests.UnpaidSubscriptionExpiresUsingServerClock` | `0A000: Monthly subscriptions are retired; request or renew an annual kiosk lease.` |
| `PreorderPickupOtpTests.CustomerCanRequestAndSellerCanQuoteNearTermPickup(10)` | `Kiosk đã hết sức chứa đặt trước (PREORDER)` |
| `PreorderPickupOtpTests.CustomerCanRequestAndSellerCanQuoteNearTermPickup(30)` | Cùng lỗi sức chứa PREORDER |
| `PreorderPickupOtpTests.SellerCanStockWellBeforeAppointmentButNotAfterDeadline(false)` | `23502: null value in column price_snapshot of bouquets` |

Các lỗi này phát sinh trong lời gọi SQL fixture về thuê tháng/sức chứa/giá báo trước, không đi qua nhánh nhận hoa thay. Giữ nguyên phạm vi task; chưa sửa các fixture hoặc nghiệp vụ preorder đó. Không tuyên bố toàn bộ suite đã xanh.

## Dấu vết kiểm thử local

- `artifacts/test-results/quality-accepted-regression.trx`: bản cuối 72/72.
- `artifacts/test-results/quality-jobs.trx`: 7 qua, 1 lỗi gói tháng.
- `artifacts/test-results/quality-final-regression.trx`: lần mở rộng OTP trước khi thêm case hủy cuối; 90 qua, 3 lỗi fixture nêu trên.
- `artifacts/test-results/quality-baseline-known-failures.trx`: DB đối chứng không migration mới; 4 lỗi giống nhau, 1 case hết hạn preorder qua.

DB local được giữ riêng: `florabot_quality_20261116_v2_tests`, `florabot_quality_jobs_20261116_tests`, `florabot_quality_baseline_20261116_tests`. Không dùng DB ứng dụng hoặc DB production.

Tài liệu FE: `docs/fe-flower-quality-complaints.md`. Quyết định nghiệp vụ: `docs/adr/0064-after-sale-flower-quality.md`. Trước triển khai: backup DB VPS → chạy migration UTF8 và kiểm tra thành công → publish API → FE sinh lại contracts từ OpenAPI và kiểm thử với kiosk thực.
