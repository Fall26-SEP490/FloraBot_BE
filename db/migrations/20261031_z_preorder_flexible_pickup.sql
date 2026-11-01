-- Allow near-term ordering and early stocking; retain future appointments and final deadlines.
BEGIN;

CREATE OR REPLACE FUNCTION flow.web_request(
  p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid,
  p_bouquet uuid, p_pickup timestamptz, p_instructions text
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  pr catalog.flower_products;
  w ordering.web_requests;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:' || p_id::text, 0));
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tai khoan khong hop le';
  END IF;

  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND w.request_payload IS DISTINCT FROM jsonb_build_object('bouquet', p_bouquet, 'pickup', p_pickup) THEN
      RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác';
    END IF;
    IF w.customer_id <> p_customer OR w.product_id <> p_product OR w.kiosk_id <> p_kiosk
       OR w.kind <> p_kind OR w.instructions <> p_instructions THEN
      RAISE EXCEPTION 'Ma yeu cau da duoc su dung';
    END IF;
    RETURN w.id;
  END IF;

  IF p_kind IS DISTINCT FROM 'CUSTOM' THEN RAISE EXCEPTION 'Web chỉ nhận đơn đặt hoa theo mẫu'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product AND status = 'ACTIVE';
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id)
     OR NOT EXISTS (SELECT 1 FROM identity.sellers WHERE id = pr.seller_id AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don';
  END IF;
  IF NOT flow.kiosk_package_valid(p_kiosk) OR length(p_instructions) > 2000 OR NOT EXISTS (
    SELECT 1 FROM kiosk_ops.kiosks
    WHERE id = p_kiosk AND seller_id = pr.seller_id AND status NOT IN ('DISABLED', 'MAINTENANCE')
      AND length(btrim(coalesce(address, ''))) > 0 AND latitude IS NOT NULL AND longitude IS NOT NULL
  ) THEN RAISE EXCEPTION 'Thông tin đặt hoa hoặc gói kiosk không hợp lệ'; END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND status NOT IN ('FREE', 'PENDING_RELEASE')
  ) THEN RAISE EXCEPTION 'Shop không phục vụ từ này'; END IF;
  IF length(btrim(p_instructions)) < 10 OR p_pickup <= public.app_now()
     OR p_pickup > public.app_now() + interval '30 days' THEN
    RAISE EXCEPTION 'Mô tả tối thiểu 10 ký tự, hẹn nhận sau thời điểm hiện tại và trong 30 ngày';
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND zone = 'PREORDER'
  ) THEN RAISE EXCEPTION 'Kiosk chưa có ô PREORDER do seller phân loại'; END IF;

  INSERT INTO ordering.web_requests(
    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, state
  ) VALUES (
    p_id, p_customer, pr.seller_id, pr.id, p_kiosk, 'CUSTOM', p_instructions, p_pickup,
    p_pickup + make_interval(hours => flow.cfg('web_pickup_hours')::integer), 'REQUESTED'
  );
  UPDATE ordering.web_requests SET request_payload = jsonb_build_object('bouquet', NULL, 'pickup', p_pickup) WHERE id = p_id;
  UPDATE ordering.web_requests SET display_snapshot = (
    SELECT jsonb_build_object('product', pr.name, 'shop', s.shop_name, 'kiosk', k.name, 'address', k.address)
    FROM identity.sellers s CROSS JOIN kiosk_ops.kiosks k WHERE s.id = pr.seller_id AND k.id = p_kiosk
  ) WHERE id = p_id;
  PERFORM flow.audit(p_customer, 'WEB_REQUEST_CREATED', 'web_requests', p_id, jsonb_build_object('kind', 'CUSTOM'));
  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION flow.web_quote(p_id uuid,p_seller uuid,p_actor uuid,p_price bigint,p_pickup timestamptz,p_note text,p_reject boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  IF w.state NOT IN ('REQUESTED','QUOTED') OR w.kind<>'CUSTOM' THEN RAISE EXCEPTION 'Yeu cau khong con cho bao gia'; END IF;
  IF length(btrim(p_note))<5 OR length(p_note)>1000 THEN RAISE EXCEPTION 'Ghi chu can tu 5 den 1000 ky tu'; END IF;
  IF p_reject THEN UPDATE ordering.web_requests SET state='REJECTED',quote_note=p_note,updated_at=public.app_now() WHERE id=p_id;
  ELSE
    IF p_price NOT BETWEEN 1000 AND 100000000 OR p_pickup<=public.app_now() OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Gia hoac lich nhan khong hop le'; END IF;
    UPDATE ordering.web_requests SET state='QUOTED',quoted_price=p_price,quote_note=p_note,pickup_at=p_pickup,
      pickup_before=p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),
      quote_expires_at=least(p_pickup,public.app_now()+make_interval(hours=>flow.cfg('web_quote_hours')::int)),updated_at=public.app_now() WHERE id=p_id;
  END IF;
  PERFORM flow.audit(p_actor,'WEB_REQUEST_QUOTED','web_requests',p_id);
END $$;

CREATE OR REPLACE FUNCTION flow.web_fulfill(p_id uuid, p_seller uuid, p_actor uuid, p_slot uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; o ordering.orders; s kiosk_ops.slots; pr catalog.flower_products; v_seller_status text; b uuid; v_now timestamptz := public.app_now();
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND seller_id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor, p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id = w.order_id FOR UPDATE;
  IF w.kind <> 'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF v_now >= w.pickup_before THEN
    RAISE EXCEPTION 'Can nap hoa truoc han nhan';
  END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = w.product_id FOR SHARE;
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
  VALUES (b, pr.id, p_seller, btrim(p_qr), pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now);
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (b, p_slot, 'STOCK_IN', flow.new_batch(), p_actor);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id = b) < w.pickup_before THEN
    RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan';
  END IF;
  UPDATE kiosk_ops.bouquets SET status = 'SOLD', price_snapshot = o.total_amount WHERE id = b;
  UPDATE kiosk_ops.slots SET status = 'HELD', hold_order_id = o.id, hold_until = w.pickup_before, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO ordering.order_items(order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
  VALUES(o.id, 'BOUQUET', b, p_slot, 'Hoa dat rieng: ' || pr.name, 1, o.total_amount, o.total_amount);
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, order_id, performed_by)
  VALUES(b, p_slot, 'SALE_OUT', o.id, p_actor);
  PERFORM flow.audit(p_actor, 'WEB_ORDER_READY', 'orders', o.id, jsonb_build_object('request', p_id, 'slot', p_slot));
  RETURN b;
END $$;

COMMIT;
