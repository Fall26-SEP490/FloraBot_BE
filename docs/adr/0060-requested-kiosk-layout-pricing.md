# 0060: Requested kiosk layout and annual pricing per slot type

Status: Superseded by `20261106_uniform_slot_price_zone_requests.sql` and `docs/kiosk-uniform-price-layout-change-for-fe.md` for new pricing and post-handover layout changes. Historical rental snapshots remain valid.

## Context

The owner superseded the Seller-configurable layout in ADR 0053. Renters must request the whole kiosk's physical capacity and Walk-in/Pre-order distribution before payment. Operations creates the matching device. The final pricing decision is a shared price list controlled by Operations Manager, rather than a separately negotiated quotation for each application.

## Decision

- Apply the same rules to first-time Customer onboarding and additional kiosks for an existing Seller. Require nonnegative `walkInSlots` and `preorderSlots`, with a total of 1–50. Either individual count may be zero.
- Operations Manager sets positive integer VND prices per slot per year: `walkInAnnualPrice` and `preorderAnnualPrice`. Do not seed an invented default price. Publish the current values and optimistic-concurrency version through the existing `GET /api/kiosk-packages` route, whose response changes from package list to pricing object. Add only the previously unavailable Manager setter, `PUT /api/operations/kiosk-pricing`.
- Creation requires the displayed `pricingVersion`. The database locks/validates the current price configuration, calculates the total, and snapshots both prices, version and annual fee. Reject a stale version with 409 and an unconfigured price list with 503. Never accept a client-supplied fee as authoritative.
- The fee is `walkInSlots × walkInAnnualPrice + preorderSlots × preorderAnnualPrice`. Preserve this application/payment snapshot when the global price list changes. The existing payment link uses the stored amount.
- Keep the existing lifecycle: `AWAITING_PAYMENT → PAID_REVIEW → CONFIRMED → SCHEDULED → INSTALLING → AWAITING_HANDOVER → ACTIVE`. There is no application quotation or quotation-acceptance state in this change. Operations contact confirmation still occurs after payment.
- Preserve subscription/package foreign keys by creating an internal inactive package snapshot for each new application. It is a contract record, not a selectable fixed-price package and not a shortcut to Seller activation. Keep historical package-priced applications unchanged.
- Modify existing enrollment to require `applicationId`; derive total capacity and zones from its paid, confirmed application. Reserve the enrolled kiosk for that application. Scheduling and handover validate the agreed layout as well as occupancy/capacity. Optional legacy `slotCount` must equal the application's total.
- Slot counts are fixed when the rental application is submitted. Enrollment derives the exact distribution; scheduling and handover cannot amend it.
- Remove zone-edit routes for both Seller and Operations Manager. Database triggers reject changing a created slot's zone, even while empty, and changing a SLOT_PRICING application's requested counts. Retired SQL setters also deny changes. Normal stocking, sales and emptying operations retain their existing slot-status transitions.
- Preserve all historical zones, including NULL; do not guess or offer later classification. A legacy unclassified slot remains unavailable at explicit zone inventory boundaries.
- Preserve accepted fees, settled payments and current leases. Updated price configuration applies to new rental applications; this increment does not add a renewal API.
- Retain one whole kiosk per lease, one-year term from handover, multiple leases per Seller, existing authorization/tenant checks, evidence and manual refund boundaries from ADRs 0052 and 0054.

## Consequences

The FE must regenerate contracts and replace fixed-package selection with a slot-count form showing both unit prices and the calculated annual fee. It must handle stale price versions explicitly, remove all zone-edit controls, and provide Manager pricing controls. The backend remains the authority for prices, totals, eligibility and state transitions.

Deploy the additive `20261104_requested_kiosk_layout_pricing.sql` migration before the new API binary, after a backup. Publish `FloraBot.Api`; no Gateway change is required for this increment. Coordinate the changed pricing response with FE deployment. Do not run initialization scripts or alter historical financial records.

Automated local tests can verify price snapshots, access controls, layout and transitions with simulated settlement/evidence. They do not establish live PayOS settlement or successful physical installation.
