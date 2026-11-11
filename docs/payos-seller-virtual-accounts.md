# Seller flower payments with VietQR Pro

This change applies to direct seller flower checkout links, their payment callbacks and seller reconciliation. Kiosk rental, subscription/application charging, platform credentials and their link adapter are unchanged.

payOS documents that a VietQR Pro payment link can use an order-specific virtual account instead of the linked bank account: https://payos.vn/docs/cau-hoi-thuong-gap/ (question 6). The original linked bank account remains the seller channel's expected recipient. Do not replace it with an account copied from one QR code.

## Opt-in and version binding

Apply `db/migrations/20261112_seller_payos_va_receiver_evidence.sql` before running this API build. It adds nullable order-recipient evidence and default-strict channel policy fields; it does not delete historical data or change leases.

Virtual-account support is off by default. For a confirmed VietQR Pro seller channel, configure this server-side setting alongside the existing seller credentials and original expected bank details:

```text
PAYOS_CHANNELS__seller_tam__AllowVirtualAccounts=true
```

An admin then binds a **new channel version** using the existing `PUT /api/sellers/{sellerId}/payment-channel` and verifies that version using `POST .../verify`. These actions are not performed by the local test suite. Verify still requires both the expected root bank account returned by payOS confirmation and the signed probe tied to that verification attempt.

Binding stores an unambiguous versioned SHA-256 fingerprint of the actual client/API/checksum credentials and root recipient configuration, not raw secrets. Verification must use those exact credentials. Its verified fingerprint and the opt-in policy are pinned to the version. Mutating keys underneath a reference cannot silently authorize a different recipient; rebind and verify a new version instead. Retain old referenced configurations for outstanding payments.

Existing versions remain strict even if the new flag is subsequently added to configuration. Their old missing proof is never backfilled by marking them ACTIVE. Previously pinned payments do not move automatically to the newest version. Do not delete or edit an expired payment to recover a failed link.

## Checkout and callback behavior

1. The backend derives seller, stored amount, order code and deadline from the checkout. It checks the channel's seller/root snapshot and credential proof. No recipient is supplied by the customer or browser.
2. A signed creation response must match the order code, amount, PENDING status and expected bank BIN. A valid different recipient is accepted only for an explicitly opted-in, root-verified version with the matching credential fingerprint. Missing/malformed recipients are rejected with safe distinct diagnostic codes.
3. The signed per-order recipient is stored atomically with the checkout URL, QR and verified channel ID. A retry cannot overwrite already-saved recipient evidence with a different receiver. Public responses contain only the masked actual payment account; internal receiver evidence is not serialized.
4. A seller webhook must use the pinned channel key and channel ID. An opted-in payment without saved order evidence is rejected. `accountNumber` must match the root or the saved per-order recipient. A supplied nonempty `virtualAccountNumber` must match that order's saved recipient. Payer fields such as `counterAccountNumber` are never used as recipient evidence.
5. Reconciliation uses the same pinned key/proof and recipient validation. A signed lookup may omit `accountNumber`, as the documented GET response does. If recipient fields are present they must agree. Existing order/amount/status/reference checks and transactional checkout settlement remain in place.

Legacy fixed-account seller versions keep their fixed-account policy. Platform/public `PayOsClient.CreateAsync` does not gain permission to accept arbitrary different accounts.

## Local validation and incident limits

Tests use fake HTTP provider responses and a disposable local PostgreSQL database, with email and MQTT simulated/disabled. They cover the fixed-account and opted-in virtual-account HTTP lifecycle, repeat links, signed callbacks, cross-key/wrong-recipient rejection, credential drift, masked output, and signed lookup with omitted or wrong account details. Payment fixtures use the existing valid paid-kiosk fixture helper; rental production rules are unchanged.

This is a compatibility fix, not proof of the cause of live order 93. The original POST response was not retained; CSV shows the linked bank account and GET omitted `accountNumber`. A live unpaid checkout after an authorized deployment is still needed to confirm the actual provider response. No live payment, webhook registration, deployment or old-order repair is performed by local tests.
