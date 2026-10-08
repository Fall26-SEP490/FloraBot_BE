# 0037 - Optional gift message at kiosk checkout

- Context: original `flow.kiosk_checkout` accepts `p_ecard`, rejects more than 150 Unicode code points and stores it on each seller order.
- Decision: add an optional kiosk textarea and an explicit action to reuse an advisor suggestion. Copying focuses the editable field; choosing a bouquet does not replace the message.
- Count Unicode code points with `Array.from`, matching PostgreSQL `length(text)` rather than the browser's UTF-16 `maxlength`. The database remains authoritative for direct API callers.
- Preserve entered content and line breaks; omit whitespace-only input. Treat message text as text, never HTML.
- The draft clears after successful checkout, visit reset, entry to customer-account view and tab hiding. It is not stored in browser storage or placed into URLs.
- Existing history and public receipt projections continue excluding e-card content. No public distribution endpoint is added.
- UI explicitly states that the message is saved with the order; printed cards and separate recipient delivery are not implemented or claimed.
- Evidence: browser limits/edit/copy/focus/reset/axe checks, original-flow tests with 150 and 151 code points, and real browser checkout persistence. Original SQL, authorization rules and API contracts are unchanged.
