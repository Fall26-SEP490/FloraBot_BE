# Product quantity held at the shop

`catalog.flower_products.quantity` is the integer count of physical bouquets
still held at the shop for that product. It is separate from the number of
bouquets already occupying kiosk slots. Sellers set the actual count when
creating or editing a product; an omitted update value preserves the count.

The additive migration `db/migrations/20261109_product_shop_quantity.sql` adds
the nonnegative column with default `0`. Existing products start at `0` until
the seller enters the actual shop count. The migration does not infer stock
from historical orders, kiosk contents, demo data or inventory history.

Every successful physical loading operation deducts one:

- Seller walk-in stock and seller staff delivery.
- Sensor-confirmed `LOAD` and the loading phase of `REPLACE`.
- Seller or seller staff fulfillment of a paid `CUSTOM` preorder into a kiosk.

Order acceptance, payment, reservation and sales of bouquets already in a kiosk
do not deduct again. Removal, return, expiration, damage, disposal, cancellation
and refund do not automatically increase shop quantity. After receiving a
bouquet back at the shop, the seller manually enters the actual stock count.

All loading paths already emit `kiosk_ops.inventory_logs` with movement
`STOCK_IN`. A trigger at that boundary inserts an append-only deduction record
keyed by physical bouquet ID, then runs a guarded product row update requiring
the correct seller, active product and positive quantity. The update locks and
rechecks the current row under concurrency, so the last bouquet can be consumed
only once. Insufficient quantity raises SQLSTATE `40001` (HTTP 409). The whole
transaction rolls back, including its bouquet, slot, preorder item, inventory
event and deduction record.

The deduction record links to the original inventory event and its actor.
Repeated `STOCK_IN` events for the same bouquet do not deduct twice. Existing
staff operation IDs, preorder fulfillment retries, slot maintenance sessions and
occupied-slot guards keep their current retry semantics. No deduction records
are created for historical inventory events during migration.

The public SQL function signatures are unchanged. The latest
`flow.staff_stock_bouquet` and `flow.web_fulfill` definitions use product
`FOR KEY SHARE` reads so simultaneous quantity updates do not deadlock while
upgrading shared locks. The existing bouquet expiry trigger uses that same
compatible product lock and still sets artificial bouquets to infinite expiry.
Inventory kind is immutable through the catalog APIs, and metadata edits retain
their `FOR UPDATE` product lock. The quantity trigger rechecks ownership and
active status during the atomic decrement. Seller stocking uses `FOR KEY SHARE`
for its product ownership lookup; other catalog resource checks retain
`FOR UPDATE`.

Deploy the additive migration before the API binary through the normal
migration workflow. Do not recreate the database or initialize historical shop
stock as part of deployment.

`ProductStockQuantityTests` covers default/nonnegative values, zero-stock
rollback, ownership and active-status rechecks, duplicate events and retries,
manual return reconciliation, competing staff loads, both seller and staff
`CUSTOM` fulfillment and rollback after a later fulfillment validation fails.
These integration tests require an explicitly prepared isolated test database
with the migration applied. They do not initialize a database.
