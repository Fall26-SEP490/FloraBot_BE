# Kế hoạch hoàn thiện nghiệp vụ FloraBot — 09/10/2026

## Nguồn phạm vi và thứ tự ưu tiên

Nguồn chính mới: PHÂN TÍCH NGHIỆP VỤ.pdf (8 trang, 92 mã UC khác nhau). Thứ tự trang: Admin Management; Operations Manager; Technician; Seller Portal; Seller Staff; Kiosk Customer; PreOrder Customer; AI/Data Mining. Bảy trang đầu là **bảy vai trò người dùng đã chốt với thầy**. AI là dịch vụ hỗ trợ, không phải user thứ tám.

Quyết định chắc chắn: không hoa hồng; một Seller thuê trọn một Kiosk và một Kiosk thuộc tối đa một Seller; hoa giả không tự hết hạn/ẩn theo độ tươi; Staff thuộc đúng shop; Technician thuộc vận hành nền tảng theo vùng. Đề trường gốc và audit cũ chỉ dùng tham khảo khi không mâu thuẫn với bản nghiệp vụ mới.

Nhánh triển khai: codex/seven-role-business-flows trên BE, FE, Kios, AI. Không merge/push vào main. Giữ thay đổi local có sẵn. Worker làm trong worktree riêng; Codex tích hợp bằng diff đã kiểm tra. Bản demo/audit cũ giữ nguyên cho người dùng thử.

## Đánh giá nghiệp vụ

Các nhóm vai trò hợp lý, tách người quản trị nền tảng, điều phối vận hành, người sửa thiết bị, shop và nhân viên shop. Quy tắc tenant/phạm vi việc và bằng chứng là cơ sở tốt để tránh mở cửa hoặc thao tác hàng sai quyền.

Tài liệu cần chuẩn hóa trước khi coi là acceptance specification:
- ADM02 vẫn ghi ma trận role cũ Admin/Seller/Staff/Customer; thay bằng bảy vai trò đã chốt, tách thiết bị/service principal.
- ADM08 nói hoàn vào ví người dùng/gateway, ADM16 và PRE04 note nói admin duyệt hoàn thủ công; cần chốt một luồng thực thi rõ ràng.
- SEL16 “ghi có ngay” không làm rõ tiền tạm giữ và tiền khả dụng; SEL19/ADM17 yêu cầu số dư thực tế trừ hold/dispute.
- SEL23 vừa nói phụ kiện tại kiosk vừa chỉ preorder; CUS02 có phụ kiện giỏ walk-in. Phải xác định cách giao phụ kiện vật lý, không chỉ trừ tồn.
- TEC09 nói kiosk ACTIVE ngay khi báo xong, MGR06 nói Manager duyệt và self-test PASS mới đóng. Trình tự đề nghị: technician gửi kết quả, manager xác nhận, hệ thống mới khôi phục bán khi không còn blockers.
- X phút P1, X giờ xin nghỉ, N lần chỉnh sửa và timeout12–24h chưa có giá trị; cần cấu hình, không rải hằng số trong UI/SQL.
- STF02 tham chiếu SEL25 là giao việc nhưng SEL25 thực tế lịch ca; cần bổ sung chức năng giao phiếu riêng.
- Slot EMPTY/AVAILABLE/RESERVED/PENDING_RELEASE không đồng nghĩa kiosk ONLINE/MAINTENANCE/DISABLED. Tách state thiết bị, sức chứa, hàng, lease và đơn.
- Reorder và Return order cần định nghĩa rõ; không tự coi Return order là một giao dịch bán mới.
- Check-in QR+GPS là tín hiệu bằng chứng, không tự chứng minh hiện diện vật lý; unlock vẫn cần quyền/phiếu/check-in hợp lệ tại server.
- AI04 note đổi export dataset thành export đối soát; AI05 vẫn yêu cầu dataset mining. Export đối soát và dataset ẩn danh nên tách mục đích/quyền; không gửi dữ liệu bank/PII sang AI.
- Mốc2giây AI cần đo toàn hành trình, không dùng mỗi provider timeout để tuyên bố đạt NFR.

Cập nhật quyết định trực tiếp của CEO (ưu tiên hơn PDF): toàn bộ hoa là **hoa giả**, kể cả Walk-in và Preorder; một kiosk có hai khu ô riêng WALK_IN/PREORDER; phụ kiện chỉ bán kèm Preorder, seller chuẩn bị chung trước khi nạp; seller cung cấp bank, admin xét yêu cầu rút rồi chuyển khoản thủ công; refund khách cũng do admin xét và chuyển thủ công. Không tạo ví khách hoặc chuyển tiền tự động theo các dòng PDF cũ. Chính sách tiền tạm giữ/khả dụng cần làm rõ riêng, không suy diễn từ quyền admin nắm tiền.

