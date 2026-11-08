# Seller PAST_DUE: hồ sơ shop và ngân hàng cho FE

Thay đổi này cho phép Seller `PAST_DUE` đọc/sửa hồ sơ shop bằng API hiện có. Chỉ cần publish lại `FloraBot.Api`; riêng thay đổi này **không có SQL migration**. Đường dẫn/body/response giữ nguyên. Gửi cookie hoặc Bearer token của phiên đăng nhập hiện tại; không sửa tay contract FE được sinh từ OpenAPI.

## 1. Quyền truy cập hồ sơ shop

`GET /api/me/shop` và `PATCH /api/me/shop` yêu cầu tài khoản `SELLER` có trạng thái tài khoản `ACTIVE`, thuộc đúng Seller trong token và không phải phiên kiosk.

| Trạng thái shop | GET/PATCH `/api/me/shop` |
| --- | --- |
| `ACTIVE` | Được phép |
| `PAST_DUE` | Được phép |
| `SUSPENDED`, `CLOSED` | `403 Forbidden` |
| `PENDING`, `APPROVED` | `403 Forbidden` |

Tài khoản bị vô hiệu hóa hoặc token không khớp shop vẫn bị chặn. Việc cho sửa hồ sơ khi `PAST_DUE` không đổi trạng thái shop, không gia hạn/kích hoạt lại hợp đồng đã hết hạn và không mở lại quyền bán hàng/nạp hoa.

Ví dụ đọc hồ sơ:

```http
GET /api/me/shop
```

`200 OK`, `Cache-Control: no-store`:

```json
{
  "id": "20000000-0000-0000-0000-000000000001",
  "shopName": "Hoa Nắng",
  "phone": "0901234567",
  "contactEmail": "shop@example.com",
  "address": "123 Đường Hoa, TP.HCM",
  "status": "PAST_DUE"
}
```

Ví dụ cập nhật một phần:

```http
PATCH /api/me/shop
Content-Type: application/json

{
  "shopName": "Hoa Nắng Mới",
  "phone": "0912345678"
}
```

`200 OK` trả lại toàn bộ hồ sơ sau cập nhật:

```json
{
  "id": "20000000-0000-0000-0000-000000000001",
  "shopName": "Hoa Nắng Mới",
  "phone": "0912345678",
  "contactEmail": "shop@example.com",
  "address": "123 Đường Hoa, TP.HCM",
  "status": "PAST_DUE"
}
```

- Chỉ sửa `shopName`, `phone`, `contactEmail`. `address`, `status` và ngân hàng không thuộc body này.
- Bỏ qua hoặc gửi `null` nghĩa là giữ giá trị cũ; API này không hỗ trợ xóa trường bằng `null`. Body không có trường cần sửa trả `400`.
- `shopName`: trim, 2–120 ký tự, không có ký tự điều khiển. `phone`: dạng `^0[35789][0-9]{8}$`.
- `contactEmail`: trim, chuyển chữ thường; phải là email đã xác minh của chính tài khoản qua `POST /api/account/contact-email` và `POST /api/account/contact-email/verify`. Email hợp lệ nhưng chưa được xác minh trả `409`; dữ liệu sai định dạng trả `400`.

## 2. Ngân hàng shop dùng API đã có

Hai endpoint này đã dùng được khi shop `PAST_DUE`; không cần endpoint mới hay gửi số tài khoản trong `PATCH /api/me/shop`. Dùng `sellerId` của chính shop, giữ nguyên kiểm tra quyền/tenant hiện tại. Seller truy cập shop khác nhận `404`.

```http
POST /api/sellers/20000000-0000-0000-0000-000000000001/flows/set_seller_bank
Content-Type: application/json

{
  "p_bank": "ACB",
  "p_holder": "Nguyen Van A",
  "p_account_enc": "00123456789"
}
```

Thành công trả `200 OK` theo `FlowResult` hiện có; sau đó tải lại bằng GET để hiển thị dữ liệu đã lưu:

```http
GET /api/sellers/20000000-0000-0000-0000-000000000001/bank
```

