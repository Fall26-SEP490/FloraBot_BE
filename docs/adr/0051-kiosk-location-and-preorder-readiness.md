# 0051: Kiosk physical location metadata, seller self-management, and preorder location readiness

## Context
Kiosks previously lacked precise physical map coordinates (latitude/longitude), leading to ambiguity during device setup and preventing customers from finding nearby kiosks or getting navigation directions. Furthermore, customers could place preorders against kiosks that lacked complete physical address or map pin information.

Several strict domain and architectural constraints must be met:
1. **Additive schema and data integrity:** Adding coordinates must not fabricate physical locations for existing rows. Legacy NULL coordinates must be permitted while enforcing that newly supplied coordinates are paired, finite, within valid geographical bounds, and associated with a non-empty physical address.
2. **Whole-kiosk tenant ownership:** In FloraBot's business model, a seller manages an entire physical kiosk. Sellers must only be permitted to edit location metadata for their own wholly assigned kiosks (where all slots belong to that seller, with no shared, foreign, or unassigned slots). Platform administrators, operations managers, and technicians do not gain location write privileges.
3. **Offline and unpaid eligibility:** A seller must be allowed to configure location metadata even when their assigned kiosk is offline or their subscription package is unpaid/pending, but doing so must not grant operational hardware or subscription entitlements.
4. **Preorder readiness and historical safety:** New customer preorders must require a complete kiosk address and map pin. However, existing preorders in the database and idempotent retries for existing request IDs must remain preserved without retroactive failures.
5. **Cross-module boundary rules:** The `Ordering` module must not execute Entity Framework queries directly against `kiosk_ops` tables. Location readiness must be queried through module-owned reader boundaries or database function boundaries.
6. **Credential privacy:** Hardware identifiers (ESP32 hardware ID, MQTT client ID) and API key hashes must never be exposed in public or seller-facing DTOs.
7. **Mapping cost control:** Distance estimation must use deterministic straight-line Haversine calculations, and directions must use standard invariant-culture encoded Google Maps destination URLs without requiring a paid Google Maps API key.

## Decision

1. **Additive Database Migration (025):**
   - Adds nullable `latitude` and `longitude` (`double precision`) to `kiosk_ops.kiosks`.
   - Enforces check constraint `chk_kiosks_coordinates_pair`: coordinates must be both NULL, or both NOT NULL with a non-blank physical address.
   - Enforces check constraint `chk_kiosks_coordinates_range`: latitude must be within `[-90.0, 90.0]` and longitude within `[-180.0, 180.0]`.
   - Updates `flow.web_request` stored procedure to verify that new preorders select only non-DISABLED, non-MAINTENANCE kiosks with non-empty addresses and non-null coordinates. Existing request IDs bypass this check to preserve idempotency and historical records.

2. **KioskOps Module-Owned Location Service & Endpoints:**
   - **Public Directory (`GET /api/kiosk-locations`):** Anonymous endpoint returning only complete, non-`DISABLED` kiosks. Truthfully displays `OFFLINE` status. Accepts optional paired `latitude` and `longitude`, bounded `radiusKm`, and bounded `limit` (1..100). Computes Haversine distance in kilometers and breaks distance ties deterministically by kiosk code and ID. Generates destination URLs for Google Maps using invariant-culture string formatting and URL encoding.
   - **Seller Directory (`GET /api/sellers/{sellerId}/kiosks`):** Restricted to `SELLER` role matching `{sellerId}` (`SameSeller`). Returns assigned kiosks with `locationComplete` and `canEditLocation` flags.
   - **Seller Location Mutation (`PUT /api/sellers/{sellerId}/kiosks/{kioskId}/location`):** Restricted to `SELLER` role matching `{sellerId}`. Performs row-level locking (`FOR UPDATE`) on the kiosk and verifies whole-kiosk assignment across all slots. If the kiosk has any unassigned or foreign slots, or is `DISABLED`, the mutation is rejected (403 Forbidden / 400 Bad Request). Upon successful update, logs audit entry `KIOSK_LOCATION_UPDATED` via `flow.audit`.
   - **Compatibility Read (`GET /api/kiosks`):** Preserves existing fields (`id`, `code`, `name`, `address`, `region`, `status`) while adding `latitude`, `longitude`, and `locationComplete`.

3. **Ordering Preorder Boundary Enforcement:**
   - In `WebOrders.cs`, `app.MapPost("/api/member/preorders")` checks whether the request ID already exists for the member.
   - If new, it invokes `KioskLocations.IsPreorderReadyAsync(db, input.KioskId, ct)`—a static reader method owned by the `KioskOps` module—rejecting requests with `400 Bad Request` if the kiosk is incomplete or not ready.
   - No `kiosk_ops` Entity Framework entities or SQL queries are referenced within the `Ordering` module namespace, strictly satisfying `ModuleBoundaryTests`.

## Consequences
- Kiosk discovery and navigation are fully functional for customers without third-party geocoding API costs.
- Sellers can safely manage map coordinates and physical addresses for their wholly owned kiosks without privilege escalation.
- Shared legacy kiosks and unauthorized roles are blocked from mutating location data.
- Existing preorders and historical data remain unaffected.
- Clean architectural seams between `KioskOps` and `Ordering` are preserved.
