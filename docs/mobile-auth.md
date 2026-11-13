# Native Flutter authentication

This additive change supports TECHNICIAN, SELLER_STAFF, SELLER and CUSTOMER native sessions. It does not change browser cookie login or introduce a PREORDER identity role. The Flutter consumer is the sibling FloraBot_Mobile repository, branch feature/flutter-role-workspaces.

## Contract

All paths below start with /api/mobile/auth and use JSON over HTTPS. Responses are no-store; the existing auth rate limiter applies.

| POST path | Body | Result |
|---|---|---|
| /login | email, password, role | accessToken, refreshToken, expiresAt, user (existing SessionResponse shape) |
| /refresh | refreshToken | same response; previous refresh consumed atomically |
| /logout | refreshToken | 204; consumes only native refresh |
| /password | currentPassword, newPassword | Account-authorized, current password checked; 204 then re-login |

Role is an explicit login constraint, not an authority granted by the request. Active identity and exact DB role must match. Seller/Staff require an existing seller association. JWTs bind credential_version and use existing tenant/authorization checks. Password changes compare-and-swap the old hash; previous access and refresh fail the existing credential-version check.

Refresh credentials are cryptographically random 32 bytes, 64 hex characters, stored for seven days in Redis using mobile:refresh:SHA256(token). Browser refresh keys remain refresh:SHA256(token). Neither realm can consume the other. StringGetDeleteAsync provides one-time consumption; this implementation needs the same Redis/Valkey GETDEL support already required by browser refresh. CUSTOMER access expires after 10 minutes, the other supported actors after 15 minutes.

Native access stays in memory and refresh in OS secure storage. No device apiKey/MQTT credentials, password persistence, cookie extraction or bearer token in navigation URLs. The app does not auto-replay mutations on transport failure. Mobile logout removes local state first, then attempts native refresh revocation. An offline logout cannot promise immediate server revocation; access JWTs follow the existing access-token lifetime.

Temporary network/5xx failure during refresh does not prove revocation and must not clear the app session. A consumed refresh whose response is lost cannot be recovered by replay: the next use will fail. If recovery without re-login is required, design a separate bounded, securely bound idempotency/grace mechanism; do not make refresh tokens reusable without controls.

## Task detail addition

StaffTaskDetail adds nullable version/status at the end of the response. Existing history/evidence consumers keep working; Mobile uses the current server version for transitions/evidence. No database migration is needed.

## Validation and rollout

```powershell
dotnet test tests/FloraBot.Api.Tests/FloraBot.Api.Tests.csproj --filter "FullyQualifiedName~MobileAuthPolicyTests|FullyQualifiedName~MobileRefreshTests"
$output = Join-Path (Get-Location) 'artifacts/mobile-openapi.json'
dotnet run --project src/FloraBot.Api -- --export-openapi $output
```

Use the existing schema-only export workflow; no database initialization or running-device calls are required. The exported schema includes MobileAuth routes and the task-detail addition. FE/Kios regenerate contracts using their normal workflow if they consume the addition; do not hand-edit generated types.

Deploy using the team's existing reviewed deployment process, then verify four test accounts on staging: wrong role/password, disabled identity, owner/staff boundaries, credential change, refresh rotation/replay, cross-realm rejection, offline recovery, and task version conflict. Unit tests use an in-memory Redis proxy and do not prove DB/Redis deployment or physical-kiosk behavior. No production deployment or live DB mutation was performed as part of this implementation.

Existing maintenance report-issue does not by itself guarantee incident/task creation. The native app does not bypass that gap, reset expired maintenance sessions, grant Technician access as Manager, or fabricate successful door events. Closing that incident workflow requires a separate domain change and staging acceptance.
