# Preorder pickup lead time

Apply `20261127_customer_preorder_pickup_lead_time.sql` before releasing the updated customer forms.
New customer requests require pickup at least one hour after submission and no later than thirty days.
Seller quotations retain the same window measured again when the seller submits the quote.
The customer reviews the quoted pickup time before accepting and paying.

The migration replaces only the new-request time check and its message. It retains the existing
retry lookup before time validation, so retrying an already-created request with its original ID
and payload still returns that request. Tenant checks, accessory snapshots, stock and capacity
checks remain unchanged. Existing requests are not rewritten.

Validation covers rejected requests under one hour and over thirty days, accepted requests with
one hour lead time, and identical request retries. Integration tests require an isolated test DB
with this migration applied; do not apply it to a project or hosted database as part of a code review.
