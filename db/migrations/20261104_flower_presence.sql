-- Latest authenticated sensor snapshot. This table never drives order completion.
CREATE TABLE IF NOT EXISTS kiosk_ops.flower_presence (
  kiosk_id uuid PRIMARY KEY REFERENCES kiosk_ops.kiosks(id) ON DELETE CASCADE,
  event_id uuid NOT NULL,
  observed_at bigint NOT NULL CHECK(observed_at>0),
  states integer[] NOT NULL CHECK(array_length(states,1)=4 AND array_lower(states,1)=1
    AND states <@ ARRAY[-1,0,1] AND array_position(states,NULL) IS NULL),
  received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
