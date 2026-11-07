BEGIN;

ALTER TABLE payment.seller_refund_transfers
  ADD COLUMN IF NOT EXISTS attempt_count integer NOT NULL DEFAULT 1,
  ADD COLUMN IF NOT EXISTS review_reason text;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='seller_refund_attempt_count_range') THEN
    ALTER TABLE payment.seller_refund_transfers
      ADD CONSTRAINT seller_refund_attempt_count_range CHECK (attempt_count BETWEEN 1 AND 4);
  END IF;
END $$;

CREATE TABLE IF NOT EXISTS payment.seller_refund_transfer_attempts (
  transfer_id uuid NOT NULL REFERENCES payment.seller_refund_transfers(id) ON DELETE RESTRICT,
  attempt_no integer NOT NULL CHECK (attempt_no BETWEEN 1 AND 3),
  provider_reference text NOT NULL,
  transferred_at timestamptz NOT NULL,
  evidence_url text NOT NULL,
  evidence_attachment_id uuid REFERENCES notify.attachments(id),
  note text,
  review_reason text NOT NULL,
  submitted_by uuid NOT NULL REFERENCES identity.users(id),
  reviewed_by uuid NOT NULL REFERENCES identity.users(id),
  reviewed_at timestamptz NOT NULL,
  created_at timestamptz NOT NULL,
  PRIMARY KEY (transfer_id, attempt_no),
  UNIQUE (evidence_url)
);

COMMIT;
