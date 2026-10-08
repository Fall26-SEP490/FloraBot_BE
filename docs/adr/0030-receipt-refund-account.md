# ADR 0030: Refund account submission from public receipts

- Context: flow.submit_refund_info accepts bank details only for an order with a pending refund and the matching receipt capability.
- Decision: keep the generated endpoint and original SQL; add an Ordering-owned capability check in the same transaction before invoking receipt commands.
- Invalid capability pairs return a uniform 404. Receipt paths use no-store, including validation, business-rule and rate-limit responses.
- Normalize tracking tokens and trim bank inputs; limit bank/holder to 120 characters and account to 4-34 ASCII letters/digits. Preserve leading zeros.
- Holder validation follows the source SQL character rule. Reject null/blank/control-character inputs before Data Protection encryption.
- The existing bank-account-v1 protector encrypts the account. No plaintext account is returned, stored in browser storage or placed in query/mutation caches.
- UI collects data only while the receipt reports a pending refund. It includes a review step, masked account suffix, edit/cancel and explicit submission.
- A failed transport/5xx has an unknown outcome: clear raw input, block immediate resubmission and ask for a fresh receipt lookup. Never claim a transfer happened.
- Source SQL remains responsible for pending-refund checks, locking, audit and clearing account data after all pending refunds are paid.
- Evidence: real-database payment tests validate encryption/leading zeros/invalid inputs/no-pending guard; browser tests cover review, privacy, accessibility and response loss; an actual gateway/API/browser test verifies storage and non-PII audit.
- Remaining: complaint evidence submission, verified media, administrator refund approval/payment/reconciliation UI, and manual accessibility/provider verification.
