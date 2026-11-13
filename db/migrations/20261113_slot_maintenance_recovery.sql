BEGIN;

ALTER TABLE kiosk_ops.slot_maintenance_sessions
  DROP CONSTRAINT slot_maintenance_sessions_stage_check,
  ADD CONSTRAINT slot_maintenance_sessions_stage_check
    CHECK (stage IN ('AwaitRemoval','AwaitLoading','NeedsInspection','Completed','Recovered'));

DROP INDEX kiosk_ops.slot_maintenance_active_slot_idx;
CREATE UNIQUE INDEX slot_maintenance_active_slot_idx
  ON kiosk_ops.slot_maintenance_sessions(slot_id)
  WHERE stage NOT IN ('Completed','Recovered');

COMMIT;
