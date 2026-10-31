BEGIN;

CREATE TABLE IF NOT EXISTS ordering.preorder_pickup_otps (
  request_id uuid PRIMARY KEY REFERENCES ordering.web_requests(id),
  order_id uuid NOT NULL UNIQUE REFERENCES ordering.orders(id),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  generation_id uuid NOT NULL UNIQUE,
  code_hash text NOT NULL,
  email_hash text NOT NULL,
  status text NOT NULL CHECK(status IN ('SENDING','ACTIVE','USED','FAILED','REVOKED')),
  issued_at timestamptz NOT NULL,
  expires_at timestamptz NOT NULL,
  sent_by uuid NOT NULL REFERENCES identity.users(id),
  send_window_at timestamptz NOT NULL,
  send_count integer NOT NULL CHECK(send_count BETWEEN 1 AND 5),
  used_at timestamptz,
  CHECK(expires_at > issued_at)
);
CREATE UNIQUE INDEX IF NOT EXISTS preorder_pickup_unique_live_code
  ON ordering.preorder_pickup_otps(kiosk_id,code_hash) WHERE status IN ('SENDING','ACTIVE');
CREATE TABLE IF NOT EXISTS ordering.preorder_pickup_attempts (
  id uuid PRIMARY KEY,
  generation_id uuid NOT NULL,
  order_id uuid NOT NULL REFERENCES ordering.orders(id),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  code_hash text NOT NULL,
  command_id uuid NOT NULL REFERENCES kiosk_ops.unlock_tokens(cmd_id),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  expires_at timestamptz NOT NULL
);
CREATE INDEX IF NOT EXISTS preorder_pickup_attempts_order ON ordering.preorder_pickup_attempts(order_id);

-- Receipt capability remains valid for lookup/support and walk-in recovery.
-- All preorder door requests must now use the email OTP endpoint.
CREATE OR REPLACE FUNCTION flow.request_pickup(p_order uuid, p_tracking text, p_kiosk uuid, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_k text; v_cmd uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.kiosk_id <> p_kiosk THEN RAISE EXCEPTION 'Đơn không nhận tại kiosk này'; END IF;
  IF EXISTS(SELECT 1 FROM ordering.web_requests WHERE order_id=o.id) THEN
    RAISE EXCEPTION 'Đơn đặt trước phải nhận bằng OTP email tại kiosk.' USING ERRCODE = '22023';
  END IF;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION 'Đơn không ở trạng thái chờ nhận (%)', o.status; END IF;
  SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF v_k IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk đang không trực tuyến, thử lại sau'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id=o.id AND status='HELD' AND hold_until>=v_now) THEN
    RAISE EXCEPTION 'Hết thời gian giữ hàng tại tủ';
  END IF;
  IF coalesce(p_tracking,'')='' OR o.tracking_token<>p_tracking THEN
    PERFORM flow.audit(p_kiosk,'PICKUP_CODE_REJECTED','orders',o.id,NULL,'KIOSK');
    RETURN NULL;
  END IF;
  v_cmd := flow.issue_pickup_tokens(o.id);
  IF o.status='PAID' THEN PERFORM flow.set_order_status(o.id,'DISPENSING','KIOSK',NULL,'Phát lệnh nhận hoa walk-in'); END IF;
  RETURN v_cmd;
END $$;

COMMIT;
