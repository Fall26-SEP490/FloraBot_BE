BEGIN;

-- Recover the first dispatch after a lost response. A second request must not
-- revoke the command already sent to the device or bypass manual retry policy.
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
  IF EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens WHERE order_id=o.id AND status='OPENED') THEN
    RAISE EXCEPTION 'Cửa đang mở. Nhận hoa và đóng cửa trước khi thao tác tiếp.' USING ERRCODE = '22023';
  END IF;
  SELECT t.cmd_id INTO v_cmd FROM kiosk_ops.unlock_tokens t
    JOIN kiosk_ops.slots s ON s.id=t.slot_id AND s.kiosk_id=t.kiosk_id
    WHERE t.order_id=o.id AND t.kiosk_id=p_kiosk AND t.purpose='CUSTOMER_PICKUP'
      AND t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at>v_now
      AND s.hold_order_id=o.id AND s.status='HELD'
    ORDER BY t.issued_at,t.cmd_id LIMIT 1;
  IF v_cmd IS NOT NULL THEN RETURN v_cmd; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens WHERE order_id=o.id AND purpose='CUSTOMER_PICKUP') THEN
    RAISE EXCEPTION 'Lệnh nhận hoa đã kết thúc. Giữ biên nhận và liên hệ shop để kiểm tra.' USING ERRCODE = '22023';
  END IF;
  v_cmd := flow.issue_pickup_tokens(o.id);
  IF o.status='PAID' THEN PERFORM flow.set_order_status(o.id,'DISPENSING','KIOSK',NULL,'Phát lệnh nhận hoa walk-in'); END IF;
  RETURN v_cmd;
END $$;

COMMIT;
