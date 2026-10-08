# FloraBot — CSDL chuẩn + dữ liệu demo (v3.1 — MVP theo BRD §5.1, gói thuê bao theo §7.1)

**Kết quả kiểm chứng:** 22 bảng / 252 cột / 7 service (v2.1: 28 / 406). Chạy lại từ đầu bằng `./run.sh` mất khoảng 3 giây: **326 kiểm tra PASS, 0 lỗi** (gồm 2 vòng red-team độc lập: 14 lỗ hổng đã vá và có hồi quy; 4 vá sau khi dò luồng nghiệp vụ 07/10; v3.1 thêm gói thuê bao + hoàn tiền tay + đối soát). Kiểm trên PostgreSQL 16; với PG18, hàm `uuid_v7()` gọi thẳng `uuidv7()`.

## v3.1 (07/10/2026) — ba quyết định sau khi dò luồng nghiệp vụ

| Vấn đề | Quyết định | Trong CSDL |
|---|---|---|
| **A2. Mô hình gói** — code cũ thuê *từng ô* có giá, BRD §7.1 mô tả *gói thuê bao có phí, có kỳ, PAST_DUE khóa bán* | Theo BRD (mentor giao tôi quyết) | `subscription_packages.monthly_fee` (Cơ bản 400k/3 ô, Chuyên nghiệp 1,5tr/10 ô — rẻ hơn mỗi ô nhưng phải cam kết, nên cả hai gói đều có lý do tồn tại). Bảng mới `identity.subscriptions` (kỳ N tháng, trả payOS, không chồng kỳ). `sellers.status`: PENDING → APPROVED (admin duyệt) → ACTIVE (đã trả phí) ↔ PAST_DUE; `sellers.package_expires_at`. **Khóa bán theo ngày** (`flow.package_valid`), job `roll_subscriptions` chỉ đổi status + ẩn catalog; trả tiền lại → ACTIVE ngay; quá `past_due_grace_days` (7) → thu hồi toàn bộ ô. Admin gán ô trong hạn mức gói: `kiosk_ops.slot_assignments` (`flow.assign_slot` / `release_slot`), không tính tiền theo ô. Bỏ `slot_rentals`. Ledger `PLATFORM_SUBSCRIPTION`. |
| **A1. Hoàn tiền cho khách vãng lai** — VietQR không có refund API | Hoàn bằng chuyển khoản tay (GVHD chấp nhận) + **đối soát** | Khách quét e-receipt → "Nhận hoàn tiền" → `flow.submit_refund_info(order, mã e-receipt, ngân hàng, STK mã hóa, tên)`; chỉ nhận khi đơn có lệnh hoàn chờ; audit không chứa PII. `confirm_refund` **chặn** nếu chưa có STK; chi hết lệnh hoàn thì **xóa STK** (`REFUND_INFO_CLEARED`, NĐ 13/2023). Màn hình `screen.v_refund_queue` (sẵn sàng chi / chờ khách khai STK / hoàn seller). Đối soát: `flow.reconcile_gateway(ngày, sao kê JSON, admin)` so khoản thu với sao kê cổng (MISSING_IN_DB / MISSING_IN_STATEMENT / AMOUNT_MISMATCH, có audit) và `flow.reconcile_daily(ngày)` (chứng từ = sổ cái GATEWAY_CLEARING cho thu / hoàn / rút; sổ cái cân; lệnh hoàn chờ > 48h; tiền chờ hoàn). Sửa BRD §9: "PII chỉ thu khi khách yêu cầu hoàn, do khách tự nhập, xóa sau khi chi". |
| **A3. Phụ kiện giao bằng cách nào** | **Chưa chốt** — xem mục "A3" cuối file (khuyến nghị: phụ kiện gói sẵn trong bó) | Code giữ nguyên v3 (tồn kho theo kiosk, đơn chỉ-phụ-kiện giao ngay). Khi chốt sẽ sửa một lượt. |

## v3 khác v2.1 ở đâu (quyết định của mentor, 06/10/2026)

