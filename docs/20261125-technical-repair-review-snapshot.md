# Technical repair review uses submission snapshot

Apply `20261125_technical_repair_review_snapshot.sql` after
`20261119_technical_repair_recovery.sql` and before deploying the matching API.
This is a forward-only migration; do not edit or rerun the earlier migration.

For new technical-repair submissions, BE still requires the kiosk to be online,
the heartbeat and authenticated flower-presence observation to be fresh, the
slot's sensors to be verified, the observed state to match slot contents, and
the repair door/recovery evidence to be complete. BE captures those verified
facts against the submitted task version in
`kiosk_ops.technical_repair_readiness_snapshots`.

Manager acceptance uses that submission snapshot instead of requiring another
heartbeat and flower-presence observation younger than 45 seconds. It still
rechecks the task/incident/slot relationship, unchanged slot status, bouquet,
seller ownership and relay, current sensor capability, absence of holds or an
active bouquet order, open maintenance sessions and pending/open door commands,
the submitted version, and the technician's required evidence. Fault-door
repair also continues to require the same closed door command and applied MQTT
receipt. A technician resubmission replaces the task's snapshot with evidence
for the new submitted version.

Already-submitted tasks that predate this migration have no trusted submission
snapshot. They keep the previous live-readiness check; do not manufacture a
snapshot from telemetry received after their submission.

The readiness endpoints continue to call `flow.technical_repair_blocker`.
For a newly submitted task, they report whether the saved evidence and current
non-telemetry safety conditions still permit Manager acceptance.
