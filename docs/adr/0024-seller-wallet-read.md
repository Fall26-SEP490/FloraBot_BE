# 0024 - Read a seller wallet from one ledger snapshot

Status: implemented; provider transfers and bank-account entry remain separate work.

The existing wallet route returned anonymous ledger data without withdrawal progress or a minimum
amount. Preserve its balance/entries fields and add typed withdrawal history, configuration-derived
minimum, bank-readiness flag and explicit truncation flags for the latest 100 records.

Payment owns the ledger and withdrawal queries. An Identity read function answers only whether the
seller exists and bank details are configured. The endpoint requires Merchant and SameSeller, retains
EF tenant filters, returns 404 for absent/foreign sellers and marks successful responses no-store.
Repeatable-read gives balance, entries and withdrawal history one consistent committed snapshot.
Bank ciphertext, account holder and bank name are not part of this response.

The portal submits the existing request_withdrawal flow after inline amount review. It does not
reserve money or change the source flow: pending requests leave available funds unchanged, and the
paying flow rechecks balance. A per-tab uncertainty marker blocks automatic resubmission after a
lost response, including reload; the user must review history before deliberately starting again.
This is a UX safeguard, not server-side exactly-once withdrawal creation.

The bank-entry/protected-storage UI is implemented in ADR 0025. Administrator payout UI still
needs implementation and verification. Missing bank details cannot enable a withdrawal form.
