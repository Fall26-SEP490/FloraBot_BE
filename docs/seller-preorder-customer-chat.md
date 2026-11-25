# Seller mở chat với khách từ preorder

Seller có thể bắt đầu hoặc tiếp tục hội thoại Customer–Seller trực tiếp từ một yêu cầu preorder. BE lấy Customer, Seller và mẫu hoa từ `requestId`; client không gửi các ID Customer/Seller/Product trong body.

## API

```http
POST /api/sellers/{sellerId}/preorders/{requestId}/chat/open
Authorization: Bearer <seller session>
```

Không cần request body. `sellerId` phải khớp Seller của phiên đăng nhập. `requestId` phải là preorder `CUSTOM` thuộc đúng Seller. Sai quyền hoặc không tìm thấy yêu cầu trả `404` để không tiết lộ đơn của Seller khác.

Response `200` dùng hợp đồng `CustomerSellerChatItem` hiện có:

```json
{
  "id": "<chatId>",
  "otherPartyName": "Tên khách",
  "createdAt": "<timestamp>",
  "lastMessageAt": null,
  "lastMessage": null,
  "lastMessageHasImage": false,
  "productContext": "Tên mẫu hoa",
  "unreadCount": 0
}
```

Một cặp Customer–Seller tiếp tục dùng chung một chat. API cập nhật ngữ cảnh mẫu hoa của preorder rồi trả `chatId`. Gọi tiếp:

- `GET /api/chats/{chatId}/messages?page=1` để đọc lịch sử.
- `POST /api/chats/{chatId}/read` sau khi hiển thị tin nhận được.
- `POST /api/chats/{chatId}/messages` để gửi tin nhắn.
- Có thể kết nối `/hub/chat` để nhận sự kiện realtime; REST history vẫn là nguồn dữ liệu chuẩn.

FE Web/Mobile thêm nút **Nhắn khách** ở chi tiết preorder. Khi bấm, gọi API mở chat; nếu thành công, điều hướng sang màn hình chat hiện có bằng `id` và dùng `productContext` để hiển thị ngữ cảnh bó hoa. Lỗi mạng/429 cần giữ chi tiết đơn và cho phép thử lại; `404` hiển thị rằng không mở được cuộc trò chuyện cho đơn này.

Không cần migration: API tái sử dụng `ordering.customer_seller_chats` và `ordering.customer_seller_chat_products`.

Xuất OpenAPI sau khi build API:

```powershell
$output = Join-Path (Get-Location) 'artifacts/seller-preorder-chat-openapi.json'
dotnet run --project src/FloraBot.Api -- --export-openapi $output
```

FE cập nhật schema và sinh lại contracts theo workflow của FE; không sửa generated types bằng tay.
