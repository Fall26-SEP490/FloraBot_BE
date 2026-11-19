# Communication email service

## Runtime

The service is a separate .NET 10 web application, built with
`dotnet publish src/FloraBot.Communication/FloraBot.Communication.csproj -c Release`.
Its default standalone listener is `http://127.0.0.1:5090`, overridable with
`ASPNETCORE_URLS` or an IIS binding. Reserve/check this port before VPS deployment.
Use an independent application pool or process and an independently versioned
release directory. Existing public FE endpoints continue entering the API.

Configure the service outside source control.

| Setting | Purpose |
| --- | --- |
| `COMMUNICATION_DATABASE_URL` | Connection to a dedicated Communication database |
| `COMMUNICATION_SERVICE_TOKEN` | Internal Bearer credential, at least 32 characters |
| `EMAIL_PROVIDER` | `Brevo`, `GoogleSmtp` or `Disabled` |
| `EMAIL_SENDER`, `EMAIL_SENDER_NAME` | Existing sender settings |
| `BREVO_API_KEY` | Brevo credential when that provider is selected |
| `GOOGLE_SMTP_APP_PASSWORD` | SMTP credential when that provider is selected |
| `GOOGLE_SMTP_ALLOWED_IN_PRODUCTION` | Existing explicit SMTP production opt-in |

Provision the dedicated database and its owning login through the normal operator
workflow. The service startup creates its own `communication.email_deliveries`
receipt table under a transaction/advisory lock. Its login needs schema creation
permission in that database. It does not require access to FloraBot's business
database or broker. Test on local databases before granting deployment access.

Configure the API to use the service after verifying its readiness.

| API setting | Value |
| --- | --- |
| `EMAIL_PROVIDER` | `Communication` |
| `COMMUNICATION_SERVICE_URL` | Service origin, for example `http://127.0.0.1:5090` |
| `COMMUNICATION_SERVICE_TOKEN` | Same internal credential as the service |

Provider credentials are required in Communication when remote mode is enabled.
Store these in the service's secured deployment configuration. Keep the internal
listener on loopback for the single-VPS layout. Use TLS on a cross-machine link.
The gateway has no public route to `/internal/v1/email-deliveries`.

## Contract and recovery

`GET /health` returns process liveness. `GET /ready` queries the Communication
receipt store and returns HTTP 503 when the provider is Disabled. These probes
perform read-only checks; provider access is verified separately through delivery.

`POST /internal/v1/email-deliveries` requires the internal Bearer token and accepts
`EmailDeliveryRequest`, containing an opaque `deliveryId` and a message. It validates
the same recipient, subject and text/HTML limits as the existing provider adapter.
The request body limit is 1 MiB to accommodate JSON escaping of bounded content.
The response `{"state":"ACCEPTED"}` confirms provider acceptance, which may precede
delivery to the recipient's inbox.

The receipt table stores a hashed delivery ID, a content fingerprint, state and
timestamps. Recipient addresses, email bodies, OTPs and provider credentials are
not persisted there. Existing .NET namespaces for shared email types are retained
for caller compatibility; their implementation assembly is `FloraBot.Email`.

| Situation | Behavior |
| --- | --- |
| New ID | Atomically records `SENDING`, calls the provider once, records `ACCEPTED` |
| Accepted ID with matching content | Returns acceptance without sending again |
| Existing ID with different content | HTTP 409 |
| Concurrent request while sending | HTTP 409, with the first attempt continuing |
| Disabled service | HTTP 503 before claiming a delivery ID |
| Provider outcome unconfirmed | Records `UNCERTAIN`, returns HTTP 503 |
| Process interrupted while sending | Stored `SENDING` remains blocked for reconciliation |
| Caller disconnects after claim | Attempt continues under a bounded deadline so acceptance can be recorded |

Automatic sender retries are disabled because provider acceptance and the receipt
database are separate transactions. A lost acknowledgment after `ACCEPTED` can be
recovered by replaying the same delivery ID. `SENDING` and `UNCERTAIN` require
operator reconciliation with provider records before any resend. A receipt
reconciliation UI/API and retention job are future work. Do not delete receipts
while upstream retries remain possible.

