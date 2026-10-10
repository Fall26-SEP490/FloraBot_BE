# Staff Role Split Contract & Specifications

## 1. Overview and Core Decision

This document specifies the authoritative contract for the retirement of legacy `STAFF` and the split of operational and shop delivery task workflows into four dedicated roles:
- **`TECHNICIAN`**: Platform technical personnel assigned to kiosk incident resolution.
- **`OPERATIONS_MANAGER`**: Platform operations supervisor coordinating technical incidents and technicians within assigned kiosk scopes.
- **`SELLER_STAFF`**: Dedicated shop delivery staff assigned to inventory restocking within their shop's rented slots.
- **`SELLER`**: Shop owner coordinating delivery tasks and managing shop staff.

Together with **`ADMIN`** (platform administrator) and **`CUSTOMER`** (end customer), these constitute the six authenticated human business roles in FloraBot:
1. `ADMIN`
2. `OPERATIONS_MANAGER`
3. `TECHNICIAN`
4. `SELLER`
5. `SELLER_STAFF`
6. `CUSTOMER`

Legacy `STAFF` is retired. Its database storage value is retained exclusively for foreign-key and audit log preservation, but runtime authentication, session validation (`OnTokenValidated`), token refresh, credential provisioning, and task assignment strictly reject `STAFF`.

---

## 2. Role Boundaries and Security Matrix

| Role | Incident Tasks | Delivery Tasks | Stocking Flowers | Resolve Incidents | Assign / Reassign Staff | Seller Financial / Wallet APIs | General Admin APIs |
|---|---|---|---|---|---|---|---|
| **ADMIN** | Full oversight | Full oversight | No | Yes (admin dispute flow) | Yes (to TECHNICIAN / SELLER_STAFF) | Yes (two-admin rules) | Yes |
| **OPERATIONS_MANAGER** | Scoped to assigned kiosks | No (denied) | No (denied) | Review / verify only | Yes (INCIDENT to TECHNICIAN in scope) | No (denied) | No (denied) |
| **TECHNICIAN** | Assigned tasks only | No (denied) | No (denied) | Yes (`resolve-incident` on assigned task) | No | No (denied) | No (denied) |
| **SELLER** | No (denied) | Own shop only | Yes (direct seller stocking) | No (denied) | Yes (DELIVERY to own SELLER_STAFF) | Yes (own wallet/bank/subscription) | No (denied) |
| **SELLER_STAFF** | No (denied) | Assigned tasks only (own shop) | Yes (`staff_stock` on assigned task) | No (denied) | No | No (denied) | No (denied) |
| **STAFF (Legacy)** | Disabled / Retired | Disabled / Retired | Disabled | Disabled | Disabled | Disabled | Disabled |

### Scope Enforcement Rules
1. **Operations Manager Scope (`kiosk_ops.manager_kiosks`)**:
   - Explicit mapping between `manager_id` and `kiosk_id`.
   - Fails closed: an Operations Manager with no mapped kiosks receives empty task/incident lists and cannot assign or transition tasks.
2. **Technician Scope**:
   - Accesses only tasks where `assignee_id = current_user.id` and `kind = 'INCIDENT'`.
3. **Seller Scope**:
   - Enforced by `SameSeller` and `seller_id = current_user.seller_id`. Can only assign DELIVERY tasks to `ACTIVE` `SELLER_STAFF` with the identical `seller_id`.
4. **Seller Staff Scope**:
   - Accesses only tasks where `assignee_id = current_user.id`, `kind = 'DELIVERY'`, and `seller_id = current_user.seller_id`.

---

## 3. Worker Endpoints (`/api/staff/*`)

Used by field workers: `TECHNICIAN` and `SELLER_STAFF`.

### 3.1 List Assigned Tasks
- **Route**: `GET /api/staff/tasks?page=1&status={status}`
- **Policies**: `StaffWorker` (Admits `TECHNICIAN` and `SELLER_STAFF`, rejects kiosk tokens and legacy `STAFF`).
- **Query Parameters**:
  - `page`: optional integer (default 1).
  - `status`: optional string (`ASSIGNED`, `IN_PROGRESS`, `SUBMITTED`, `COMPLETED`, `CANCELLED`).
