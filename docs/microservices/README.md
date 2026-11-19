# FloraBot service migration

## Target ownership

The target has six .NET business services and the existing Python AI service.
The gateway routes public requests. Migration proceeds in independently verified
increments while the existing API serves the remaining capabilities.

| Service | Authoritative data and behavior |
| --- | --- |
| Identity | Accounts, login/refresh, account status, role and seller/staff relationships |
| Catalog | Public shop profiles, artificial flower templates, tags, descriptions, photos, listed prices, preorder accessories and reviews |
| Ordering | Quotes, walk-in/preorder orders, checkout, receipts, customer pickup state, cancellation, quality complaints and loyalty |
| Payment | Seller/platform payment channels, PayOS links/webhooks, charges, refunds, ledger, manual transfer evidence and financial reports |
| Kiosk Operations | Kiosk applications, leases and expiry, assignment, slot ownership, physical bouquets/reservations, stocking/replacement/removal, access OTPs, MQTT, sensor evidence, incidents and technician tasks |
| Communication | Chat, delivery transport, notifications, email and delivery receipts |
| AI | Suggestions and forecasting over the authorized context supplied by business services |

Identity owns authentication OTP issuance and verification. Kiosk Operations owns
physical-access OTP authorization. Communication transports email containing an
OTP prepared by its owning service.

Catalog owns a product template. Kiosk Operations owns its physical bouquet and
slot availability. Ordering coordinates a reservation against that authority.
Payment owns money movement evidence. Ordering owns the resulting order state.
Lease dates and entitlement belong to Kiosk Operations; lease charges belong to
Payment. Financial reporting can use a Payment-owned projection of order/kiosk
events. Historical report rows retain ownership at the event date.

Each service has an independent startup/build/deployment configuration and owns
its data. One PostgreSQL server can host separate databases with separate login
permissions. Sharing the physical VPS does not provide machine failure isolation.

## First implemented increment

`FloraBot.Communication` runs email delivery on an authenticated internal HTTP
endpoint with its own PostgreSQL database. It references `FloraBot.Email`, a
library containing the existing provider adapter/templates and the transport
contract. It has no project reference to `FloraBot.Api` and performs no reads of
Identity, Ordering, Payment or kiosk tables.

API business readers and CAP subscribers continue selecting valid recipients,
checking current business state and preparing content. Setting
`EMAIL_PROVIDER=Communication` sends those messages through the new service.
The API's existing default and direct-provider modes continue working.

This increment covers email delivery. Chat, in-app notification persistence,
broker consumption by Communication and the five other .NET service extractions
remain subsequent increments. Existing ID-only seller events remain in the CAP
outbox. Their payloads do not gain email text or mailbox addresses.

See [Communication setup and verification](communication.md).

## Subsequent increments

1. Extend Communication with its own notification/chat ownership and versioned
   event contracts. Move subscriber responsibility after current-state eligibility
   and privacy rules have explicit contracts.
2. Extract Catalog. Preserve API paths/response shapes, tenant checks, photo
   access, preorder-only reviews and the walk-in/preorder distinction. Replace
   cross-domain SQL joins with published data or bounded API composition.
3. Extract Identity with compatible access/refresh semantics and service-level
   authorization checks. Role/account changes must continue invalidating access.
4. Extract Payment with durable inbox/outbox, provider idempotency, grouped-checkout
   allocation and existing manual refund review/evidence behavior.
5. Separate Ordering and Kiosk Operations through persisted workflow state and
   versioned reservation/payment/device events. Payment confirmation, command
   publication, relay acknowledgment and physical pickup remain distinct facts.

For every extraction, verify independent startup, owner-only database access,
authentication, replay/concurrent handling, bounded timeouts and service outage
behavior. Verify the affected existing flows before switching their gateway route.
Resource usage on the shared 4 GB VPS requires measurement as each process is added.
