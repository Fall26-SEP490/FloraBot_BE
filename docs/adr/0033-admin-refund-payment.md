# ADR 0033: Refund queue and audited destination access

- Context: confirm_refund records manual transfers; automatic refunds require human approval and a different active administrator must record payment.
- Decision: Payment owns the paginated refund queue/detail and payment lock. Identity supplies approver-role checks; Ordering owns order/destination reads; Notify owns the explicit-access audit.
- Ordinary responses omit bank names, holders, accounts, ciphertext and tracking capabilities. Detail returns destination readiness and action eligibility, never raw bank data.
- Explicit POST bank-details requires an active admin, a pending refund, a human approver and a different payer. It locks payment then order in the same order as confirm_refund and commits REFUND_BANK_VIEWED before disclosure.
- Destination decryption uses the existing bank-account-v1 purpose and input validation. Missing, malformed and legacy plaintext values require resubmission through the receipt; no plaintext fallback exists.
- Refund endpoints use no-store even for authorization and validation failures. No account value is written to audit or browser persistence/query caches.
- The portal requires review before approval and proof URL plus transferred/checked confirmation before recording payment. It does not initiate a bank transfer. Unknown outcomes require explicit reload and reconciliation before retry.
- Account data hides after one minute, page hiding or closing detail; late responses after hiding/unmount are discarded. A paid refund cannot reveal a destination even if another refund on that order is still pending.
- The customer may update the order's destination while refunds remain pending; disclosure is a current snapshot, not a reservation or immutable payment instruction. Operators must coordinate external transfers and recheck destination; SQL deduplication only prevents duplicate recording.
- Tests cover roles, two-admin separation, automatic approval, explicit audit, invalid ciphertext, payment and final erasure, pagination, browser privacy/recovery/accessibility and a synthetic real browser/gateway/API/database payout. Verified proof bytes and external bank reconciliation remain incomplete.
