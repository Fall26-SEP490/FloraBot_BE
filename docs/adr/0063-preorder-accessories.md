# 0063 - Accessories on web preorders

Date: 2026-11-15. Status: accepted.

## Context

Flower preorders are quoted by the Seller before the customer pays. Accessories already have kiosk-scoped inventory and server-owned prices, but the web preorder request and quote did not include them. Reserving stock when a customer first submits a request would block inventory before the Seller has made an offer.

## Decision

- A customer may select kiosk accessories together with a custom flower preorder. The selection is advisory and does not reserve stock.
- Seller quote input contains the flower price and the accessory IDs/quantities offered. The backend reads accessory names and prices from the active kiosk inventory and returns a total quote plus a monotonically increasing quote version.
- When the customer accepts the exact quote version, one database transaction revalidates the quote and kiosk accessory stock, decreases inventory, creates accessory order lines, and creates the charge for the full amount.
- The bouquet and accepted accessories are fulfilled together in the same PREORDER slot. Staff and Seller loading views include the accepted accessory snapshots so the items can be packed into that compartment.
- Unpaid cancellation/expiry restores reserved accessory stock through the existing checkout release flow. A paid cancellation before flower loading creates a full refund and restores the accessories. After loading, existing delivery/refund rules treat the co-packed accessories as delivered when the bouquet is picked up.
- Receipt reads show a quoted flower line until an actual bouquet line is created, and list accessory snapshots as separate lines.

## Consequences

- `ordering.web_requests` stores requested and quoted accessory snapshots and a quote version. `ordering.order_items` remains the accepted-order inventory ledger.
- The member catalog is exposed separately from the kiosk-authenticated catalog at `GET /api/member/kiosks/{kioskId}/catalog/accessories`.
- Frontend clients must pass `quoteVersion` on accept and display `quotedPrice` as the payable total. They must not provide price or seller identity for accessories.
- Migration `20261115_preorder_accessories.sql` must be applied before deploying the API version that calls the new function signatures.

## Limits

Database inventory is the authority for available quantity. The system does not sensor-verify accessory placement inside the compartment; Seller/staff must pack the listed items with the flower.
