CREATE TABLE IF NOT EXISTS kiosk_ops.mqtt_dispatches (
  cmd_id uuid PRIMARY KEY REFERENCES kiosk_ops.unlock_tokens(cmd_id),
  delivery_id uuid NOT NULL,
  attempted_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  next_attempt_at timestamptz NOT NULL,
  pubacked_at timestamptz,
  CHECK (next_attempt_at > attempted_at)
);
