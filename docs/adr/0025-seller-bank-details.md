# 0025 - Validate bank input and return only masked account details

Status: implemented; bank ownership verification and actual transfers are not provided.

The generated set_seller_bank endpoint already passes p_account_enc through ASP.NET Data Protection
before the source flow stores it. Preserve that route/field and the bank-account-v1 protection
purpose. Despite its SQL-derived name, the HTTP field contains the entered account, not ciphertext
supplied by the browser. Use HTTPS outside local development and preserve/back up the existing
Data Protection key volume; loss of its keys makes protected account values unreadable.

Validate and trim bank/holder text (1-120 characters, no control characters) and account identifiers
(4-34 ASCII letters/digits). This is bounded input validation, not a bank lookup or proof of ownership.
The source flow continues to uppercase the holder, audit changes and copy current protected bank
details into each withdrawal request. Editing the seller does not rewrite prior payout destinations.

Identity owns a no-store GET bank endpoint with Merchant/SameSeller and EF tenant filters. Return
bank/holder plus a suffix only: four trailing characters, or two for four-character accounts. Missing
data and undecryptable/legacy values have distinct states. Never treat failed decryption as plaintext
or return the ciphertext. The wallet read remains a separate non-sensitive readiness summary and
requires a decryptable, valid account before enabling the portal withdrawal form. This avoids
presenting old plaintext demo fixtures or missing-key values as usable bank details.

The portal uses an inline edit/review form. Raw account input stays in form/request memory, outside
TanStack mutation variables, query data and browser storage; success/cancel clears it. Only masked
server data is cached. Tests cover encryption round-trip, tenant denial, audit omission, retained
withdrawal snapshots, invalid inputs, responsive/accessible forms and a real browser-to-SQL save.
