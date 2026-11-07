# FloraBot CRUD handoff for FE (7 November 2026)

These endpoints are in the BE working tree. Publish `FloraBot.Api` and apply the new SQL migrations before using them on the VPS. Paths are relative to the selected API base URL. Send the current login cookie or Bearer token. `403` means the role or tenant is not allowed; `409` means the record changed or its current state prevents the action.

| Area | Role | Endpoint | FE use |
| --- | --- | --- | --- |
| Kiosk manager | Admin | `GET /api/admin/kiosk-manager-assignments?managerId=&page=` | List kiosk assignments; one Manager per kiosk, one Manager may manage many kiosks. |
| Kiosk manager | Admin | `PUT /api/admin/kiosks/{kioskId}/operations-manager` | `{ "managerId": "uuid" }` assigns/reassigns; `null` removes. Active incident work prevents reassignment. |
| Renewal | Seller | `POST /api/me/kiosks/{kioskId}/renewals` | Creates a pending annual lease at the current per-slot price. Returns amount, period, subscription ID, and payment link path. |
| Renewal | Seller | `POST /api/sellers/{sellerId}/subscriptions/{subscriptionId}/payment-link` | Existing PayOS link endpoint. After payment, refresh the lease status. |
| Accessories | Seller | `GET/POST /api/sellers/{sellerId}/accessories`, `GET/PUT /api/sellers/{sellerId}/accessories/{id}`, `PATCH /api/sellers/{sellerId}/accessories/{id}/status` | Create, view, edit and deactivate accessories. Deactivation retains order history. |
| Refund proof | Seller | `GET /api/sellers/{sellerId}/refunds/{refundId}`, `POST .../evidence/upload`, `POST .../submit-transfer` | Submit bank-transfer reference, amount, time and evidence. If Admin rejects with a reason, replace the proof up to three times. |
| Refund proof | Admin | `GET /api/admin/refunds/{refundId}/seller-submission`, `POST /api/admin/refunds/{refundId}/seller-submission/reject` | Inspect proof and reject it with a reason. Existing confirmation endpoint still confirms an accepted transfer. |
| Customer account | Customer | `POST /api/account/close` | `{ "password": "..." }`. Disables and anonymizes the account, retains order/payment history and clears cookies. |
| Staff profile | Seller | `GET/PUT /api/sellers/{sellerId}/staff/{staffId}` | Edit staff name, phone and personal address. Email, role and Seller ownership are fixed. |
| Product photos | Seller | `GET /api/me/products/{productId}/photos`, `PUT .../photos/order`, `DELETE .../photos/{photoId}` | List visible photos, reorder with `{ "photoIds": ["uuid", "..."] }` (first becomes cover), and hide one photo. Upload uses the existing `POST .../photos`. |
| Inventory history | Seller | `GET /api/me/inventory-history?kioskId=&slotId=&bouquetId=&from=&to=&page=` | Read historical bouquet movements, including movements from a kiosk no longer rented by this Seller. |
| Audit | Admin | `GET /api/admin/audit-logs?...`, `GET /api/admin/audit-logs/{id}` | List/detail, 50 per page. Payload is redacted. |
| Termination | Seller | `POST /api/sellers/{sellerId}/kiosks/{kioskId}/termination-requests/{id}/withdraw` | Withdraw only a still pending request. |
| Shop profile | Seller | `GET/PATCH /api/me/shop` | Edit shop name, phone and verified contact email. Bank details use the existing `set_seller_bank` flow. Address is read-only here. |
| Kiosk location | Seller | `PUT /api/sellers/{sellerId}/kiosks/{kioskId}/location`, `GET .../location-requests` | PUT now submits a request containing address, latitude, longitude and optional reason. It does not change the kiosk immediately. |
| Kiosk location | Operations Manager | `GET /api/operations/kiosk-location-requests?status=`, `POST /api/operations/kiosk-location-requests/{id}/review` | Review `{ "decision": "APPROVED" }` or `{ "decision": "REJECTED", "note": "reason" }`. The Manager must be assigned to the kiosk. Approval is allowed while orders or flowers remain. |
| Staff tasks | Seller / Manager / Admin | `PATCH /api/me/staff-tasks/{taskId}` or role-specific `/api/operations/...`, `/api/admin/...` | `{ "version": 1, "instructions": "..." }`; allowed in ASSIGNED and IN_PROGRESS. Reassignment and cancellation use the existing transition APIs. Changing kiosk requires cancel and recreate. |

## Existing flows retained

- Seller bank changes use `POST /api/sellers/{sellerId}/flows/set_seller_bank`; do not put account numbers in shop profile updates.
- Seller contact email must first be verified with the account contact-email flow, then used in `PATCH /api/me/shop`.
- Staff task cancellation uses `POST /api/sellers/{sellerId}/staff-tasks/{taskId}/transition` (or the Manager/Admin equivalent), body `{ "version": 1, "status": "CANCELLED", "report": "Reason with at least 10 characters" }`. `IN_PROGRESS` is allowed.
- Seller cannot directly change kiosk address or slot zone. Manager approval controls a kiosk address change. Slot layout follows the separately documented request/review flow.
- A product photo DELETE hides the image from catalog views and keeps metadata for historical records. It does not immediately remove the Cloudinary object.

## SQL migration order

Apply new files in filename order after the earlier deployed migrations, with `ON_ERROR_STOP=1` and a database backup:

1. `20261106_uniform_slot_price_zone_requests.sql`
2. `20261107_kiosk_location_change_requests.sql`
3. `20261107_kiosk_renewals.sql`
4. `20261107_manager_kiosk_assignment.sql`
5. `20261107_product_photo_management.sql`
6. `20261107_seller_refund_resubmission.sql`
7. `20261107_staff_task_instructions.sql`

The current code remains in a working tree. None of these changes has been deployed to the VPS by this work.
