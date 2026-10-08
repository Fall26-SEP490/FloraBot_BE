> Cập nhật 08/10/2026: user đã chọn cả hai kiểu preorder. Phần mô tả thiếu membership/preorder dưới đây là mốc điều tra trước triển khai. Xem [bàn giao thành viên và đặt trước](member-preorder-handoff.md) cho hành vi hiện tại.

# Điều tra luồng khách đặt trước và trang quản trị — 08/10/2026

## Luồng khách đặt trước hiện chưa tồn tại

Nguồn DB v3, `db/FloraBot_DB_v3/01_schema.sql:2`, ghi rõ bỏ preorder.
`04_scenarios.sql:634` còn kiểm tra việc loại bỏ các cột liên quan.
Brief người dùng gửi mô tả landing là trang bán gói thuê cho seller; code landing
hiện hướng CTA về gói thuê, vị trí tủ và đăng ký shop. Không có route mua hàng
trên web hoặc chọn lịch nhận cho khách.

Checkout hiện phục vụ mua tại kiosk: giữ hoa chờ thanh toán 7 phút; sau trả tiền,
`flow.checkout_paid` phát token mở cửa khi kiosk online; token có hạn 1 phút,
thời hạn nhận lại qua e-receipt là 30 phút theo cấu hình seed. Vì vậy việc gắn
nút đặt trước vào flow này có thể mở cửa khi khách chưa có mặt.

Đã gửi câu hỏi chốt phạm vi cho người dùng, chưa thay nghiệp vụ/database:

1. Mua online hoa có sẵn trong tủ rồi đến nhận: cần tách trả tiền khỏi phát lệnh
   mở cửa, xác thực khách tại tủ, quy tắc giữ tồn/hạn nhận/hết hạn/hoàn tiền và
   xử lý tranh chấp với mua trực tiếp tại kiosk.
2. Đặt hoa theo yêu cầu để shop chuẩn bị: cần thêm lịch chuẩn bị, xác nhận shop,
   hạn chót, ô nhận, nạp đúng đơn, hủy và hoàn tiền. Đây là phạm vi lớn hơn.
3. Giữ v3: chỉ bổ sung hướng dẫn mua tại kiosk cho khách trên landing.

Không dùng khóa thiết bị kiosk trong trình duyệt khách từ xa. Dù chọn hướng nào,
khách cần đường dẫn rõ từ navbar/hero; hiện chưa thêm CTA hứa chức năng chưa có.

## Navbar

Trước sửa `.site-header` là header thường nên cuộn khỏi viewport. Đã đổi sang
sticky, nền đặc và đo chiều cao thực tế để offset anchor không che tiêu đề.
Kiểm thử ở 390/820/1440px: header bám đỉnh, anchor không bị che, không tràn ngang.
Bằng chứng: `FE/artifacts/navigation-browser.log`.

## Admin: chức năng có thật, bản demo còn thiếu cấp tài khoản

- `http://127.0.0.1:4321` là preview landing; `/admin`, `/login`, `/api/auth/me`
  đều trả 404 ở đó. Đây không phải origin chạy toàn bộ ứng dụng.
- `http://localhost:8088/admin` được Caddy phục vụ portal; `/api/auth/me` trả 401
  khi chưa đăng nhập. `/admin` có guard role và chuyển khách chưa đăng nhập về login.
- `apps/portal/src/main.tsx` dựng bảy khu vực: shop, ô tủ, sự cố, phản ánh,
  hoàn tiền, rút tiền và đối soát. Các khu vực gọi API, không chỉ là ảnh demo.
- Bài `FE/tests/integration/admin-readiness.spec.ts` dùng tài khoản cấp bằng CLI
  trên database `florabot_browser_tests`, đi qua browser, gateway, API và PostgreSQL
  thật: đăng nhập, tải sáu API danh sách 200, thấy đủ bảy khu vực, duyệt shop,
  đăng xuất và bị chặn khi quay lại `/admin`. Không mock các API này.
- Database chính `florabot` hiện có 3 ADMIN, không tài khoản nào có bcrypt hash
  đã provision (chỉ kiểm tra số lượng, không ghi mật khẩu/hash vào báo cáo).
  Cần cấp thông tin đăng nhập qua quy trình operator trước khi sử dụng demo chính.
  Điều tra này không tự đặt/đổi mật khẩu các tài khoản đó.

Về trải nghiệm: `/admin` hiện là workspace dài gồm nhiều section, chưa có sidebar,
trang tổng quan hay điều hướng riêng từng nghiệp vụ. Có thể vận hành các chức năng
đã có, nhưng chưa phải dashboard quản trị hoàn chỉnh. Bài kiểm tra mới chứng minh
đăng nhập/đọc dữ liệu/duyệt shop; không thay thế nghiệm thu mọi thao tác admin.

Lần kiểm thử đầu điều hướng về `/admin` ngay sau click logout mà chưa chờ request
kết thúc nên thất bại. Đã sửa test chờ về login trước khi truy cập lại, lần chạy
cuối đạt: `FE/artifacts/admin-readiness-integration-final.log`.

Một bộ integration cũ (`registration.spec.ts`) còn nhập URL ở form báo lỗi sau khi
form đã chuyển upload riêng tư; đây là test cần cập nhật, không phải bằng chứng
luồng hiện tại vẫn nhận URL. Đợt này chỉ chạy bài admin mới, không tuyên bố cả bộ
integration cũ đều đạt.
