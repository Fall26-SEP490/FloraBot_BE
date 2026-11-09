BEGIN;

-- One stable credential pair per explicitly enrolled physical kiosk. The only
-- persisted secret is an IDataProtection ciphertext, bound to kiosk+hardware.
-- Existing/manual devices are not enrolled or rotated by this migration.
CREATE TABLE kiosk_ops.esp32_provisioning_jobs (
  kiosk_id uuid PRIMARY KEY REFERENCES kiosk_ops.kiosks(id) ON DELETE CASCADE,
  hardware_id text NOT NULL UNIQUE CHECK (hardware_id ~ '^[A-Za-z0-9_-]{1,64}$'),
  protected_secrets text NOT NULL CHECK (length(protected_secrets) BETWEEN 1 AND 16384),
  state text NOT NULL DEFAULT 'PENDING' CHECK (state IN ('PENDING','PROVISIONING','READY','FAILED')),
  error_code text CHECK (error_code ~ '^[A-Z0-9_]{1,80}$'),
  attempts integer NOT NULL DEFAULT 0 CHECK (attempts BETWEEN 0 AND 5),
  next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  lease_token uuid,
  lease_expires_at timestamptz,
  ready_at timestamptz,
  created_by uuid NOT NULL,
  created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  CHECK ((state = 'READY') = (ready_at IS NOT NULL)),
  CHECK ((state = 'PROVISIONING' AND lease_token IS NOT NULL AND lease_expires_at IS NOT NULL)
      OR (state <> 'PROVISIONING' AND lease_token IS NULL AND lease_expires_at IS NULL)),
  CHECK (state <> 'PENDING' OR attempts < 5)
);
CREATE INDEX esp32_provisioning_pending_idx
  ON kiosk_ops.esp32_provisioning_jobs(next_attempt_at,kiosk_id)
  WHERE state IN ('PENDING','PROVISIONING');

-- A manual retry resets scheduling, never the identity or stored credentials.
CREATE OR REPLACE FUNCTION kiosk_ops.guard_esp32_provisioning_credentials()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.kiosk_id IS DISTINCT FROM OLD.kiosk_id
     OR NEW.hardware_id IS DISTINCT FROM OLD.hardware_id
     OR NEW.protected_secrets IS DISTINCT FROM OLD.protected_secrets THEN
    RAISE EXCEPTION 'ESP32_PROVISIONING_IDENTITY_IMMUTABLE' USING ERRCODE='22023';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER esp32_provisioning_credentials_immutable
  BEFORE UPDATE ON kiosk_ops.esp32_provisioning_jobs
  FOR EACH ROW EXECUTE FUNCTION kiosk_ops.guard_esp32_provisioning_credentials();

COMMIT;
