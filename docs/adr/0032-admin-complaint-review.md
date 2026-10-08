# ADR 0032: Administrator complaint review

- Context: public receipt holders can submit complaints; administrators need the order and evidence before calling resolve_dispute.
- Decision: add Admin-only list/detail reads in Ordering, retaining the unchanged source resolution flow and authenticated actor injection.
- List filters cover OPEN, RESOLVED_REJECT and RESOLVED_REFUND with 25-row pages and a hasMore marker; optional kiosk filtering supports operational review.
- Ordering projects only complaint/order/item fields. Notify supplies evidence references through its existing internal reader. Detail uses a repeatable-read snapshot.
- Responses are no-store and omit customer identifiers, tracking capabilities, bank fields and e-card contents.
- The portal requires an explicit amount and decision, followed by review/confirmation. Zero means rejection; a positive value requests a refund. SQL remains authoritative for the aggregate refund cap and concurrent resolution.
- Resolution does not transfer money. Existing separate approver/payer rules still apply to refund payment; the UI makes that distinction explicit.
- Unknown transport/5xx outcomes block resubmission until explicit detail reload; background refresh does not disable the confirmation during pointer activation.
- Evidence URLs are not verified image uploads. Unsafe links are not opened; the existing 20-image truncation is disclosed. Verified media and refund payout/reconciliation UI remain separate unfinished work.
- Verification includes real database authorization, both resolution branches, over-refund/repeat rejection, audit attribution, pagination, browser accessibility/review/recovery and a real gateway/API/browser complaint-to-pending-refund scenario.
