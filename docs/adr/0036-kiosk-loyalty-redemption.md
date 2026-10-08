# 0036 - Customer point redemption at kiosk checkout

- Context: source `flow.kiosk_checkout` owns redemption, including balance locking, largest seller order cap and restoration on unpaid release.
- Decision: expose a customer-only `GET /api/kiosks/{kioskId}/customer/points` containing own balance and live configured percentage; derive customer from signed subject and require matching kiosk claim.
- Keep `redeem_points` internal. Send optional `p_points` through the existing checkout endpoint; reject negative input with field-specific 400. SQL domain rejections remain 409.
- The UI explains the cap applies to the largest seller order, not the combined basket. It does not estimate a per-shop cap using potentially duplicate shop display names.
- Balance and entered points remain in component state. Read explicitly, clear on visit reset, invalidate late reads on unmount and end the customer visit when the points screen is hidden. Do not persist customer data in browser storage.
- Unsafe integer balances cannot be used from the browser. Input accepts only nonnegative safe integers within the displayed balance; the transaction rechecks current balance and cap.
- Add redeemed points and discount to the existing authorized checkout response. Display committed amounts, not a subtraction from a stale client catalog.
- Confirmed 400/409 checkout rejection allows correction and explicit retry. Network/5xx ambiguity still blocks another checkout; no automatic mutation retry.
- Evidence: PostgreSQL loyalty tests, full API/gateway regression, kiosk browser and accessibility cases, real gateway/API/database browser checkout. Physical pickup, external payment provider verification and manual screen-reader certification are separate work.
