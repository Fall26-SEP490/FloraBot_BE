BEGIN;
CREATE TABLE IF NOT EXISTS notify.communication_deliveries (
    delivery_id text PRIMARY KEY,
    batch_key text NOT NULL,
    seller_id uuid NOT NULL,
    recipient_user_id uuid NOT NULL,
    kind text NOT NULL CHECK(kind IN ('approval','subscription-receipt','subscription-reminder')),
    subscription_id uuid,
    state text NOT NULL DEFAULT 'QUEUED' CHECK(state IN ('QUEUED','ACCEPTED','SKIPPED','UNCERTAIN')),
    outcome_revision bigint NOT NULL DEFAULT 0,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS communication_deliveries_batch_idx ON notify.communication_deliveries(batch_key);
COMMIT;
