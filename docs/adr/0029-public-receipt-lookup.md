# ADR 0029: Public receipt lookup and kiosk handoff

- Context: the brief grants receipt access with an order UUID and eight-character tracking token, independently of portal login.
- Decision: POST /api/receipts/lookup accepts the pair in a JSON body; invalid pairs return the same 404 and responses are no-store.
- The existing receipt IP limiter applies. No customer identity, account number, ciphertext, tracking token or e-card is returned.
- Ordering reads its own order/items/disputes; Payment supplies refund totals through an internal module method in the same repeatable-read transaction.
- Complaint eligibility uses server time and the source dispute-window setting. No original SQL or financial business rule changes.
- The kiosk QR/link carries the capability in a URL fragment; the portal consumes and removes it from the current history entry.
- The public client omits cookies, bypasses portal refresh, and retains credentials/results only in component memory. Clear aborts pending reads.
- Production uses the same origin /receipt. VITE_RECEIPT_URL can override the kiosk handoff for other environments.
- Evidence: six real-database payment tests, three public receipt browser cases, generated OpenAPI, mobile/desktop axe/screenshots.
- Limitation: this change supplies lookup and handoff only; complaint evidence submission and refund-account forms remain required work, as do provider/media/manual accessibility verification.
