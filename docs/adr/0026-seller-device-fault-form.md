# 0026 - Seller device-fault form

- Context: seller inventory could display faults but had no UI for the existing report_device_fault command.
- Decision: place an inline form under the selected slot, using generated request types and existing merchant/tenant authorization.
- The backend supplies p_reporter from the authenticated principal; clients submit only kiosk/slot, description and photo URL.
- Require an inline review explaining sales interruption, checkout cancellation and possible refund obligations before sending.
- Preserve source SQL and existing domain-event publication; reject open-door operations according to the source flow.
- Block automatic repetition after an unknown outcome; persist only a per-tab seller/slot uncertainty flag until explicit reconciliation.
- This guard does not provide server idempotency or deduplication across tabs. FAULT/LOCKED slots direct users to operations.
- Evidence is currently an HTTPS URL, not an upload or verified media object; verified storage/bytes/hash remain a separate unfinished requirement.
- Validation: controlled browser cases cover errors, confirmation, reload, responsive layout and axe; actual browser integration checks PostgreSQL effects and foreign-slot denial.
- Consequence: reporting works without inventing new business transitions; technical return-door actions remain unfinished. Admin incident resolution UI is covered by ADR 0027.