| Quyết định | Tác động |
|---|---|
| **Bỏ preorder** | Kiosk chỉ bán bó đang nằm trong hộc. Mất PIN, khung giờ nhận, duyệt ảnh, chat đơn, `order_type`, `flower_products.channel/lead_time`. Trạng thái đơn về 9 giá trị theo BRD Hình 8. |
| **Bỏ feedback** | Mất `orders.rating/feedback_*`. Hiệu quả AI đo bằng `gift_surveys.selected_product_id / purchased_order_id`. |
| **Bỏ hoa hồng** | Nguồn thu nền tảng = **phí gói thuê bao** (`PLATFORM_SUBSCRIPTION`, v3.1; v3 là tiền thuê ô). Seller nhận trọn giá bán. Cần sửa BRD §7.5 / O4. |
| **Giữ accessories** | Phụ kiện bán kèm, tồn kho theo kiosk, trừ/hoàn kho theo đơn. |
| **Giữ khách có tài khoản + tích điểm** | `users.loyalty_points`, `orders.points_earned/points_redeemed`. Đăng nhập tại kiosk bằng SĐT + OTP (OTP ở Valkey, DB chỉ `flow.customer_by_phone`). Điểm = 1% đơn, cộng khi đơn COMPLETED, đổi tối đa 50% giá trị hàng, nền tảng chịu (`PLATFORM_LOYALTY`). Khách vãng lai vẫn không lưu PII; có `flow.forget_customer` (NĐ 13/2023). Cần sửa BRD §9. |
| Bỏ 7 bảng | vouchers, shifts, work_tickets (→ `inventory_logs.batch_id`), notifications (→ SignalR, không lưu), association_rules, order_events (→ `audit_logs`), incident_tickets (→ `disputes.kind`). |
| Thêm cột | `sellers.brand_tone` (giọng thương hiệu đưa vào prompt AI), `disputes.kind`, `inventory_logs.batch_id`, `orders.completed_at`. |

Cột/bảng bị bỏ **không mất**: nằm ở tab "Phase 2" của từ điển dữ liệu — bảo vệ nói "đã thiết kế, cố ý chưa làm".

## Chạy

```bash
# cần PostgreSQL 16+ (đang chạy) và psql
DB=florabot ./run.sh                      # tạo DB mới, nạp schema, dữ liệu và kịch bản
PGARGS="-h localhost -U postgres" DB=florabot ./run.sh
DB=florabot python3 gen_erd.py           # sinh lại ERD Mermaid từ DB đang chạy (OVERVIEW=1 cho bản chỉ cột khóa)
DB=florabot python3 gen_erd_html.py      # sinh lại ERD tương tác (cần florabot_erd_v2.html làm khung, đường dẫn qua V2_HTML)
```

