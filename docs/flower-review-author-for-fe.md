# Tên người đánh giá mẫu hoa

`GET /api/shop/products/{productId}/reviews?page=1` bổ sung `reviewerName`
trong từng phần tử `items`. Tên lấy từ `identity.users.full_name` của
`customer_id` gắn với đánh giá, không lấy từ tài khoản đang xem hoặc request gửi lên.
Nếu tên trống hoặc không có hồ sơ tương ứng, API trả `Khách hàng`.

Các trường cũ (`id`, `stars`, `comment`, `photoUrl`, `createdAt`), điểm trung bình,
phân trang và điều kiện gửi đánh giá giữ nguyên. API không trả email, số điện thoại
hay ID tài khoản của người đánh giá. Tên hiển thị theo hồ sơ hiện tại.

Không cần migration database. Cần deploy API mới để có tên thật trên FE.
FE cũ tiếp tục dùng được; FE mới hiển thị `Khách hàng` khi API cũ chưa trả trường này.

OpenAPI được xuất qua `--export-openapi`; FE lấy schema `FlowerReview` từ bản xuất
và chạy `pnpm contracts`, không sửa tay generated types.

Kiểm thử BE: `FlowerReviewTests` kiểm tra tên tiếng Việt của hai người đánh giá
khác nhau trong cùng một mẫu hoa, đồng thời giữ kiểm tra điểm trung bình và ảnh.
