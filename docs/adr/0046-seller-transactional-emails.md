# 0046 - Seller transactional email events

The source brief A1 requires approval, expiring-plan reminders and receipts. Build
on the existing Notify transport and CAP outbox, without a notification table or
changing original SQL. Identity owns recipient and subscription queries; Notify
owns external delivery, duplicate suppression and audit-marker access.

Approval audit detection publishes an ID-only event in the same transaction as
approve_seller. SubscriptionPaid also publishes a receipt event in the payment
transaction, only for successfully activated subscriptions. Late canceled payments
do not produce a subscription receipt. CAP carries seller ID, kind, optional
subscription ID/expiry date, never email addresses, names or email text.

The reminder job selects ACTIVE shops with expiry 1-3 days from today's server date
in Asia/Ho_Chi_Minh. A database advisory transaction lock and existing audit log
record one queued event per shop/expiry date atomically with the CAP outbox. The
marker means queued, not delivered. Disabled email skips reminder scheduling.
The reader checks current expiry/status again so renewal, suspension and expired
plans suppress stale reminders. A later distinct expiry can produce a new reminder.

Identity resolves all ACTIVE SELLER accounts with valid email for that shop.
There is no separate owner/notification-recipient flag in the source schema.
Registration creates a phone-only user; provisioning supplies its email separately.
Missing valid recipients cause a retryable CAP failure, never false success.
After retry exhaustion the operator must provision and requeue the failed event.
Closed/suspended shops suppress obsolete approval letters; receipts still go to
their active accounts for historical paid subscriptions. Guest checkout stays PII-free
and retains its existing tracking-token e-receipt flow; these new receipt emails
confirm seller subscription payments, not customer email collection or tax invoices.

Notify acquires a one-minute Valkey lease per business-event/user, sends using the
15-second transport deadline, then records provider acceptance for 90 days. Keys
are scoped by database and hashed; they contain no mailbox or body. Duplicate
consumers retry on a held lease and skip an accepted marker. This is best-effort
suppression: provider/Valkey are not one transaction; lost responses, process crash,
cache loss, or retention expiry can cause duplicate mail. No exactly-once claim.

Disabled mode logs that delivery was skipped, then consumes the CAP event. Enabling
email is not a historical replay mechanism. Configure the provider and recipients
before real transactions, monitor failed CAP events and reconcile uncertain sends.

Verification covers actual approval API/outbox, transaction rollback, real broker
delivery into the enabled consumer with a fake provider, recipient isolation/locking,
source receipt amounts/periods, signed webhook replay and late payment exclusion,
reminder date boundaries/rollback/dedup/stale checks, and Valkey send suppression.
No external email was sent; live Brevo/Google SMTP remain unverified.
