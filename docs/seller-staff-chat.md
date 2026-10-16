# Seller ↔ Seller_staff chat

This is an internal, one-to-one shop conversation. It is separate from the preorder customer chat. A conversation is unique per shop and staff account. Only active `SELLER` accounts for that shop and the associated active `SELLER_STAFF` account can use it. There is no customer, admin, operations manager, technician, or staff-to-staff access.

The seller opens a conversation from the shop's staff list. A staff member opens or continues the conversation with their own shop. The API derives tenant access from the authenticated identity and checks the staff account's `seller_id`; clients cannot select another shop. If a staff account is disabled, it cannot access the API or SignalR. The seller can still read the retained history, but cannot send to an inactive counterpart.

## Endpoints

| Method | Route | Purpose |
| --- | --- | --- |
| `POST` | `/api/sellers/{sellerId}/staff/{staffId}/chat/open` | Seller creates or opens the one-to-one conversation with an active staff account in the same shop. |
| `POST` | `/api/staff/chats/open` | Seller_staff creates or opens its shop conversation. No request body. |
| `GET` | `/api/seller-staff/chats?page=1` | Seller lists shop conversations; seller_staff receives only its own conversation. |
| `GET` | `/api/seller-staff/chats/{chatId}/messages?page=1` | Read message history. Page 1 contains the newest messages. |
| `POST` | `/api/seller-staff/chats/{chatId}/messages` | Send text and/or an image reference. |
| `POST` | `/api/seller-staff/chats/{chatId}/read` | Mark incoming messages read after displaying history. |
| `POST` | `/api/seller-staff/chats/{chatId}/images` | Upload a private PNG/JPEG/WebP image (raw body, maximum 5 MiB). |
| `GET` | `/api/seller-staff/chats/{chatId}/messages/{messageId}/image` | Read a private chat image after participant authorization. |

Message text is limited to 2,000 characters. Unlike the customer-preorder chat, this internal chat does not reject phone numbers, email addresses, or links. Image upload references are purpose- and chat-bound, expire after 30 minutes, and can be used once.

## Unread and read receipts

Chat list items include `unreadCount`. Message history includes `readAt`; a non-null value on the sender's message means the other side has read it. The read endpoint is idempotent and returns `messagesMarkedRead`, the new `unreadCount`, and the last read message cursor.

The authenticated `/hub/chat` hub delivers `ChatMessageReceived` and `ChatMessagesRead` to participant user IDs. Clients cannot choose or join arbitrary chat groups. REST history remains authoritative when a participant is offline.

## Storage

Migration `025_seller_staff_chat.sql` creates separate shop/staff chat, message, and image tables. Message content and read timestamps are stored in PostgreSQL; private image bytes remain in authenticated Cloudinary storage, with validated metadata and hashes in PostgreSQL. Apply the migration before calling these endpoints.
