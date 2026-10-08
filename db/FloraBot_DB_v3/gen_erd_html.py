"""Sinh florabot_erd_v3.html (ERD tương tác) từ CSDL đang chạy: khung HTML lấy từ v2, DATA sinh lại từ information_schema.
Mô tả cột: kế thừa từ erd v2 cho cột không đổi, cột mới/đổi nghĩa lấy từ bảng NEW_DESC bên dưới."""
import re, json, subprocess, os, sys
V2 = os.environ.get("V2_HTML", "/home/claude/v2src/FloraBot_DB_v2/florabot_erd_v2.html")
PSQL = ["psql", "-h", "/tmp", "-U", "postgres", "-d", os.environ.get("DB", "florabot"), "-At", "-F", "\t", "-c"]
def q(sql): return [l.split("\t") for l in subprocess.check_output(PSQL + [sql], text=True).strip().splitlines() if l]
SCHEMAS = "('identity','catalog','kiosk_ops','ordering','payment','notify','ai')"

shell = open(V2, encoding="utf8").read()
old = json.loads(re.search(r"const DATA = (\{.*?\});\n", shell, re.S).group(1))
old_tables = {t["name"]: t for t in old["tables"]}
old_desc = {(t["name"], c["n"]): c["d"] for t in old["tables"] for c in t["columns"]}

