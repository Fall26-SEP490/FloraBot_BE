# Repair recovery and revenue reports — 2026-11-19

Apply the existing migrations in filename order, including
`20261119_revenue_views.sql` and `20261119_technical_repair_recovery.sql`, before
deploying this API and its matching FE/contracts. These are forward migrations;
do not rerun the technical-repair migration against an already migrated database.

## Technical incidents

The existing FAULT/door-repair path remains authoritative. A LOCKED slot still
requires maintenance-session recovery; reporting a repair does not unlock it.
The task readiness endpoints expose the blocking reason, session and command IDs
without unlock credentials. Conflicts return HTTP 409 with a machine-readable
`code` and a Vietnamese explanation. Migration
`20261125_technical_repair_review_snapshot.sql` supersedes the review-time
telemetry behavior described below.

After recovery, an assigned technician can submit a report for an OPEN incident
without fabricating another door cycle. Submission requires a matching recovered
session since task creation, a task-bound evidence attachment uploaded by that
technician after recovery, verified sensor capabilities, a current heartbeat,
fresh presence matching the slot contents, and no competing session or command.
Both the observation time and receipt time must follow recovery (or door closure
for the door-repair path); a delayed pre-recovery packet cannot prove recovery.
The submission-time sensor and slot evidence is captured. Manager acceptance
may happen later without a new 45-second telemetry sample, provided the slot
state and other safety conditions remain valid. The assigned kiosk manager must
review and accept the submitted report, which closes the incident and completes
the task. Returned reports can be resubmitted with a new operation ID; old
retries remain idempotent. Refund processing remains separate.

## Revenue

`GET /api/admin/revenue/flowers` accepts inclusive `from`/`to` dates in Vietnam
time, optional `sellerId`/`kioskId`, and `page`/`pageSize` (maximum 100). Successful
charges and refunds use the order payment allocation view and their money event
date. Page rows and full-period totals come from one SQL statement. Money is
returned as decimal strings. This measures cash receipts, not order GMV.

`GET /api/admin/revenue/rental` reports subscription ledger revenue separately;
seller/kiosk filters are rejected because those ledger rows lack that scope.
Historical kiosk selectors use order/subscription ownership, not today's kiosk
owner. Subscription responses label a lease TERMINATED only when its expiry
update matches a completed termination, and include its termination reason.
The FE shop selector searches by name and pages through 25 shops at a time,
preserving its selected shop while browsing other result pages.

## Validation

Eight integration cases passed against disposable PostgreSQL/Valkey/RabbitMQ:
130 daily rows with stable full-period totals across pages, Vietnam midnight
boundaries, historical kiosk ownership, authorization/version checks, recovered
repair evidence/sensor checks (including delayed pre-recovery observations),
replay, manager return/resubmission and acceptance, legacy incomplete repair
protection, shared-checkout allocation, cross-month successful refunds excluding
pending refunds, separate rental ledger revenue, 105-shop pagination/search,
and the existing seller report test. These simulate device
events; they do not certify physical door hardware or a production deployment.
