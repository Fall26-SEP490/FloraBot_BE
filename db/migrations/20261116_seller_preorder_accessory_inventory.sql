-- Shared Seller inventory for optional accessories on flower preorders.
-- Existing kiosk rows and accepted/historical orders retain their IDs and stock;
-- their physical inventory is not automatically moved into the shop.
BEGIN;

ALTER TABLE kiosk_ops.accessories ALTER COLUMN kiosk_id DROP NOT NULL;
CREATE UNIQUE INDEX accessories_shop_seller_name_unique
  ON kiosk_ops.accessories(seller_id, lower(btrim(name))) WHERE kiosk_id IS NULL;

COMMENT ON COLUMN kiosk_ops.accessories.kiosk_id IS
  'NULL: shared shop stock packed with preorder flowers; non-null: existing physical kiosk inventory';

-- The same tenant-authorized restock flow supports both inventories.
CREATE OR REPLACE FUNCTION flow.restock_accessory(p_accessory uuid, p_user uuid, p_qty integer)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE a kiosk_ops.accessories;
BEGIN
  IF p_qty IS NULL OR p_qty <= 0 THEN
    RAISE EXCEPTION 'Số lượng nhập kho phải lớn hơn 0' USING ERRCODE = 'P0001';
  END IF;
  SELECT * INTO a FROM kiosk_ops.accessories WHERE id = p_accessory FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Phụ kiện không tồn tại' USING ERRCODE = 'P0001'; END IF;
  PERFORM flow.check_seller_user(p_user, a.seller_id);
  UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + p_qty, updated_at = public.app_now()
  WHERE id = a.id AND stock_quantity <= 2147483647 - p_qty;
  IF NOT FOUND THEN RAISE EXCEPTION 'Tồn kho phụ kiện vượt giới hạn' USING ERRCODE = 'P0001'; END IF;
  PERFORM flow.audit(p_user, 'ACCESSORY_RESTOCKED', 'accessories', a.id,
    jsonb_build_object('qty', p_qty, 'scope', CASE WHEN a.kiosk_id IS NULL THEN 'SHOP' ELSE 'KIOSK' END));
END;
$$;

CREATE OR REPLACE FUNCTION flow.snapshot_preorder_accessories(
  p_items jsonb, p_kiosk uuid, p_seller uuid, p_requested jsonb DEFAULT NULL,
  p_include_price boolean DEFAULT false
) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE
  v_selection jsonb;
  v_item jsonb;
  v_id uuid;
  v_qty integer;
  v_requested_qty integer;
  v_name text;
  v_price bigint;
  v_stock integer;
  v_line_total numeric;
  v_output jsonb := '[]'::jsonb;
BEGIN
  v_selection := flow.normalize_preorder_accessory_selection(p_items);
  FOR v_item IN SELECT value FROM jsonb_array_elements(v_selection) AS rows(value) LOOP
    v_id := (v_item->>'accessoryId')::uuid;
    v_qty := (v_item->>'quantity')::integer;

    IF p_requested IS NOT NULL THEN
      SELECT (requested.value->>'quantity')::integer INTO v_requested_qty
      FROM jsonb_array_elements(p_requested) AS requested(value)
      WHERE requested.value->>'accessoryId' = v_id::text;
      IF NOT FOUND OR v_qty > v_requested_qty THEN
        RAISE EXCEPTION 'Báo giá phụ kiện phải nằm trong số lượng khách yêu cầu' USING ERRCODE = 'P0001';
      END IF;
    END IF;

    SELECT a.name, a.price, a.stock_quantity
      INTO v_name, v_price, v_stock
    FROM kiosk_ops.accessories a
    WHERE a.id = v_id AND (a.kiosk_id IS NULL OR a.kiosk_id = p_kiosk) AND a.seller_id = p_seller AND a.status = 'ACTIVE'
    FOR SHARE;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Phụ kiện không thuộc shop hoặc đã ngừng bán' USING ERRCODE = 'P0001';
    END IF;
    IF v_stock < v_qty THEN
      RAISE EXCEPTION 'Phụ kiện không còn đủ tồn để báo giá' USING ERRCODE = 'P0001';
    END IF;

    IF p_include_price THEN
      v_line_total := v_price::numeric * v_qty;
      IF v_line_total > 100000000 THEN
        RAISE EXCEPTION 'Thành tiền phụ kiện vượt giới hạn đơn hàng' USING ERRCODE = 'P0001';
      END IF;
      v_output := v_output || jsonb_build_array(jsonb_build_object(
        'accessoryId', v_id, 'name', v_name, 'quantity', v_qty,
        'unitPrice', v_price, 'lineTotal', v_line_total::bigint));
    ELSE
      v_output := v_output || jsonb_build_array(jsonb_build_object(
        'accessoryId', v_id, 'name', v_name, 'quantity', v_qty));
    END IF;
  END LOOP;
  RETURN v_output;
END;
$$;

