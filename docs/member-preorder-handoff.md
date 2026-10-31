# Thành viên và đặt hoa trên website

Website chỉ nhận đơn đặt hoa theo mẫu. Hoa bán sẵn được nạp vào kiosk và khách mua trực tiếp tại kiosk; API vẫn giữ các đơn `STOCK` cũ để đọc và xử lý tính lặp.

## Đường dẫn

Dùng full stack `http://localhost:8088`, không dùng cổng preview Astro để mở trực tiếp trang portal.

- `/dat-hoa`: danh mục thật theo shop/gói còn hiệu lực, tủ nhận và tồn bó hoa.
- `/dang-ky`, `/dang-nhap`: đăng ký/đăng nhập thành viên.
- `/tai-khoan`: hồ sơ, đơn đặt trước, báo giá, thanh toán, biên nhận, lịch sử, đổi mật khẩu.
- `/quen-mat-khau`, `/dat-lai-mat-khau`: khôi phục mật khẩu qua email.
- `/login`: đăng nhập shop/ADMIN; `/seller` và `/admin` có danh sách đơn web và các chức năng vận hành hiện hữu.

Tài khoản đăng ký công khai luôn là CUSTOMER. Không tự chọn quyền shop/ADMIN. Phiên kiosk có `kiosk_id` không được dùng các endpoint `/api/member/*`. Đăng ký email không tự liên kết với hồ sơ khách OTP theo số điện thoại để tránh nhận nhầm tài khoản.

## Luồng đặt hoa trên website

Chỉ tài khoản có role `CUSTOMER` được dùng các API `/api/member/preorders` để tạo, xem, xác nhận, hủy và lấy liên kết thanh toán đơn đặt trước. Tài khoản `SELLER` chỉ xử lý đơn của shop qua các API seller; người bán muốn mua hoa phải đăng nhập bằng một tài khoản `CUSTOMER` riêng.

Chọn mẫu và tủ → ghi mong muốn → shop báo giá/lịch nhận hoặc từ chối → khách đối chiếu giá, ghi chú, giờ nhận và xác nhận → thanh toán → seller hoặc seller_staff được giao công việc đưa bó hoa vào ô `PREORDER` tại đúng kiosk → khách dùng mã biên nhận tại tủ để nhận.

Ảnh xem trước gửi qua chat là tùy chọn, không tạo bước duyệt ảnh và không chặn việc đưa hoa vào kiosk. Khách vẫn phải xác nhận báo giá/lịch của shop trước khi thanh toán. Giá danh mục chỉ là giá tham khảo. Nếu shop sửa báo giá lúc khách đang xem, backend từ chối xác nhận nội dung cũ. Không tạo `order_items` trước khi hoa được nạp thật. Sau thanh toán, đơn giữ PAID, không tự hoàn thành và không phát token mở cửa.

Luồng nạp bán sẵn chỉ nhận ô `WALK_IN`; seller_staff chỉ nạp qua công việc giao hoa được phân công. Ô có `zone = NULL` chưa được phân loại và không nhận hàng cho tới khi seller gán `WALK_IN` hoặc `PREORDER`.

Các giá trị mặc định hiện tại (cần shop thông báo rõ cho khách):

- Khách đề nghị nhận mẫu riêng từ 2 giờ đến 30 ngày tới.
- Shop báo lịch nhận từ 1 giờ đến 30 ngày tới.
- Báo giá có hiệu lực tối đa 24 giờ, kết thúc muộn nhất 30 phút trước giờ nhận.
- Thanh toán theo `hold_minutes` hiện hữu (seed: 7 phút).
- Cửa sổ nhận mặc định 2 giờ (`web_pickup_hours`).
- Shop nạp mẫu riêng từ 2 giờ trước giờ hẹn, phải đủ hạn tươi đến hết cửa sổ nhận.
- Khách không nhận trước giờ hẹn. Thanh toán muộn tạo khoản hoàn, không mở cửa.
- Khách tự hủy khi chưa thanh toán. Sau thanh toán cần shop/ADMIN đối chiếu và hủy; tạo yêu cầu hoàn tiền qua quy trình hiện hữu, không giả lập chuyển tiền ngân hàng.
- Job `expire_web_orders` xử lý mẫu riêng đã trả tiền nhưng chưa nạp trước hạn. Bó đã nạp dùng job hết hạn nhận hiện hữu.