- **Response**: `200 OK` (`StaffTaskPage`)
  ```json
  {
    "items": [
      {
        "id": "uuid",
        "kind": "INCIDENT | DELIVERY",
        "status": "ASSIGNED | IN_PROGRESS | SUBMITTED | COMPLETED | CANCELLED",
        "version": 1,
        "assigneeId": "uuid",
        "assigneeName": "string",
        "kioskId": "uuid",
        "kioskName": "string",
        "address": "string",
        "sellerId": "uuid | null",
        "shopName": "string | null",
        "incidentId": "uuid | null",
        "instructions": "string",
        "report": "string | null",
        "createdAt": "2026-10-09T08:00:00Z",
        "updatedAt": "2026-10-09T08:00:00Z"
      }
    ],
    "page": 1,
    "hasMore": false
  }
  ```
- **Security**:
  - `TECHNICIAN` sees only rows with `assignee_id = me` AND `kind = 'INCIDENT'`.
  - `SELLER_STAFF` sees only rows with `assignee_id = me` AND `kind = 'DELIVERY'` AND `seller_id = my_seller_id`.

### 3.2 Task Details and Audit History
- **Route**: `GET /api/staff/tasks/{taskId}?page=1`
- **Policies**: `StaffWorker`
- **Response**: `200 OK`
  ```json
  {
    "task": { /* StaffTaskListRow fields */ },
    "kioskStatus": "ONLINE | OFFLINE | DISABLED",
    "lastHeartbeatAt": "2026-10-09T08:00:00Z | null",
    "incident": {
      "kind": "DEVICE_FAULT | DISPENSE_FAILED",
      "description": "string",
      "customerPhone": "string | null",
      "slotNumber": 2
    },
    "deliverySlots": [
      {
        "slotId": "uuid",
        "slotNumber": 2,
        "status": "RENTED_EMPTY | STOCKED",
        "productName": "string | null"
      }
    ],
    "history": [
      {
        "id": "123",
        "actorId": "uuid",
        "action": "STAFF_TASK_ASSIGNED | STAFF_TASK_UPDATED | STAFF_TASK_STOCKED | STAFF_TASK_REASSIGNED | STAFF_TASK_EVIDENCE_ADDED | STAFF_INCIDENT_RESOLVED",
        "fromStatus": "ASSIGNED | null",
        "toStatus": "IN_PROGRESS | null",
        "report": "string | null",
        "createdAt": "2026-10-09T08:00:00Z"
      }
    ],
    "page": 1,
    "hasMore": false
  }
  ```
- **Error Codes**: `404 Not Found` if task does not exist, belongs to another user, or violates role-kind restrictions.

### 3.3 Task State Transition
- **Route**: `POST /api/staff/tasks/{taskId}/transition`
- **Policies**: `StaffWorker`
- **Request Body**:
  ```json
  {
    "version": 1,
    "status": "IN_PROGRESS | SUBMITTED",
    "report": "Detailed progress notes (10 to 2000 characters)"
  }
  ```
- **Response**: `200 OK` -> `{"version": 2}`
- **Allowed Transitions**:
  - `ASSIGNED` -> `IN_PROGRESS`
  - `IN_PROGRESS` -> `SUBMITTED`
- **Error Codes**: `409 Conflict` on version mismatch; `400 Bad Request` on invalid transition or short report.

### 3.4 Upload Evidence (Photo)
- **Step 1 - Upload**: `POST /api/staff/tasks/{taskId}/evidence/upload`
  - **Body**: binary `image/jpeg`, `image/png`, or `image/webp` (max 5 MB).
  - **Response**: `200 OK` -> `{"reference": "string", "expiresAt": "2026-10-09T08:30:00Z"}`
- **Step 2 - Attach**: `POST /api/staff/tasks/{taskId}/evidence`
  - **Body**:
    ```json
    {
      "id": "uuid",
      "version": 2,
      "reference": "ticket-reference"
    }
    ```
  - **Response**: `200 OK` -> `{"id": "uuid", "createdAt": "2026-10-09T08:05:00Z"}`
