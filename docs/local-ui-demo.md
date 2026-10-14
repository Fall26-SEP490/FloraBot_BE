# Local UI demonstration data

This script adds records in UUID namespace de000000-* to the local demo only. Back up the database first. It preserves existing accounts, credentials, seller entitlement, payments and ledger entries. It does not recreate databases or replay migrations.

## Inputs and execution

Provide a public media manifest containing exactly four entries: rose, pastel, sunflower, mini. Each needs name, url, publicId, mimeType, sizeBytes and sha256. The digest/size describe stored image bytes, not the URL. Use original public FloraBot Cloudinary URLs. Demo images are AI-generated artificial-flower illustrations, not photographs of actual stock.

    pwsh scripts/local-demo/seed-local-demo.ps1 -ManifestPath <manifest.json> -DryRun
    pwsh scripts/local-demo/seed-local-demo.ps1 -ManifestPath <manifest.json>

Default target: database florabot, container florabot_demo_20261012-postgres-1. Only local demo containers and permitted local test database names are accepted. For the isolated DB florabot_ui_seed_verify_20261014, use a separate PrivateKioskOutputPath.

The private device configuration defaults to C:/Users/tamin/.config/florabot/ui-demo-kiosk.local.json. It contains kioskId, apiKey, database/container binding and setup metadata. Never print, commit, screenshot or put the key in a URL. The script checks key binding, stages a temporary file and publishes it after a successful transaction. Failed SQL preserves the prior config; failed post-commit publication retains a recovery file and reports failure. Missing/mismatched keys require recovery or explicit RotateDeviceKey. It never silently reuses a key file for another database.

## Dataset and boundaries

- One explicitly offline simulated kiosk, no physical hardware or heartbeat.
- Eight slots W01-W04 and P01-P04, all assigned to the same Hoa Sai Gon seller. Codes illustrate WALK_IN/PREORDER zones; this script adds no new server-side zone enforcement.
- Six artificial-flower products: four ACTIVE with verified image metadata, two DRAFT; four stocked artificial bouquets.
- One manager mapping, three device incidents, four assigned technical/delivery tasks.
- Two preorder requests (REQUESTED and QUOTED), one AWAITING_PAYMENT sample order.
- Existing human accounts reused; no accounts, roles or passwords changed.

Default reruns preserve task/order/hold progress. ResetDemoState explicitly resets demo rows; do not use it during an ongoing manual test. IncludePendingSubscription adds only a PENDING_PAYMENT example: it NEVER activates the seller, extends package expiry or records payment. Gain entitlement through the real approved subscription/payment flow. Prototype automatic activation options are intentionally unsupported.

Because the shop is unpaid and the device offline, public sellable catalogues can be empty and checkout/stock mutations can be rejected. Inspect seeded records in internal role screens; use the explicitly labelled kiosk preview for the tablet experience. Do not forge online heartbeats, payment, door acknowledgements or physical pickup to make a demo appear complete.

## Read verification and UI map

    Get-Content scripts/local-demo/verify-demo-data.sql -Raw |
      docker exec -i florabot_demo_20261012-postgres-1 psql -U florabot -d florabot -v ON_ERROR_STOP=1

Use API routes consumed by the checked-out frontend rather than guessing route names.

| Role | UI | Inspect |
|---|---|---|
| Admin | /admin, /admin/slots, /admin/incidents, /admin/tasks, /admin/orders | Manager summary, demo slots/incidents/tasks/preorders |
| Operations Manager | /operations, /operations/technicians | Assigned kiosk scope, technical tasks, technician directory |
| Technician | /technician | Assigned incident tasks, locations, instructions |
| Seller | /seller/products, /seller/stock, /seller/tasks, /seller/orders | Photos/dimensions/status, stock, delivery tasks, quote requests |
| Seller Staff | /staff | Only own assigned shop tasks |
| Customer | /tai-khoan, /ho-so | Own preorder/pending order and own profile |
| Kiosk device | /kiosk/ | Staff setup from private config, truthful offline state, separate no-network preview |

Database rows do not prove API journeys work. Independently log in, inspect actual responses and visually verify desktop/mobile/tablet before handoff. Distinguish real inbox/payment/hardware checks from mocked browser tests.

## Optional cleanup

Data remains for manual testing. Do not automatically clean the application DB after seeding. cleanup-local-demo.ps1 removes only this namespace, refuses genuine customer/financial references and never resets an existing seller's status/expiry. Review its dry run first. It removes no user accounts. Retain the backup and private config for recovery.
