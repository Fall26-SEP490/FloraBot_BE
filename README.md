# FloraBot BE

.NET 10 Minimal API, PostgreSQL 16, Valkey and RabbitMQ. Git identity is local to this folder: TaHoang715 / taminhhoang.nk@gmail.com. No push has been performed.

## Local setup

Requires .NET 10 SDK, Docker, Node.js (only for contract generation), and PowerShell. The integrated MQTT setup also requires OpenSSL (available with Git for Windows).

```powershell
docker compose up -d postgres valkey rabbitmq
./scripts/initialize-db.ps1
./scripts/run-local.ps1
```

API: http://127.0.0.1:5080. The application refuses startup without a signing key outside Development. Development generates an ephemeral key, so restarting invalidates existing access tokens. The local compose credentials are development-only examples.

Initialization refuses to overwrite an existing application database. Source SQL is under `db/FloraBot_DB_v3`; runtime migrations are applied afterward. Do not run the archive's `run.sh` against a database containing needed data: that script drops its target.

## Local MQTT broker

```powershell
./scripts/initialize-mqtt.ps1
docker compose up -d mosquitto
./scripts/test-mqtt.ps1
```

Mosquitto 2.1.2 accepts TLS only on `localhost:58883` (container `mosquitto:8883`).
The startup script also provisions it automatically. An existing credentials directory is preserved;
incomplete provisioning fails rather than replacing keys. Local CA and per-device passwords/HMAC keys
are generated under ignored `infra/secrets/mqtt`, never printed or passed as command arguments.
Use `broker/ca.crt` as the trusted CA; never disable hostname or certificate verification.
The local certificates expire after 365 days and are not production certificates.

Seed devices `ESP32-A1B2C3` and `ESP32-D4E5F6` each read only their own `/cmd` and write only their own `/evt`.
The backend account has the inverse permissions. New hardware needs an explicit account and ACL entry.
Broker runtime secrets are copied into a private tmpfs with Linux ownership/permissions, including on Windows hosts.
Transport tests cover both directions, cross-device denial, prohibited device command publication,
anonymous access and rejection of an untrusted CA with otherwise valid credentials.
These transport probes never contain unlock instructions. A signed command/event protocol and persistent
Python door simulator are available in `firmware/README.md`, with real-broker replay/restart tests.
The .NET MQTT event receiver stores verified events in the migration-007 inbox before acknowledging them,
then processes source flows and CAP events transactionally. It is enabled by integrated compose; standalone
API runs use the MQTT settings in `.env.example`. TestServer hosts disable the worker.
Run `./scripts/test-mqtt-backend.ps1` against the integrated stack to verify signed heartbeat receipt,
duplicate suppression and rejection of invalid signatures/foreign commands. This harmless probe updates
the seed kiosk heartbeat and leaves its uniquely identified transport evidence in the local database.
Outbound dispatch now polls existing live unlock tokens, signs the stored command identity, persists retry
deadlines and rechecks authorization before publishing (migration 008; ADR 0019). It never mints a token from
a historical payment event. Run `./scripts/test-mqtt-door-cycle.ps1` to verify a complete technical-door round
trip through the real API/broker/software simulator. That test starts/stops its own simulator and preserves
an empty unassigned test slot, command and event evidence. Physical ESP32 firmware and the complete live
payOS sandbox purchase-to-door/settlement demonstration remain outstanding.

## YARP gateway

`src/FloraBot.Gateway` proxies `/api/*`, `/hub/*` and `/openapi/*` to `API_UPSTREAM_URL`
(default `http://127.0.0.1:5080`). Set the **same** `JWT_SIGNING_KEY` (at least 32 bytes)
in both API and gateway process environments before starting either, then run:

```powershell
dotnet run --project src/FloraBot.Gateway --no-launch-profile --urls http://127.0.0.1:5082
```

Set FE's `API_PROXY_TARGET=http://127.0.0.1:5082` to use it during development.
Invalid access tokens return 401, but login/refresh/logout remain reachable for session recovery.
API policies, database user validation and kiosk-key validation remain authoritative.
`GATEWAY_REQUESTS_PER_MINUTE` defaults to 600 per client IP (allowed range 1-10000).
Only explicitly configured `TRUSTED_PROXY_IPS` may supply forwarded client IPs; configure
the immediate Caddy proxy address when deployed. Do not trust arbitrary Internet proxy ranges.
Original host, cookies and WebSocket upgrades are preserved. `/health` is gateway liveness,
not database/upstream readiness; a disconnected upstream returns 502. Caddy/TLS/container
deployment remains unfinished. `dotnet test` includes the gateway's independent tests.

