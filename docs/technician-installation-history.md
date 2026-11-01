# Technician installation history

`GET /api/staff/kiosk-installations` returns current installation jobs and handed-over
history for the authenticated Technician. Included statuses:

- `SCHEDULED`: scheduled installation.
- `INSTALLING`: installation or rework in progress.
- `AWAITING_HANDOVER`: report submitted, awaiting Operations Manager handover.
- `ACTIVE`: handed over successfully, display as "Da ban giao" in Technician history.

The query always restricts `assigned_technician_id` to the current actor. Other
Technicians' work, unassigned applications and cancelled applications remain excluded.
The detail API uses the same assignment restriction. This change expands only the
read list and grants no additional write, evidence, submit or handover permissions.

FE can filter the returned array by `status === 'ACTIVE'` for the history tab.
Keep an active job's detail readable. Do not call handover again to populate history.
No new route, request parameter or response field is required.

Deploy only `FloraBot.Api` and recycle its app pool. No database migration is needed
for this list change. Existing handed-over records appear after reloading the list.

The regression test in `TechnicianInstallationReadTests` exercises HTTP list/detail,
repeated reads, current statuses, handed-over history, foreign/unassigned exclusion
and the Technician role requirement. Run against a disposable local test database,
never the VPS.

## Interpreting a successful onboarding test

A `SUCCEEDED` payment, manager review/schedule, technician report with image evidence,
and successful handover are consistent with promotion to Seller and a one-year kiosk
lease. The lease starts at handover. Handover on 2026-11-01 is consistent with an
expiry date of 2027-11-01.

HTTP 200 and an uploaded model photo verify software state transitions, not physical
installation. Handover validates stored evidence and business conditions but does not
perform a real MQTT/relay/sensor cycle. Real installation/IoT requires separate evidence.

Use `scripts/verify-kiosk-handover.sql` for a read-only check of the concrete account,
assigned technician, kiosk, slot ownership, payment and lease on the intended database.
