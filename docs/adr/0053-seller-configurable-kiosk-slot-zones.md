# 0053: Seller-configurable kiosk slot zones

Status: Accepted — 2026-10-30

## Decision

Operations enrollment records the kiosk identity and its total physical slot count. It does not set the Walk-in or Pre-order split. Newly enrolled slots receive generated `S01`…`S50` labels and start with `zone = NULL`.

After whole-kiosk handover, the active Seller assigned to that kiosk chooses `WALK_IN` or `PREORDER` for each rented empty slot using `PUT /api/me/kiosks/{kioskId}/slots/{slotId}/zone`. The database checks ownership and permits changing the zone only while the slot is `RENTED_EMPTY`, contains no bouquet, and has no active hold. An empty slot may be reclassified later.

The backend requires explicit zones at inventory boundaries: walk-in stocking accepts only `WALK_IN`; preorder creation and paid preorder fulfillment require a seller-configured `PREORDER` slot. An unclassified slot is unavailable for either use.

## Compatibility

Historical `W##` and `P##` slot labels with null zones are backfilled to their prior zones. Other historical null zones stay null and are not silently assigned. Operations handover and scheduling validate the kiosk's total slot count; they no longer require the kiosk to match fixed zone counts in a package.

## Consequences

- Package and kiosk enrollment screens display total capacity only.
- Operations Managers can no longer assign a zone by choosing per-zone counts during enrollment.
- Sellers classify their own empty slots in the inventory screen.
- `NULL` means “not classified yet,” not “shared zone.”
- Zone cannot be changed while a slot is stocked, held, or otherwise unavailable.