Request ID và payload được giữ lại khi mạng không rõ kết quả; gửi lại cùng yêu cầu không tạo đơn trùng. Không đổi mẫu/giờ trong lần gửi lại cùng ID. Dữ liệu hiển thị tên mẫu, shop, tủ và địa chỉ được chụp tại thời điểm đặt.

## Database và triển khai

Các migration đặt trước ở `db/migrations/010_web_preorders.sql`, `db/migrations/20261029_preorder_fulfillment_zones.sql`, `db/migrations/20261029_z_customer_only_web_preorders.sql` và `db/migrations/20261030_seller_configurable_slot_zones.sql`. Không sửa SQL nguồn `db/FloraBot_DB_v3`. Migration ngày 2026-10-30 chuyển enrollment sang tổng số ô, thêm API cho seller phân loại ô trống, giới hạn nạp bán sẵn vào `WALK_IN`, và yêu cầu `PREORDER` rõ ràng cho đơn đặt trước và fulfillment. Ô `NULL` là chưa phân loại, không được dùng làm ô chung.

Áp dụng migration trước khi chạy API mới. Bootstrap `db-init` tự chạy migration; không dùng script reset database test với database runtime. Backup trước khi áp dụng trên môi trường có đơn thật. Rollback API về bản cũ chỉ sau khi xử lý hết đơn web đang chờ; không xóa bảng đơn để rollback.

## Tích hợp bên ngoài

- payOS: `PAYOS_CLIENT_ID`, `PAYOS_API_KEY`, `PAYOS_CHECKSUM_KEY`, `PAYOS_RETURN_URL`, `PAYOS_CANCEL_URL`; cấu hình webhook về `/api/payments/webhook`. Trang trả về tài khoản không tự đánh dấu đã thanh toán; chỉ webhook hợp lệ cập nhật tiền.
- Email: `EMAIL_PROVIDER`, `EMAIL_SENDER` và credential Brevo/Google SMTP theo hướng dẫn handoff chính; `MEMBER_WEB_URL` là origin public tin cậy, bắt buộc HTTPS ngoài Development. Giữ DataProtection key bền vững qua các lần deploy. Link reset 20 phút, dùng một lần; đổi mật khẩu thu hồi access/refresh theo phiên bản credential.
- Thiếu provider: API trả lỗi 503 rõ ràng. Không báo gửi email hoặc thanh toán thành công giả.
- Cloudinary private evidence: xem [handoff chính](handoff.md) và [Cloudinary Console](https://console.cloudinary.com/). Evidence vẫn theo purpose/actor/resource; không dùng ảnh minh chứng công khai làm mẫu hoa.
- Tài khoản vận hành cần cấp bằng CLI `provision-user` như handoff chính. Kiểm thử dùng tài khoản riêng trong database test, không thay mật khẩu tài khoản runtime.

## Kiểm thử

- Backend: `MemberTests`, `WebPreorderTests`, ma trận quyền và hồi quy payment/kiosk/jobs.
- Browser mock: `FE/tests/portal/member-preorder.spec.ts`, gồm WCAG automated checks, desktop/mobile và hai biểu mẫu đặt hoa.
- Browser thật: `FE/tests/integration/member-readiness.spec.ts` và `admin-readiness.spec.ts`, qua gateway/API/PostgreSQL.
- Kiểm thử phần cứng thật, giao dịch payOS thật và email thật cần môi trường/credentials; kết quả tự động không chứng nhận mọi tình huống thực tế hay toàn bộ WCAG thủ công.


### Kết quả kiểm tra local

75 bài portal, 6 bài deployed stack, 3 bài navbar, 8 trường hợp preorder và 6 bài gateway đã qua. Full API có 210 bài qua, 1 bài email qua RabbitMQ timeout và 1 MQTT tùy chọn skip; bài email chạy riêng đã qua, không đổi code hoặc nâng timeout. Xem ACCEPTANCE.md ở workspace để đọc log tương ứng. Không xem kết quả chạy lại riêng là một lần full suite hoàn toàn xanh.
