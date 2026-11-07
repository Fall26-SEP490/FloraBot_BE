BEGIN;

CREATE TABLE IF NOT EXISTS kiosk_ops.kiosk_location_requests (
  id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  seller_id uuid NOT NULL REFERENCES identity.sellers(id),
  requested_by uuid NOT NULL REFERENCES identity.users(id),
  address text NOT NULL CHECK (length(btrim(address)) BETWEEN 1 AND 300),
  latitude double precision NOT NULL CHECK (latitude BETWEEN -90 AND 90),
  longitude double precision NOT NULL CHECK (longitude BETWEEN -180 AND 180),
  reason text NOT NULL CHECK (length(btrim(reason)) BETWEEN 5 AND 1000),
  status text NOT NULL DEFAULT 'REQUESTED' CHECK (status IN ('REQUESTED','APPROVED','REJECTED')),
  reviewed_by uuid REFERENCES identity.users(id),
  review_note text,
  old_address text,
  old_latitude double precision,
  old_longitude double precision,
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  reviewed_at timestamptz
);

CREATE UNIQUE INDEX IF NOT EXISTS kiosk_location_one_requested
  ON kiosk_ops.kiosk_location_requests(kiosk_id) WHERE status='REQUESTED';
CREATE INDEX IF NOT EXISTS kiosk_location_requests_seller
  ON kiosk_ops.kiosk_location_requests(seller_id,created_at DESC);

COMMIT;
