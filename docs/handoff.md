# FloraBot — bàn giao vận hành

Ngày cập nhật: 08/10/2026. FE và BE là hai thư mục độc lập.

## Chạy tại máy

Từ thư mục BE, chạy `./scripts/start-local-stack.ps1` khi Docker đang hoạt động.
Mở [FloraBot](http://localhost:8088/), [portal](http://localhost:8088/login)
và [kiosk](http://localhost:8088/kiosk/).
Sau khi thay biến môi trường phải tạo lại container API; sau thay FE phải build lại web.
Không xóa volume PostgreSQL hoặc Data Protection khi khởi động lại.

## Cloudinary: cấu hình một lần

1. Đăng nhập [Cloudinary Console](https://console.cloudinary.com/), chọn đúng product environment.
2. Lấy Cloud name, API key và API secret tại phần API Keys. Đặt ba biến
   `CLOUDINARY_CLOUD_NAME`, `CLOUDINARY_API_KEY`, `CLOUDINARY_API_SECRET` trong
   `BE/infra/secrets/local-stack.env` đã được gitignore, hoặc secret manager của môi trường triển khai.
   Không đưa secret vào FE, `PUBLIC_*`, ảnh chụp màn hình hoặc tài liệu bàn giao.
3. API ký yêu cầu upload bằng server secret. Không cần unsigned upload preset.
   Ảnh sản phẩm dùng `upload` trong `florabot/`; ảnh minh chứng dùng
   `authenticated` trong `florabot-evidence/`. Không chuyển minh chứng sang public.
4. Từ BE, chạy `docker compose --env-file infra/secrets/local-stack.env -f docker-compose.yml -f docker-compose.app.yml up -d --build --no-deps api`.
5. Dùng ảnh thử không chứa dữ liệu cá nhân: tải qua form báo lỗi hoặc phản ánh,
   kiểm tra Console hiển thị delivery type authenticated, gửi hồ sơ và mở ảnh bằng tài khoản admin.
   Kiểm tra link CDN gốc không mở được khi chưa có chữ ký. Không nhầm tài khoản đang đăng nhập Console với người xem ẩn danh.

Tài liệu nhà cung cấp:
[kiểm soát truy cập ảnh](https://cloudinary.com/documentation/control_access_to_media),
[Upload API](https://cloudinary.com/documentation/image_upload_api_reference),
[cách ký yêu cầu](https://cloudinary.com/documentation/authentication_signatures).

## Luồng ảnh minh chứng

| Tình huống | Cách sử dụng |
| --- | --- |
| Shop báo lỗi tủ | Chọn ô, mô tả, chọn tệp, bấm tải ảnh, xem lại và xác nhận báo sự cố. |
| Shop nhận lại hoa | Với ô PENDING_REMOVAL/PENDING_RELEASE, nhập lý do, tình trạng hư hỏng, tải ảnh rồi xác nhận đã nhận lại hoa. |
| Khách phản ánh | Mở biên nhận bằng mã đơn/mã tra cứu, chọn phản ánh, tải ảnh rồi xác nhận. Không cần tài khoản portal. |
| Admin xem minh chứng | Mở hồ sơ sự cố/phản ánh; liên kết ảnh đi qua API kiểm tra quyền admin. |
| Admin ghi nhận hoàn tiền/chi trả | Tải ảnh chứng từ trong hồ sơ khoản hoàn hoặc yêu cầu rút tiền, đối chiếu giao dịch ngân hàng rồi xác nhận. Người chi phải khác người duyệt. Hồ sơ đã chi có liên kết xem chứng từ qua API admin. |

Chỉ nhận PNG/JPEG/WebP, tối đa 5 MiB. Mã ảnh có hạn 30 phút, gắn với loại nghiệp vụ,
người gửi và ô/đơn; không chuyển mã ảnh sang hồ sơ khác. Mã biên nhận đi trong header,
không nằm trong URL tải ảnh. Ảnh được kiểm tra dung lượng, loại và SHA-256 từ dữ liệu
lưu tại nhà cung cấp. Cùng một ảnh tải lên không được ghi nhận hai lần qua gửi đồng thời.

API dành cho bên tích hợp:

| Endpoint | Nội dung và quyền |
| --- | --- |
| `POST /api/sellers/{sellerId}/slots/{slotId}/evidence/{purpose}` | Body nhị phân và Content-Type tương ứng; Merchant + SameSeller + quyền sở hữu ô. purpose là return_to_seller hoặc report_device_fault. |
| `POST /api/receipts/{orderId}/evidence` | Body nhị phân; header X-Receipt-Token; kiểm tra capability của đơn và rate limit. |
| `GET /api/admin/evidence/{attachmentId}` | Admin; trả byte ảnh sau kiểm tra hash, với no-store và nosniff. Không trả link tải Cloudinary có chữ ký. |
| `POST /api/admin/evidence/{purpose}/{resourceId}` | Admin; body ảnh nhị phân. purpose là confirm_refund hoặc pay_withdrawal; resourceId là ID khoản hoàn hoặc yêu cầu rút tiền. Reference gắn với đúng ADMIN, hồ sơ và nghiệp vụ. |
| `GET /api/admin/evidence/{purpose}/{resourceId}` | Admin; danh sách chứng từ tài chính chỉ chứa liên kết đọc qua API, không trả URL Cloudinary. |

Upload trả `reference` và `expiresAt`. Truyền reference vào `p_photo`, `p_photo_url` hoặc `p_proof_url`
của flow tương ứng. URL HTTPS tự nhập không còn được dùng để tạo minh chứng mới.
Giữ nguyên hợp đồng nghiệp vụ của SQL gốc; server thay reference bằng URL authenticated
và ghi metadata trong cùng giao dịch. Lưu Data Protection key ring bền vững để mã ảnh
không mất hiệu lực khi container được thay thế.

## Lỗi và cách khôi phục

| Trạng thái | Hành động |
| --- | --- |
| 400 | Kiểm tra loại ảnh/nội dung; nếu mã ảnh hết hạn hoặc sai hồ sơ, tải lại ảnh đúng hồ sơ. |
| 401/403 | Đăng nhập lại hoặc dùng đúng tài khoản. Không mở quyền public để xử lý lỗi. |
| 404 | Kiểm tra ô/đơn và quyền truy cập; hệ thống không tiết lộ tài nguyên của shop khác. |
| 409 | Kiểm tra trạng thái nghiệp vụ và kết quả lần gửi trước; có thể ảnh đã được dùng. |
| 413/415 | Giảm dung lượng về tối đa 5 MiB hoặc đổi sang định dạng được hỗ trợ. |
| 429 | Chờ rồi thao tác lại, không gửi tự động liên tiếp. |
| 503 khi tải/xem ảnh | Kiểm tra secret, cloud, quota và kết nối nhà cung cấp. Không thay bằng URL công khai. |
| Mất phản hồi lúc gửi nghiệp vụ | Làm mới biên nhận/danh sách ô và đối chiếu với đội ngũ trước khi gửi lại. Không tự động lặp lệnh có tác động nghiệp vụ. |

Tải ảnh thành công nhưng bỏ form hoặc mất phản hồi có thể để lại ảnh chưa gắn hồ sơ.
Không tự xóa ảnh sau timeout: giao dịch có thể đã hoàn tất. Khi dọn kho, đội vận hành
đối chiếu URL trong `notify.attachments` với thư mục authenticated `florabot-evidence/`,
chỉ xem xét ảnh quá 24 giờ chưa được tham chiếu; xác nhận với người phụ trách trước khi
xóa bằng Console. Bản này chưa có job tự xóa ảnh mồ côi hoặc tự xóa minh chứng theo thời hạn.
Dữ liệu minh chứng demo cũ không được chuyển đổi/xác minh hồi tố.
Chứng từ tài chính cũ không phải ảnh private đã xác minh sẽ không mở qua private reader;
cần đối chiếu dữ liệu gốc khi bàn giao, không tự coi metadata demo là bằng chứng thật.
Tải ảnh chỉ kiểm tra định dạng, dung lượng và tính toàn vẹn của file; ADMIN vẫn phải
đối chiếu nội dung chứng từ với giao dịch ngân hàng. Hiện form nhận ảnh PNG/JPEG/WebP,
không nhận PDF. Nếu ngân hàng xuất PDF, xuất trang chứng từ thành ảnh trước khi tải.

## Các dịch vụ còn lại

Các cấu hình mẫu và hướng dẫn chi tiết ở [BE README](../README.md) và `BE/.env.example`.
Đăng ký/cấu hình qua [payOS](https://my.payos.vn/), [Gemini API](https://aistudio.google.com/)
và [Brevo](https://app.brevo.com/). Không dùng khóa thật trong test tự động.
Sau khi có tài khoản thử nghiệm, chạy một vòng seller trả gói → gán ô → nạp hoa →
khách thanh toán → thiết bị MQTT giả lập → nhận hàng → đối soát theo README.

## Kiểm chứng và giới hạn bàn giao

Người dùng đã bỏ yêu cầu bắt buộc JS <100 kB: CI tiếp tục đo và lưu số liệu,
không thất bại chỉ vì vượt mốc này. Các kiểm tra LCP, accessibility và hành vi vẫn giữ nguyên.
Các kết quả chi tiết nằm trong `REQUIREMENTS.md`, `ACCEPTANCE.md` và artifacts của FE/BE.
Kiểm thử provider dùng HTTP giả lập và PostgreSQL thật; không thay thế bước nghiệm thu
Cloudinary/payOS với tài khoản được cấu hình. Kiểm tra NVDA/Firefox thủ công và CI trên
GitHub phải được đội nhận bàn giao thực hiện trước khi tuyên bố nghiệm thu các mục đó.
Không có commit, push hoặc triển khai production trong lần bàn giao này.

## Thành viên và preorder (08/10/2026)

Xem [luồng, quy tắc, cấu hình và kiểm thử](member-preorder-handoff.md). Dùng full stack cổng 8088; migration 010 bắt buộc trước API mới.
