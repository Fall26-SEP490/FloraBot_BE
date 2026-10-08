# 0027 - Administrator incident queue

- Context: seller/device reports existed, but administrators had no focused incident review and resolution UI.
- Decision: add Admin-only, no-store `/api/admin/incidents` and `/api/admin/incidents/{id}` reads in Ordering.
- Lists support OPEN and RESOLVED_FIXED, optional kiosk filtering and 25-row pages with explicit hasMore; open incidents are oldest first and resolved incidents newest first.
- Pagination is a live offset view, not a stable historical snapshot; concurrent resolution can move rows. Refresh returns the UI to the first page.
- Ordering owns dispute reads; KioskOps supplies kiosk/slot summaries and Notify supplies evidence via internal module methods. No new cross-schema query is added.
- Evidence detail returns the first 20 attachment URLs with explicit truncation. It does not fetch remote URLs or claim file/hash verification; browser links accept HTTPS without embedded credentials.
- Resolution uses the existing resolve_device_fault endpoint/source flow and authenticated admin actor; no new state transitions, door command or bank transfer are introduced.
- An inline confirmation follows the technician's note. Pending refunds on DISPENSE_FAILED remain a source-flow rejection, displayed without losing the note.
- Unknown outcomes refresh list/detail and tell the administrator to check state; the source flow rejects repeated resolution. No automatic mutation retry is introduced.
- Tests cover role denial, unknown ID, pagination, evidence, resolution/audit and duplicate rejection, plus browser keyboard/axe/error handling and real shop-report-to-admin-resolution HTTP/PostgreSQL.
- Scope remaining: financial payout UI, verified media, physical repair evidence and remaining customer/technical-return workflows.