API seller-email deliveries pass the existing database-scoped hashed business
delivery key to Communication. API CAP retries can therefore recover an accepted
delivery even if the API's Valkey marker was not written. Direct OTP send attempts
receive a new opaque ID per invocation; the owning OTP flow keeps its existing
resend/rate-limit rules. This increment does not promise exactly-once email delivery.

## Local checks

Use a dedicated disposable database such as `florabot_communication_tests` and set
`TEST_COMMUNICATION_DATABASE_URL` to its connection string. Run
`dotnet test tests/FloraBot.Communication.Tests`. The tests use an in-process fake
email provider and a real PostgreSQL receipt database, without external sends.
The CI workflow provisions a dedicated test database before the test suite.

Run the existing transactional email, seller email, registration/auth and preorder
pickup OTP tests with the normal isolated API test dependencies as regressions.
For resource assessment, publish Release builds and measure the new service and
the remaining API together under representative local traffic. VPS capacity and
live provider delivery remain separate deployment checks.

## Verification checkpoint — 2026-11-19

- Twelve Communication integration cases passed against a disposable PostgreSQL
  database with fake provider delivery. Cases cover authentication, readiness,
  duplicate/concurrent delivery, mismatched content, restart persistence,
  interrupted attempts, caller disconnect and sanitized transport failures.
- The Release publish ran as its own process on local loopback port 5090.
  Liveness returned 200, disabled-provider readiness returned 503, and anonymous
  delivery returned 401. Its observed idle working set was 63.3 MB at that point.
  This observation uses Disabled mode on the local machine; capacity under load
  on the shared VPS requires a separate measurement.
- The wider API email/auth/OTP selection passed 100 of 104 cases. A clean main
  baseline at `4d62629` failed the same four cases. They concern legacy kiosk
  customer read access, two near-term preorder capacity fixtures and a missing
  bouquet price snapshot in preorder fulfillment. Evidence is retained under
  `D:/log/FloraBot-Communication-20261119` on the implementation machine.
- A separate GitHub Actions job builds/tests/publishes the service with only its
  PostgreSQL dependency. Its hosted CI execution awaits publication of this branch.

## Bước 2 — Hàng đợi và đối soát email (local, chưa triển khai VPS)

Luồng bất đồng bộ chỉ áp dụng email Seller: duyệt shop, biên nhận thuê và nhắc hết hạn.
API giữ quyền đọc dữ liệu nghiệp vụ và người nhận. Communication giữ vận chuyển,
DB riêng, nội dung chờ gửi được bảo vệ bằng ASP.NET Data Protection, và trạng thái gửi.
OTP tiếp tục gọi đồng bộ để giữ thời hạn và phản hồi hiện tại.

1. API kiểm tra nghiệp vụ và stage nội dung qua HTTP nội bộ có token.
2. API ghi metadata kỹ thuật và sự kiện ID có chữ ký HMAC trong cùng transaction CAP.
3. Communication nhận RabbitMQ, xác thực chữ ký, gọi API kiểm tra lại người nhận,
   nội dung và điều kiện nghiệp vụ ngay trước gửi. Không đủ điều kiện thì SKIPPED.
4. Nhà cung cấp chấp nhận thì ACCEPTED; không xác định kết quả thì UNCERTAIN.
   CAP outbox ghi kết quả và publish cùng transaction. API chỉ nhận revision mới hơn.
5. Với nhắc hạn thuê, API chỉ audit SENT khi toàn bộ người nhận đã ACCEPTED.
   UNCERTAIN không được tính là đã gửi thành công.
6. Một job nhẹ mỗi phút tìm tối đa 100 bản QUEUED cũ trên 2 phút để phát lại ID.
   Nó xử lý trường hợp queue chưa được bind lúc khởi động hoặc hết lượt retry CAP.
   Receipt ngăn gửi lại email đã ACCEPTED; kết quả có thể phát lại an toàn.

### Cấu hình bổ sung khi bật

