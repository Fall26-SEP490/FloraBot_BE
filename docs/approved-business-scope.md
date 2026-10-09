# Approved business scope and actor boundaries

Status: accepted product scope; runtime migration not yet complete.
Source: PHÂN TÍCH NGHIỆP VỤ.pdf and direct product-owner clarifications on 2026-10-09. Later direct instructions override document wording.

## Seven user actors

Admin; Operations Manager; Technician; Seller; Seller Staff; Kiosk Customer; PreOrder Customer.
AI/Data Mining, SYSTEM and kiosk/device credentials are service principals, not additional human actors.

The present runtime still uses ADMIN/STAFF/SELLER/CUSTOMER. Do not claim the seven-actor migration is complete by adding labels. Introduce OPERATIONS_MANAGER, TECHNICIAN and SELLER_STAFF with explicit scopes and negative authorization tests. Customer identity may be shared across the two customer personas; kiosk and preorder capabilities must remain separately enforced without requiring every walk-in user to create an account.

## Inventory, tenancy and money

- ALL products are artificial flowers (hoa giả), both walk-in and preorder. The PDF's dried-flower wording is superseded. No freshness expiry or automatic age-based disposal.
- One seller leases one whole physical kiosk, divided into WALK_IN and PREORDER slot zones. A zone is not a second kiosk/tenant.
- Walk-in catalog/checkout exclude preorder slots and reserved products, at the server boundary.
- Accessories are PREORDER ONLY, prepared/priced with the bouquet before the seller stocks it. No independent walk-in accessory sale.
- Subscription fees only, no sales commission.
- Platform records revenue. Seller supplies bank details and requests withdrawal; admin reviews, manually transfers funds and records evidence. Customer refund also follows admin-reviewed manual bank transfer. Do not fake automated gateway payouts or customer-wallet refunds.
- Availability/hold policy requires a separate clear decision; admin custody alone does not define when seller funds become withdrawable.
- Hold/order/pickup/token expiry remains distinct from product age, and never means artificial flowers must be destroyed.

## Migration safety and authorization

- Legacy STAFF is platform-operated today and has no seller association. Never automatically turn those accounts into seller staff or infer a tenant from a past assignment.
- Do not widen SameSeller to SELLER_STAFF: existing routes also include wallet/bank/subscriptions. Create explicit narrow tenant-staff policies on only allowed operational routes.
- A staff's assigned kiosk must use a separate assignment concept/claim. Existing kiosk_id denotes a device/customer kiosk session and affects web policy denial; do not reuse it as a generic staff assignment claim.
- Scope enforcement uses authoritative current membership/lease/region/task state, not client-side navigation or stale JWT claims alone. Deactivation/scope changes must revoke or reject sessions.
- Do not infer whole-kiosk ownership from legacy per-slot leases when multiple tenants share a kiosk. Rehearse and resolve conflicts explicitly; require empty slots before reassignment.
- Preserve financial snapshots, ledger history, evidence, audit and existing users during forward-only migrations.
- New catalog increment may represent legacy freshness behavior explicitly as transitional until the separate inventory migration. It must not pretend old data is already migrated.

## Pending timing decisions

P1 dispatch15min, leave notice12h, maximum2 photo revisions and pickup24h/reminder2h remain proposals only. No-response-to-photo2h is approved: measure from latest seller photo delivery; if customer has not responded, permit progression to stocking. A revision request received during that window continues the revision flow. Implement server-side idempotent transition with audit; do not infer pickup expiry or monetary/refund consequences.

## Delivery order and acceptance

1. Identity/permission and tenant foundations.
2. Whole-kiosk lease, slot zones/dimensions and artificial catalog/inventory.
3. Walk-in checkout/payment/device cycle/feedback.
4. Preorder payment/reservation/chat/photo approval/ready/pickup/refund.
5. Seller staff accounts/shifts/tasks/check-in.
6. Operations/technician ticket/SLA/parts/self-test.
7. Manual disbursement, reconciliation and reports.
8. AI, anonymized data/mining and restocking planning.
9. Cross-repository acceptance and measured nonfunctional requirements.

A catalog CRUD slice can proceed independently using existing seller authorization before new role migration. It does not complete all seven actors or the physical fit/stocking workflow.

Acceptance requires UC-to-source/API/UI/test traceability, positive/negative role+tenant/region tests, invalid transitions/concurrency/retry checks, read-after-write and real API integration for changed UI. External mock/simulator evidence must be distinguished from live providers/hardware.
All implementation is on feature branches; never update main.



Approved clarification — 2026-10-09: photo no-response timeout is2hours from latest seller photo delivery. Permit progression to stocking when no response, to avoid prolonged waiting. This rule is recorded for the preorder implementation; catalog increment does not yet implement the timer. It does not free an occupied slot, guarantee slot availability, override a timely revision request, or automatically release money/refund rights. Other time proposals remain pending.
