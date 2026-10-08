# 0039 - Kiosk accessory catalog and basket

- Context: source kiosk_checkout already supports accessory lines, authoritative price snapshots, atomic inventory deduction, per-seller orders and restoration on unpaid release.
- Decision: add Shopping-authorized GET `/api/kiosks/{kioskId}/catalog/accessories`, requiring the matching kiosk claim and existing no-store middleware.
- KioskOps reads its own accessory rows; Identity supplies active merchant names through an internal helper. RepeatableRead keeps both reads consistent. Filter inactive/empty items and non-active sellers, matching source take_accessories.
- Generate the response contract from actual OpenAPI. The FE explicitly loads accessories and refreshes availability while browsing; it supports mixed and accessory-only baskets.
- The browser sends only accessory ID and positive integer quantity. Client price and seller identity never authorize a sale; source flow retrieves both from the database.
- Validate stock bounds and safe integer arithmetic before checkout. Stale/unavailable selections block submission and can be removed. Optional catalog errors do not block a flowers-only purchase.
- Keep backend authoritative on concurrent stock changes. Confirmed checkout rejection refreshes both catalogs; ambiguous mutation outcomes retain existing no-retry behavior.
- Source semantics complete accessory-only orders after successful payment without unlock tokens. The UI directs customers to the accessory compartment; no sensor-confirmed accessory delivery is claimed.
- Evidence includes visibility/authorization, forged price/seller input, atomic rejection/restoration, competing purchases, accessory-only completion, responsive browser checks and a real mixed-basket integration. Physical inventory and external provider execution remain separate verification.