TABLE_META = {  # purpose / example / old / why cho v3
 "users": ("Tài khoản mọi vai trò: ADMIN, SELLER (mọi người của shop), CUSTOMER (đăng nhập tại kiosk bằng SĐT + OTP, không mật khẩu), SYSTEM. Điểm tích lũy của khách lưu ngay trên dòng.",
           "Khách Nguyễn Thảo My, SĐT 0913000101, CUSTOMER, 35.100 điểm.", "USER + CUSTOMER", "v3: bỏ phân quyền nhân viên, xác thực email/SĐT, khóa đăng nhập; thêm loyalty_points; role gộp SELLER_OWNER/SELLER_STAFF thành SELLER."),
 "sellers": ("Hồ sơ cửa hàng. Admin duyệt (PENDING → APPROVED) kèm gói; trả phí gói → ACTIVE; gói hết hạn → PAST_DUE (khóa bán/nạp, catalog ẩn), trả tiền lại thì ACTIVE. Seller tự khai STK nhận tiền. brand_tone là một câu giọng thương hiệu đưa vào prompt AI.",
             "Shop 'Hoa Sài Gòn', gói Cơ bản, ACTIVE, hạn gói 07/11, giọng 'Sang trọng, ít lời'.", "SELLER", "v3.1: status theo BRD §7.1 (APPROVED/PAST_DUE), thêm package_expires_at; bỏ hoa hồng, mã số thuế, KYC; thêm brand_tone."),
 "subscription_packages": ("Gói thuê bao (BRD §7.1): phí cố định mỗi tháng, bao gồm tối đa max_slots ô. Phí gói là nguồn thu duy nhất của nền tảng ở MVP (không hoa hồng, không tính tiền theo ô).",
                           "Gói 'Cơ bản' 400.000 đ/tháng, tối đa 3 ô; 'Chuyên nghiệp' 1.500.000 đ/tháng, 10 ô.", "SUBSCRIPTIONPACKAGE", "v3.1: price_per_slot_month → monthly_fee (mô hình gói theo BRD, không thuê từng ô); bỏ commission_rate."),
 "subscriptions": ("Kỳ thuê bao của seller: đăng ký N tháng, trả qua payOS, ACTIVE khi đã thu tiền; chuỗi kỳ liên tục = hạn gói (sellers.package_expires_at). Một seller không có hai kỳ chồng nhau.",
                   "Hoa Mộc, gói Chuyên nghiệp, 07/10 → 07/01, 4.500.000 đ, ACTIVE.", "SUBSCRIPTION", "v3.1: thay slot_rentals (thuê từng ô) bằng kỳ gói; tiền và hạn nằm ở seller, không ở ô."),
 "flower_products": ("Mẫu hoa seller đăng bán; mỗi bó thật nạp vào ô là một dòng bouquets trỏ về đây. Tag và mô tả là dữ liệu AI dùng để gợi ý.",
                     "'Hồng đỏ 10 bông' 350.000 đ, tags {LOVER,VALENTINE,ROMANTIC,RED}, hạn tươi 72 giờ.", "FLOWERPRODUCT", "v3: bỏ kênh bán, thời gian chuẩn bị, kích thước (preorder và kiểm vừa ô đã bỏ)."),
 "kiosks": ("Tủ bán hoa tự động: định danh thiết bị (ESP32, MQTT), trạng thái online qua heartbeat, khóa API gọi Core API.",
            "Kiosk K-Q1-01 'Nhà Văn hóa Thanh Niên', ESP32-A1B2C3, ONLINE.", "KIOSK", "v3: bỏ tọa độ, firmware, giờ mở cửa, bảo trì định kỳ."),
 "slots": ("Ô (hộc) vật lý của kiosk: đang gán cho seller nào, bó nào đang nằm trong, đang giữ cho đơn nào, chân relay điều khiển khóa.",
           "Ô A01 của K-Q1-01, STOCKED, Hoa Sài Gòn thuê, relay 0.", "SLOT", "v3: bỏ kích thước, QR dán cửa, status_before_fault (suy từ dữ liệu khi sửa xong)."),
 "slot_assignments": ("Admin gán ô cho seller trong hạn mức gói (FR-ADM-01), không tính tiền theo ô. Một ô chỉ một dòng ACTIVE; thu hồi (admin, seller trả ô, quá ân hạn PAST_DUE, đóng seller) ghi RELEASED kèm lý do — giữ lịch sử.",
                      "A01 gán cho Hoa Sài Gòn ngày 06/09 bởi admin, ACTIVE.", "SLOT_ALLOCATION", "v3.1: thay slot_rentals; bỏ kỳ hạn và giá (thuộc gói)."),
 "bouquets": ("Từng bó hoa thật đã nạp vào ô: giá chốt lúc nạp, hạn bán, vòng đời STOCKED → HELD → SOLD → PICKED_UP.",
              "Bó 'Hồng đỏ' BQ-0001 nạp 08:10, hạn bán 72 giờ sau, SOLD.", "BOUQUET", "v3: bỏ condition (tình trạng ghi ở inventory_logs.reason)."),
 "inventory_logs": ("Nhật ký kho chỉ ghi thêm: nạp (theo lô batch_id), giữ, nhả, bán, quá hạn, hỏng, trả seller. Một lần seller đến nạp nhiều ô = một batch_id.",
                    "Bó BQ-0001 STOCK_IN vào A01, lô 01a1…, Dung thực hiện.", "INVENTORYLOG + WORKTICKET (gộp)", "v3: phiếu nạp hàng thay bằng batch_id; bỏ liable_party, damage_cost, condition, MOVE."),
 "unlock_tokens": ("Lệnh mở hộc một lần: TTL 60 giây, cmd_id chống phát lại, vòng đời ISSUED → SENT → ACKED → OPENED → CLOSED. SELLER_ACCESS cho cửa kỹ thuật nạp/lấy hàng.",
                   "Token CUSTOMER_PICKUP cho ô A01, đơn FB…, CLOSED sau 40 giây.", "UNLOCKTOKEN", "v3: bỏ work_ref_id, sent_at, acked_at; purpose còn CUSTOMER_PICKUP / SELLER_ACCESS."),
 "accessories": ("Phụ kiện bán kèm (thiệp giấy, nơ, gấu nhỏ) do seller quản lý, tồn kho theo kiosk; nhân viên bỏ kèm khi nạp hoặc để ở ngăn phụ kiện chung.",
                 "'Thiệp chúc mừng' 20.000 đ, Hoa Sài Gòn tại K-Q1-01, còn 30.", "ACCESSORY", "Giữ nguyên (mentor quyết định giữ)."),
 "system_settings": ("Tham số vận hành admin chỉnh được, thay mọi hằng số cứng: TTL giữ ô, TTL token, số lần mở cửa, hạn khiếu nại, tỷ lệ tích điểm…",
                     "key 'hold_minutes' = 7.", "—", "Thêm 3 khóa tích điểm/OTP, bỏ các khóa preorder/ca làm."),
 "orders": ("Đơn hàng của khách cho một seller tại một kiosk (giỏ nhiều seller tách nhiều đơn cùng checkout_id). Vòng đời theo BRD Hình 8. Khách vãng lai không lưu PII; khách đăng nhập có điểm tích/đổi. Khi có lệnh hoàn, khách tự khai STK trên e-receipt (refund_bank_*), chi xong thì xóa.",
            "Đơn FB261006-AB12CD, khách Thảo My, Hoa Sài Gòn tại K-Q1-01, 350.000 đ, COMPLETED, +3.500 điểm.", "ORDER", "v3: bỏ feedback, hoa hồng, voucher, preorder, guest_phone; thêm points_*; v3.1: thêm refund_bank_* (hoàn tay, không có refund API VietQR)."),
 "order_items": ("Dòng hàng của đơn: bó trong ô (đúng 1, có slot) hoặc phụ kiện. Một bó chỉ bán được một lần (unique).",
                 "Đơn FB…: 1 × Hồng đỏ 350.000 đ (ô A01) + 1 × Thiệp 20.000 đ.", "ORDERITEM", "v3: bỏ item_type PRODUCT (đặt trước)."),
 "disputes": ("Khiếu nại và sự cố gộp một bảng theo kind: COMPLAINT (khách quét e-receipt), DISPENSE_FAILED (tủ không nhả hàng, tự tạo + hoàn 100%), DEVICE_FAULT (seller/kỹ thuật báo). Admin đối chiếu log rồi quyết định.",
              "COMPLAINT đơn FB…: 'Hoa bị dập', RESOLVED_REFUND 100.000 đ.", "DISPUTE + INCIDENTTICKET (gộp)", "v3: bỏ phản hồi seller, hạn phản hồi, SLA/priority; thêm kind, kiosk_id."),
 "payments": ("Mọi giao dịch tiền với cổng: CHARGE (thu VietQR cho đơn hoặc phí gói) và REFUND (hoàn tay, trỏ về khoản thu gốc). Idempotent theo khóa và mã giao dịch cổng; đối soát với sao kê qua flow.reconcile_gateway.",
              "CHARGE ORDER_CHECKOUT 650.000 đ PAYOS, SUCCEEDED.", "PAYMENT", "v3: bỏ payer_user_id, proof_attachment_id (chứng từ ở attachments)."),
 "ledger_entries": ("Sổ cái kép, chỉ ghi thêm: mọi biến động tiền là bút toán cân (tổng = 0). Số dư ví seller tính từ đây. PLATFORM_SUBSCRIPTION = doanh thu phí gói; PLATFORM_LOYALTY = nền tảng chịu phần đổi điểm.",
                    "Bút toán đơn FB…: GATEWAY_CLEARING +350.000, SELLER_PENDING −350.000.", "WALLETTRANSACTION", "v3: bỏ PLATFORM_REVENUE (hoa hồng), SELLER_PAYOUT; đổi PLATFORM_DISCOUNT → PLATFORM_LOYALTY."),
 "withdrawal_requests": ("Seller yêu cầu rút tiền từ ví khả dụng; admin duyệt và chi (chứng từ ở attachments). Mỗi seller một yêu cầu đang chờ.",
                         "Hoa Mộc rút 500.000 đ, PAID.", "WITHDRAWALREQUEST", "v3: bank_snapshot JSON → 3 cột text; bỏ requested_by, approved_at, bank_txn_ref."),
 "audit_logs": ("Nhật ký kiểm toán chỉ ghi thêm: thao tác nhạy cảm (đổi giá, hoàn tiền, cấu hình) và toàn bộ lịch sử trạng thái đơn (thay order_events).",
                "ORDER_STATUS_CHANGED đơn FB…: {from: PAID, to: DISPENSING}.", "AUDITLOG + ORDER_EVENTS (gộp)", "v3: bỏ chuỗi hash, IP, kiosk_id, source_event_id."),
 "attachments": ("Kho tệp: ảnh sản phẩm, bằng chứng khiếu nại/sự cố, ảnh trả hàng, chứng từ chi tiền.",
                 "Ảnh PRODUCT của 'Hồng đỏ 10 bông'.", "ATTACHMENT", "v3: phase còn PRODUCT / EVIDENCE / RETURN / PROOF."),
 "gift_surveys": ("Mỗi lượt khảo sát 5 câu tại kiosk: đầu vào, 3 gợi ý đã validate (chỉ bó còn trong ô), nguồn (LLM/FALLBACK), độ trễ, khách chọn gì, mua gì. Là log để train learning-to-rank ở phase sau.",
                  "Khách tại K-Q1-01: tặng bạn, chúc mừng, ấm áp, ≤ 400k → 3 gợi ý, nguồn LLM, 1.166 ms, đã mua.", "GIFTSURVEY + AIRECOMMENDATION", "v3: kiosk_id bắt buộc; bỏ bảng luật Apriori."),
}
NEW_DESC = {
 ("users","loyalty_points"): "Điểm tích lũy, 1 điểm = 1 đồng. Cộng khi đơn COMPLETED (points_rate_percent), trừ khi đổi điểm. CHECK ≥ 0; chỉ CUSTOMER có điểm.",
 ("users","role"): "Vai trò: ADMIN, SELLER (mọi tài khoản của shop), CUSTOMER (đăng nhập OTP tại kiosk), SYSTEM. SELLER bắt buộc có seller_id.",
 ("users","password_hash"): "Mật khẩu đã băm (bcrypt). Bắt buộc với ADMIN/SELLER; CUSTOMER không có (đăng nhập bằng SĐT + OTP, OTP ở Valkey).",
 ("sellers","status"): "PENDING (chờ duyệt hồ sơ) → APPROVED (đã duyệt, chưa trả phí gói) → ACTIVE (gói còn hạn) ↔ PAST_DUE (gói hết hạn: khóa bán/nạp, catalog ẩn; quá past_due_grace_days thì thu hồi ô); SUSPENDED/CLOSED do admin.",
 ("sellers","package_expires_at"): "Ngày hết hạn gói (không bao gồm) = cuối chuỗi kỳ subscriptions ACTIVE liên tục; flow.refresh_seller_package cập nhật. Khóa bán theo ngày này, không chờ job.",
 ("subscriptions","id"): "Khóa chính UUID v7.",
 ("subscriptions","created_at"): "Thời điểm đăng ký; mốc tính hạn thanh toán (subscription_payment_hours).",
 ("subscriptions","updated_at"): "Thời điểm cập nhật gần nhất (trigger).",
 ("slot_assignments","id"): "Khóa chính UUID v7.",
 ("subscriptions","seller_id"): "FK → sellers. Seller đăng ký kỳ.",
 ("subscriptions","package_id"): "FK → subscription_packages. Gói tại thời điểm đăng ký.",
 ("subscriptions","period_from"): "Ngày bắt đầu kỳ (bao gồm). Gia hạn thì = period_to của kỳ trước; đã PAST_DUE thì = hôm nay.",
 ("subscriptions","period_to"): "Ngày kết thúc kỳ (không bao gồm) = period_from + N tháng. Exclusion: một seller không có hai kỳ PENDING_PAYMENT/ACTIVE chồng nhau.",
 ("subscriptions","price"): "Phí kỳ = monthly_fee × số tháng, chụp lúc đăng ký (đổi giá gói sau này không ảnh hưởng).",
 ("subscriptions","status"): "PENDING_PAYMENT (chờ payOS, hủy sau subscription_payment_hours), ACTIVE (đã thu), EXPIRED (job roll_subscriptions), CANCELLED (không trả; tiền về muộn → lệnh hoàn).",
 ("subscriptions","payment_id"): "Tham chiếu logic tới payment.payments (CHARGE, purpose SUBSCRIPTION). Bắt buộc khi ACTIVE.",
 ("subscription_packages","monthly_fee"): "Phí gói mỗi tháng (đồng), cố định, không tính thêm theo ô. Nguồn thu duy nhất của nền tảng ở MVP.",
 ("subscription_packages","max_slots"): "Số ô tối đa admin được gán cho seller trong gói (flow.assign_slot kiểm hạn mức).",
 ("slot_assignments","slot_id"): "FK → slots. Ô được gán.",
 ("slot_assignments","seller_id"): "Tham chiếu logic tới identity.sellers. Seller nhận ô; chép sang slots.current_seller_id.",
 ("slot_assignments","assigned_by"): "Tham chiếu logic tới identity.users. Admin gán ô (chỉ ADMIN).",
 ("slot_assignments","assigned_at"): "Thời điểm gán.",
 ("slot_assignments","status"): "ACTIVE (một ô chỉ một dòng ACTIVE — unique) hoặc RELEASED.",
 ("slot_assignments","released_at"): "Thời điểm thu hồi; có khi và chỉ khi RELEASED.",
 ("slot_assignments","release_reason"): "ADMIN (admin thu hồi), SELLER (seller trả ô), PAST_DUE_GRACE (gói quá ân hạn), SELLER_CLOSED.",
 ("orders","refund_bank_name"): "Ngân hàng nhận hoàn do khách tự khai trên e-receipt khi đơn có lệnh hoàn (flow.submit_refund_info); xóa sau khi chi hết lệnh hoàn (NĐ 13/2023).",
 ("orders","refund_bank_account_enc"): "Số tài khoản nhận hoàn, mã hóa ở tầng ứng dụng; không hiện trên màn hình danh sách, chỉ admin chi tiền xem; xóa sau khi chi xong.",
 ("orders","refund_bank_holder"): "Tên chủ tài khoản nhận hoàn (chữ cái, viết hoa); xóa sau khi chi xong.",
 ("payments","purpose"): "ORDER_CHECKOUT (thu/hoàn cho giỏ hàng tại kiosk) hoặc SUBSCRIPTION (phí gói thuê bao / hoàn phí gói về muộn).",
 ("payments","subscription_id"): "Tham chiếu logic tới identity.subscriptions. Bắt buộc khi purpose = SUBSCRIPTION.",
 ("ledger_entries","ref_type"): "ORDER / SETTLEMENT (ref_id = đơn), REFUND / REFUND_PAYOUT (ref_id = lệnh hoàn), SUBSCRIPTION (ref_id = kỳ gói), WITHDRAWAL (ref_id = yêu cầu rút).",
 ("sellers","brand_tone"): "Một câu giọng thương hiệu (≤ 200 ký tự) đưa vào prompt AI Gift Advisor; lọc từ ngữ injection khi lưu.",
 ("flower_products","description"): "Ý nghĩa hoa 1–2 câu; AI dùng để viết lý do gợi ý. Text seller nhập cũng vào prompt nên có kiểm tra độ dài.",
 ("flower_products","tags"): "Mảng tag người nhận / dịp / phong cách / màu, vd {LOVER,VALENTINE,RED}; AI chấm điểm theo tag. Index GIN.",
 ("kiosks","api_key_hash"): "Băm SHA-256 khóa API cấp cho kiosk gọi Core API; không lưu khóa gốc.",
 ("slots","relay_channel"): "Chân relay điều khiển khóa ô trên ESP32 (0–63), UNIQUE trong một kiosk. Bắt buộc.",
 ("slots","status"): "FREE, RENTED_EMPTY (đã gán cho seller, trống), STOCKED, HELD, LOCKED, FAULT, PENDING_REMOVAL (hoa quá hạn / đơn hoàn tiền, chờ seller lấy), PENDING_RELEASE (ô bị thu hồi nhưng còn hoa).",
 ("slots","current_seller_id"): "Tham chiếu logic tới identity.sellers. Seller đang được gán ô (chép từ slot_assignments ACTIVE); NULL khi FREE.",
 ("bouquets","status"): "STOCKED, HELD (đang giữ chờ thanh toán), SOLD, PICKED_UP, EXPIRED, DAMAGED, RETURNED.",
 ("bouquets","stocked_at"): "Thời điểm nạp vào ô. Bắt buộc; sellable_until = stocked_at + shelf_life_hours.",
 ("inventory_logs","batch_id"): "Lô nạp hàng: một lần seller đến kiosk nạp nhiều ô dùng chung một batch_id (thay bảng phiếu công việc). Bắt buộc với STOCK_IN.",
 ("inventory_logs","slot_id"): "FK → slots. Ô liên quan. Bắt buộc.",
 ("inventory_logs","movement_type"): "STOCK_IN, HOLD, RELEASE, SALE_OUT, EXPIRE_OUT, DAMAGE, RETURN_SELLER.",
 ("inventory_logs","order_id"): "Tham chiếu logic tới ordering.orders. Bắt buộc với HOLD/RELEASE/SALE_OUT.",
 ("unlock_tokens","purpose"): "CUSTOMER_PICKUP (khách lấy hàng, bắt buộc order_id) hoặc SELLER_ACCESS (cửa kỹ thuật nạp/lấy hàng, bắt buộc issued_to_user_id).",
 ("unlock_tokens","expires_at"): "Hạn dùng token = issued_at + token_minutes (BRD: 60 giây). Phải > issued_at.",
 ("unlock_tokens","issued_to_user_id"): "Tham chiếu logic tới identity.users. Người của seller được cấp token SELLER_ACCESS.",
 ("unlock_tokens","attempts"): "Số lần đã gửi lệnh (0–5). Đạt door_max_attempts (BRD: 2) mà lỗi thì FAILED → DISPENSE_FAILED.",
 ("orders","status"): "AWAITING_PAYMENT, PAID, DISPENSING, COMPLETED, DISPUTED, REFUNDED, DISPENSE_FAILED, EXPIRED, CANCELLED (BRD Hình 8; RESERVED gộp vào AWAITING_PAYMENT).",
 ("orders","customer_id"): "Tham chiếu logic tới identity.users. Khách đã đăng nhập OTP để tích điểm; NULL = khách vãng lai (không lưu PII).",
 ("orders","discount_amount"): "Số tiền giảm, VND. Chỉ từ đổi điểm (= points_redeemed); nền tảng chịu (PLATFORM_LOYALTY).",
 ("orders","points_redeemed"): "Số điểm khách đổi cho đơn này (1 điểm = 1 đồng), trừ ngay khi tạo đơn, trả lại nếu EXPIRED/CANCELLED/DISPENSE_FAILED. Tối đa points_max_redeem_percent × subtotal.",
 ("orders","points_earned"): "Điểm khách sẽ nhận = points_rate_percent × total_amount; cộng vào users.loyalty_points khi đơn COMPLETED. 0 với khách vãng lai.",
 ("orders","tracking_token"): "Mã ngẫu nhiên in dạng QR trên e-receipt: tra cứu đơn, mở lại hộc khi token hết hạn, mở khiếu nại. UNIQUE.",
 ("orders","ecard_content"): "Lời thiệp điện tử (AI gợi ý, khách sửa), ≤ 150 ký tự.",
 ("orders","completed_at"): "Thời điểm cảm biến cửa đóng sau khi khách lấy hàng; bắt buộc khi COMPLETED/DISPUTED; mốc tính hạn khiếu nại và đối soát.",
 ("order_items","item_type"): "BOUQUET (bó trong ô, SL = 1, có slot_id) hoặc ACCESSORY (phụ kiện).",
 ("disputes","kind"): "COMPLAINT (khách khiếu nại từ e-receipt), DISPENSE_FAILED (tủ không nhả hàng, hệ thống tự tạo kèm lệnh hoàn 100%), DEVICE_FAULT (sự cố thiết bị, không gắn đơn).",
 ("disputes","order_id"): "FK → orders. Bắt buộc trừ DEVICE_FAULT. Mỗi đơn chỉ một COMPLAINT (unique).",
 ("disputes","kiosk_id"): "Tham chiếu logic tới kiosk_ops.kiosks. Kiosk liên quan. Bắt buộc.",
 ("disputes","slot_id"): "Tham chiếu logic tới kiosk_ops.slots. Ô gặp sự cố (nếu có); ô vào FAULT khi mở ticket.",
 ("disputes","reported_by"): "Tham chiếu logic tới identity.users. Người báo; NULL khi thiết bị/hệ thống báo hoặc khách vãng lai.",
 ("disputes","status"): "OPEN, RESOLVED_REFUND (có refund_amount), RESOLVED_REJECT, RESOLVED_FIXED (sự cố đã sửa), CANCELLED.",
 ("disputes","decision"): "Nội dung quyết định của admin / ghi chú xử lý của kỹ thuật; bắt buộc khi RESOLVED_*.",
 ("payments","gateway"): "PAYOS (VietQR qua payOS/SePay) hoặc MANUAL (chuyển khoản tay, có chứng từ ở attachments).",
 ("ledger_entries","account"): "GATEWAY_CLEARING, SELLER_PENDING, SELLER_AVAILABLE, PLATFORM_SUBSCRIPTION (doanh thu phí gói), PLATFORM_LOYALTY (nền tảng chịu đổi điểm), REFUND_CLEARING (tiền chờ hoàn).",
 ("withdrawal_requests","bank_name"): "Tên ngân hàng chụp lại lúc yêu cầu (seller đổi tài khoản sau đó không ảnh hưởng).",
 ("withdrawal_requests","bank_account_no_enc"): "Số tài khoản đã mã hóa, chụp lại lúc yêu cầu.",
 ("withdrawal_requests","bank_holder"): "Tên chủ tài khoản, chụp lại lúc yêu cầu.",
 ("withdrawal_requests","paid_at"): "Thời điểm đã chuyển tiền; có khi và chỉ khi PAID. Chứng từ ở attachments (owner_type = withdrawal, phase = PROOF).",
 ("audit_logs","actor_id"): "Id người dùng hoặc kiosk thực hiện (tham chiếu logic); NULL khi hệ thống.",
 ("audit_logs","action"): "Hành động, vd ORDER_STATUS_CHANGED (lịch sử đơn), PRODUCT_PRICE_CHANGED, REFUND_CONFIRMED, SETTING_CHANGED, POINTS_EARNED.",
 ("audit_logs","payload"): "Dữ liệu chi tiết JSON, vd {from, to, note} cho trạng thái đơn, {old, new} khi đổi giá.",
 ("attachments","phase"): "PRODUCT (ảnh sản phẩm), EVIDENCE (bằng chứng khiếu nại/sự cố), RETURN (ảnh trả hàng), PROOF (chứng từ chi tiền).",
 ("attachments","mime_type"): "image/jpeg, image/png, image/webp, application/pdf.",
 ("gift_surveys","kiosk_id"): "Tham chiếu logic tới kiosk_ops.kiosks. Bắt buộc: chỉ gợi ý bó đang còn trong ô tại kiosk này.",
 ("gift_surveys","results"): "Mảng JSON 3 gợi ý đã validate: product_id, bouquet_id (phải thuộc tập ứng viên), rank, score, reason ≤ 300 ký tự, card_message ≤ 200 ký tự.",
 ("gift_surveys","model_version"): "Phiên bản đã chấm: rules-v1 (fallback) hoặc tên model LLM + rules.",
 ("gift_surveys","source"): "LLM (kết quả LLM qua validate), FALLBACK (rule-based), CACHE.",
 ("gift_surveys","latency_ms"): "Thời gian trả gợi ý (ms); mục tiêu ≤ 2000 ms kể cả fallback (BRD O3).",
 ("sellers","package_id"): "FK → subscription_packages. Gói đang dùng, gán khi admin duyệt hồ sơ; bắt buộc từ APPROVED trở đi.",
 ("users","seller_id"): "FK → sellers. Shop mà tài khoản thuộc về; có (và bắt buộc) khi và chỉ khi role = SELLER.",
 ("attachments","owner_type"): "Loại đối tượng sở hữu: flower_product, dispute, inventory_log, refund, withdrawal.",
 ("slots","hold_order_id"): "Tham chiếu logic tới ordering.orders. Đơn đang giữ ô (chờ thanh toán hoặc chờ khách mở hộc); bắt buộc khi HELD.",
}