- **Step 3 - List Evidence**: `GET /api/staff/tasks/{taskId}/evidence?page=1&attachmentId={optional}`
  - **Response**: `200 OK` -> `{"items": [{"id": "uuid", "createdAt": "2026-10-09T08:05:00Z"}], "page": 1, "hasMore": false}`
- **Step 4 - Download Evidence**: `GET /api/staff/tasks/{taskId}/evidence/{attachmentId}`
  - **Response**: `200 OK` (binary image data).

### 3.5 Delivery Restocking (`SELLER_STAFF` Only)
- **Route**: `GET /api/staff/tasks/{taskId}/stock`
  - **Response**: `200 OK`
    ```json
    {
      "products": [{"id": "uuid", "name": "Rose Bouquet", "price": 350000}],
      "slots": [{"slotId": "uuid", "slotNumber": 3, "status": "RENTED_EMPTY"}],
      "recentActions": [
        {
          "id": "uuid",
          "productId": "uuid",
          "slotId": "uuid",
          "qrCode": "QR-12345",
          "bouquetId": "uuid",
          "createdAt": "2026-10-09T08:10:00Z"
        }
      ]
    }
    ```
  - **Security**: Forbidden (`403`) for `TECHNICIAN`.
- **Route**: `POST /api/staff/tasks/{taskId}/stock`
  - **Request Body**:
    ```json
    {
      "id": "uuid",
      "productId": "uuid",
      "slotId": "uuid",
      "qrCode": "QR-12345"
    }
    ```
  - **Response**: `200 OK` -> `{"bouquetId": "uuid"}`
  - **Security**: Forbidden (`403`) for `TECHNICIAN`.

### 3.6 Incident Repair Resolution (`TECHNICIAN` Only)
- **Route**: `POST /api/staff/tasks/{taskId}/resolve-incident`
  - **Request Body**:
    ```json
    {
      "id": "uuid",
      "version": 2,
      "report": "Sensor re-calibrated and door tested on site (10 to 2000 chars)"
    }
    ```
  - **Response**: `200 OK` -> `{"version": 3}`
  - **Security**: Forbidden (`403`) for `SELLER_STAFF`.

---

## 4. Operations Manager Workspace (`/api/operations/*`)

Used by `OPERATIONS_MANAGER` within their assigned kiosk scope.

### 4.1 Available Technicians
- **Route**: `GET /api/operations/staff`
- **Response**: `200 OK`
  ```json
  [
    {
      "id": "uuid",
      "name": "Nguyen Van Tech"
    }
  ]
  ```
  Returns active accounts where `role = 'TECHNICIAN'` from the shared technician pool.
  - **Scope Enforcement**: Fails closed (`[]`) if the operations manager has no mapped kiosks in `kiosk_ops.manager_kiosks`.
  - **Architecture Note**: This surfaces the shared active technician pool for operations managers with mapped kiosks pending a future regional workforce/shift schema. It does NOT claim complete regional/shift/GPS workforce management.

### 4.2 Kiosk Incidents Queue
- **Route**: `GET /api/operations/incidents?page=1&status=OPEN`
- **Response**: `200 OK`
  Lists disputes of type `DEVICE_FAULT` and `DISPENSE_FAILED` for kiosks assigned to the manager.

### 4.3 Incident Tasks Management
- **List Tasks**: `GET /api/operations/staff-tasks?page=1&status={status}`
  - Lists INCIDENT tasks at manager-assigned kiosks.
- **Assign Task**: `POST /api/operations/staff-tasks`
  - **Request Body**:
    ```json
    {
      "id": "uuid",
      "assigneeId": "uuid",
      "kind": "INCIDENT",
      "kioskId": "uuid",
      "sellerId": null,
      "incidentId": "uuid",
      "instructions": "Inspect door sensor at slot 2"
    }
    ```
  - **Enforcement**: Fails with `403` / `400` if `kioskId` is not in manager's assigned kiosks, or if `assigneeId` is not an active `TECHNICIAN`, or if `kind != 'INCIDENT'`.
