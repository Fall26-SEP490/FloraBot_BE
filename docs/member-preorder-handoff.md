# Thành viên và đặt hoa trên website

User đã chốt hỗ trợ cả mua bó có sẵn và đặt mẫu riêng; ưu tiên đặt mẫu trên web để shop chuẩn bị, đưa đến tủ và khách nhận theo lịch. Tài liệu này thay thế các kết luận "chưa chốt preorder" trong báo cáo điều tra trước đó.

## Đường dẫn

Dùng full stack `http://localhost:8088`, không dùng cổng preview Astro để mở trực tiếp trang portal.

- `/dat-hoa`: danh mục thật theo shop/gói còn hiệu lực, tủ nhận và tồn bó hoa.
- `/dang-ky`, `/dang-nhap`: đăng ký/đăng nhập thành viên.
- `/tai-khoan`: hồ sơ, đơn đặt trước, báo giá, thanh toán, biên nhận, lịch sử, đổi mật khẩu.
- `/quen-mat-khau`, `/dat-lai-mat-khau`: khôi phục mật khẩu qua email.
- `/login`: đăng nhập shop/ADMIN; `/seller` và `/admin` có danh sách đơn web và các chức năng vận hành hiện hữu.

Tài khoản đăng ký công khai luôn là CUSTOMER. Không tự chọn quyền shop/ADMIN. Phiên kiosk có `kiosk_id` không được dùng các endpoint `/api/member/*`. Đăng ký email không tự liên kết với hồ sơ khách OTP theo số điện thoại để tránh nhận nhầm tài khoản.

## Hai luồng nhận hoa

**Mẫu riêng:** chọn mẫu và tủ → ghi mong muốn → shop báo giá/lịch nhận hoặc từ chối → khách đối chiếu giá, ghi chú, giờ nhận và xác nhận → thanh toán → shop nạp bó đúng ô tại đúng tủ → khách dùng mã biên nhận tại tủ để nhận.

Giá danh mục chỉ là giá tham khảo cho mẫu riêng. Nếu shop sửa báo giá lúc khách đang xem, backend từ chối xác nhận nội dung cũ. Không tạo order_items giả trước khi hoa được nạp thật. Sau thanh toán, đơn giữ PAID, không tự hoàn thành và không phát token mở cửa.

**Bó có sẵn:** chọn mẫu/tủ và mua bó đang khả dụng → tạo checkout giữ tồn kho → thanh toán trong thời hạn → hoa giữ riêng đến hạn nhận → khách xác thực tại tủ. Dùng cùng quy tắc tồn kho và khóa của kiosk; không cấp khóa thiết bị cho trình duyệt khách.

Các giá trị mặc định hiện tại (cần shop thông báo rõ cho khách):

- Khách đề nghị nhận mẫu riêng từ 2 giờ đến 30 ngày tới.
- Shop báo lịch nhận từ 1 giờ đến 30 ngày tới.
- Báo giá có hiệu lực tối đa 24 giờ, kết thúc muộn nhất 30 phút trước giờ nhận.
- Thanh toán theo `hold_minutes` hiện hữu (seed: 7 phút).
- Cửa sổ nhận mặc định 2 giờ (`web_pickup_hours`); bó có sẵn còn bị giới hạn bởi hạn tươi.
- Shop nạp mẫu riêng từ 2 giờ trước giờ hẹn, phải đủ hạn tươi đến hết cửa sổ nhận.
- Khách không nhận trước giờ hẹn. Thanh toán muộn tạo khoản hoàn, không mở cửa.
- Khách tự hủy khi chưa thanh toán. Sau thanh toán cần shop/ADMIN đối chiếu và hủy; tạo yêu cầu hoàn tiền qua quy trình hiện hữu, không giả lập chuyển tiền ngân hàng.
- Job `expire_web_orders` xử lý mẫu riêng đã trả tiền nhưng chưa nạp trước hạn. Bó đã nạp dùng job hết hạn nhận hiện hữu.

Request ID và payload được giữ lại khi mạng không rõ kết quả; gửi lại cùng yêu cầu không tạo đơn trùng. Không đổi mẫu/giờ trong lần gửi lại cùng ID. Dữ liệu hiển thị tên mẫu, shop, tủ và địa chỉ được chụp tại thời điểm đặt.

## Database và triển khai

Migration bổ sung: `db/migrations/010_web_preorders.sql`. Không sửa SQL nguồn `db/FloraBot_DB_v3`. Migration tạo `ordering.web_requests`, view catalog và các hàm web; bổ sung nhánh trì hoãn mở cửa cho checkout web. Checkout tại kiosk vẫn đi nhánh hiện hữu.

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
