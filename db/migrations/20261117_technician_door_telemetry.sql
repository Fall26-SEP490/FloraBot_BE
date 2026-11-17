-- Diagnostic observations are optional for kiosks still publishing the v1 snapshot.
ALTER TABLE kiosk_ops.flower_presence
  ADD COLUMN IF NOT EXISTS door_states integer[],
  ADD COLUMN IF NOT EXISTS door_cycles integer[],
  ADD COLUMN IF NOT EXISTS door_cycle_times bigint[];
