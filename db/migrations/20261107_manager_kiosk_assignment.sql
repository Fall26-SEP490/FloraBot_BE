BEGIN;

-- A kiosk has one Operations Manager at a time. Resolve any pre-existing
-- duplicate assignments explicitly before running this migration.
DO $$
BEGIN
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.manager_kiosks
    GROUP BY kiosk_id HAVING count(*) > 1
  ) THEN
    RAISE EXCEPTION 'Resolve kiosks assigned to multiple managers before applying this migration';
  END IF;
END $$;

CREATE UNIQUE INDEX IF NOT EXISTS uq_manager_kiosks_one_manager
  ON kiosk_ops.manager_kiosks(kiosk_id);

COMMIT;