- **Task Detail & History**: `GET /api/operations/staff-tasks/{taskId}?page=1`
- **Review / Transition**: `POST /api/operations/staff-tasks/{taskId}/transition`
  - **Request Body**:
    ```json
    {
      "version": 2,
      "status": "COMPLETED | IN_PROGRESS | CANCELLED",
      "report": "Report reviewed and repair confirmed"
    }
    ```
- **Handoff / Reassign**: `POST /api/operations/staff-tasks/{taskId}/reassign`
  - **Request Body**:
    ```json
    {
      "version": 2,
      "assigneeId": "uuid",
      "reason": "Handing off to night shift technician"
    }
    ```
  - **Enforcement**: `assigneeId` must be an active `TECHNICIAN`.
- **Evidence Inspection**:
  - `GET /api/operations/staff-tasks/{taskId}/evidence?page=1&attachmentId={id}`
  - `GET /api/operations/staff-tasks/{taskId}/evidence/{attachmentId}`

---

## 5. Seller Workspace (`/api/sellers/{sellerId}/*`)

Used by `SELLER` (and `ADMIN`) for their shop's delivery tasks.

### 5.1 Shop Staff List
- **Route**: `GET /api/sellers/{sellerId}/staff`
- **Policy**: `Merchant`, `SameSeller`
- **Response**: `200 OK`
  ```json
  [
    {
      "id": "uuid",
      "name": "Tran Delivery"
    }
  ]
  ```
  Returns active accounts where `role = 'SELLER_STAFF'` and `seller_id = sellerId`.

### 5.2 Delivery Scopes
- **Route**: `GET /api/sellers/{sellerId}/staff-delivery-scopes`
- **Response**: `200 OK`
  ```json
  [
    {
      "kioskId": "uuid",
      "kioskName": "FloraBot Q1",
      "sellerId": "uuid",
      "sellerName": "Hoa Tuoi Sai Gon",
      "rentedSlots": 4,
      "stockedSlots": 2
    }
  ]
  ```
  Returns kiosks where the seller holds active rented slots.

### 5.3 Delivery Tasks Management
- **List Tasks**: `GET /api/sellers/{sellerId}/staff-tasks?page=1&status={status}`
  - Returns only `DELIVERY` tasks for this shop.
- **Assign Task**: `POST /api/sellers/{sellerId}/staff-tasks`
  - **Request Body**:
    ```json
    {
      "id": "uuid",
      "assigneeId": "uuid",
      "kind": "DELIVERY",
      "kioskId": "uuid",
      "sellerId": "uuid",
      "incidentId": null,
      "instructions": "Restock slots 1 and 2 with Fresh Roses"
    }
    ```
  - **Enforcement**: `sellerId` must match the authenticated seller; `assigneeId` must be an active `SELLER_STAFF` of this shop; `kioskId` must be a valid delivery scope.
- **Task Detail & History**: `GET /api/sellers/{sellerId}/staff-tasks/{taskId}?page=1`
- **Review / Transition**: `POST /api/sellers/{sellerId}/staff-tasks/{taskId}/transition`
  - **Request Body**:
    ```json
    {
      "version": 2,
      "status": "COMPLETED | IN_PROGRESS | CANCELLED",
      "report": "Delivery confirmed and bouquet verified"
    }
    ```
- **Handoff / Reassign**: `POST /api/sellers/{sellerId}/staff-tasks/{taskId}/reassign`
  - **Request Body**:
    ```json
    {
      "version": 2,
      "assigneeId": "uuid",
      "reason": "Reassigned to second delivery staff"
    }
    ```
  - **Enforcement**: `assigneeId` must be an active `SELLER_STAFF` of the same shop.
- **Evidence Inspection**:
  - `GET /api/sellers/{sellerId}/staff-tasks/{taskId}/evidence?page=1&attachmentId={id}`
  - `GET /api/sellers/{sellerId}/staff-tasks/{taskId}/evidence/{attachmentId}`

---

## 6. Admin Historical Oversight Paths (`/api/admin/*`)

