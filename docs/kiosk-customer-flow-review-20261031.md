# Kiosk customer flow review — 2026-10-31

## Source and configuration

The review incorporates BE `origin/main` at `b22a5b5`, FE at `b621046`, and Kios at `00ad077`. Changes are maintained on feature branches in their separate repositories.

Connected mode calls same-origin `/api` endpoints. FE forwards these through its server route; Vercel defaults to `https://api.florabot.site` when no explicit `API_PROXY_TARGET` is set. Kios commits its Vercel rewrite/build configuration and reads local development settings from repository-root `.env.local`. No production or device secret is committed. `?preview=1` remains an isolated demonstration and cannot establish a successful payment or hardware cycle.

Both consumers regenerate their contracts from the updated BE schema, including the additional-kiosk lease endpoint from current main. Export uses an absolute output filename and does not initialize a database or run background jobs.

## Journeys

| Step | API / authority |
| --- | --- |
| Walk-in stock | `GET /api/kiosks/{id}/catalog/items`; source view supplies sellable bouquets and physical slots |
| Reserve basket | `POST /api/kiosks/{id}/flows/kiosk_checkout`; server owns availability, amount, holds and order identity |
| Payment QR | `POST /api/kiosks/{id}/checkouts/{checkoutId}/payment-link`; signed payOS response supplies optional native `qrCode`, with checkout-link fallback |
| Payment and collection status | `GET /api/kiosks/{id}/checkouts/{checkoutId}`; only verified provider callbacks advance payment and create device work |
| Website preorder | Existing member create → seller quote → member acceptance/payment → seller or assigned staff fulfillment into a PREORDER slot |
| SMS collection | Kiosk OTP request/verify → customer Bearer token → `GET .../pickup/orders` → explicit `POST .../pickup/orders/{orderId}/open` → scoped status polling |
| Receipt collection | Explicit original `flows/request_pickup` with receipt ID/code; original kiosk, receipt, payment, readiness and window rules remain authoritative |
| Shop support | Public kiosk business metadata → customer login → explicit existing customer/shop chat open |
| Device support | Bounded public report → audited admin incident; does not mutate stock, payment or hardware |

An email-only customer must save their actual phone in that account's website profile before SMS lookup can identify their orders. No account is merged by email/phone inference. The receipt path remains available without SMS.

Returning Home ends the browser visit. Existing unpaid checkout holds still follow the server's expiration rules; it is not a payment-provider cancellation. This review does not introduce a new cancellation/refund flow.

## Verification and deployment boundary

- BE builds; 28 focused support, signed-payOS-QR, device-protocol and email-template tests pass. The entire API test project compiles after aligning old onboarding test constructors/capacity assertions with current total-slot contracts.
- FE lint, TypeScript, production build and 155 unit tests pass. Twenty browser cases cover customer creation, seller quote, customer acceptance/payment, seller and staff fulfillment, uncertain writes, receipt privacy, and shop support using intercepted HTTP fixtures.
- Kios build/lint/TypeScript, seven cabinet unit tests and all 59 browser tests pass. Browser coverage includes Home/language, device authentication, checkout and bank QR, SMS ownership token, explicit pickup, receipt fallback on SMS 503, privacy cleanup, errors/offline and isolated simulation. Browser tests use intercepted transports, not live bank payments or physical doors.
- Read-only production probes through the API, FE and kiosk origins all returned HTTP 200 and the same kiosk list. The live OpenAPI had 224 paths and did not yet contain the new SMS order-list/support endpoints or `PaymentLinkResponse.qrCode`; unknown protected paths returned 401, which alone is not proof that a route exists.

Push is not deployment. Deploy the BE changes before enabling the matching FE/Kios screens. Existing main's database migrations, including multi-kiosk leases, must be reviewed/applied through the normal deployment process if that target has not received them. This feature adds no migration. Configure the production HTTPS SMS provider/token; code cannot infer or create provider credentials. Existing payOS signed webhook, MQTT provisioning, relay mapping and closed-door sensors remain required.

No runtime database initialization/reset, real SMS delivery, payOS charge/refund, deployment, or physical unlock is performed in this review. Full PostgreSQL/cache/broker integration is a separate gate; local browser fixtures do not certify it.
