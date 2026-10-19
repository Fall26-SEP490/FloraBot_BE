# 0052: Annual kiosk onboarding lifecycle, whole-kiosk slot zones, and handover promotion

## Context

FloraBot's business model assigns one Seller to lease one whole physical kiosk. Monopolizing or sharing individual slots across arbitrary sellers is not permitted; each kiosk consists of designated `WALK_IN` and `PREORDER` slot zones. Under the approved 2026-10-15 annual onboarding model:
1. **Packages:** Three annual packages are provided:
   - `Trial`: 10,000 VND annual fee, 1 WALK_IN + 1 PREORDER (2 slots total).
   - `Pro`: 15,000 VND annual fee, 4 WALK_IN + 4 PREORDER (8 slots total).
   - `Max`: 20,000 VND annual fee, 6 WALK_IN + 6 PREORDER (12 slots total).
   The price represents a fixed annual total (not a monthly fee or free trial), with entitlement lasting exactly one calendar year starting from accepted handover.
2. **Customer Onboarding Lifecycle:**
   - The application requires shop name, business phone, business contact email, installation address, paired geographic coordinates (lat/long), and encrypted bank details.
   - Customers remain in role `CUSTOMER` upon submission and payment. Only a verified, completed handover by an Operations Manager promotes the user to `SELLER`.
   - Legacy paths such as `POST /api/member/shop` which directly promoted Customers to Sellers without approved onboarding or physical kiosk allocation must be closed.
3. **Payment & Reconciliation:**
   - Online payments use payOS. Successful payment marks the application `PAID_REVIEW` and never grants seller claims or entitlements directly.
   - Local development and test environments lack public HTTPS webhooks; an authenticated owner-only server-to-server reconciliation endpoint must verify payment status directly with payOS.
4. **Operations & Technician Workflow:**
   - Operations Manager reviews application contact details -> marks `CONFIRMED`. This establishes an irreversible lock on voluntary customer cancellations.
   - Manager schedules an active Technician and selects an eligible unassigned whole kiosk with exact matching slot zones (`SCHEDULED`).
   - Assigned technician starts the visit (`INSTALLING`), attaches genuine installation photos via the secure Cloudinary media pipeline, submits a report (`AWAITING_HANDOVER`).
   - Operations Manager may request rework (returning to `INSTALLING`) or confirm final handover (`ACTIVE`), executing an atomic database transaction that updates kiosk location, creates seller and 1-year subscription records, assigns all slots, promotes the user to `SELLER`, and logs an audit trail.
5. **Cancellation & Manual Refund Remediation:**
   - Unpaid applications can be cancelled by the customer (`CANCELLED`).
   - Paid applications before `CONFIRMED` can request cancellation (`REFUND_PENDING`), routed to manual Admin remediation.
   - Late payments arriving after cancellation route to `REFUND_PENDING` (never reactivating the cancelled application).
   - After `CONFIRMED`, voluntary cancellation is strictly blocked. Exceptional platform/installation failures are routed to `REFUND_PENDING` with audit records.
   - Admin refunds require two-admin separation of duty (one approves, another confirms payout with transfer reference).

## Decision

1. **Additive Database Migrations (026/027):**
   - **`kiosk_ops.kiosks.seller_id`:** Records the single seller that owns the kiosk. Admin assignment changes the whole kiosk atomically; its slots cannot be assigned across sellers.
   - **`identity.subscription_packages`:** Adds `annual_fee` (bigint), `walk_in_slots` (int), `preorder_slots` (int), and `billing_cycle` (text DEFAULT 'MONTHLY'). Inserts or ensures the 3 annual packages: `Trial`, `Pro`, and `Max`.
   - **`kiosk_ops.slots`:** Adds `zone` (text CHECK (zone IS NULL OR zone IN ('WALK_IN', 'PREORDER'))). Legacy slots conservatively retain NULL to avoid inferring false historical allocation; explicit zones are assigned to newly enrolled or reconfigured whole kiosks.
   - **`kiosk_ops.kiosk_applications`:** New table storing onboarding applications:
     - `id` (uuid primary key), `customer_id` (uuid FK `identity.users`), `package_id` (uuid FK `identity.subscription_packages`).
     - `shop_name`, `phone`, `contact_email`, `installation_address`, `latitude`, `longitude`.
     - `bank_name`, `bank_holder`, `bank_account_no_enc` (encrypted with ASP.NET Data Protection).
     - `status` (text: `AWAITING_PAYMENT`, `PAID_REVIEW`, `CONFIRMED`, `SCHEDULED`, `INSTALLING`, `AWAITING_HANDOVER`, `ACTIVE`, `CANCELLED`, `REFUND_PENDING`, `REFUNDED`, `REJECTED`).
     - `payment_id` (uuid FK `payment.payments`), `assigned_technician_id` (uuid FK `identity.users`), `assigned_kiosk_id` (uuid FK `kiosk_ops.kiosks`).
     - `scheduled_date` (date), `time_window` (text), `review_note` (text), `confirmed_at` (timestamptz).
     - `technician_report` (text), `rework_reason` (text), `handover_at` (timestamptz), `handover_by` (uuid), `seller_id` (uuid FK `identity.sellers`).
     - `refund_approved_by` (uuid), `refund_paid_by` (uuid), `refund_reference` (text).
     - Partial unique index ensuring only one in-flight active application per customer.
   - **Stored Procedure / Flow `flow.assign_kiosk`:** Lets an Admin assign or safely transfer a whole empty kiosk to one active, subscribed Seller. Per-slot Admin assignment is retired.
   - **Stored Procedure / Flow `flow.kiosk_application_handover`:** Executes whole-kiosk assignment, seller creation, active subscription, user role promotion, and audit log atomically.

