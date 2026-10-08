# 0028 - Administrator withdrawal workspace

- Context: seller withdrawal requests and two-admin payout flows existed, without a complete admin workspace.
- Decision: add Admin-only no-store list/detail reads, 25-row pages and explicit state filters; Identity supplies shop names through an internal reader.
- Ordinary reads expose only account suffixes and bank metadata. The destination is the immutable withdrawal snapshot, never the seller's current account.
- Full account access is an explicit POST, allowed only for an APPROVED request and an active admin different from the approver; invalid ciphertext has no plaintext fallback.
- Row lock, admin validation and Notify-owned WITHDRAWAL_BANK_VIEWED audit commit before disclosure. Audit includes actor/entity, never account number or ciphertext.
- The browser keeps full account data only in component state, outside query/mutation caches and storage; hide after one minute, page hiding or closing the detail.
- Approval, rejection and payout recording reuse existing flows. Inline review and a transfer confirmation checkbox distinguish recording a completed transfer from initiating one.
- Unknown outcomes refresh authoritative state and warn against sending money again. SQL enforces one active request per seller, distinct approver/payer and nonduplicate ledger entries.
- External bank transfers are manual: this UI cannot reserve or atomically execute them. Staff must reconcile the request/reference and proof before transferring; proof URL bytes/storage verification remains unfinished.
- Validation: backend covers access/audit/snapshot/ciphertext/pagination; browser covers review, roles, uncertainty and account clearing; isolated real HTTP/PostgreSQL exercises two admins and synthetic payout recording.
- No migration or provider transfer is added. Refund/reconciliation UI and verified proof storage remain separate unfinished requirements.