- API: EMAIL_PROVIDER=Communication, COMMUNICATION_SERVICE_URL,
  COMMUNICATION_SERVICE_TOKEN và COMMUNICATION_BROKER_ENABLED=true.
- Service: EMAIL_PROVIDER=Brevo/GoogleSmtp, thông tin provider riêng,
  COMMUNICATION_DATABASE_URL, COMMUNICATION_SERVICE_TOKEN,
  COMMUNICATION_BROKER_ENABLED=true, COMMUNICATION_API_URL trỏ loopback API,
  DATA_PROTECTION_KEYS_PATH bền vững, chỉ service được đọc.
- Hai bên dùng cùng RABBITMQ_EXCHANGE và thông tin broker. Service có consumer group riêng.
- Token chỉ lưu trên máy triển khai. COMMUNICATION_PREVIOUS_SERVICE_TOKEN hỗ trợ khoảng
  chuyển tiếp khi xoay token; loại bỏ sau khi hai bên và các sự kiện tồn đã chuyển xong.
- Chạy migration 20261119_communication_delivery_results.sql trong DB API trước khi bật.
  DB Communication tự tạo các bảng kỹ thuật; không đọc trực tiếp bảng nghiệp vụ API.
- Khởi động Communication và xác nhận broker consumer hoạt động trước khi bật phía API.
  /ready hiện kiểm tra DB/provider, chưa chứng minh RabbitMQ hoặc provider bên ngoài khỏe.
- Sao lưu cả DB Communication và thư mục khóa Data Protection. Mất khóa sẽ không đọc
  được email đang chờ. Bảo vệ khóa bằng ACL; mặc định không mã hóa khóa trên đĩa.
- Các endpoint /internal chỉ được truy cập qua mạng nội bộ/loopback.

### API Admin, không thêm giao diện FE

GET /api/admin/communication/email-deliveries?state=UNCERTAIN&page=1&pageSize=25
trả ID kỹ thuật, trạng thái, revision và thời điểm cập nhật; không trả nội dung/email.
POST /api/admin/communication/email-deliveries/reconcile nhận operationId, deliveryId,
expectedState, expectedRevision, decision, providerReference và reason.
ActorId luôn được API lấy lại từ phiên Admin, không tin giá trị client gửi.
Decision ACCEPTED chỉ dùng khi đã đối chiếu provider xác nhận chấp nhận gửi.
RETRY_CONFIRMED_NOT_SENT chỉ dùng khi provider xác nhận chưa gửi. Không dùng để thử may rủi.
SENDING chỉ được đối soát sau 60 giây. OperationId chống lặp và optimistic revision
chặn thao tác trên trạng thái cũ. Không có cam kết exactly-once với provider bên ngoài.

### Giới hạn phạm vi

Đây là một bước tách Communication, chưa phải hoàn tất bảy service.
Chat và notification hiện có tiếp tục nằm trong API. Chưa merge main/deploy VPS.
Kiểm thử dùng email provider giả, PostgreSQL/RabbitMQ local, không gửi email thật.

## Kiểm chứng phục hồi bổ sung — 2026-11-19

Kiểm thử dùng TCP proxy local để cắt kết nối AMQP thật, ghi nhận CAP outbox vẫn giữ
request, dừng Communication rồi khởi động lại với cùng DB và khóa Data Protection.
Request được tự retry và gửi đúng một lần qua provider giả; replay cả request cũ
và mới không làm gửi trùng. Không restart broker dùng chung hoặc truy cập VPS.
Test rút ngắn FailedRetryInterval và FallbackWindowLookbackSeconds xuống 1 giây;
production giữ mặc định CAP (lookback 240 giây, chu kỳ retry 60 giây). Vì vậy sau
restart, outbox có thể chờ vài phút trước khi được CAP thử lại. Khoảng chờ này
không phải mất dữ liệu hay lỗi múi giờ.
Danh sách quyền API được cập nhật độc lập theo các endpoint hiện tại, chuẩn hóa
trailing slash trong test và dùng payload hợp lệ cho kiểm tra kiosk khác.
Không thay đổi authorization production để làm test qua.
