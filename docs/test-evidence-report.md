# Test Evidence Report: Staff Role Split Migration

## 1. Overview and Delivered Scope

In accordance with architectural decisions and coordinator guidance, this worktree implements the retirement of legacy `STAFF` and splits existing task business flows into four specialized roles:
- **`TECHNICIAN`**: Field technicians assigned to kiosk hardware and dispensing incident tasks (`INCIDENT`).
- **`OPERATIONS_MANAGER`**: Regional operations supervisors coordinating kiosk incidents and assigning tasks to technicians within mapped kiosks (`kiosk_ops.manager_kiosks`).
- **`SELLER_STAFF`**: Dedicated shop delivery staff assigned to inventory restocking (`DELIVERY`) within their own shop's rented slots.
- **`SELLER`**: Shop owners coordinating delivery tasks and managing shop delivery personnel.

These join `ADMIN` and `CUSTOMER` to form the six authenticated human roles in FloraBot.

### Key Architectural and Security Guarantees
1. **Legacy STAFF Retirement & History Preservation**:
   - Database storage enum retains `STAFF` for foreign key integrity and audit logs.
   - Legacy rows in `identity.users` and `kiosk_ops.staff_tasks` are strictly preserved without deletion or re-assignment.
   - Runtime authentication (`/api/auth/admin/login`, `/api/auth/login`), `OnTokenValidated`, token refresh (`/api/auth/refresh`), `/api/auth/me`, and credential provisioning (`flow.provision_portal_user`) strictly reject `STAFF` with `401 Unauthorized` / exceptions.
2. **Operations Manager Scope (`kiosk_ops.manager_kiosks`) & Fail-Closed Enforcement**:
   - Minimal explicit mapping table `kiosk_ops.manager_kiosks` defines authorized kiosks per manager.
   - Managers with zero mapped kiosks fail closed: incident queue returns `[]`, technician directory returns `[]`, and task assignment returns `42501` / `403 Forbidden`.
   - Security Fix: Replay validation in `flow.assign_staff_task` rechecks current caller scope and qualifications before returning on idempotent replays, ensuring that revoking a manager's kiosk scope blocks subsequent replays of previous assignment IDs.
   - `/api/operations/staff` provides access to the active technician pool for mapped kiosks pending regional workforce management.
3. **Role & Kind Enforcements**:
   - `TECHNICIAN` cannot stock flowers (`403 Forbidden`), cannot access seller financial APIs or wallets (`404`/`403`), and cannot access delivery tasks.
   - `SELLER_STAFF` cannot resolve incidents (`403 Forbidden`), cannot access cross-tenant data, and cannot assign tasks.
   - Operations Manager has no financial route access (refunds, withdrawals, etc. remain Admin-only).
   - Sellers can only assign `DELIVERY` tasks to active `SELLER_STAFF` belonging to their own `seller_id`.

---

## 2. Changed Files Summary

- `db/migrations/020_staff_role_split.sql`: Forward migration adding `kiosk_ops.manager_kiosks`, updated `flow.assign_staff_task`, `flow.transition_staff_task`, `flow.reassign_staff_task`, `flow.staff_stock_bouquet`, and `flow.staff_resolve_incident`.
- `docs/staff-role-split-contract.md`: Exact API contract specification documenting routes, policies, query parameters, and paginated `StaffTaskPage` DTOs for the frontend worker.
- `src/FloraBot.Api/Program.cs`: Updated authentication policies (`StaffWorker`, `OperationsManager`, `Technician`, `SellerStaff`, `Account`) and token revocation guards.
- `src/FloraBot.Api/Auth/AuthEndpoints.cs`: Disallowed `STAFF` login, refresh, and session inspection.
- `src/FloraBot.Api/Modules/Identity/StaffIdentityRead.cs`: Read methods for active technicians and seller staff.
- `src/FloraBot.Api/Modules/KioskOps/StaffTasks.cs`: Operations Manager, Seller, and split Staff routes, paginated task listings, assignment, transition, and reassignment.
- `src/FloraBot.Api/Modules/KioskOps/StaffTaskAccess.cs`: Multi-role scope verification and manager kiosk resolution.
- `src/FloraBot.Api/Modules/KioskOps/StaffTaskDetails.cs`: Scoped detail inspection routes for `/api/operations/*` and `/api/sellers/{sellerId}/*`.
- `src/FloraBot.Api/Modules/KioskOps/StaffStock.cs`: Restricted restocking to `SellerStaff` with tenant matching.
- `src/FloraBot.Api/Modules/KioskOps/StaffIncidentResolution.cs`: Restricted incident resolution to `Technician`.
- `src/FloraBot.Api/Modules/Notify/StaffEvidence.cs`: Scoped evidence uploads and reads for `OperationsManager` and `Merchant`.
- `src/FloraBot.Api/Modules/Notify/StaffTaskHistory.cs`: Deterministic audit log ordering.
- `src/FloraBot.Api/Modules/Ordering/AdminIncidentsRead.cs`: Added scoped `/api/operations/incidents` endpoint with fail-closed kiosk check.
- `tests/FloraBot.Api.Tests/SplitRoleTaskLifecycleTests.cs`: Comprehensive lifecycle and scope regression test suite.
- `tests/FloraBot.Api.Tests/CustomEndpointAccessTests.cs`: Updated custom HTTP endpoint authorization matrix.
- `tests/FloraBot.Api.Tests/LoginRealmTests.cs`: Realm enforcement and legacy staff token revocation regressions.
- `tests/FloraBot.Api.Tests/ProvisioningTests.cs`: Updated provisioning test matrix to verify rejection of legacy staff.
- `tests/FloraBot.Api.Tests/StaffTaskTests.cs`, `StaffIncidentResolutionTests.cs`, `PrivateEvidenceTests.cs`: Adapted existing test fixtures to new roles while preserving full assertions.

---

## 3. Verification Execution and Results

### Code Formatting
```powershell
dotnet format FloraBot.slnx --verify-no-changes
```
**Result**: 0 files changed; exit code 0. Passed.

### Automated Test Runs
1. **`SplitRoleTaskLifecycleTests`**:
   - `TechnicalIncidentLifecycleAndScopeEnforcement`: PASSED
   - `DeliveryTaskLifecycleAndTenantScopeEnforcement`: PASSED
   - `ManagerScopeRevokedDeniesReplayAndFailsClosed`: PASSED
   - `MigrationPreservesLegacyStaffRowsAndHistoryAndDeniesRuntimeAccess`: PASSED
   - Total: 4 Passed, 0 Failed. Duration: 19 s.

2. **Endpoint Access & Module Boundary Verification**:
   - `CustomEndpointAccessTests` (4 tests): PASSED
   - `ModuleBoundaryTests` (2 tests): PASSED
   - Total: 6 Passed, 0 Failed.

3. **Domain Task & Security Regressions**:
   - `StaffTaskTests` (2 tests): PASSED
   - `StaffIncidentResolutionTests` (2 tests): PASSED
   - `LoginRealmTests` (10 tests): PASSED
   - `PrivateEvidenceTests` (5 tests): PASSED
   - `ProvisioningTests` (15 tests): PASSED
   - Total: 34 Passed, 0 Failed.

## Coordinator verification scope
Manager scope uses explicit kiosk mappings; regional workforce/shift management and complete hardware recovery are not implemented. Coordinator full suite before the two final regression additions: API273 passed /1 actual MQTT skipped; Gateway11 passed. Final targeted evidence is maintained in the workspace checkpoint, including any unsuccessful runs. No live payments or hardware acceptance is claimed.

