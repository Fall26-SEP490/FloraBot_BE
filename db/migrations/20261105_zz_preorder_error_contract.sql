-- 20261105_zz_preorder_error_contract.sql
-- Migration: Restore canonical SQLSTATE P0001 for web_accept business validations.
-- Preserves preorder capacity commitment and lock hierarchy from 20261104_z_preorder_capacity.sql
-- while restoring canonical 409 Conflict behavior for quote and shop validation errors.

BEGIN;

CREATE OR REPLACE FUNCTION flow.web_accept(
  p_id uuid,
  p_customer uuid,
  p_price bigint,
  p_pickup timestamptz,
  p_note text
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  w ordering.web_requests%ROWTYPE;
  v_order uuid := public.uuid_v7();
  v_checkout uuid := public.uuid_v7();
  v_capacity integer;
  v_occupied_slots integer;
  v_unfulfilled_preorders integer;
  v_total_occupied integer;
  v_is_terminating boolean := false;
BEGIN
  -- 1. Initial nonlocking lookup
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND customer_id = p_customer;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Khong tim thay yeu cau' USING ERRCODE = 'P0001';
  END IF;

  -- 2. Idempotent return if already ordered
  IF w.state = 'ORDERED' THEN
    RETURN w.order_id;
  END IF;

  -- 3. Strict global lock order: Kiosk -> Seller -> Request
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = w.kiosk_id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = w.seller_id FOR UPDATE;
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id FOR UPDATE;

  -- 4. Re-check idempotency after lock
  IF w.state = 'ORDERED' THEN
    RETURN w.order_id;
  END IF;

  -- 5. Business validations from existing canonical web_accept (restored to P0001 -> 409 Conflict)
  IF w.quoted_price IS DISTINCT FROM p_price
     OR w.pickup_at IS DISTINCT FROM p_pickup
     OR w.quote_note IS DISTINCT FROM p_note THEN
    RAISE EXCEPTION 'Báo giá đã thay đổi. Hãy cập nhật và kiểm tra lại trước khi xác nhận' USING ERRCODE = 'P0001';
  END IF;

  IF w.state <> 'QUOTED' OR w.quote_expires_at <= public.app_now() THEN
    RAISE EXCEPTION 'Bao gia da het han hoac khong con hieu luc' USING ERRCODE = 'P0001';
  END IF;

  IF NOT flow.package_valid(w.seller_id)
     OR NOT EXISTS (SELECT 1 FROM identity.sellers WHERE id = w.seller_id AND status = 'ACTIVE')
     OR NOT EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = w.product_id AND status = 'ACTIVE')
     OR NOT EXISTS (SELECT 1 FROM kiosk_ops.kiosks WHERE id = w.kiosk_id AND status NOT IN ('DISABLED', 'MAINTENANCE')) THEN
    RAISE EXCEPTION 'Shop tam ngung nhan don' USING ERRCODE = 'P0001';
  END IF;

  -- 6. Kiosk termination guard
  v_is_terminating := flow.is_kiosk_terminating(w.kiosk_id);

  IF v_is_terminating THEN
    RAISE EXCEPTION 'Kiosk đang trong quá trình chấm dứt hợp đồng, không nhận đơn đặt trước mới' USING ERRCODE = 'P0001';
  END IF;

  -- 7. Atomic capacity commitment: PREORDER zone capacity and occupancy
  SELECT count(*)::integer INTO v_capacity
  FROM kiosk_ops.slots
  WHERE kiosk_id = w.kiosk_id
    AND current_seller_id = w.seller_id
    AND zone = 'PREORDER'
    AND status NOT IN ('FAULT', 'LOCKED');

  IF v_capacity = 0 THEN
    RAISE EXCEPTION 'Kiosk không có ô đặt trước (PREORDER) khả dụng' USING ERRCODE = 'P0001';
  END IF;

  -- Count occupied physical PREORDER slots
  SELECT count(*)::integer INTO v_occupied_slots
  FROM kiosk_ops.slots s
  WHERE s.kiosk_id = w.kiosk_id
    AND s.zone = 'PREORDER'
    AND (s.bouquet_id IS NOT NULL OR s.status IN ('STOCKED', 'HELD') OR s.hold_order_id IS NOT NULL);

  -- Count active unfulfilled preorders
  SELECT count(*)::integer INTO v_unfulfilled_preorders
  FROM ordering.web_requests req
  JOIN ordering.orders o ON o.id = req.order_id
  WHERE req.kiosk_id = w.kiosk_id
    AND req.state = 'ORDERED'
    AND o.status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING')
    AND NOT EXISTS (
      SELECT 1 FROM kiosk_ops.slots sl
      WHERE sl.kiosk_id = w.kiosk_id
        AND (sl.hold_order_id = o.id
             OR (sl.bouquet_id IS NOT NULL AND sl.bouquet_id IN (
                   SELECT oi.bouquet_id FROM ordering.order_items oi
                   WHERE oi.order_id = o.id AND oi.bouquet_id IS NOT NULL
                )))
    );

  v_total_occupied := v_occupied_slots + v_unfulfilled_preorders;

  IF v_total_occupied >= v_capacity THEN
    RAISE EXCEPTION 'Kiosk đã hết sức chứa đặt trước (PREORDER)' USING ERRCODE = 'P0001';
  END IF;

  -- 8. Create order and charge payment
  INSERT INTO ordering.orders(id, order_code, checkout_id, customer_id, seller_id, kiosk_id, subtotal, total_amount, tracking_token)
  VALUES(v_order, flow.new_order_code(), v_checkout, p_customer, w.seller_id, w.kiosk_id, w.quoted_price, w.quoted_price, flow.new_tracking_token());

  INSERT INTO payment.payments(kind, purpose, checkout_id, gateway, idempotency_key, amount, status)
  VALUES('CHARGE', 'ORDER_CHECKOUT', v_checkout, 'PAYOS', 'web-' || p_id::text, w.quoted_price, 'PENDING');

  UPDATE ordering.web_requests
  SET state = 'ORDERED', order_id = v_order, updated_at = public.app_now()
  WHERE id = p_id;

  PERFORM flow.audit(p_customer, 'WEB_QUOTE_ACCEPTED', 'web_requests', p_id,
    jsonb_build_object('kiosk_id', w.kiosk_id, 'order_id', v_order, 'occupied', v_total_occupied + 1, 'capacity', v_capacity));

  RETURN v_order;
END $$;

COMMIT;
