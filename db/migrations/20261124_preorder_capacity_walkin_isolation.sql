-- Count only occupied PREORDER slots when committing a customer quote.
-- Keep the rest of the latest quote-acceptance flow unchanged.
BEGIN;

CREATE OR REPLACE FUNCTION flow.web_accept(
  p_id uuid, p_customer uuid, p_price bigint, p_pickup timestamptz, p_note text,
  p_quote_version integer DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  w ordering.web_requests%ROWTYPE;
  v_order uuid := public.uuid_v7();
  v_checkout uuid := public.uuid_v7();
  v_capacity integer;
  v_occupied_slots integer;
  v_unfulfilled_preorders integer;
  v_total_occupied integer;
  v_accessory jsonb;
  v_accessory_id uuid;
  v_accessory_quantity integer;
  v_accessory_total numeric := 0;
  v_stock integer;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND customer_id = p_customer;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong tim thay yeu cau' USING ERRCODE = 'P0001'; END IF;
  IF w.state = 'ORDERED' THEN RETURN w.order_id; END IF;

  -- Preserve the established global order: kiosk -> seller -> request -> accessories.
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = w.kiosk_id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = w.seller_id FOR UPDATE;
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id FOR UPDATE;
  IF w.state = 'ORDERED' THEN RETURN w.order_id; END IF;

  IF w.quoted_price IS DISTINCT FROM p_price OR w.pickup_at IS DISTINCT FROM p_pickup
     OR w.quote_note IS DISTINCT FROM p_note
     OR (w.quote_version > 0 AND p_quote_version IS DISTINCT FROM w.quote_version) THEN
    RAISE EXCEPTION 'Báo giá đã thay đổi. Hãy tải lại báo giá và kiểm tra trước khi xác nhận' USING ERRCODE = 'P0001';
  END IF;
  IF w.state <> 'QUOTED' OR w.quote_expires_at <= public.app_now() THEN
    RAISE EXCEPTION 'Bao gia da het han hoac khong con hieu luc' USING ERRCODE = 'P0001';
  END IF;
  IF NOT flow.package_valid(w.seller_id)
     OR NOT flow.kiosk_package_valid(w.kiosk_id)
     OR NOT EXISTS (SELECT 1 FROM identity.sellers WHERE id = w.seller_id AND status = 'ACTIVE')
     OR NOT EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = w.product_id AND status = 'ACTIVE')
     OR NOT EXISTS (SELECT 1 FROM kiosk_ops.kiosks WHERE id = w.kiosk_id AND status NOT IN ('DISABLED', 'MAINTENANCE')) THEN
    RAISE EXCEPTION 'Shop tam ngung nhan don' USING ERRCODE = 'P0001';
  END IF;
  IF flow.is_kiosk_terminating(w.kiosk_id) THEN
    RAISE EXCEPTION 'Kiosk đang trong quá trình chấm dứt hợp đồng, không nhận đơn đặt trước mới' USING ERRCODE = 'P0001';
  END IF;

  SELECT count(*)::integer INTO v_capacity FROM kiosk_ops.slots
  WHERE kiosk_id = w.kiosk_id AND current_seller_id = w.seller_id AND zone = 'PREORDER'
    AND status NOT IN ('FAULT', 'LOCKED');
  IF v_capacity = 0 THEN RAISE EXCEPTION 'Kiosk không có ô đặt trước (PREORDER) khả dụng' USING ERRCODE = 'P0001'; END IF;
  SELECT count(*)::integer INTO v_occupied_slots FROM kiosk_ops.slots s
  WHERE s.kiosk_id = w.kiosk_id
    AND s.zone = 'PREORDER'
    AND (s.bouquet_id IS NOT NULL OR s.status IN ('STOCKED', 'HELD') OR s.hold_order_id IS NOT NULL);
  SELECT count(*)::integer INTO v_unfulfilled_preorders
  FROM ordering.web_requests req JOIN ordering.orders o ON o.id = req.order_id
  WHERE req.kiosk_id = w.kiosk_id AND req.state = 'ORDERED'
    AND o.status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING')
    AND NOT EXISTS (SELECT 1 FROM kiosk_ops.slots sl
      WHERE sl.kiosk_id = w.kiosk_id AND (sl.hold_order_id = o.id OR sl.bouquet_id IN (
        SELECT oi.bouquet_id FROM ordering.order_items oi WHERE oi.order_id = o.id AND oi.bouquet_id IS NOT NULL)));
  v_total_occupied := v_occupied_slots + v_unfulfilled_preorders;
  IF v_total_occupied >= v_capacity THEN
    RAISE EXCEPTION 'Kiosk đã hết sức chứa đặt trước (PREORDER)' USING ERRCODE = 'P0001';
  END IF;

  SELECT coalesce(sum((value->>'lineTotal')::numeric), 0) INTO v_accessory_total
  FROM jsonb_array_elements(w.quoted_accessories) AS rows(value);
  IF w.quoted_flower_price IS NULL
     OR w.quoted_price::numeric <> w.quoted_flower_price::numeric + v_accessory_total
     OR w.quoted_price > 100000000 THEN
    RAISE EXCEPTION 'Báo giá phụ kiện không hợp lệ. Hãy yêu cầu Seller báo giá lại' USING ERRCODE = 'P0001';
  END IF;

  INSERT INTO ordering.orders(id, order_code, checkout_id, customer_id, seller_id, kiosk_id,
    subtotal, total_amount, tracking_token)
  VALUES(v_order, flow.new_order_code(), v_checkout, p_customer, w.seller_id, w.kiosk_id,
    w.quoted_price, w.quoted_price, flow.new_tracking_token());
  INSERT INTO payment.payments(kind, purpose, checkout_id, gateway, idempotency_key, amount, status)
  VALUES('CHARGE', 'ORDER_CHECKOUT', v_checkout, 'PAYOS', 'web-' || p_id::text, w.quoted_price, 'PENDING');

  -- The sorted JSON order locks accessory rows deterministically. A conflict rolls
  -- back the order, charge and every prior decrement in this transaction.
  FOR v_accessory IN
    SELECT value FROM jsonb_array_elements(w.quoted_accessories) AS rows(value)
    ORDER BY (value->>'accessoryId')::uuid
  LOOP
    v_accessory_id := (v_accessory->>'accessoryId')::uuid;
    v_accessory_quantity := (v_accessory->>'quantity')::integer;
    UPDATE kiosk_ops.accessories
    SET stock_quantity = stock_quantity - v_accessory_quantity, updated_at = public.app_now()
    WHERE id = v_accessory_id AND (kiosk_id IS NULL OR kiosk_id = w.kiosk_id) AND seller_id = w.seller_id
      AND status = 'ACTIVE' AND stock_quantity >= v_accessory_quantity
    RETURNING stock_quantity INTO v_stock;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'PREORDER_ACCESSORY_OUT_OF_STOCK' USING ERRCODE = 'P0001';
    END IF;
    INSERT INTO ordering.order_items(order_id, item_type, accessory_id, name_snapshot,
      quantity, unit_price, line_total)
    VALUES(v_order, 'ACCESSORY', v_accessory_id, v_accessory->>'name', v_accessory_quantity,
      (v_accessory->>'unitPrice')::bigint, (v_accessory->>'lineTotal')::bigint);
  END LOOP;

  UPDATE ordering.web_requests SET state = 'ORDERED', order_id = v_order, updated_at = public.app_now()
  WHERE id = p_id;
  PERFORM flow.audit(p_customer, 'WEB_QUOTE_ACCEPTED', 'web_requests', p_id,
    jsonb_build_object('kiosk_id', w.kiosk_id, 'order_id', v_order,
      'total', w.quoted_price, 'accessory_count', jsonb_array_length(w.quoted_accessories),
      'occupied', v_total_occupied + 1, 'capacity', v_capacity));
  RETURN v_order;
END;
$$;

COMMIT;
