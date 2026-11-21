# Nhập OTP thao tác nội bộ trên web/mobile

Giữ nguyên các API cấp mã và OTP email nạp đơn đặt trước. Sau khi có challenge ID,
người thao tác nhập mã 6 số và gọi `POST /api/me/maintenance-access/{id}/confirm`
với body `{ "code": "012345" }` bằng phiên đăng nhập của chính người được cấp mã.

Cho phép SELLER, SELLER_STAFF và TECHNICIAN. BE lấy kiosk/ô/công việc từ challenge,
kiểm tra người đang hoạt động, vai trò và shop trước khi dùng lại quy trình xác nhận
cũ. Không cần đưa khóa thiết bị lên web. Mã dùng một lần, hết hạn sau 2 phút,
khóa sau 5 lần sai; giới hạn tần suất theo người dùng.

Nạp preorder trả thêm `expiresAt` của phiên nạp sau khi xác nhận OTP thành công.
Giữ `id` của challenge làm `loadingAccessId` cho bước xác nhận đã nạp. Lệnh mở cửa
vẫn qua outbox; cảm biến và quyền hiện tại vẫn được kiểm tra khi mở và hoàn tất.

Kiosk bỏ nút/dialog nhập mã nội bộ và truy vấn pending. Các API kiosk cũ được giữ
để tương thích; OTP nhận hoa của khách, biên nhận và giao thức thiết bị không đổi.

Thao tác hoa thông thường của Seller và nhân viên dùng API mở trực tiếp có sẵn
trên cả web/mobile, không cấp OTP. Nhân viên nạp hoa thay thế theo phản ánh vẫn
dùng OTP, giống web. Kiosk bỏ nhánh đăng nhập SMS để thử mở lại khi nhận preorder;
nếu cửa chưa mở thì hướng dẫn liên hệ shop, không bỏ qua xác thực để gửi lệnh lại.

Không thêm migration. Xuất OpenAPI bằng `--export-openapi <absolute-path>` rồi
sinh lại contracts FE. Kiểm thử tích hợp preorder dùng endpoint xác nhận web,
bao gồm mã sai, không cho thiết bị dùng phiên web, và không dùng lại mã đã nhận.
