# Account Management & Operations Architecture

## Overview

This document specifies the backend account management endpoints, authorization matrix, soft status lifecycle, sensitive banking data protection, and the seller onboarding constraint.

## 1. 16 Bounded New Routes & Capability Matrix

The 16 newly delivered account management endpoints integrate with existing compatible hierarchy routes (such as Manager/Tech creation and Active Seller status toggle) without disrupting prior contracts:

| # | Method | Endpoint | Authorization | Description |
| --- | --- | --- | --- | --- |
| 1 | `GET` | `/api/admin/managers/{id}` | `Admin` | Detailed profile of an Operations Manager (`AccountDetailResponse`) |
| 2 | `PUT` | `/api/admin/managers/{id}` | `Admin` | Update Manager full name, phone, or address (actor active check `FOR SHARE`, target row lock `FOR UPDATE`) |
| 3 | `GET` | `/api/operations/technicians/{id}` | `OperationsManager` | Detailed profile of a Technician (`AccountDetailResponse`) |
| 4 | `PUT` | `/api/operations/technicians/{id}` | `OperationsManager` | Update Technician full name, phone, or address (`FOR UPDATE` target lock) |
| 5 | `POST` | `/api/operations/customers` | `OperationsManager` | Create Customer (validated bcrypt-12, points 0, status ACTIVE, conflict 409, no fake Google/OTP) |
| 6 | `GET` | `/api/operations/customers` | `OperationsManager` | Paged & filtered Customer directory (`name`, `email`, `search` trimmed <= 100, `status` filter) |
| 7 | `GET` | `/api/operations/customers/{id}` | `OperationsManager` | Detailed profile of a Customer (`AccountDetailResponse`) |
| 8 | `PUT` | `/api/operations/customers/{id}` | `OperationsManager` | Update Customer full name, phone, or address (email, role, loyalty points strictly preserved) |
| 9 | `PATCH` | `/api/operations/customers/{id}/status` | `OperationsManager` | Update Customer status (`ACTIVE`, `LOCKED`, `DISABLED`); same-state returns 409 |
| 10 | `PUT` | `/api/operations/sellers/{sellerId}` | `OperationsManager` | Update Seller shop name, phone, address (preserves verified `contactEmail`, package, bank, kiosk) |
| 11 | `GET` | `/api/operations/sellers/{sellerId}/accounts` | `OperationsManager` | Inspect safe owner account DTOs for a shop (`SellerAccountResponse`) |
| 12 | `PUT` | `/api/operations/sellers/{sellerId}/accounts/{userId}` | `OperationsManager` | Edit Seller owner name, phone, address (scoped strictly to `Role == SELLER` and matching `sellerId`) |
| 13 | `GET` | `/api/admin/accounts` | `Admin` | Paged directory across all 6 human roles with masked bank (excludes legacy `STAFF`) |
| 14 | `GET` | `/api/admin/accounts/{userId}/profile` | `Admin` | Comprehensive profile for any of the 6 roles; separates personal bank from shop bank |
| 15 | `POST` | `/api/admin/accounts/{userId}/bank/reveal` | `Admin` | Reveal personal bank (`personal-bank-account-v1`), audited, mandatory reason (5..1000) |
| 16 | `POST` | `/api/admin/sellers/{sellerId}/bank/reveal` | `Admin` | Reveal shop bank (`bank-account-v1`), audited, mandatory reason (5..1000) |

## 2. Role Hierarchy & Mutation Boundaries

FloraBot enforces strict role management boundaries:

```mermaid
graph TD
    Admin[Admin] -->|Creates & Manages| OM[Operations Manager]
    Admin -->|Inspects Profiles & Reveals Bank| AllRoles[All 6 Roles]
    OM -->|Creates & Manages| Tech[Technician]
    OM -->|Creates & Manages| Cust[Customer]
    OM -->|Updates Shop & Owner Account| Seller[Seller]
    Seller -->|Creates & Manages| Staff[Seller Staff]

    subgraph Boundaries [Strict Prohibitions]
        OM -.x|Cannot Modify| Admin
        OM -.x|Cannot Modify| OM
        OM -.x|Cannot Modify| Staff
        OM -.x|Cannot View Full Bank| BankData[Unmasked Banking]
        AllRoles -.x|Cannot Promote Generic DTO| Seller
    end
```

- **Admin Privilege**: Manages Operations Managers. Has global visibility across all 6 human roles (`ADMIN`, `OPERATIONS_MANAGER`, `TECHNICIAN`, `SELLER`, `SELLER_STAFF`, `CUSTOMER`) for manual transfer and compliance, including authorized bank disclosure.
- **Operations Manager Scope**: Restricted to platform operations (Technicians, Customers, Seller shops, and Seller owner accounts). Cannot create, modify, or lock Admins, fellow Operations Managers, or Seller Staff. Cannot access full banking credentials.
- **Seller Scope**: Sellers manage only Seller Staff tied to their own `sellerId`.
- **Target Role Validation**: Every mutation endpoint enforces target role checks (`Role == "OPERATIONS_MANAGER"`, `Role == "TECHNICIAN"`, `Role == "CUSTOMER"`, or `Role == "SELLER" && SellerId == sellerId`). Foreign or invalid role mutations return `404 Not Found`.

