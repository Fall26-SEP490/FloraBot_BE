-- Accessories in custom web preorders.
-- Customer selections are advisory until quote acceptance. Acceptance atomically
-- snapshots order lines and reserves stock by decrementing available inventory.
BEGIN;

ALTER TABLE ordering.web_requests
  ADD COLUMN requested_accessories jsonb NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN quoted_flower_price bigint,
  ADD COLUMN quoted_accessories jsonb NOT NULL DEFAULT '[]'::jsonb,
  ADD COLUMN quote_version integer NOT NULL DEFAULT 0;

UPDATE ordering.web_requests
SET quoted_flower_price = quoted_price
WHERE quoted_price IS NOT NULL AND quoted_flower_price IS NULL;

ALTER TABLE ordering.web_requests
  ADD CONSTRAINT web_requests_requested_accessories_array
    CHECK (jsonb_typeof(requested_accessories) = 'array'),
  ADD CONSTRAINT web_requests_quoted_accessories_array
    CHECK (jsonb_typeof(quoted_accessories) = 'array'),
  ADD CONSTRAINT web_requests_quoted_flower_price_range
    CHECK (quoted_flower_price IS NULL OR quoted_flower_price BETWEEN 1000 AND 100000000),
  ADD CONSTRAINT web_requests_quote_version_nonnegative
    CHECK (quote_version >= 0);

-- Normalize only IDs and positive quantities from caller input. Names and prices
-- are always fetched from the kiosk-owned inventory rows below.
CREATE OR REPLACE FUNCTION flow.normalize_preorder_accessory_selection(p_items jsonb)
RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE
  v_item jsonb;
  v_id uuid;
  v_qty_text text;
  v_qty bigint;
  v_seen uuid[] := ARRAY[]::uuid[];
  v_output jsonb := '[]'::jsonb;
BEGIN
  p_items := coalesce(p_items, '[]'::jsonb);
  IF jsonb_typeof(p_items) IS DISTINCT FROM 'array' OR jsonb_array_length(p_items) > 20 THEN
    RAISE EXCEPTION 'Danh sách phụ kiện không hợp lệ (tối đa 20 loại)' USING ERRCODE = 'P0001';
  END IF;

  FOR v_item IN SELECT value FROM jsonb_array_elements(p_items) AS rows(value) LOOP
    IF jsonb_typeof(v_item) IS DISTINCT FROM 'object'
       OR jsonb_typeof(v_item->'accessoryId') IS DISTINCT FROM 'string'
       OR jsonb_typeof(v_item->'quantity') IS DISTINCT FROM 'number' THEN
      RAISE EXCEPTION 'Mỗi phụ kiện cần accessoryId và quantity hợp lệ' USING ERRCODE = 'P0001';
    END IF;
    v_qty_text := v_item->>'quantity';
    IF v_qty_text IS NULL OR v_qty_text !~ '^[1-9][0-9]{0,9}$' THEN
      RAISE EXCEPTION 'Số lượng phụ kiện phải là số nguyên dương' USING ERRCODE = 'P0001';
    END IF;
    v_qty := v_qty_text::bigint;
    IF v_qty > 2147483647 THEN
      RAISE EXCEPTION 'Số lượng phụ kiện vượt giới hạn' USING ERRCODE = 'P0001';
    END IF;
    BEGIN
      v_id := (v_item->>'accessoryId')::uuid;
    EXCEPTION WHEN invalid_text_representation THEN
      RAISE EXCEPTION 'Mã phụ kiện không hợp lệ' USING ERRCODE = 'P0001';
    END;
    IF v_id = ANY(v_seen) THEN
      RAISE EXCEPTION 'Không gửi lặp cùng một phụ kiện' USING ERRCODE = 'P0001';
    END IF;
    v_seen := array_append(v_seen, v_id);
    v_output := v_output || jsonb_build_array(jsonb_build_object('accessoryId', v_id, 'quantity', v_qty::integer));
  END LOOP;

  SELECT coalesce(jsonb_agg(value ORDER BY (value->>'accessoryId')::uuid), '[]'::jsonb)
  INTO v_output
  FROM jsonb_array_elements(v_output) AS rows(value);
  RETURN v_output;
END;
$$;