CREATE OR REPLACE FUNCTION flow.web_request(
  p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid,
  p_bouquet uuid, p_pickup timestamptz, p_instructions text, p_accessories jsonb DEFAULT '[]'::jsonb
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  pr catalog.flower_products;
  w ordering.web_requests;
  v_selection jsonb;
  v_requested jsonb;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:' || p_id::text, 0));
  v_selection := flow.normalize_preorder_accessory_selection(p_accessories);
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tai khoan khong hop le' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND (
      w.request_payload->'bouquet' IS DISTINCT FROM coalesce(to_jsonb(p_bouquet), 'null'::jsonb)
      OR (w.request_payload->>'pickup')::timestamptz IS DISTINCT FROM p_pickup
      OR coalesce(w.request_payload->'accessories', '[]'::jsonb) IS DISTINCT FROM v_selection
    ) THEN
      RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác' USING ERRCODE = 'P0001';
    END IF;
    IF w.customer_id <> p_customer OR w.product_id <> p_product OR w.kiosk_id <> p_kiosk
       OR w.kind <> p_kind OR w.instructions <> p_instructions THEN
      RAISE EXCEPTION 'Ma yeu cau da duoc su dung' USING ERRCODE = 'P0001';
    END IF;
    RETURN w.id;
  END IF;

  IF p_kind IS DISTINCT FROM 'CUSTOM' THEN RAISE EXCEPTION 'Web chỉ nhận đơn đặt hoa theo mẫu' USING ERRCODE = 'P0001'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id)
     OR NOT EXISTS (SELECT 1 FROM identity.sellers WHERE id = pr.seller_id AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don' USING ERRCODE = 'P0001';
  END IF;
  IF pr.quantity <= 0 THEN RAISE EXCEPTION 'PREORDER_PRODUCT_OUT_OF_STOCK' USING ERRCODE = 'P0001'; END IF;
  IF NOT flow.kiosk_package_valid(p_kiosk) OR flow.is_kiosk_terminating(p_kiosk)
     OR length(p_instructions) > 2000 OR NOT EXISTS (
    SELECT 1 FROM kiosk_ops.kiosks
    WHERE id = p_kiosk AND seller_id = pr.seller_id AND status NOT IN ('DISABLED', 'MAINTENANCE')
      AND length(btrim(coalesce(address, ''))) > 0 AND latitude IS NOT NULL AND longitude IS NOT NULL
  ) THEN RAISE EXCEPTION 'Thông tin đặt hoa hoặc gói kiosk không hợp lệ' USING ERRCODE = 'P0001'; END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND status NOT IN ('FREE', 'PENDING_RELEASE')
  ) THEN RAISE EXCEPTION 'Shop không phục vụ từ này' USING ERRCODE = 'P0001'; END IF;
  IF length(btrim(p_instructions)) < 10 OR p_pickup <= public.app_now()
     OR p_pickup > public.app_now() + interval '30 days' THEN
    RAISE EXCEPTION 'Mô tả tối thiểu 10 ký tự, hẹn nhận sau thời điểm hiện tại và trong 30 ngày' USING ERRCODE = 'P0001';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND zone = 'PREORDER') THEN
    RAISE EXCEPTION 'Kiosk chưa có ô PREORDER do seller phân loại' USING ERRCODE = 'P0001';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM flow.get_kiosk_preorder_availability(p_kiosk) WHERE is_available) THEN
    RAISE EXCEPTION 'Kiosk đã hết sức chứa đặt trước (PREORDER)' USING ERRCODE = 'P0001';
  END IF;

  -- New requests use shop stock. Existing request/quote snapshots above remain retryable.
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(v_selection) selected(value)
    LEFT JOIN kiosk_ops.accessories a ON a.id = (selected.value->>'accessoryId')::uuid
    WHERE a.id IS NULL OR a.seller_id <> pr.seller_id OR a.kiosk_id IS NOT NULL
  ) THEN
    RAISE EXCEPTION 'PREORDER_ACCESSORY_NOT_IN_SHOP' USING ERRCODE = 'P0001';
  END IF;
  v_requested := flow.snapshot_preorder_accessories(v_selection, p_kiosk, pr.seller_id, NULL, false);
  INSERT INTO ordering.web_requests(
    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, state,
    requested_accessories
  ) VALUES (
    p_id, p_customer, pr.seller_id, pr.id, p_kiosk, 'CUSTOM', p_instructions, p_pickup,
    p_pickup + make_interval(hours => flow.cfg('web_pickup_hours')::integer), 'REQUESTED', v_requested
  );
  UPDATE ordering.web_requests
  SET request_payload = jsonb_build_object('bouquet', p_bouquet, 'pickup', p_pickup, 'accessories', v_selection),
      display_snapshot = (
        SELECT jsonb_build_object('product', pr.name, 'shop', shop.shop_name, 'kiosk', k.name, 'address', k.address)
        FROM identity.sellers shop CROSS JOIN kiosk_ops.kiosks k WHERE shop.id = pr.seller_id AND k.id = p_kiosk
      )
  WHERE id = p_id;
  PERFORM flow.audit(p_customer, 'WEB_REQUEST_CREATED', 'web_requests', p_id,
    jsonb_build_object('kind', 'CUSTOM', 'accessory_count', jsonb_array_length(v_requested)));
  RETURN p_id;
END;
$$;

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