## 3. Soft Disable & Immediate Token Revocation

- Hard deletion of user accounts or operational histories is prohibited.
- Accounts support three explicit statuses: `ACTIVE`, `LOCKED`, `DISABLED`.
- Concurrency and target locking: New customer status updates and the seller shop moderation flow acquire a row-level lock (`SELECT ... FOR UPDATE`) inside a database transaction and append an immutable audit record (`flow.audit`) before committing.
- **Immediate Revocation**: The JWT authentication middleware re-verifies user status on every request (`OnTokenValidated` queries `identity.users` for `Status == 'ACTIVE'`). When an account is moved to `LOCKED` or `DISABLED`, active sessions fail authorization with `401 Unauthorized` on their very next request.

## 4. Sensitive Banking Protection

Banking data across FloraBot is partitioned by ownership and protected via ASP.NET Data Protection:

1. **Personal Bank Account**:
   - Stored in `identity.user_profiles` (`account_encrypted`, `account_last4`, `bank_name`, `account_holder`).
   - Protected with purpose: `"personal-bank-account-v1"`.
   - Masked in all directory listings and profile summaries (returns `BankName`, `AccountHolder`, `AccountLast4`).
   - Disclosed exclusively via `POST /api/admin/accounts/{userId}/bank/reveal`.
2. **Seller Shop Payout Bank**:
   - Stored in `identity.sellers` (`bank_account_no_enc`, `bank_name`, `bank_holder`).
   - Protected with purpose: `"bank-account-v1"`.
   - Distinct from personal banking: Admin profiles for Sellers present `PersonalBank` and `ShopBank` in clearly separate DTO structures.
   - Disclosed exclusively via `POST /api/admin/sellers/{sellerId}/bank/reveal`.
3. **Audit Before Disclosure & No-Store**:
   - Reveal endpoints require a non-empty `reason` between 5 and 1000 characters.
   - An audit row is persisted (`ADMIN_PERSONAL_BANK_REVEALED` or `ADMIN_SELLER_BANK_REVEALED`) *prior* to disclosure.
   - The audit payload logs the reason and actor, but **never** plaintext banking secrets or ciphertext.
   - All sensitive responses return `Cache-Control: no-store`.
   - Missing bank configuration returns `404 Not Found`; decryption failures return `409 Conflict` (never `500 Internal Server Error`).

## 5. Seller Onboarding Exception

- Customer accounts cannot be transitioned or promoted to `SELLER` via administrative DTO mutation.
- Seller status and privileges are acquired exclusively through the verified annual onboarding lifecycle (package selection, advance subscription payment, kiosk assignment, evidence review, and physical handover).
- Administrative and Operations endpoints modify only identity and contact fields (`FullName`, `Phone`, `Address`), preserving package lifecycle, expiration timestamps, and role entitlements.

## 6. System Boundaries & Limitations

- **Banking Verification**: Banking fields undergo strict bounded syntactic checks (1..120 chars for bank/holder, 4..34 ASCII letters/digits for account number), not direct integration with bank lookup APIs or external account holder verification.
- **Manual Money Transfer**: Disclosed bank account numbers support manual transfers by authenticated administrators; the backend does not initiate direct bank-to-bank disbursements and protects stored bank account numbers with ASP.NET Data Protection.
- **Provider Mocking**: In automated test suites, email delivery and identity provider validations use local loopback mocks; no real emails are dispatched and no real third-party services are invoked.
- **No Hard Deletion**: All user accounts, seller configurations, and audit events are permanent. Deactivation is exclusively soft (`LOCKED` or `DISABLED`), retaining historic records for auditing and reconciliation.
- **Regional Scoping**: Platform operations directories for Technicians maintain global visibility; regional technician ownership boundaries are outside the scope of this increment.
## Acceptance evidence

The independently executed local suite passed59/59 tests:16 account-management tests,5 hierarchy regressions,18 personal-profile regressions,11 seller moderation tests, and9 bank/withdrawal regressions. It uses HTTP TestServer and a separate PostgreSQL database. It does not verify real bank ownership, provider delivery, UI or VPS deployment.

Touched C# files pass `dotnet format --verify-no-changes`. The repository-wide module-boundary test has one pre-existing failure in Catalog/chat; none of the new account-management classes appears in its violation list. No migrations, FE, chat or payment execution flows were changed.
