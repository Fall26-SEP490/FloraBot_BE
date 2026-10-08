# 0038 - Explicit pickup recovery at the original kiosk

- Context: original `flow.request_pickup` accepts order ID, receipt token and injected kiosk ID; paid orders may need new unlock tokens after an offline period or token expiry.
- Decision: a dedicated kiosk screen accepts the ID and 8-character receipt token, validates their format, and requires a separate review/send action. Device authentication and existing Shopping policy are unchanged; a customer session is not required.
- A null flow result means a rejected receipt code, despite HTTP 200, because the source preserves its rejection audit. Only a UUID result is presented as accepted.
- Acceptance means an unlock instruction exists, not MQTT delivery, an open door or completed pickup. UI instructs the customer to observe the cabinet and retain the receipt for faults.
- No automatic retries. Known HTTP rejections return to editable input; ambiguous network/server outcomes clear input and remove the send action from that attempt.
- Order ID and receipt token stay in component state, outside query/mutation caches and browser storage. Back/reset/unmount drops them; tab hiding ends the visit and invalidates late responses. Original inactivity/session expiry still applies.
- Source flow owns eligibility and token creation; the live-token unique index rejects a new token while a door is OPENED. No source SQL or public API contract is changed.
- Backend tests cover guest/customer offline recovery, wrong kiosk/code, committed rejection audit, replacement after terminal expiry, open-door rejection and completed-order rejection. HTTP device events and signed inbox simulation reach COMPLETED; no physical hardware is claimed.
- Browser tests cover review/focus, null and error responses, no automatic resend, offline/hidden-tab races and responsive axe. Isolated integration verifies PAID -> DISPENSING and an ISSUED token without charging a provider or opening hardware.