2. **Module Ownership & Boundary Enforcement:**
   - **`KioskOps`:** Owns `kiosk_ops.kiosk_applications`, slot zone checks, technician job scheduling, and handover execution.
   - **`Payment`:** Owns payment order reservation and payOS webhook / reconciliation processing. For `KIOSK_APPLICATION` charges, payments transition the application to `PAID_REVIEW` without touching seller roles.
   - **`Identity`:** Owns user credentials and role promotion upon handover.
   - **`Notify`:** Owns installation photo attachments (`notify.attachments` with `owner_service='kiosk_ops'`, `owner_type='kiosk_application'`, `phase='EVIDENCE'`).
   - Cross-module operations delegate via module-owned services or SQL stored procedures in schema `flow`.

3. **Data Protection & Minimal Exposure:**
   - Bank account numbers are encrypted using `IDataProtectionProvider.CreateProtector("bank-account-v1")`.
   - Reads return masked suffixes only (e.g. `4567`). No raw bank details appear in public endpoints, technician endpoints, or audit logs.
   - Technicians receive only operational details (shop name, phone, contact email, address, coordinates, schedule window).

4. **PayOs Client Dev Mode & Loopback Support:**
   - Allow loopback HTTP return/cancel URLs (`http://localhost:*`, `http://127.0.0.1:*`, `http://[::1]:*`) only when explicit configuration flag `PAYOS_ALLOW_DEV_LOOPBACK` is enabled. Reject all non-loopback HTTP and userinfo in URLs.
   - Return/cancel URL queries never settle payment.
   - Honest HTTP 503 when payOS credentials are missing; HTTP 502 on upstream gateway or network errors.

5. **Legacy Route Closure:**
   - `POST /api/member/shop` returns HTTP 410 Gone for `CUSTOMER` users. Existing `SELLER` users calling the route receive their current session idempotently without creating duplicate sellers.

## State Transition Matrix

| Current State | Action | Next State | Authorized Roles | Pre-conditions / Notes |
|---|---|---|---|---|
| *(None)* | Apply | `AWAITING_PAYMENT` | CUSTOMER | Customer has no in-flight application; valid phone, address, coordinates, bank. |
| `AWAITING_PAYMENT` | Pay (Webhook / Reconcile) | `PAID_REVIEW` | SYSTEM / CUSTOMER | Exact amount verified; marks payment `SUCCEEDED`. |
| `AWAITING_PAYMENT` | Cancel | `CANCELLED` | CUSTOMER | Voluntary cancel of unpaid application. Marks payment `CANCELLED`. |
| `AWAITING_PAYMENT` | Reject | `REJECTED` | OPERATIONS_MANAGER | Unpaid rejection. Admin denied operational role. |
| `PAID_REVIEW` | Confirm Contact | `CONFIRMED` | OPERATIONS_MANAGER | Records contact review note; locks voluntary cancellation. Admin denied. |
| `PAID_REVIEW` | Cancel / Request Refund | `REFUND_PENDING` | CUSTOMER | Pre-confirmation voluntary cancellation routes to manual refund. |
| `PAID_REVIEW` | Reject | `REFUND_PENDING` | OPERATIONS_MANAGER | Paid application rejection routes to refund required. Admin denied. |
| `CONFIRMED` | Schedule | `SCHEDULED` | OPERATIONS_MANAGER | Assigns active technician, visit window, and eligible whole kiosk. Admin denied. |
| `CONFIRMED` | Platform Failure | `REFUND_PENDING` | OPERATIONS_MANAGER | Exceptional remediation for physical impossibility. Admin denied. |
| `SCHEDULED` | Start Visit | `INSTALLING` | TECHNICIAN | Assigned technician only. |
| `SCHEDULED` | Reschedule | `SCHEDULED` | OPERATIONS_MANAGER | Updates technician or date window. Admin denied. |
| `INSTALLING` | Submit Report | `AWAITING_HANDOVER` | TECHNICIAN | Assigned technician only; requires >= 1 photo evidence attachment. |
| `AWAITING_HANDOVER` | Require Rework | `INSTALLING` | OPERATIONS_MANAGER | Records rework reason. Admin denied. |
| `AWAITING_HANDOVER` | Handover | `ACTIVE` | OPERATIONS_MANAGER | Atomic transaction: updates kiosk address/pin, creates seller, 1-year subscription, promotes user to SELLER. Admin denied. |
| `CANCELLED` | Late Payment | `REFUND_PENDING` | SYSTEM / CUSTOMER | Never reactivates cancelled application; routes to refund required. |
| `REFUND_PENDING` | Approve Refund | `REFUND_PENDING` (Approved) | ADMIN | First admin approval recorded. |
| `REFUND_PENDING` | Confirm Payout | `REFUNDED` | ADMIN | Second admin confirms manual payout with proof attachment + transfer reference (separation of duty). |

## Consequences

- End-to-end annual kiosk lease and installation lifecycle is fully enforceable with auditability.
- No customer can bypass the physical installation and handover flow to obtain seller claims.
- Hardware is initially OFFLINE with genuine metadata; no fabricated sensors or online heartbeats.
- Separation of duties between Operations Managers, Technicians, and Financial Admins is strictly maintained.
