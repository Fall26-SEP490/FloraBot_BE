# Cancel a pending kiosk renewal

Apply `db/migrations/20261120_cancel_kiosk_renewal.sql` before deploying this endpoint.

`POST /api/sellers/{sellerId}/subscriptions/{subscriptionId}/cancel` has no body.
An active seller can cancel its own pending kiosk renewal. Returns 204, including
repeated cancellation; 404 for inaccessible renewals; 409 if already paid or no
longer awaiting payment. Current rental periods and seller expiry are untouched.

The transaction locks the subscription before its payment, matching payment
confirmation. It cancels the pending local charge and audits the cancellation.
It does not revoke a QR already issued by PayOS. A payment arriving afterward
uses the existing `flow.subscription_paid` late-payment refund path; the cancelled
renewal stays cancelled. Provider reconciliation and refund processing remain
necessary; 204 does not mean a bank transfer has been reversed.

Frontend reloads authoritative subscriptions after success or failure, and asks
for confirmation before submitting cancellation.
