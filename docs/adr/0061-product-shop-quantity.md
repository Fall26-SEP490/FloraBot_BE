# 0061 - Physical shop stock per flower product

Date: 2026-11-09. Status: accepted.

`quantity` is an absolute nonnegative integer count of physical bouquets still
at the shop. Existing products start at zero until their seller reconciles the
real count. Kiosk contents and sales history are not shop stock. Seller enters
the count on the existing create/update endpoints; an omitted update preserves
the count. Returns/removals never add stock automatically.

Deduct exactly once per physical bouquet at the existing `STOCK_IN` inventory
event boundary, including seller/staff loading, sensor-confirmed loading and
CUSTOM preorder fulfillment. Keep an append-only bouquet-keyed deduction ledger
in the same transaction. A guarded product update prevents negative quantity;
failure rolls back the whole loading operation and maps to HTTP 409.

Exception to ADR 0047's default owner `FOR UPDATE` lookup: seller stocking uses
`FOR KEY SHARE` for product ownership. The stock trigger rechecks active product,
seller ownership and positive quantity at the actual update. Staff stocking and
preorder fulfillment use the same lock mode instead of upgrading `FOR SHARE`.
The artificial-bouquet expiry trigger also uses this compatible read lock while
preserving its non-expiring rule. Inventory kind is immutable through catalog
APIs, and product metadata edits retain their existing `FOR UPDATE` lock.
Other catalog owner checks retain their existing locks. This avoids deadlock
against slot-first loading flows without weakening the final stock mutation.

Sensor loading conflicts retain the session and evidence for a later retry.
Background synchronization continues with other sessions after a single stock
conflict. Product metadata, count changes and audit remain transactional.

No change to public flow signatures, sale/payment timing, kiosk zone selection,
or existing role/tenant/lease/slot checks. Deploy the additive migration before
the new API binary; the API does not apply migrations on startup.
