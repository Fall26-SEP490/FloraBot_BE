# Customer–Seller chat real-time contract

## Connection

- SignalR hub: `/hub/chat`
- Authentication: the existing `florabot_access` HttpOnly cookie. Connect to the API origin with credentials enabled; do not put the access token in the URL.
- Allowed identities: active `CUSTOMER` preorder and `SELLER` accounts. Kiosk sessions and other roles are denied.
- No client-selected chat groups are used. The server routes each notification only to active user accounts authorized for that chat by the existing chat membership.

## Server event

Listen for `ChatMessageReceived`:

```json
{
  "chatId": "<chat-guid>",
  "id": "<message-guid>",
  "senderName": "Tên hiển thị theo phía nhận",
  "senderRole": "CUSTOMER hoặc SELLER",
  "content": "Nội dung tin nhắn",
  "createdAt": "2026-10-15T10:00:00+00:00",
  "imageUrl": "/api/chats/<chat-guid>/messages/<message-guid>/image",
  "unreadCount": 1
}
```

The event goes to the other chat side after the message transaction commits. The sender receives the normal `POST /api/chats/{chatId}/messages` response. Offline clients load missed messages from `GET /api/chats/{chatId}/messages`; REST remains the source of truth. Notification delivery is best-effort and does not undo a committed message.

## Unread counts and read receipts

- `GET /api/member/chats` and `GET /api/sellers/{sellerId}/chats` return `unreadCount` per conversation. It counts only messages sent by the other side that have not been read; messages sent by the current side do not count.
- `GET /api/chats/{chatId}/messages` returns `readAt` for each message. For a message sent by the caller, a non-null value means the other side has read it.
- After the conversation history is displayed, call `POST /api/chats/{chatId}/read` with no body. It marks the currently unread incoming messages as read and is safe to repeat. The response includes `messagesMarkedRead`, the caller's remaining `unreadCount`, and the read-through message cursor.
- The other side receives `ChatMessagesRead` after the database update commits. The event identifies the reader and the last message read, allowing the sender to update their outgoing messages through that cursor. REST remains authoritative if the receiver was offline.
- Migration 024 treats messages that existed before read receipts were installed as already read, so existing conversations do not suddenly show every historical message as unread.

Example `ChatMessagesRead` event:

```json
{
  "chatId": "<chat-guid>",
  "readerName": "Tên hiển thị theo phía nhận",
  "readerRole": "CUSTOMER hoặc SELLER",
  "readThroughMessageId": "<message-guid>",
  "readThroughCreatedAt": "2026-10-15T10:00:00+00:00",
  "readAt": "2026-10-15T10:00:03+00:00",
  "messagesMarkedRead": 3
}
```

## Browser client outline

Use `@microsoft/signalr`, connect to `${API_BASE_URL}/hub/chat` with `withCredentials: true`, and register the `ChatMessageReceived` handler before starting the connection. On reconnect, refresh the current conversation through the messages API. Deduplicate by message `id`, because the REST response and a later history refresh may contain the same message.

```ts
const connection = new HubConnectionBuilder()
  .withUrl(`${API_BASE_URL}/hub/chat`, { withCredentials: true })
  .withAutomaticReconnect()
  .build();

connection.on("ChatMessageReceived", (message) => {
  // Route by message.chatId and deduplicate by message.id.
});

await connection.start();
```
