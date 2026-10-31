# 0054: Multiple kiosk leases per Seller

Status: Accepted — 2026-10-31

## Context

The initial annual onboarding flow creates a Seller after a Customer pays, Operations confirms and schedules installation, a technician submits evidence, and Operations completes handover. It previously assumed one physical kiosk and one Seller-wide annual expiry. The product owner approved that an existing Seller may lease a second kiosk at another location.

## Decision

- Each physical kiosk is leased whole to one Seller. One Seller may own multiple leased kiosks.
- Every kiosk lease selects its own annual package, payment and one-year term. `identity.subscriptions.kiosk_id` is the entitlement boundary. Existing annual subscriptions are backfilled only when the Seller currently owns exactly one kiosk; ambiguous multi-kiosk records are not guessed.
- The existing Seller/shop identity, contact and bank profile are shared. The new application snapshots those values. Installation address and coordinates are supplied separately for the additional kiosk and stored on that kiosk at handover.
- A Seller creates an additional lease through `POST /api/me/kiosk-applications` with `packageId`, `installationAddress`, `latitude`, and `longitude`. The first-time Customer API `POST /api/member/kiosk-applications` remains unchanged and cannot be used by Sellers.
- Payment, reconciliation, Operations confirmation and scheduling, technician installation/evidence/report, and Operations handover stay in the existing workflow. Handover attaches the kiosk and its active subscription to the existing Seller. It does not create another Seller, change the user role, or overwrite the shared Seller profile.
- Package validity is checked for the kiosk involved in a stock, checkout, catalog, AI recommendation, or preorder operation. An expired kiosk cannot sell or stock, even when another kiosk for that Seller remains active.
- The existing seven-day past-due grace is retained. After the grace period, only slots assigned to the expired kiosk enter the existing release flow. Other kiosk leases and their inventory are not changed.
- Seller-wide `package_valid` remains a compatibility gate for Seller-level catalog writes and is true while at least one kiosk lease is active. `identity.sellers.package_expires_at` is a summary date; individual kiosk dates come from subscription rows.

## Consequences

- Seller subscription reads include the associated `kioskId` and per-lease period.
- Operations and technician routes retain the existing application state machine and authorization. The existing kiosk-eligibility endpoint still selects an unassigned physical kiosk with the package's total slot capacity.
- A Seller can have at most one in-flight additional kiosk application at a time. Payment/refund and audit records remain tied to the existing application/payment identifiers.
- Reapplying for an additional physical kiosk is distinct from renewing an existing kiosk lease; this change does not add a kiosk-renewal endpoint.