| File | Nội dung |
|---|---|
| `01_schema.sql` | 22 bảng. Máy trạng thái dùng trigger `guard_transition` nên UPDATE tay cũng bị chặn; chốt theo dữ liệu (DISPENSE_FAILED phải có lệnh hoàn đủ, COMPLETED phải có `completed_at`). Sổ cái kép tự kiểm cân, chống bán trùng, một ô chỉ gán một seller, một seller không chồng kỳ gói, một token sống/ô. |
| `02_seed.sql` | Dữ liệu gốc: 2 gói thuê bao (phí/tháng + số ô), 3 seller (2 APPROVED chưa trả phí, 1 chờ duyệt) có `brand_tone`, người dùng các vai trò, 2 kiosk / 10 ô, 8 sản phẩm, 3 phụ kiện, 14 tham số hệ thống. |
| `03_flows.sql` | 85 hàm `flow.*` mô phỏng lệnh nghiệp vụ: đăng ký gói + webhook phí gói + job PAST_DUE, admin gán/thu hồi ô, nạp hoa theo lô, checkout VietQR + đổi điểm, webhook payOS, token mở hộc 60 giây, sự kiện tủ, tủ không nhả hàng → hoàn 100%, đối soát, khiếu nại từ e-receipt, sự cố thiết bị, khách khai STK nhận hoàn qua e-receipt, đối soát sao kê + đối soát nội bộ, rút tiền, AI gợi ý (rule-based + validate kết quả LLM). Cuối file: 9 view `screen.*` cho màn hình. |
| `04_scenarios.sql` | 16 kịch bản có kiểm tra (~215 PASS): gói thuê bao (đăng ký → trả → gán ô; không trả → hủy; hết hạn → PAST_DUE khóa ngay → trả lại mở → quá ân hạn thu hồi ô, chạy trong hộp cát rollback), các ca *phải bị chặn*, race webhook/job qua dblink, tích điểm trọn vòng, injection vào text seller, hoàn tiền tay + đối soát sao kê. |
| `05_demo_history.sql` | 30 ngày lịch sử × 2 kiosk dựng hoàn toàn qua `flow.*`: ~230 đơn, ~900 bút toán, ~220 bó, ~55 khảo sát AI (LLM và FALLBACK), 2 ca tủ không nhả hàng, 6 khiếu nại, ~15 lần rút tiền, mọi lần hoàn đều qua khách khai STK; lịch sử có seller hết hạn gói → PAST_DUE một ngày → trả tiền mở lại. Hàng chờ demo: khiếu nại OPEN, sự cố OPEN, đơn đang nhả hàng, seller chờ duyệt, **seller quá hạn gói** (Hoa Cúc Vàng), rút tiền chờ, bó sắp hết hạn, lệnh hoàn chờ khách khai STK. |
| `06_boundaries.sql` | Kiểm ranh giới service: FK thật chỉ cùng schema; đúng 22 bảng, ≤ 260 cột; mỗi hàm flow.* ghi tối đa 3 schema nghiệp vụ. |
| `07_regression.sql` | Hồi quy A1/A2/A6 từ v2 + L1 (điểm), S1 (injection) + red-team v3: R1 cửa đang mở không được đánh thất bại, R2 giao thiếu chỉ hoàn phần thiếu, R3 sự cố ô trước khi trả tiền, R4/R5 trạng thái gắn với khoản thu, R7 hoàn 100% sau đối soát → REFUNDED, R9 giao thiếu + đổi điểm phân bổ đúng, R10 còn lệnh hoàn chờ thì chưa đối soát, R11 tài khoản xóa / sai kiosk / cửa kẹt mở. |
| `florabot_erd.mmd` / `.svg` / `.png` | ERD Mermaid đầy đủ cột, sinh tự động từ DB. Nét liền là FK trong service, nét đứt là tham chiếu logic sang service khác. |
| `florabot_erd_overview.*` | ERD tổng quan, chỉ cột khóa. |
| `florabot_erd_v3.html` | ERD tương tác: mở bằng trình duyệt, bấm bảng để xem quan hệ và giải thích từng cột (252 mô tả). |

## 22 bảng theo service

| Service | Bảng | Số bảng |
|---|---|---|
| identity | users, sellers, subscription_packages, subscriptions | 4 |
| catalog | flower_products | 1 |
| kiosk_ops | kiosks, slots, slot_assignments, bouquets, inventory_logs, unlock_tokens, accessories, system_settings | 8 |
| ordering | orders, order_items, disputes | 3 |
| payment | payments, ledger_entries, withdrawal_requests | 3 |
| notify | audit_logs, attachments | 2 |
| ai | gift_surveys | 1 |

## Vòng đời đơn (BRD Hình 8)

```
AWAITING_PAYMENT ─webhook hợp lệ─▶ PAID ─phát token─▶ DISPENSING ─cửa đóng─▶ COMPLETED ─khiếu nại─▶ DISPUTED ─▶ COMPLETED | REFUNDED
       │                              │                   │
       ├─quá 7 phút─▶ EXPIRED         └──── cửa lỗi 2 lần / kiosk offline / hết giờ giữ ────▶ DISPENSE_FAILED ─chi hoàn─▶ REFUNDED
       └─thanh toán thất bại─▶ CANCELLED
```
RESERVED của BRD gộp vào AWAITING_PAYMENT vì giữ ô và sinh QR là một bước nguyên tử. Điểm cộng khi COMPLETED; điểm đã đổi trả lại khi EXPIRED / CANCELLED / DISPENSE_FAILED.

## Vòng đời seller và gói thuê bao (BRD §7.1)