-- Read authoritative catalog snapshots. p_requested=null is customer selection;
-- a JSON array means Seller quote and limits the offer to requested items/quantities.
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
    WHERE a.id = v_id AND a.kiosk_id = p_kiosk AND a.seller_id = p_seller AND a.status = 'ACTIVE'
    FOR SHARE;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Phụ kiện không thuộc kiosk/shop hoặc đã ngừng bán' USING ERRCODE = 'P0001';
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

DROP FUNCTION IF EXISTS flow.web_request(uuid,uuid,text,uuid,uuid,uuid,timestamptz,text);
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

-- Price is the flower/bouquet component. BE snapshots catalog prices for offered
-- accessories and exposes quoted_price as the full payable total.
DROP FUNCTION IF EXISTS flow.web_quote(uuid,uuid,uuid,bigint,timestamptz,text,boolean);
CREATE OR REPLACE FUNCTION flow.web_quote(
  p_id uuid, p_seller uuid, p_actor uuid, p_price bigint, p_pickup timestamptz,
  p_note text, p_reject boolean DEFAULT false, p_accessories jsonb DEFAULT '[]'::jsonb
) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  w ordering.web_requests;
  v_accessories jsonb;
  v_accessory_total numeric := 0;
  v_total numeric;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND seller_id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau' USING ERRCODE = 'P0001'; END IF;
  PERFORM flow.check_seller_user(p_actor, p_seller);
  IF w.state NOT IN ('REQUESTED', 'QUOTED') OR w.kind <> 'CUSTOM' THEN
    RAISE EXCEPTION 'Yeu cau khong con cho bao gia' USING ERRCODE = 'P0001';
  END IF;
  IF NOT flow.kiosk_package_valid(w.kiosk_id) OR flow.is_kiosk_terminating(w.kiosk_id) THEN
    RAISE EXCEPTION 'Kiosk không còn trong kỳ thuê hợp lệ hoặc đang chấm dứt hợp đồng' USING ERRCODE = 'P0001';
  END IF;
  IF length(btrim(p_note)) < 5 OR length(p_note) > 1000 THEN
    RAISE EXCEPTION 'Ghi chu can tu 5 den 1000 ky tu' USING ERRCODE = 'P0001';
  END IF;

  IF p_reject THEN
    UPDATE ordering.web_requests
    SET state = 'REJECTED', quote_note = p_note, quoted_price = NULL, quoted_flower_price = NULL,
        quoted_accessories = '[]'::jsonb, quote_version = quote_version + 1, updated_at = public.app_now()
    WHERE id = p_id;
  ELSE
    IF p_price NOT BETWEEN 1000 AND 100000000 OR p_pickup < public.app_now() + interval '1 hour'
       OR p_pickup > public.app_now() + interval '30 days' THEN
      RAISE EXCEPTION 'Gia hoa hoac lich nhan khong hop le' USING ERRCODE = 'P0001';
    END IF;
    v_accessories := flow.snapshot_preorder_accessories(
      p_accessories, w.kiosk_id, w.seller_id, w.requested_accessories, true);
    SELECT coalesce(sum((value->>'lineTotal')::numeric), 0) INTO v_accessory_total
    FROM jsonb_array_elements(v_accessories) AS rows(value);
    v_total := p_price::numeric + v_accessory_total;
    IF v_total > 100000000 THEN RAISE EXCEPTION 'Tổng báo giá vượt giới hạn đơn hàng' USING ERRCODE = 'P0001'; END IF;

    UPDATE ordering.web_requests
    SET state = 'QUOTED', quoted_flower_price = p_price, quoted_accessories = v_accessories,
        quoted_price = v_total::bigint, quote_note = p_note, pickup_at = p_pickup,
        pickup_before = p_pickup + make_interval(hours => flow.cfg('web_pickup_hours')::integer),
        quote_expires_at = least(p_pickup - interval '30 minutes',
          public.app_now() + make_interval(hours => flow.cfg('web_quote_hours')::integer)),
        quote_version = quote_version + 1, updated_at = public.app_now()
    WHERE id = p_id;
  END IF;
  PERFORM flow.audit(p_actor, 'WEB_REQUEST_QUOTED', 'web_requests', p_id,
    jsonb_build_object('rejected', p_reject, 'quote_version', w.quote_version + 1,
      'flower_price', CASE WHEN p_reject THEN NULL ELSE p_price END,
      'accessory_count', CASE WHEN p_reject THEN 0 ELSE jsonb_array_length(v_accessories) END));
