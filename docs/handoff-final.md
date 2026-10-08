# FloraBot final handoff - 2026-10-09

## Delivery scope

- FE: Next.js public website, member authentication/account, custom flower requests,
  available-stock ordering, saved guest receipts, Admin/Seller/Staff workspaces.
- BE: authenticated API and gateway, PostgreSQL migrations through 017, member/shop
  identity, staff assignment/reassignment, stock, incident resolution and private evidence.
- Kios: independent kiosk PWA with guest/member purchase and pickup journeys.
- AI: independent FastAPI advisor service with its own Docker verification workflow.
- Mobile: repository placeholder only. No mobile application is implemented.

Repositories: [FE](https://github.com/Fall26-SEP490/FloraBot_FE),
[BE](https://github.com/Fall26-SEP490/FloraBot_BE),
[Kios](https://github.com/Fall26-SEP490/FloraBot_Kios),
[AI](https://github.com/Fall26-SEP490/FloraBot_AI),
[Mobile](https://github.com/Fall26-SEP490/FloraBot_Mobile).

## Verified locally

- FE lint, unit tests (3), production build; public browser/accessibility tests (23).
- Real FE/Gateway/API/PostgreSQL integration: 7 passed. Cloudinary outbound HTTP is
  simulated by `tests/FloraBot.BrowserHost`; these are not live-provider tests.
- API tests: 237 passed, 1 optional actual-MQTT test skipped. Gateway: 11 passed.
- Kios: lint/typecheck/build and 31 browser tests passed.
- AI: Docker test target passed lint/format checks; fresh container pytest: 31 passed,
  one upstream Starlette/AnyIO deprecation warning.
- Deployed local stack: 9 browser tests passed in the preceding verification run.
- Portal initial final run: 100 passed, 1 mobile guest-history navigation timeout.
  The failed scenario passed all 6 isolated repetitions. The final full portal rerun
  passed 101/101; post-format lint and production build passed too. The first timeout
  remains recorded rather than being presented as an uninterrupted passing run.

## Local entry points

The composed stack is at http://localhost:8088/.
Customer/shop login: `/dang-nhap` (`/login` redirects there).
Admin/Staff login: `/admin/login`; workspaces: `/admin`, `/seller`, `/staff`.
Kiosk: `/kiosk/`. Admin API documentation: `/openapi` -> `/openapi/v1.json`.
See [account roles](account-roles.md) and [backend setup](../README.md).

For integration verification, check out FE, BE and Kios as sibling directories,
install dependencies, start local PostgreSQL/Valkey, then run `pnpm test:integration`
from FE. This recreates only the disposable `florabot_browser_tests` database.
The BrowserHost refuses other database targets.

## Remaining work and limits

1. Implement the Mobile application; the five-repository split does not imply five
   completed applications.
2. Verify Cloudinary private upload/read with real credentials and provider settings.
   Integration is implemented, but simulated transport does not prove live delivery.
   Provider reference: https://cloudinary.com/documentation/upload_images#authenticated_assets
3. Verify live payment, email/SMS, MQTT and physical kiosk pickup against actual
   provider/device environments. Local tests do not certify these operations.
4. Review GitHub Actions after push; local passing checks do not prove remote CI passed.
5. ERD files in `docs/diagrams` are dated snapshots from 2026-10-08; regenerate them
   from current schema before treating them as a complete model of migrations 011-017.
6. Automated accessibility checks cover tested pages/states; they do not constitute
   a complete manual WCAG audit of every user journey.

## Git attribution

Commits use the five local identities explicitly supplied by the project owner:
thanhloinguyen18, Thanhtu18, trandinhphong0310, TaHoang715, vochitam112004.
For vochitam112004, the previously linked GitHub noreply email is used:
`178009735+vochitam112004@users.noreply.github.com`.
Local author/committer metadata does not mean authenticating to five GitHub accounts.
No coauthor trailers or history rewrites are required.

Local secrets, credentials, database backups, build output and verification logs are
excluded from commits. Completion of this handoff is not a claim that the original
full product scope, live providers or Mobile are production-ready.