cols = q(f"""SELECT c.table_schema, c.table_name, c.column_name,
  CASE WHEN c.data_type='ARRAY' THEN replace(c.udt_name,'_','')||'[]' WHEN c.data_type LIKE 'timestamp%' THEN 'timestamptz' WHEN c.data_type='character varying' THEN 'text'
       WHEN c.data_type='USER-DEFINED' THEN c.udt_name ELSE replace(c.data_type,' ','_') END, c.is_nullable
  FROM information_schema.columns c JOIN information_schema.tables t USING (table_schema, table_name)
  WHERE t.table_type='BASE TABLE' AND c.table_schema IN {SCHEMAS} ORDER BY c.table_schema, c.table_name, c.ordinal_position""")
pk = {(r[1], r[2]) for r in q(f"""SELECT kcu.table_schema, kcu.table_name, kcu.column_name FROM information_schema.table_constraints tc
  JOIN information_schema.key_column_usage kcu USING (constraint_schema, constraint_name) WHERE tc.constraint_type='PRIMARY KEY' AND tc.table_schema IN {SCHEMAS}""")}
uq = {(r[1], r[2]) for r in q(f"""SELECT n.nspname, c.relname, a.attname FROM pg_index i JOIN pg_class c ON c.oid=i.indrelid JOIN pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum = i.indkey[0] WHERE i.indisunique AND NOT i.indisprimary AND i.indnatts=1 AND n.nspname IN {SCHEMAS}""")}
fks = {(r[1], r[2]): r[4] for r in q(f"""SELECT n.nspname, c.relname, a.attname, fn.nspname, fc.relname FROM pg_constraint k JOIN pg_class c ON c.oid=k.conrelid JOIN pg_namespace n ON n.oid=c.relnamespace
  JOIN pg_class fc ON fc.oid=k.confrelid JOIN pg_namespace fn ON fn.oid=fc.relnamespace JOIN pg_attribute a ON a.attrelid=c.oid AND a.attnum=k.conkey[1]
  WHERE k.contype='f' AND n.nspname IN {SCHEMAS}""")}
