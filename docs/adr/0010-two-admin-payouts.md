# Separate approval and payout actors

The supplied pay_withdrawal accepts PENDING and overwrites approved_by with the payer.
The runtime brief explicitly requires two different admins for approval and payout.
Migration 005 adds paid_by for withdrawals/refunds and approved_at for withdrawals.
approve_withdrawal changes PENDING to APPROVED; an existing approval cannot be reassigned.
approve_refund assigns a human approver to an automatic SYSTEM-approved refund.
Manually created refunds already identify their human approver; replacing that person is rejected.
Both payout flows require active ADMIN identities and reject matching approver/payer before any attachment or ledger write.
Existing balance recheck, proof requirement, row/advisory locks, ledger logic and refund bank-data erasure remain intact.
The generator now reads supplemental migrations and exposes two Admin-only approval commands with server-injected actors.
Tests cover bypass attempts, same-person rejection, approval reassignment, concurrent payouts, replay, attribution and balanced ledger.
The original SQL archive and its 326-check verification path remain unchanged; migrations apply to runtime/test databases separately.
Historical payouts retain unknown paid_by rather than inventing attribution. This migration does not verify actual proof-file bytes or perform bank transfers.
