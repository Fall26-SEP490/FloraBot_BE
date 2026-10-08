# Transactional payment events with CAP

Payment callbacks must not lose events when RabbitMQ is unavailable after a database commit.
Use DotNetCore.CAP PostgreSQL/RabbitMQ 10.0.2 with publisher confirms and durable queues.
CAP creates its own `cap` schema, as permitted by the source database specification; original SQL files remain unchanged.
The webhook executes the existing flow and stores `PaymentSucceeded` in the same PostgreSQL transaction.
An advisory transaction lock keyed by gateway order serializes callback retries before reading prior payment state without reversing the SQL flow's row-lock order.
Only a new transition to SUCCEEDED emits an event; amounts, IDs, transaction reference and paid_at are read from the committed-intent database state, not copied unchecked from the callback.
The versioned topic is `florabot.payment.succeeded.v1`; JSON field names follow the source contract. No bank details, phone numbers or raw callback payload are published.
CAP retries transport failures from durable storage after restart. Delivery is at least once; consumers must remain idempotent.
The first consumer only invalidates the admin portal. Existing flows still perform business transitions synchronously, so the consumer does not repeat charges, ledger writes or dispense commands.
Database-specific exchanges/groups isolate local runtime and test databases. The current portal registry supports one application instance; polling remains the fallback for missed invalidations.

## Evidence and remaining integration

Webhook tests assert a single outbox event across concurrent/replayed callbacks and check its authoritative amount/reference.
The outbox test rolls back both an audit write and event, then commits with an unreachable broker and restarts against real RabbitMQ, requiring CAP received status Succeeded.
The outage test isolates its storage schema and exchange; it never stops the shared broker or changes application data.
Existing successful payments are not backfilled. SellerPastDue and complete job/device-fault event coverage remain required.
MQTT dispatch must reuse the existing command UUID and must not mint another unlock token on redelivery.
Production RabbitMQ credentials/TLS and deployment verification remain separate work. Default credentials are local-development examples only.

## Payment follow-up events

New successful callbacks also invoke Identity and Ordering module publication methods inside the same CAP transaction.
SubscriptionPaid contains the seller and half-open subscription period, and is emitted only for ACTIVE subscriptions.
OrderPaid contains one seller order, kiosk and active item identifiers/quantities. It includes immediate accessory completion and dispensing failure; it describes payment, not permission to unlock.
Late payment on expired/cancelled orders does not emit OrderPaid. Cancelled subscription payment does not emit SubscriptionPaid.
Subscribers invalidate the matching seller and administrator portal via the existing live identity checks. Financial side effects remain in the validated source flows.
The eventual MQTT subscriber must read live command/token state and expiry before dispatch. A retained OrderPaid message alone must never open a door.

API startup explicitly initializes CAP storage before accepting HTTP requests; CAP's own background bootstrap alone is too late for first-request publication.
A database advisory transaction lock serializes first-time initialization across local processes, preventing concurrent DDL on a fresh database.

## Device and refund events

FlowExecutor uses a CAP transaction. Device and manual-close commands snapshot the locked token and its slot bouquet before calling the original flow.
A successful customer-pickup transition to CLOSED emits SlotPickedUp with the original bouquet ID and server door_closed_at; technical access emits no pickup event.
A transition to FAILED publishes the new dispute's DispenseFailed and its pending refund's RefundApproved. Source-flow duplicate/error behavior is preserved.
PostgreSQL xmin=current transaction filters are combined with order/payment identifiers to exclude historical or concurrent refunds and disputes; no notification rows or source-schema columns are added.
The same refund publisher covers new payOS late-payment refunds and the UUID returned by admin_refund/resolve_dispute.
RefundApproved denotes the refund obligation created with an approver in the source flow. It does not authorize a transfer: automated refunds still require human approval and a different paying admin under migration 005. Subscribers only refresh portal data.
Pickup/failure subscribers resolve the order's seller within Ordering and reuse live identity authorization before notification.
SQL remains the business authority; consumers do not replay financial or loyalty side effects.

## Scheduled transitions

JobRunner resolves a scoped CAP publisher for each run and commits source-flow changes and events together.
FlowExecutor and jobs capture the audit primary-key watermark before the flow, then read new SELLER_PAST_DUE and DISPENSE_FAILED audit entries belonging to the current transaction.
This avoids emitting SellerPastDue for unchanged sellers: refresh_seller_package updates the row even when its status stays the same.
Module-owned publishers load the authoritative expiry, dispute and refund data. Device FAILED publication uses this common path to avoid publishing twice.
Regression tests in the isolated jobs database cover repeated subscription rollover and pickup expiry, asserting one past-due event and one failure/refund pair with the correct expiry/amount.
The report_device_fault path uses the same transition reader. A seller-authenticated endpoint test reports the same paid slot fault twice and verifies a single failure/refund pair with the correct slot and amount, and no pickup event. Actual MQTT domain-event delivery remains outstanding.