`200 OK`, `Cache-Control: no-store`:

```json
{
  "status": "READY",
  "bankName": "ACB",
  "holder": "NGUYEN VAN A",
  "accountSuffix": "6789"
}
```

| `status` của GET bank | FE hiển thị |
| --- | --- |
| `MISSING` | Chưa có tài khoản; `bankName`, `holder`, `accountSuffix` đều `null`. |
| `NEEDS_UPDATE` | Dữ liệu thiếu/không hợp lệ hoặc BE không giải mã được dữ liệu cũ; yêu cầu nhập lại. `accountSuffix` là `null`; tên ngân hàng/chủ tài khoản có thể còn giá trị. |
| `READY` | Có dữ liệu hợp lệ và giải mã được; hiển thị tên ngân hàng, chủ tài khoản và hậu tố đã che. Đây không phải xác minh sở hữu ngân hàng. |

`p_account_enc` là **số tài khoản người dùng nhập dạng plaintext trong request HTTPS**, không phải ciphertext FE tự tạo. BE trim/validate rồi bảo vệ bằng ASP.NET Data Protection trước khi lưu. `p_bank`/`p_holder` dài 1–120 ký tự sau trim, không có ký tự điều khiển; số tài khoản gồm 4–34 chữ ASCII hoặc chữ số, không có khoảng trắng bên trong. Dữ liệu sai trả `400`. GET không trả số tài khoản đầy đủ hoặc ciphertext; hậu tố có 4 ký tự, riêng tài khoản dài 4 ký tự chỉ trả 2 ký tự cuối. Xóa số tài khoản thô khỏi form sau khi lưu/hủy; chỉ giữ dữ liệu đã che trong cache FE.

## 3. Phân biệt ngân hàng shop, kênh thu và đích hoàn tiền

- **Ngân hàng shop** ở trên được dùng khi Seller gửi hồ sơ thuê thêm kiosk: BE chụp ngân hàng này vào hồ sơ làm đích để Admin hoàn phí thuê nếu cần. Lưu thông tin đúng qua `set_seller_bank`, sau đó đọc GET bank và chỉ gửi lại hồ sơ khi đã `READY`. Cập nhật shop không sửa lại ngân hàng đã chụp trong các hồ sơ/giao dịch cũ.
- **Payment channel** ở `/api/sellers/{sellerId}/payment-channel` là cấu hình/kênh nhận thanh toán hoa qua payOS, do Admin cấu hình và xác minh riêng. Lưu ngân hàng shop không tự đổi thông tin người nhận của channel, không tự xác minh hoặc kích hoạt channel. FE không được dùng `READY` của ngân hàng shop làm bằng chứng channel sẵn sàng nhận tiền. Xem [hướng dẫn payment channel](business-v2-payment-channels.md).
- **Hoàn phí thuê kiosk**: Admin chuyển khoản ngoài PayOS tới ngân hàng đã chụp trong hồ sơ thuê, theo quy trình duyệt/chứng từ hiện có. Không dùng link thu PayOS của Seller để thay cho chuyển hoàn.
- **Hoàn tiền hoa cho Customer**: ngân hàng của khách gắn với đơn/refund qua luồng riêng; không lấy ngân hàng shop làm đích nhận hoàn tiền của khách.

Không chép `payment_channel_versions.expected_bank_account` trực tiếp vào `identity.sellers.bank_account_no_enc`: dữ liệu nguồn là số tài khoản rõ, trong khi đích phải được Data Protection bảo vệ. `expected_bank_name` thực tế là tên chủ tài khoản theo cấu hình `ExpectedName`, không phải tên ngân hàng. Nếu Seller chọn cùng tài khoản PayOS làm đích nhận hoàn phí, vẫn cần nhập/xác nhận các trường ngân hàng thật và lưu qua setter hiện có.

FE cần cho phép mở màn hình hồ sơ/ngân hàng khi shop `PAST_DUE`, đồng thời giữ thông báo hết hạn và dùng trạng thái hợp đồng/API nghiệp vụ để quyết định quyền bán hàng.
