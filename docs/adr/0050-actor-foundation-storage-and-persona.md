# 0050: Seven-actor identity foundation, storage roles, and persona boundaries

## Context
The product specification defines seven distinct human actors: Platform Administrator, Operations Manager, Technician, Shop Owner (Seller), Seller Staff, Kiosk Customer, and PreOrder Customer. Non-human principals (SYSTEM, KIOSK device credentials, AI service token) are technical service principals and must not be treated as human user roles.

Historically, the schema supported ADMIN, STAFF, SELLER, CUSTOMER, and SYSTEM. Legacy STAFF rows represent platform-operated staff without seller association. Walk-in kiosk customers must not be forced to register persistent accounts. Furthermore, introducing SELLER_STAFF and operational roles must not prematurely widen sensitive financial/administrative routes (such as seller wallets, bank accounts, or subscriptions).

## Decision

1. **Storage roles vs. Channel personas:**
   `identity.users.role` stores persistent identity roles: `ADMIN`, `STAFF` (legacy), `SELLER`, `CUSTOMER`, `SYSTEM`, `OPERATIONS_MANAGER`, `TECHNICIAN`, and `SELLER_STAFF`.
   Kiosk Customer and PreOrder Customer are channel-scoped personas rather than separate database roles:
   - Walk-in Kiosk Customers interact anonymously or through temporary receipt lookup tokens without database user registration.
   - Kiosk members authenticate via OTP bound to `CUSTOMER` (or `SELLER` buying as customer) and receive session tokens containing a `kiosk_id` claim, scoped strictly to the physical device.
   - PreOrder Customers authenticate via standard member sessions without a `kiosk_id` claim.
   No artificial eighth human role is added.

2. **Legacy compatibility:**
   Legacy `STAFF` rows are preserved without modification. They are never silently converted into `SELLER_STAFF` or assigned arbitrary seller tenants or regions.

3. **Authoritative seller membership:**
   Migration 019 tightens user constraints: `CHECK ((role IN ('SELLER', 'SELLER_STAFF')) = (seller_id IS NOT NULL))`. Both `SELLER` and `SELLER_STAFF` require an authoritative, non-null `seller_id`. All other roles (`ADMIN`, `STAFF`, `OPERATIONS_MANAGER`, `TECHNICIAN`, `CUSTOMER`, `SYSTEM`) must have `seller_id IS NULL`. Missing tenant records for `SELLER_STAFF` are rejected at authentication, refresh, and session resolution.

4. **Portal realm separation:**
   - Platform login (`/api/auth/admin/login`) admits only `ADMIN`, `STAFF`, `OPERATIONS_MANAGER`, and `TECHNICIAN`.
   - Ordinary portal login (`/api/auth/login`) admits only `CUSTOMER`, `SELLER`, and `SELLER_STAFF`.
   - Cross-realm login attempts return 401 Unauthorized without setting session cookies.

5. **Strict policy boundaries:**
   - `SameSeller` is not widened to `SELLER_STAFF`. Owner endpoints (wallet, bank details, subscriptions, product creation/editing) remain restricted to `ADMIN` and `SELLER`.
   - Operational endpoints (`Staff` policy) are not widened to new platform roles. `OPERATIONS_MANAGER`, `TECHNICIAN`, and `SELLER_STAFF` are default-denied across business, financial, and operational routes until explicit scoped assignment endpoints are introduced.
   - `Account` policy admits all seven human roles for `/api/auth/me` and session inspection, rejecting device sessions with `kiosk_id`.

6. **Credential provisioning:**
   `flow.provision_portal_user` is extended to admit already-created `OPERATIONS_MANAGER`, `TECHNICIAN`, and `SELLER_STAFF` accounts while preserving bcrypt cost 12, '!unprovisioned' safeguards, and audit logging. No new general user CRUD endpoint is introduced.

7. **Authoritative session validation and refresh semantics:**
   Old access claims are revoked when role or tenant changes occur: `OnTokenValidated` authoritatively validates active status, current role, current seller ID, and credential version on every request.
   Token refresh (`/api/auth/refresh`) single-use resolves the CURRENT eligible identity and scope from PostgreSQL (issuing new tokens reflecting current role and seller membership, never resurrecting old privileges), while account deactivation or credential changes reject refresh with 401 Unauthorized.

## Consequences
- The authentication and identity foundation safely admits all seven human actors.
- Existing owner, staff, and customer capabilities remain backward-compatible and protected against privilege escalation.
- Scoped domain permissions (e.g., ticket assignment, shift management, kiosk technical operations) are deferred to subsequent scoped increments.