- `ADMIN` retains oversight access across all tasks:
  - `GET /api/admin/staff`: Lists active `TECHNICIAN` and `SELLER_STAFF` accounts (legacy `STAFF` excluded).
  - `GET /api/admin/staff-delivery-scopes`: Unscoped delivery scopes across all kiosks.
  - `GET /api/admin/staff-tasks`: Unscoped tasks list across all kiosks and sellers.
  - `GET /api/admin/staff-tasks/{taskId}`: Unscoped task detail.
  - `POST /api/admin/staff-tasks/{taskId}/transition`: Oversight transition.
  - `POST /api/admin/staff-tasks/{taskId}/reassign`: Admin reassignment (must assign `TECHNICIAN` for `INCIDENT`, `SELLER_STAFF` for `DELIVERY`).
  - `POST /api/admin/staff-tasks`: Admin assignment (must assign `TECHNICIAN` for `INCIDENT`, `SELLER_STAFF` for `DELIVERY`; assigning legacy `STAFF` is rejected).
  - `GET /api/admin/staff-tasks/{taskId}/evidence`: Unscoped evidence view.

---

## 7. Migration 020 Schema and Functions Contract

1. **Table `kiosk_ops.manager_kiosks`**:
   ```sql
   CREATE TABLE kiosk_ops.manager_kiosks (
     manager_id uuid NOT NULL REFERENCES identity.users(id),
     kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
     created_at timestamptz NOT NULL DEFAULT public.app_now(),
     PRIMARY KEY (manager_id, kiosk_id)
   );
   ```
2. **Updated Assignment Function `flow.assign_staff_task`**:
   - `p_actor uuid`: Can be `ADMIN`, `OPERATIONS_MANAGER`, or `SELLER`.
   - If `OPERATIONS_MANAGER`: `p_kind` must be `INCIDENT`, actor must have entry in `kiosk_ops.manager_kiosks` for `p_kiosk`, `p_assignee` must be `ACTIVE TECHNICIAN`.
   - If `SELLER`: `p_kind` must be `DELIVERY`, `p_seller` must match actor's `seller_id`, `p_assignee` must be `ACTIVE SELLER_STAFF` with same `seller_id`.
   - If `ADMIN`: can assign `INCIDENT` to `ACTIVE TECHNICIAN` or `DELIVERY` to `ACTIVE SELLER_STAFF`.
   - Rejects retired `STAFF`.
3. **Updated Reassignment Function `flow.reassign_staff_task`**:
   - Validates caller role and scope (`ADMIN`, `OPERATIONS_MANAGER` for `INCIDENT` at mapped kiosk, `SELLER` for `DELIVERY` of own shop).
   - Enforces new assignee role matches task kind (`TECHNICIAN` for `INCIDENT`, `SELLER_STAFF` for `DELIVERY` within same shop).
4. **Updated Transition Function `flow.transition_staff_task`**:
   - `TECHNICIAN` can transition assigned `INCIDENT` from `ASSIGNED` -> `IN_PROGRESS` or `IN_PROGRESS` -> `SUBMITTED`.
   - `SELLER_STAFF` can transition assigned `DELIVERY` from `ASSIGNED` -> `IN_PROGRESS` or `IN_PROGRESS` -> `SUBMITTED`.
   - `OPERATIONS_MANAGER` can transition `SUBMITTED` -> `COMPLETED`/`IN_PROGRESS` or open -> `CANCELLED` for `INCIDENT` at mapped kiosk.
   - `SELLER` can transition `SUBMITTED` -> `COMPLETED`/`IN_PROGRESS` or open -> `CANCELLED` for `DELIVERY` of own shop.
   - `ADMIN` retains oversight transitions.
5. **Updated Stock Function `flow.staff_stock_bouquet`**:
   - Validates `p_staff` has role `SELLER_STAFF` and matches task `assignee_id` and task `seller_id`.
   - Rejects `TECHNICIAN`, `STAFF`, or mismatching seller.
6. **Updated Resolution Function `flow.staff_resolve_incident`**:
   - Validates `p_actor` has role `TECHNICIAN` and matches task `assignee_id`.
   - Rejects `SELLER_STAFF`, `STAFF`.