refs, cur = {}, None
for line in open("01_schema.sql", encoding="utf8"):
    m = re.match(r"CREATE TABLE (\w+)\.(\w+)", line)
    if m: cur = m.groups(); continue
    m = re.match(r"\s+(\w+)\s+uuid.*--\s*ref (\w+)\.(\w+)", line)
    if m and cur: refs[(cur[1], m.group(1))] = m.group(3)

tables, rels = {}, []
for s, t, c, typ, nl in cols:
    tb = tables.setdefault(t, {"name": t, "service": s, "columns": []})
    keys = [k for k, ok in (("PK", (t, c) in pk), ("FK", (t, c) in fks or (t, c) in refs), ("UK", (t, c) in uq)) if ok]
    to = fks.get((t, c)) or refs.get((t, c))
    d = NEW_DESC.get((t, c)) or old_desc.get((t, c)) or "(mô tả cập nhật ở từ điển dữ liệu)"
    tb["columns"].append({"n": c, "t": typ, "null": nl == "YES", "k": keys, "to": to, "d": d})
    if to and c not in ("id",):
        rels.append({"from": t, "col": c, "to": to, "kind": "fk" if (t, c) in fks else "ref", "null": nl == "YES", "uniq": (t, c) in uq})
for t, tb in tables.items():
    purpose, example, oldn, why = TABLE_META[t]
    tb.update({"purpose": purpose, "example": example, "old": oldn, "why": why})
