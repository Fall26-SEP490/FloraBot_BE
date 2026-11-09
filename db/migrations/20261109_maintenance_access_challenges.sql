BEGIN;

CREATE TABLE IF NOT EXISTS kiosk_ops.maintenance_access_challenges (
  id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id) ON DELETE CASCADE,
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id) ON DELETE CASCADE,
  seller_id uuid NOT NULL REFERENCES identity.sellers(id) ON DELETE CASCADE,
  actor_id uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
  actor_role text NOT NULL CHECK (actor_role IN ('SELLER', 'SELLER_STAFF', 'TECHNICIAN')),
  task_id uuid REFERENCES kiosk_ops.staff_tasks(id) ON DELETE CASCADE,
  operation text NOT NULL CHECK (operation IN ('START', 'OPEN_NEXT', 'OPEN_FOR_CHECK', 'RETRY', 'REPAIR')),
  payload jsonb NOT NULL DEFAULT '{}'::jsonb,
  digest bytea NOT NULL CHECK (octet_length(digest) = 32),
  created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  expires_at timestamptz NOT NULL,
  failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0 AND failed_attempts <= 5),
  consumed_at timestamptz,
  CONSTRAINT maintenance_access_challenges_expiry_check CHECK (expires_at > created_at)
);

CREATE INDEX IF NOT EXISTS maintenance_access_challenges_pending_idx
  ON kiosk_ops.maintenance_access_challenges (kiosk_id, expires_at)
  WHERE consumed_at IS NULL;

CREATE INDEX IF NOT EXISTS maintenance_access_challenges_replacement_idx
  ON kiosk_ops.maintenance_access_challenges (actor_id, kiosk_id, slot_id);

COMMIT;
