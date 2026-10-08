-- Transport state only; original flow functions remain the business authority.
CREATE TABLE IF NOT EXISTS kiosk_ops.mqtt_inbox (
  event_id uuid PRIMARY KEY,
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  cmd_id uuid REFERENCES kiosk_ops.unlock_tokens(cmd_id),
  event text NOT NULL CHECK (event IN ('ACK','OPENED','CLOSED','FAILED','HEARTBEAT')),
  payload_hash text NOT NULL,
  occurred_at timestamptz NOT NULL,
  received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  processed_at timestamptz,
  disposition text NOT NULL DEFAULT 'PENDING' CHECK (disposition IN ('PENDING','APPLIED','IGNORED','REJECTED')),
  CHECK ((event='HEARTBEAT') = (cmd_id IS NULL)),
  CHECK ((disposition='PENDING') = (processed_at IS NULL))
);
CREATE INDEX IF NOT EXISTS mqtt_inbox_pending ON kiosk_ops.mqtt_inbox(received_at,event_id) WHERE disposition='PENDING';
