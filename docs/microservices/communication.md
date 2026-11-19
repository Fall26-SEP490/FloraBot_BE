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
