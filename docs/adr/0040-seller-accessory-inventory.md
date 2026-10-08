# 0040 - Seller accessory inventory and restock

Date: 2026-10-08. Status: accepted for local implementation.
Context: source `flow.restock_accessory` adds positive quantities and audits the actor.
Decision: expose a no-store Merchant/SameSeller read in KioskOps, using tenant-filtered EF rows.
Return all owned accessories, including zero stock and INACTIVE, with their kiosk metadata.
Reuse the generated restock endpoint; actor comes from authentication, never from the form.
The portal loads inventory on demand, validates int32 positive quantities and asks for explicit confirmation.
Do not add ACTIVE/ONLINE restrictions absent from source SQL or claim that restock opens a door.
Writes have no automatic retry; ambiguous outcomes block the current form and instruct audit reconciliation.
The block is component-local, not server idempotency; reopening the page does not prove an earlier write failed.
Verification: SellerAccessoryTests covers tenancy, actor audit and stock; portal/accessories.spec.ts covers interaction, lost responses, responsive reflow and axe.
Limits: browser inventory/write tests use mocks; the API test uses PostgreSQL. Physical replenishment is operator-confirmed, not sensed.