END;
$$;

DROP FUNCTION IF EXISTS flow.web_accept(uuid,uuid,bigint,timestamptz,text);
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
    WHERE id = v_accessory_id AND kiosk_id = w.kiosk_id AND seller_id = w.seller_id
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

-- Add the bouquet's flower-only snapshot to the slot selected by Seller/Staff;
-- accessory order lines are colocated in the same compartment.
CREATE OR REPLACE FUNCTION flow.web_fulfill(p_id uuid, p_seller uuid, p_actor uuid, p_slot uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  w ordering.web_requests;
  o ordering.orders;
  s kiosk_ops.slots;
  pr catalog.flower_products;
  v_seller_status text;
  b uuid;
  v_now timestamptz := public.app_now();
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND seller_id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor, p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id = w.order_id FOR UPDATE;
  IF w.kind <> 'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF v_now >= w.pickup_before THEN RAISE EXCEPTION 'Can nap hoa truoc han nhan'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = w.product_id FOR KEY SHARE;
  IF NOT FOUND OR pr.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Mau hoa khong con duoc bat ban'; END IF;
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = p_seller;
  IF v_seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller dang % (goi qua han hoac bi khoa), khong nap duoc', v_seller_status; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot AND current_seller_id = p_seller AND kiosk_id = w.kiosk_id FOR UPDATE;
  IF NOT FOUND OR s.status <> 'RENTED_EMPTY' OR s.zone IS DISTINCT FROM 'PREORDER' THEN
    RAISE EXCEPTION 'Can o PREORDER trong cua shop tai dung tu nhan';
  END IF;
  IF NOT flow.slot_sellable(p_slot, p_seller) THEN RAISE EXCEPTION 'Goi cua seller da het han hoac o da bi thu hoi'; END IF;
  b := public.uuid_v7();
  INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (b, pr.id, p_seller, btrim(p_qr), coalesce(w.quoted_flower_price, w.quoted_price), 'STOCKED',
    v_now + make_interval(hours => pr.shelf_life_hours), v_now);
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (b, p_slot, 'STOCK_IN', flow.new_batch(), p_actor);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id = b) < w.pickup_before THEN
    RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan';
  END IF;
  UPDATE kiosk_ops.bouquets SET status = 'SOLD' WHERE id = b;
  UPDATE kiosk_ops.slots SET status = 'HELD', hold_order_id = o.id, hold_until = w.pickup_before,
    row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO ordering.order_items(order_id, item_type, bouquet_id, slot_id, name_snapshot,
    quantity, unit_price, line_total)
  VALUES(o.id, 'BOUQUET', b, p_slot, 'Hoa dat rieng: ' || pr.name, 1,
    coalesce(w.quoted_flower_price, w.quoted_price), coalesce(w.quoted_flower_price, w.quoted_price));
  UPDATE ordering.order_items SET slot_id = p_slot
  WHERE order_id = o.id AND item_type = 'ACCESSORY' AND line_status = 'ACTIVE';
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, order_id, performed_by)
  VALUES (b, p_slot, 'SALE_OUT', o.id, p_actor);
  PERFORM flow.audit(p_actor, 'WEB_ORDER_READY', 'orders', o.id,
    jsonb_build_object('request', p_id, 'slot', p_slot, 'accessory_count', jsonb_array_length(w.quoted_accessories)));
  RETURN b;
END;
$$;