```
PENDING ─admin duyệt (gán gói)─▶ APPROVED ─trả phí gói (webhook)─▶ ACTIVE ◀─trả tiền lại (ACTIVE ngay)─ PAST_DUE
                                                                     │ gói hết hạn (khóa bán/nạp theo NGÀY; job đổi status, ẩn catalog)
                                                                     └──────────────────────────────────────────▶ PAST_DUE ─quá 7 ngày─▶ thu hồi mọi ô (trống → FREE, còn hoa → PENDING_RELEASE)
admin: SUSPENDED / CLOSED (CLOSED thu hồi ô)
```
Kỳ gói (`subscriptions`): PENDING_PAYMENT (24h không trả → CANCELLED; tiền về muộn → lệnh hoàn seller) → ACTIVE → EXPIRED. Gia hạn nối liền ngày hết hạn; đã PAST_DUE thì kỳ mới từ hôm nay. Ô: admin gán (`assign_slot`, ≤ `max_slots`), thu hồi (`release_slot`); seller tự trả ô được.

## AI Gift Advisor trong CSDL

- `flow.ai_candidates(kiosk, budget, occasion)`: tập ứng viên = bó **đang còn trong ô tại kiosk**, ≤ ngân sách, qua luật văn hóa cứng (hoa trắng không gợi ý cho sinh nhật/chúc mừng…).
- `flow.ai_suggest(...)`: RuleBasedAdvisor chấm theo tag → 3 gợi ý, `source = FALLBACK`, có `latency_ms`.
- `flow.ai_record_llm(survey, results, model, latency)`: backend ghi kết quả LLM **đã validate** — đúng 3 phần tử, mọi `bouquet_id` ∈ tập ứng viên, lý do ≤ 300 ký tự, không URL; sai → giữ FALLBACK, ghi audit. Đây là chốt "LLM không có tay chân".
- `sellers.brand_tone` và `flower_products.description` là text seller đi vào prompt → `set_seller_tone` chặn từ ngữ injection, giới hạn độ dài.
- `screen.v_ai_effectiveness`: tỷ lệ khảo sát dẫn tới mua theo nguồn LLM/FALLBACK — số liệu cho báo cáo eval.

## Quy tắc nghiệp vụ đã chốt qua red-team (đưa vào SRS)

- Cửa đang mở (token OPENED) thì không job nào được đánh đơn thất bại; chỉ sự kiện CLOSED (hoặc kỹ thuật xác nhận `flow.admin_close_door`) kết thúc.
- Giao thiếu (đơn nhiều bó, một bó không mở được): đơn COMPLETED, hoàn đúng giá bó thiếu, seller chỉ chịu phần thiếu, điểm tích trên tiền thật còn lại.
- Sự cố ô khi khách chưa trả tiền: hủy giỏ, khoản thu CANCELLED; tiền về sau đó đi đường hoàn tự động. Ô vào FAULT lúc đang thanh toán → hoàn 100% ngay khi webhook về.
- Trạng thái đơn gắn với khoản thu: PAID cần CHARGE SUCCEEDED; EXPIRED/CANCELLED chỉ khi CHARGE không còn PENDING/SUCCEEDED; COMPLETED cần không còn ô giữ; DISPUTED cần dòng khiếu nại.
- Tiền seller của đơn chỉ về ví khả dụng khi không còn lệnh hoàn nào chờ chi.
- Điểm chỉ cộng/trả cho tài khoản ACTIVE; tài khoản đã xóa ghi audit POINTS_SKIPPED_INACTIVE.
- Yêu cầu thuê ô chưa trả tiền quá `rental_payment_hours` (24h) thì hủy, nhả ô (`flow.expire_pending_rentals`); tiền về muộn → lệnh hoàn cho seller.
- Mã e-receipt 8 ký tự (không O/0/I/1) để khách gõ được trên màn kiosk khi token 60 giây đã hết; QR vẫn in kèm.
- Seller tự khai tài khoản nhận tiền (`flow.set_seller_bank`); admin duyệt chỉ gán gói + giọng thương hiệu.
- Hoa chờ trả seller quá `return_deadline_hours` (48h) → admin/kỹ thuật thanh lý (`flow.admin_dispose_slot`), ô trống lại, seller chịu.
- (v3.1) Gói hết hạn khóa bán/nạp **theo ngày**, không chờ job; seller PAST_DUE trả tiền là mở ngay; quá ân hạn `past_due_grace_days` mới thu hồi ô. Seller ACTIVE luôn có kỳ ACTIVE phủ hôm nay; không seller nào vượt `max_slots` (bất biến trong `check_invariants`).
- (v3.1) Không chi hoàn khi chưa có STK khách khai qua e-receipt; chi hết lệnh hoàn thì xóa STK; mỗi ngày đối soát sao kê (`reconcile_gateway`) và nội bộ (`reconcile_daily`).
- (v3.1) LLM trả kết quả NULL / không phải mảng → từ chối, giữ FALLBACK (lỗi phát hiện khi dựng lịch sử).