missing = [(t, c["n"]) for t, tb in tables.items() for c in tb["columns"] if c["d"].startswith("(mô tả")]
if missing: print("THIẾU MÔ TẢ:", missing, file=sys.stderr)
services = [["identity", "Identity & Merchant"], ["catalog", "Catalog"], ["ordering", "Ordering"], ["kiosk_ops", "Kiosk Ops"], ["payment", "Payment"], ["notify", "Audit & Media"], ["ai", "AI Advisor"]]
data = {"services": services, "tables": sorted(tables.values(), key=lambda x: (x["service"], x["name"])), "rels": rels}
ncol = sum(len(t["columns"]) for t in tables.values())

out = shell
out = re.sub(r"const DATA = \{.*?\};\n", "const DATA = " + json.dumps(data, ensure_ascii=False) + ";\n", out, count=1, flags=re.S)
out = out.replace("<title>FloraBot ERD v2</title>", "<title>FloraBot ERD v3.1 (MVP)</title>")
out = out.replace("<h1>FloraBot ERD v2<small>28 bảng · 406 cột · 7 service</small></h1>", f"<h1>FloraBot ERD v3.1 — MVP<small>{len(tables)} bảng · {ncol} cột · 7 service</small></h1>")
out = re.sub(r"const ZONES = \[.*?\];", """const ZONES = [
  ["identity",2,0],["catalog",1,0],["ordering",2,0],["payment",2,0],
  ["kiosk_ops",4,1],["notify",2,1],["ai",1,1]
];""", out, count=1, flags=re.S)
out = re.sub(r"const ORDER = \{.*?\};", """const ORDER = {
  identity:["users","sellers","subscription_packages","subscriptions"], catalog:["flower_products"],
  ordering:["orders","order_items","disputes"],
  payment:["payments","ledger_entries","withdrawal_requests"],
  kiosk_ops:["kiosks","slots","slot_assignments","bouquets","unlock_tokens","inventory_logs","accessories","system_settings"],
  notify:["audit_logs","attachments"], ai:["gift_surveys"]
};""", out, count=1, flags=re.S)
old_box = re.search(r'<div class="chg" style="margin-top:14px"><b>So với ERD sinh viên \(30 bảng\)</b>.*?</div>', out, re.S).group(0)
out = out.replace(old_box, '<div class="chg" style="margin-top:14px"><b>v3.1 (MVP) so với v2.1</b>' + f"{len(tables)} bảng / {ncol} cột (v2.1: 28 / 406). Bỏ preorder, feedback, hoa hồng, voucher, ca làm nhân viên, phiếu công việc, thông báo lưu DB, luật Apriori, order_events, incident_tickets riêng. Thêm tích điểm khách (users.loyalty_points, orders.points_*), nạp hàng theo batch_id, disputes.kind gộp sự cố, sellers.brand_tone cho AI. v3.1 (07/10): gói thuê bao theo BRD §7.1 (subscriptions, sellers PAST_DUE, admin gán ô qua slot_assignments, bỏ slot_rentals); hoàn tiền tay với STK khách tự khai trên e-receipt (orders.refund_bank_*) + đối soát hằng ngày. Giữ nguyên: sổ cái kép, token mở hộc một lần, cách ly tenant, log AI để train.</div>")
out = out.replace("Nhãn <b>MỚI</b>: bảng không có trong ERD của sinh viên.", "Cột <b>•</b>: bắt buộc (NOT NULL).")
open("florabot_erd_v3.html", "w", encoding="utf8").write(out)
print(len(tables), "bảng,", ncol, "cột,", len(rels), "quan hệ →", "florabot_erd_v3.html")