Các mốc P1=15phút, xin nghỉ12giờ, tối đa2lần chỉnh ảnh, nhận24giờ/nhắc2giờ vẫn là PENDING_DECISION. Mốc không phản hồi ảnh2giờ đã được CEO phê duyệt: tính từ ảnh mẫu mới nhất seller gửi, không có phản hồi thì cho phép tiếp tục nạp tủ; có yêu cầu sửa trong hạn thì theo luồng sửa. Không áp dụng tự động khi chưa chốt; phần catalog không phụ thuộc tiếp tục làm. Hết thời hạn nhận hàng là giải phóng sức chứa/quyền nhận, không phải hoa giả bị hết hạn/hủy.

## Vai trò và ranh giới quyền

| Vai trò | Phạm vi | Không được có |
|---|---|---|
| Admin | Tài khoản nền tảng, tenant/kiosk/gói, audit, phê duyệt tài chính | Bỏ qua kiểm soát hai người/ledger/evidence bằng API tùy ý |
| Operations Manager | Kiosk/vật tư/Technician/ticket/ca trong vùng được giao | Ví shop, payout, tự cấp Admin hoặc vùng ngoài phạm vi |
| Technician | Phiếu mình nhận, kiosk/slot đúng phiếu và vùng, thao tác kỹ thuật có check-in | Quản shop, sửa giá, payout; mở tủ không phiếu/token một lần |
| Seller | Shop của mình, một kiosk leased, hàng, staff, ca/voucher/ví/gói | Shop khác, role vận hành, tự duyệt rút/hoàn |
| Seller Staff | Phiếu, ca, đơn/hàng thuộc shop và kiosk được giao, cờ quyền tùy chọn | Ví, payout, gói, voucher, báo cáo doanh thu, cấp Staff |
| Kiosk Customer | Mua tại kiosk, giỏ/survey/feedback; OTP khi cần điểm | Xem dữ liệu khách trước, hàng reserved, quyền thiết bị chung |
| PreOrder Customer | Tạo/xem/trao đổi/duyệt ảnh của đơn mình bằng account hoặc link có proof | Dò/list đơn khách khác, tự đổi trạng thái PAID, mở ô khác |

Device/API key, SYSTEM và AI internal token là tài khoản kỹ thuật, không cộng vào bảy vai trò. Khách walk-in có thể ẩn danh; không bắt buộc tạo tài khoản chỉ để đạt số role. Cùng một người có thể mua tại kiosk và preorder nhưng phải qua ranh giới quyền theo kênh/đơn. Chi tiết migration/storage cần ADR và test thực tế, không chỉ sửa nhãn frontend.

## Các đợt triển khai theo dependency

| Đợt | Phạm vi UC | Kết quả nghiệm thu |
|---|---|---|
| 1. Quyền, tenant, tài khoản | ADM01/02; MGR01/04; TEC01; SEL18/20; STF01; customer actor boundaries | Ma trận bảy actor; session/revocation; chặn truy cập chéo shop/vùng và leo thang; migration không âm thầm biến staff cũ thành seller staff |
| 2. Kiosk lease và catalog hoa giả | ADM04/05/06/09/10/11/15; SEL01/02/03/04/07/08/09/21/23 | Một seller–một kiosk; kích thước phù hợp; thu hồi không gán lại khi còn hàng; nhắc kiểm tra thay freshness expiry; CRUD có audit |
| 3. Walk-in hoàn chỉnh | CUS01–05; SEL12/15/16; ADM03 | Multi-bouquet hold5phút, webhook idempotent, unlock token hợp lệ, chu trình cửa, không bán reserved, feedback sau nhận; phụ kiện theo quyết định |
| 4. Preorder hoàn chỉnh | PRE01–13; SEL05/06/11/13/24; STF04 | Thanh toán100%, lịch/sức chứa reserved, chat scoped, ảnh/chỉnh sửa/timeout, nạp đúng slot, mã nhận giới hạn thử, quá hạn/reminder/refund/feedback |
| 5. Shop staff và ca/phiếu | SEL18/25; STF01–07 | Invitation/đổi pass đầu, pool/nhận/từ chối, không chồng ca, check-in, nạp/thu hồi theo task, báo cáo tự sinh; khóa staff thu hồi session/task/ca |
| 6. Vận hành kỹ thuật | MGR01–12; TEC01–10 | Ticket P1–P3/SLA, giao việc theo vùng/ca/kỹ năng, check-in, trước/sau repair, vật tư, self-test, manager duyệt, maintenance/escalation |
| 7. Tiền, đối soát, báo cáo | ADM07/08/16/17; SEL10/16/19/22/24; PRE13 | Tổng tiền ledger nhất quán, không hoa hồng, hold/refund/withdrawal đúng policy; hai-admin; bằng chứng; báo cáo/export đúng quyền |
| 8. AI và planning | AI01–07 | Survey/card preview, chỉ candidate còn hàng, fallback/deadline; export ẩn danh/đối soát tách; mining support/confidence; topcombo/theo dịp; nạp không vượt ô trống trừ reserved |
| 9. Nghiệm thu liên repo | Tất cả 92 UC | Trace UC→source→API→UI→test; E2E bảy actor, test simulator/MQTT, test provider sandbox, restore/deploy/docs; đo NFR và ghi rõ chưa kiểm chứng |