## Provision local portal accounts

After initial database setup (or when upgrading an existing local database), apply the
supplemental runtime migrations without recreating the database:

```powershell
./scripts/apply-runtime-migrations.ps1
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:DATABASE_URL = 'Host=localhost;Port=55436;Database=florabot;Username=florabot;Password=local-florabot-only'
./scripts/provision-user.ps1 -UserId '10000000-0000-0000-0000-000000000001' -Email 'admin@florabot.vn'
./scripts/provision-user.ps1 -UserId '10000000-0000-0000-0000-000000000003' -Email 'an@hoasaigon.vn'
```

Each command asks for a new password twice with hidden input. Use at least 12 characters
and no more than 72 UTF-8 bytes (bcrypt's limit). Passwords are passed through stdin, not
process arguments. No default password is installed. Log in to the portal using the email
and password you just chose. Account roles, shop membership and subscription status stay unchanged.

The CLI only provisions an existing ACTIVE ADMIN/SELLER with `!unprovisioned` credentials.
Development additionally accepts the exact unusable seed placeholders. It refuses existing
real passwords, locked users, customer accounts, a mismatched existing email or a duplicate
normalized email. For new registrations whose owner has no email yet, the supplied email
is assigned during provisioning. `PORTAL_USER_PROVISIONED` is audited without credentials.
This is an operator task requiring database access, not an HTTP endpoint or password-reset facility.

For automation, run `dotnet run --project src/FloraBot.Api --no-launch-profile -- provision-user <user-uuid> <email>`
with the password on redirected stdin and `DATABASE_URL` in the environment. Never put a
password literal in shell history, scripts, transcripts or CI output. No API signing key or
Valkey connection is required for this command. Production accepts only `!unprovisioned` accounts.

## Verification

```powershell
./scripts/initialize-db.ps1 -Verify
./scripts/initialize-db.ps1 -ApiTests
./scripts/initialize-db.ps1 -JobsTests
dotnet test
dotnet format --verify-no-changes
```

`-Verify` recreates only `florabot_verification` and runs all seven original scripts. `-ApiTests` recreates only `florabot_api_tests`. Tests use a real PostgreSQL and Valkey instance, not an in-memory authorization substitute. Set `TEST_DATABASE_URL` / `TEST_VALKEY_URL` for CI.

## Contracts

Authorization regression coverage includes two independent inventories:
`FlowContractTests` checks the 39 generated commands, while
`CustomEndpointAccessTests` checks the 48 custom HTTP method/routes under `/api`,
`/openapi` and `/health`. The latter compares the exact route/method inventory and
role/public policies, then sends real HTTP requests for anonymous rejection,
foreign/missing seller concealment and foreign-device scope. `PortalHubTests`
covers SignalR separately. These checks do not replace each endpoint's resource,
business-error, valid-user success or provider integration tests.

`node scripts/generate-flows.mjs` regenerates the explicitly exposed flow endpoints. Parameters naming actors are injected from the authenticated principal; p_now is absent from HTTP DTOs. Unknown body fields are rejected. `/openapi/v1.json` requires ADMIN.

### Subscription payment links

Apply migration `004_gateway_orders.sql` before using payment endpoints. Configure `PAYOS_CLIENT_ID`, `PAYOS_API_KEY`, `PAYOS_CHECKSUM_KEY`, `PAYOS_RETURN_URL` and `PAYOS_CANCEL_URL` (absolute HTTPS callback URLs). After the existing `subscribe` command returns a subscription ID, POST `/api/sellers/{sellerId}/subscriptions/{subscriptionId}/payment-link` as the owning seller or admin. No amount or redirect URL is accepted from the browser. The response contains `orderCode`, `amount` and `checkoutUrl`; only a verified webhook activates the subscription, never a return-page visit. A 502 can be retried on the same subscription; the persisted gateway order code remains unchanged. Credentials absent yields 503. Local tests use a fake HTTP transport; a live payOS account has not been verified.

To export OpenAPI without creating login credentials:

```powershell
$env:EXPORT_OPENAPI_PATH = Join-Path (Get-Location) 'artifacts/openapi.json'
dotnet test --filter OpenApiIsProducedFromActualEndpoints
```

Copy that artifact to FE/packages/contracts/openapi.json, then run `pnpm contracts` in FE.

### Kiosk checkout payment

After `kiosk_checkout` returns its checkout UUID, the same kiosk key or owning customer session can GET `/api/kiosks/{kioskId}/checkouts/{checkoutId}` for payment state, order state, receipt codes and payment deadline. Responses containing receipt codes use `Cache-Control: no-store`. POST the same path plus `/payment-link` to obtain the payOS URL. The amount and expiry come from the stored checkout; an elapsed hold deadline returns 409 even before the expiration job runs. A customer session cannot read another customer's or a guest's checkout. A device key is restricted to its kiosk. Signed webhook processing and simulated `device_event` transitions are integration-tested; physical MQTT delivery is still pending.

## Payout approval

Migration `005_two_admin_payouts.sql` enforces two separate active admins. Call `approve_withdrawal` before `pay_withdrawal`; automatic refunds require `approve_refund` before `confirm_refund`. The payer must differ from the recorded approver. Approval cannot be reassigned to bypass that rule. Existing human-approved manual refunds retain their creator as approver. Both approval endpoints use the Admin policy and inject the actor from authentication. Payout records preserve `approved_by` and store `paid_by` separately. This records manual transfer confirmation; it does not initiate a bank transfer, and proof-file verification remains unfinished.

## Background jobs and CI

The API runs nine scheduled SQL flows every 30 seconds by default. Set `JOBS_ENABLED=false`
to disable the worker; `JOBS_INTERVAL_SECONDS` accepts 5 through 300. Testing disables the
hosted worker and calls the runner explicitly against `florabot_jobs_tests`.
`-JobsTests` recreates only that disposable database. Use `TEST_JOBS_DATABASE_URL` to override it.
Jobs use Valkey leases plus transaction advisory locks; failures roll back and retry on a later tick.
Daily reconciliation checkpoints yesterday's report in append-only audit. Reconcile older missed
dates explicitly after prolonged downtime. This is internal ledger reconciliation, not a gateway bank statement import.

`.github/workflows/verify.yml` runs original SQL, format and all API/jobs tests in GitHub Actions.
It has not run on GitHub until the repository is pushed and Actions starts the workflow.

Enable local hooks with `git config core.hooksPath .githooks` (and `chmod +x .githooks/*` on Unix).
The pre-push hook expects the disposable integration databases initialized and Docker services running.
It verifies formatting, API/jobs tests and original SQL. Commit messages use conventional scopes.

## Integrated local application

Place FE and BE next to each other. With Docker running, execute from BE:

```powershell
./scripts/start-local-stack.ps1
```

This builds the three frontend apps, API and YARP gateway, then serves them through Caddy:

- `http://localhost:8088/`: landing
- `http://localhost:8088/login`: seller/admin portal
- `http://localhost:8088/kiosk/`: kiosk PWA
- `http://localhost:8088/api/packages`: public API through Caddy and YARP

The script creates a shared random JWT key once in ignored `infra/secrets/local-stack.env`.
It reuses the existing PostgreSQL volume and applies `db/migrations` without recreating the database.
Data Protection keys persist in a named volume; retain that volume to keep encrypted bank details readable.
The original seed accounts still require explicit provisioning with `scripts/provision-user.ps1`.
The seed kiosk `40000000-0000-0000-0000-000000000001` uses the demo-only key `demo-key-q1`.
Use `-NoBuild` only when images are already current. Run `pnpm test:stack` from FE to verify
same-origin routes, actual API access, mobile accessibility and kiosk offline scope without HTTP mocks.

This is a local HTTP/Development configuration, bound to loopback. It does not verify production
HTTPS or external payment/SMS integrations. Keep `infra/secrets` private and do not remove the
database or Data Protection volumes when stopping the stack.

## Gift advisor API

POST `/api/kiosks/{kioskId}/flows/ai_suggest` retains the generated SQL request
fields and returns `{"result":"survey-uuid"}`. It creates `flow.ai_suggest` fallback
results before optionally calling the internal FastAPI service. Responses from
that service must pass .NET validation and `flow.ai_record_llm`; errors or timeout
leave the fallback intact. No client actor, clock, price or stock ID is accepted.

GET `/api/kiosks/{kioskId}/surveys/{surveyId}` returns `GiftSurveyResponse` with
current eligible bouquets, current stored prices and safe reasons/card messages.
Device credentials can read only guest surveys for that device; a customer reads
only their own survey for the session kiosk. Responses are `no-store`. Surveys do
not reserve stock; checkout confirms availability separately. Command generation
preserves this orchestration and the ten-per-minute kiosk rate limit.

Set `AI_SERVICE_URL`, `AI_SERVICE_TOKEN` and the service's Gemini configuration to
enable generation. With missing service configuration the SQL fallback still
works. See `ai/README.md` and ADR 0014 for the service boundary and current limits.

Validated LLM answers are cached for 60 seconds in Valkey. A change to kiosk,
preferences, eligible inventory, prices, descriptions, brand tone, GEMINI_MODEL
or AI_CACHE_VERSION changes the hashed key. Session/customer identifiers are not
part of the cache key or value. Cached answers are validated again in .NET and SQL;
accepted reuse is recorded as CACHE in the same transaction. Cache errors/timeouts
are misses and do not prevent the existing fallback path. This is advisory data,
never a stock reservation or a cached checkout price.

## Delivery status

This is an in-progress implementation. Refer to the workspace REQUIREMENTS.md for the complete delivery checklist. The seeded SQL passwords are intentionally not usable bcrypt passwords. Live payOS, Gemini, SMS, Cloudinary and SMTP credentials have not been supplied. No real payment or deployment has been performed.

### Payment outbox

payOS callbacks now persist PaymentSucceeded using CAP 10.0.2 in the same PostgreSQL
transaction as the original payment flow. RabbitMQ outages do not discard the event;
CAP retries stored work after recovery. Concurrent callback retries emit one event.
The first subscriber refreshes the admin portal without replaying financial mutations.
All seven domain contracts and signed MQTT delivery are implemented; see ADRs 0016-0019.

Local API settings are RABBITMQ_HOST=127.0.0.1, RABBITMQ_PORT=55672,
RABBITMQ_USER=florabot and the local-only password in .env.example. Compose uses
rabbitmq:5672 internally. PostgreSQL credentials must permit CAP to initialize its
own cap schema. Start RabbitMQ before running the full backend test suite.

### Paid purchase through the MQTT simulator

With the local PostgreSQL, Valkey and RabbitMQ services running, execute:

```powershell
./scripts/initialize-mqtt.ps1
./scripts/test-paid-mqtt-cycle.ps1
```

This opt-in integration test runs seller approval, subscription payment, slot assignment,
stocking, checkout, signed payment callbacks, actual MQTT/TLS delivery, signed sensor events
and settlement through the application host and original SQL flows. It verifies one CLOSED
command per slot, three applied sensor events and one pickup event per slot, and a balanced
settlement of 350000 VND (one bouquet) or 700000 VND (two bouquets in the same order).
Repeated callbacks and settlement do not duplicate the result. Settlement is first rejected by
the time window; only the isolated order fixture is then aged to exercise the real job.

The payment provider is a signed HTTP test double, not the payOS sandbox. GPIO is simulated.
The test uses database `florabot_mqtt_tests`, a separate Compose broker on loopback port 58884
and separate persistent simulator state; it does not publish into the main broker. It refuses
to take over an already-running test stack and stops its services afterward. Database evidence
and volumes are preserved. A failed fixture may need inspection before its occupied test slot
can be reused. The regular test suite skips this opt-in case; CI runs the wrapper separately.

### Seller wallet reads

`GET /api/sellers/{sellerId}/wallet` requires Merchant and SameSeller. It returns typed balances,
the latest 100 ledger movements/withdrawals with truncation flags, bank readiness and the SQL
withdrawal minimum from one repeatable-read snapshot. Bank ciphertext/name/holder are omitted;
responses use `Cache-Control: no-store`. The existing withdrawal command remains authoritative
and does not reserve ledger funds at request time. See ADR 0024 and WalletReadTests.

`GET /api/sellers/{sellerId}/bank` returns masked account information to the authorized merchant,
with no-store and MISSING/READY/NEEDS_UPDATE states. The existing set_seller_bank command validates
bank/holder/account input and encrypts the account using the persisted Data Protection key ring.
The SQL-derived HTTP field p_account_enc is entered account text; clients must not encrypt it.
Use HTTPS outside local development. Protect and retain the key volume across deployments.
Bank ownership is not verified; no actual transfer is triggered. See ADR 0025 and SellerBankTests.

### Administrator incident queue

Admin-only `/api/admin/incidents` supports status OPEN/RESOLVED_FIXED, optional kioskId and page
(25 rows, hasMore). `/api/admin/incidents/{id}` returns the selected incident and evidence metadata.
Both are no-store. Module-owned internal readers supply locations and evidence without querying
another module's tables from Ordering. Evidence is bounded to 20 URLs with a truncation flag;
verified media bytes/hash remain unfinished. Resolution uses the existing resolve_device_fault
flow and audit actor. Pending refunds for DISPENSE_FAILED prevent closing; repeat resolution is
rejected. See ADR 0027, AdminIncidentsTests and the kiosk payment regression.

### Administrator withdrawals

`/api/admin/withdrawals` lists PENDING/APPROVED/PAID/REJECTED requests, with page (25 rows),
hasMore and optional sellerId. The detail read returns the immutable masked destination,
current ledger-derived balance and permission to access the account. All reads are Admin-only/no-store.
`POST /api/admin/withdrawals/{id}/bank-details` reveals the destination only for an approved,
unpaid request to an active admin different from the approver. It locks the request and commits
a WITHDRAWAL_BANK_VIEWED audit before disclosure, without putting bank values in that audit.
Undecryptable legacy values are rejected; the seller's later bank edits never change old requests.
Approval/rejection/payout recording use the existing flows. No bank transfer is initiated by this API.
See ADR 0028 and AdminWithdrawalsTests; manual-transfer reconciliation and verified proof storage
remain necessary before real operation.

### Administrator reconciliation

`POST /api/admin/flows/reconcile_gateway` accepts `p_date` (`YYYY-MM-DD`) and
`p_statement` (an array of `{ "txn": "provider-transaction-id", "amount": 100000 }`).
Only Admin can call it. Amounts must be positive integer VND, IDs 1–200 characters,
and a request may contain at most 5,000 rows. Duplicate IDs are summed; empty arrays
are permitted. No payment or bank transfer is created from the statement.

Migration 009 fixes the source function's inconsistent audit count and duplicate-input
aggregation without modifying the supplied SQL archive. Apply runtime migrations with
`pwsh -File scripts/apply-runtime-migrations.ps1 -Database florabot` after verification.

`POST /api/admin/flows/reconcile_daily` accepts `p_date`. Manual calls record the actor
and returned report in the audit log. Reports run in Vietnam time and both endpoints
are no-store. Daily output combines date-specific comparisons with current all-time
balances and a pending-refund count; not every column is a currency or historical balance.
See ADR 0034. The administrator UI now supports these reports and manually pasted JSON
statements. Actual provider statement verification remains pending.

The gateway-reconciliation route has an 8 MiB HTTP body limit at both gateway and API,
alongside the 5,000-row validation limit. Other routes retain the default 64 KiB limit.

### Customer history and identity erasure

`GET /api/kiosks/{kioskId}/customer/history?page=1` returns 25 own orders per page,
with `hasMore` and current loyalty points. It requires an active customer bearer session
with the same kiosk claim. Phone, email, bank details, e-card text and tracking tokens are excluded.

`POST /api/kiosks/{kioskId}/flows/forget_customer` accepts `{}` and derives the customer
from the session. The source flow anonymizes identity data, disables the account and zeroes
points; existing tokens then stop working. Historical transaction records remain.
The kiosk provides history and an explicit review/checkbox confirmation before erasure.
Real OTP/browser verification runs against the disposable stack with Development-only OTP
delivery. External SMS delivery is not verified. See ADR 0035.

### Loyalty checkout validation

`POST /api/kiosks/{kioskId}/flows/kiosk_checkout` accepts optional `p_points`.
Negative values return HTTP 400 with a field error. Positive redemption still uses
the original SQL flow: authenticated customer, available balance and the configured
percentage cap on the largest seller order. Domain rejections remain HTTP 409.
The source SQL deducts points inside the checkout transaction and restores them
when the unpaid checkout is released. No standalone redeem endpoint is exposed.
`LoyaltyCheckoutTests` exercises a two-seller basket, atomic rejection, the reduced
payment amount and idempotent restoration.

`GET /api/kiosks/{kioskId}/customer/points` returns the signed customer's balance and
current configured redemption percentage, with Customer policy, matching kiosk claim
and no-store responses. The kiosk offers an explicit points read, amount entry and
manual correction after confirmed checkout rejection. Checkout order responses include
`pointsRedeemed` and `discountAmount` so receipts show committed discounts. See ADR 0036.

The kiosk also supplies optional `p_ecard` through the existing checkout endpoint.
The unchanged source flow enforces 150 Unicode code points and stores the same message
on each seller order. History/public receipt projections still exclude it. The UI does
not claim printing or separate recipient delivery. See ADR 0037.

The kiosk provides explicit recovery through existing `flow.request_pickup` when a paid
order is still held at its original kiosk. Wrong receipt codes return a null flow result
with HTTP 200 so the source rejection audit commits; consumers must inspect the result.
UUID acceptance is not physical dispense confirmation. Recovery tests cover offline
payment, terminal token expiry, wrong kiosk/code, already-open doors and completed orders.
See ADR 0038. No physical device or external payment is exercised by these test fixtures.

### Accessory catalog and checkout

`GET /api/kiosks/{kioskId}/catalog/accessories` uses Shopping policy and matching kiosk
claim, returning only active, stocked accessories from active sellers at that kiosk.
KioskOps and Identity retain module-owned reads. Responses are no-store.
Existing `kiosk_checkout` accepts `p_accessories: [{ id, qty }]`, determines price and
seller itself, reserves inventory atomically and restores it once on unpaid release.
Accessory-only paid orders complete without door commands, as specified by source SQL.
See ADR 0039 and AccessoryCheckoutTests for concurrency, policy and accounting checks.

### Seller accessory inventory

`GET /api/sellers/{sellerId}/accessories` returns owned stock and kiosk metadata,
including INACTIVE/zero-stock rows, with Merchant/SameSeller policies and no-store.
The existing `restock_accessory` flow increments stock and records the authenticated actor.
`SellerAccessoryTests` verifies tenant isolation, invalid quantities, forged actor rejection,
unchanged sale status and the stock/audit result in PostgreSQL. See ADR 0040.

### Verified product-image upload

Private evidence for flower returns, kiosk faults and receipt complaints is now
integrated separately from public product photos. Configure Cloudinary and follow
the workflow, API and recovery instructions in [the handoff](docs/handoff.md).
New evidence uses authenticated delivery and an expiring, resource-bound upload
reference. Admin downloads go through the API. Original SQL and historical demo
records remain unchanged; live provider acceptance requires configured credentials.

`POST /api/sellers/{sellerId}/products/{productId}/photos` accepts a raw PNG/JPEG/WebP
body (matching Content-Type), at most 5 MiB. Merchant/SameSeller and the existing
catalog-editor subscription guard apply before provider traffic and before DB commit.
Configure CLOUDINARY_CLOUD_NAME, CLOUDINARY_API_KEY and CLOUDINARY_API_SECRET only on
the API. Missing configuration returns 503. The server signs uploads and downloads
the stored original to verify its byte count and SHA-256 before recording metadata.
This endpoint is for public product photos, not private payment evidence. Do not
automatically retry an uncertain upload; it can leave an unreferenced provider object.
The generated `add_product_photo` URL command also verifies stored bytes before
recording metadata (ADR 0049). It accepts only versioned original PNG/JPEG/WebP URLs
under `https://res.cloudinary.com/{configured-cloud}/image/upload/v{version}/florabot/{name}.{extension}`.
External URLs, transformations and query strings return 400; provider failure or
missing cloud configuration returns 503. Use raw upload for new files. Historic
records and other attachment URL flows still have demo metadata. Orphan cleanup
and live provider verification remain pending; see ADR 0041.

The matching GET route accepts `page` (default 1) and returns `items`/`hasMore`,
20 images per page. It checks ownership, permits expired shops to read, excludes
other attachment services/phases and sets no-store. The portal has explicit gallery
loading, preview and raw upload controls; see ADR 0042. Existing URL-based image
records are shown without claiming their legacy metadata was byte-verified.

### Module read boundaries

`FlowContractTests` verifies all generated command signatures against the installed
PostgreSQL `pg_proc` catalog: name, argument order/type/default status and scalar
versus set return shape. It independently checks the command role matrix against
the actual endpoint policies, including anonymous denial and foreign-seller scope,
and checks every generated OpenAPI request for exposed server clock/actor fields.
These checks run in the normal test/CI pipeline. They complement the HTTP and SQL
business tests; they do not prove every business error branch or capability flow.

Payment link handlers obtain subscription ownership from Identity and checkout scope,
order details and deadline from Ordering. Shared RepeatableRead transactions keep
those reads and payment amounts consistent. Public routes/DTOs remain unchanged.
`ModuleBoundaryTests` checks compiled Modules code against EF table/view ownership,
including generic entity references and schema-qualified SQL strings. It runs in the
normal `dotnet test` CI step without extra packages. Dynamic SQL/reflection and
unclassified Infrastructure adapters still require review; see ADR 0043.

Identity, Catalog and KioskOps now own their basic EF read endpoints. The shared
ReadEndpoints class only registers these routes. Original `screen.*` report/catalog
views are read by the explicit ReadModels adapter, with boundary checks against
business-table access and literal mutations. Existing DTO namespaces/routes are
preserved; see ADR 0044.

ResourceAccess delegates ownership lookups to Identity, Catalog and KioskOps.
Each lookup retains the caller's transaction and FOR UPDATE lock, so authorization
cannot race a resource reassignment before the business flow. Missing and foreign
resources still return the same 404; foreign device commands still return 403.
The compiled guard covers this orchestration class, and a real PostgreSQL test
checks lock contention and release. See ADR 0047.

Auth is constrained to Identity entities; device-credential lookup goes through
KioskOps and returns only the device ID. Jobs and Realtime may orchestrate module
calls but cannot query business tables. Notify owns daily reconciliation audit
checkpoints, using the job's transaction and Vietnam server date. These namespaces
are now covered by the compiled guard. Key rotation/disabled-device revocation and
checkpoint rollback have focused integration coverage; see ADR 0048.

### Transactional seller email

`EMAIL_PROVIDER=Disabled` is the default. `Brevo` requires `EMAIL_SENDER` and
`BREVO_API_KEY`; `GoogleSmtp` requires `EMAIL_SENDER` and
`GOOGLE_SMTP_APP_PASSWORD` and is allowed only in Development. SMTP uses Google's
port 587 with mandatory STARTTLS. Sender must be a single mailbox; Brevo requires
that sender to be verified with the provider. Secrets stay on the server.

The Notify sender uses bounded plain-text messages, a 15-second deadline and no
automatic retries. Provider acceptance is not proof of inbox delivery, and a lost
response must not be treated as proof that no email was sent. Approval and paid
subscription receipt events use CAP outbox in the business transaction. The
`remind_subscriptions` job queues one reminder per shop/expiry date when 1-3 days
remain, using server time in Vietnam. It skips scheduling while email is disabled.
The consumer suppresses stale reminders after renewal, suspension or expiration.

Identity resolves ACTIVE SELLER accounts with valid email addresses. Registration
creates a phone-only account; use the existing provisioning command to supply its
email. Missing recipients cause a failed/retryable CAP event; after provisioning,
an operator must requeue it if CAP retries have already been exhausted. No guest
email is collected. Subscription receipts are payment confirmations, not tax invoices.

Valkey suppresses repeated sends per business event/user for 90 days and leases
in-flight sends. This is not exactly-once delivery: a crash between provider
acceptance and recording the marker, lost provider response, cache loss, or replay
after retention may duplicate mail. CAP may retry failed deliveries. `Disabled`
explicitly consumes events without sending; enabling later does not replay them
automatically. Configure before real transactions and review CAP failure queues.

Tests use real PostgreSQL/Valkey/RabbitMQ with fake provider HTTP; no external mail
is sent. Live Brevo and Google's SMTP/TLS remain unverified. See ADR 0045/0046.
