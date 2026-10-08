# Durable payOS order identity

The source subscription_paid flow overwrites raw_payload.orderCode with a transaction reference.
Looking up future webhooks by that JSON value therefore breaks retries after a successful commit.
Keep the supplied SQL intact and add payment.gateway_orders in migration 004 instead.
Each charge has one unique numeric order code, bounded by JavaScript's safe integer limit.
The internal flow.reserve_gateway_order function allocates or reuses the mapping for pending payOS charges.
Payment-link creation must commit this mapping before contacting payOS and reuse it on retry.
Webhook lookup uses the durable mapping; SQL business flows own locking and ledger idempotency.
Do not lock the payment first in HTTP code: subscription_paid locks subscription before payment.
Cache markers are optional; a Valkey failure after commit must not fail acknowledgement.
The integration test sends concurrent signed callbacks and replay after JSON replacement, verifying one balanced journal.
The authorized seller subscription payment-link endpoint now calls payOS with database-derived amounts and fixed HTTPS callback configuration.
The client signs the documented five fields, verifies response signatures and amount/order identity, and only returns pay.payos.vn HTTPS URLs.
On a duplicate/rejected create response it queries the same gateway order to recover a pending link; timeout retries reuse the committed code.
Gateway response loss, tenant isolation, invalid signatures, incorrect amount/status/URL and transaction-array canonicalization have automated tests.
Webhook DTO accepts the documented outer code/desc/success envelope while trusting only the signed data.
No live payOS transaction is proven. Kiosk link creation and portal payment screens remain separate implementation work.