-- A paid preorder cancelled before flower loading refunds the full quote and
-- returns the co-packed accessories. After flower loading, the existing
-- dispense_failed line-level refund/stock handling remains authoritative.
CREATE OR REPLACE FUNCTION flow.web_cancel(p_id uuid, p_actor uuid, p_reason text)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  w ordering.web_requests;
  o ordering.orders;
  it record;
  v_ref uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id FOR UPDATE;
  IF NOT FOUND OR NOT (p_actor = flow.sys_user() OR w.customer_id = p_actor
    OR EXISTS (SELECT 1 FROM identity.users WHERE id = p_actor AND role = 'SELLER' AND seller_id = w.seller_id AND status = 'ACTIVE')
    OR EXISTS (SELECT 1 FROM identity.users WHERE id = p_actor AND role IN ('ADMIN', 'SYSTEM') AND status = 'ACTIVE')) THEN
    RAISE EXCEPTION 'Khong co quyen huy yeu cau';
  END IF;
  IF length(btrim(p_reason)) < 5 OR length(p_reason) > 1000 THEN RAISE EXCEPTION 'Ly do can tu 5 den 1000 ky tu'; END IF;
  IF w.state IN ('CANCELLED', 'REJECTED') THEN RETURN; END IF;
  IF w.order_id IS NOT NULL THEN
    PERFORM 1 FROM payment.payments WHERE checkout_id = (SELECT checkout_id FROM ordering.orders WHERE id = w.order_id)
      AND kind = 'CHARGE' FOR UPDATE;
    SELECT * INTO o FROM ordering.orders WHERE id = w.order_id FOR UPDATE;
    IF o.status = 'AWAITING_PAYMENT' THEN
      UPDATE payment.payments SET status = 'CANCELLED'
      WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'PENDING';
      PERFORM flow.release_checkout(o.checkout_id, 'CANCELLED');
    ELSIF o.status = 'PAID' THEN
      IF w.customer_id = p_actor THEN
        RAISE EXCEPTION 'Don da tra tien: lien he shop qua ho so de doi chieu truoc khi huy';
      END IF;
      IF EXISTS (SELECT 1 FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET' AND line_status = 'ACTIVE') THEN
        PERFORM flow.dispense_failed(o.id, p_reason);
      ELSE
        v_ref := flow.create_refund(o.id, o.total_amount, p_reason, flow.sys_user(), 'MANUAL');
        PERFORM flow.refund_allocation(v_ref, 'REFUND_CLEARING', o.subtotal);
        FOR it IN SELECT * FROM ordering.order_items
          WHERE order_id = o.id AND item_type = 'ACCESSORY' AND line_status = 'ACTIVE'
          ORDER BY accessory_id FOR UPDATE
        LOOP
          UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + it.quantity, updated_at = public.app_now()
          WHERE id = it.accessory_id;
          UPDATE ordering.order_items SET line_status = 'REFUNDED' WHERE id = it.id;
        END LOOP;
        INSERT INTO ordering.disputes(kind, order_id, kiosk_id, reason)
        VALUES('DISPENSE_FAILED', o.id, o.kiosk_id, p_reason);
        PERFORM flow.set_order_status(o.id, 'DISPENSE_FAILED', 'SYSTEM', NULL, p_reason);
        PERFORM flow.audit(NULL, 'DISPENSE_FAILED', 'orders', o.id, jsonb_build_object('refund', v_ref, 'reason', p_reason));
      END IF;
    ELSIF o.status NOT IN ('EXPIRED', 'CANCELLED', 'DISPENSE_FAILED', 'REFUNDED') THEN
      RAISE EXCEPTION 'Khong the huy don dang mo cua hoac da nhan';
    END IF;
  END IF;
  UPDATE ordering.web_requests SET state = 'CANCELLED', quote_note = p_reason, updated_at = public.app_now() WHERE id = p_id;
  PERFORM flow.audit(p_actor, 'WEB_REQUEST_CANCELLED', 'web_requests', p_id);
END;
$$;

CREATE OR REPLACE FUNCTION flow.expire_web_orders() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE w record; n integer := 0;
BEGIN
  FOR w IN
    SELECT r.id FROM ordering.web_requests r JOIN ordering.orders o ON o.id = r.order_id
    WHERE r.kind = 'CUSTOM' AND r.state = 'ORDERED' AND o.status = 'PAID'
      AND r.pickup_before < public.app_now()
      AND NOT EXISTS (SELECT 1 FROM ordering.order_items i
        WHERE i.order_id = o.id AND i.item_type = 'BOUQUET' AND i.line_status = 'ACTIVE')
  LOOP
    PERFORM flow.web_cancel(w.id, flow.sys_user(), 'Shop chưa nạp hoa trước hạn nhận');
    n := n + 1;
  END LOOP;
  RETURN n;
END;
$$;

COMMIT;