Đợt3–6 có thể triển khai module độc lập sau nền nhưng không cho nhiều worker cùng sửa migration/contracts/lockfiles. Các phụ thuộc external không được giả thành thành công; mock dùng cho test có nhãn.

## Gate đóng từng luồng

- Positive test đúng actor, trạng thái và tiền đề; negative test actor khác/tenant khác/vùng khác và invalid transition.
- API/SQL là nơi quyết định quyền và trạng thái, UI chỉ phản ánh.
- Migration forward-only; rehearsal trên DB disposable; không drop app/demo DB hoặc suy diễn ownership nếu dữ liệu legacy nhiều seller trong một kiosk.
- Tranh chấp concurrency/retry: giữ slot, thu hồi lease, chi tiền và webhook phải an toàn khi lặp.
- FE lint/typecheck/unit/build + Playwright case thật cho phần đổi; BE format/tests/source-SQL gates. Kios/AI theo CI.
- Worker báo cáo constraint trước edit, diff/check/remaining sau edit; Codex review độc lập rồi mới tích hợp.
- Không đánh dấu Done nếu chỉ thêm role label hoặc trang trống.
- Không push main, không sửa hook/test gate để né lỗi, không commit credentials.

## Trạng thái khởi động

- Đã trích PDF, đếm92 UC, lưu use-case-ids.json.
- Đã tạo/push nhánh riêng bốn repo; remote main không đổi.
- Ba worker đang rà soát read-only mapping và đề xuất increment đầu (BE quyền/tenant, FE navigation/session, Kios+AI dependencies).
- Implementation đầu được chọn sau khi kiểm tra phương án migration và hợp đồng BE↔FE; theo dõi evidence riêng từng increment tại đây.


## Increment danh mục hoa giả — đã tích hợp 09/10/2026

BE feature commit cb027d9; FE feature commit 9f308b4. POST tạo ARTIFICIAL/DRAFT và PUT metadata/kích thước, kiểm tra shop/quyền/thuê bao; đổi kích thước khi còn STOCKED/HELD bị chặn. Giá của bó đã nạp giữ snapshot. Trigger mới ngăn hết hạn theo tuổi cho ARTIFICIAL; dữ liệu LEGACY_FRESH chưa chuyển đổi hàng loạt.

Evidence: source SQL326pass; API253pass/1MQTTskip, gateway11pass; portal129pass (API mocked), FEunit3/lint/typecheck/buildpass, BEbuild/formatpass. Contracts xuất từ API thật và generate bằng pnpm contracts. Chưa có acceptance browser-to-live-API cho catalog mới; chưa xác nhận MQTT vật lý hoặc rotation3tài khoản.

UC_SEL_01/08 PARTIALLY_IMPLEMENTED, không coi toàn bộ UC hoặc migration7role hoàn tất. Mốc thời gian vẫn PENDING_DECISION. Tiếp theo: permission7actors/tenant và whole-kiosk zones, chuyển đổi inventory legacy có rehearsal; rồi nghiệm thu các luồng đầy đủ theo thứ tự wave. Các audit worker ban đầu đã kết thúc, không có background scheduler.

Approved clarification — 2026-10-09: photo no-response timeout is2hours from latest seller photo delivery. Permit progression to stocking when no response, to avoid prolonged waiting. This rule is recorded for the preorder implementation; catalog increment does not yet implement the timer. It does not free an occupied slot, guarantee slot availability, override a timely revision request, or automatically release money/refund rights. Other time proposals remain pending.