## Việc sinh viên còn phải làm (ngoài DB)

- Sửa UC list: UC_PRE_*, ca làm, voucher, KYC, bảo trì, chat đơn → Phase 2; thêm UC đăng nhập OTP tại kiosk, nhận điểm qua e-receipt, đổi điểm, xóa tài khoản.
- Sửa BRD: §7.5 / O4 (hoa hồng → phí gói thuê bao), §9 (khách tự nguyện đăng ký lưu SĐT + tên; PII nhận hoàn chỉ thu khi có lệnh hoàn, xóa sau khi chi), thêm FR-KIO tích điểm, FR-SEL đăng ký/gia hạn gói, FR-ADM gán/thu hồi ô, UC khách khai STK nhận hoàn từ e-receipt, UC admin đối soát hằng ngày (nhập sao kê cổng).
- Màn hình seller phải hiện rõ: hạn gói, trạng thái PAST_DUE và nút "Gia hạn" (vì khóa bán xảy ra đúng 0h ngày hết hạn).
- API .NET chỉ dùng giờ server; `p_now` chỉ để mô phỏng trong demo, không nhận từ client.
- OTP, refresh token, cache gợi ý AI: Valkey/Redis, không lưu DB.
- Chạy lại `run.sh` ngay trước buổi bảo vệ, vì dữ liệu demo tính theo giờ hiện tại.
- Chờ GVHD chốt: rút tối thiểu 50k hay 500k (`system_settings.withdraw_min`); gói miễn phí có hay không; ân hạn PAST_DUE 7 ngày (`past_due_grace_days`).

## A3 — Phụ kiện giao bằng cách nào (chưa chốt, khuyến nghị của mentor-AI)

Tủ chỉ có ô khóa điện (1 bộ ESP32 + relay); không có "ngăn phụ kiện chung" mở được. Ba phương án:

| | 1. Phụ kiện **gói sẵn trong bó** (khuyên) | 2. **Ô nhỏ** cho phụ kiện (`slots.kind` FLOWER/ACCESSORY) | 3. Khay mở, khách tự lấy |
|---|---|---|---|
| Nghiệp vụ | Khi nạp bó, seller tick "kèm thiệp / kèm nơ / kèm gấu"; giá bó = hoa + phụ kiện; kiosk không bán phụ kiện rời | Kiosk có vài ô nhỏ (thiệp, nơ, gấu) cùng cơ chế khóa/token; đơn bó + phụ kiện mở 2 cửa | Phụ kiện để khay hở cạnh màn hình |
| Phần cứng | Không đổi | Thêm ô nhỏ, thêm kênh relay (ESP32 đủ 64 kênh); đồ 3D-print/mica | Không đổi |
| CSDL | `bouquet_accessories(bouquet_id, accessory_id, qty)` hoặc `bouquets.accessory_ids`; bỏ `accessories.stock_quantity` theo kiosk; bỏ đơn chỉ-phụ-kiện; AI gợi ý "bó có kèm thiệp" | `slots.kind`; phụ kiện là `bouquets` kiểu ACCESSORY nằm trong ô nhỏ (một món/ô) → gần như không sửa luồng bán, chỉ thêm kind + validate | Giữ nguyên code; ghi rõ "chấp nhận thất thoát" trong SRS |
| Ưu | Đúng hành vi mua hoa (người mua chọn "bó có thiệp"); không thất thoát; AI dễ gợi ý combo; ít code nhất | Đúng "máy bán hàng"; mở rộng được | Không làm gì |
| Nhược | Phụ kiện không bán rời; seller phải chuẩn bị combo trước | Tốn ô (một nơ = một ô nhỏ); phần cứng + firmware thêm việc | Thất thoát, không demo được "khóa", GVHD sẽ hỏi |

Khuyến nghị: **chốt phương án 1 cho MVP** (một ngày sửa CSDL + test), ghi phương án 2 vào Phase 2 nếu nhóm phần cứng làm được ô nhỏ. Không chọn 3.
