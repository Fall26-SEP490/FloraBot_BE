-- A shop owner retains the same purchasing identity and points.
BEGIN;
ALTER TABLE identity.users DROP CONSTRAINT IF EXISTS users_check3;
ALTER TABLE identity.users ADD CONSTRAINT users_check3
  CHECK (role IN ('CUSTOMER','SELLER') OR loyalty_points = 0);

CREATE OR REPLACE FUNCTION flow.open_member_shop(p_user uuid,p_shop text,p_phone text,p_address text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE u identity.users; v uuid;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id=p_user FOR UPDATE;
  IF NOT FOUND OR u.status<>'ACTIVE' OR u.role NOT IN ('CUSTOMER','SELLER') OR u.password_hash IS NULL THEN
    RAISE EXCEPTION 'Active member account required' USING ERRCODE='42501';
  END IF;
  IF p_shop IS NULL OR length(btrim(p_shop)) NOT BETWEEN 2 AND 120
     OR p_phone IS NULL OR p_phone !~ '^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$'
     OR p_address IS NULL OR length(btrim(p_address)) NOT BETWEEN 1 AND 200 THEN
    RAISE EXCEPTION 'Invalid shop details' USING ERRCODE='22023';
  END IF;
  IF u.seller_id IS NOT NULL THEN RETURN u.seller_id; END IF;
  INSERT INTO identity.sellers(shop_name,phone,address)
    VALUES(btrim(p_shop),p_phone,btrim(p_address)) RETURNING id INTO v;
  UPDATE identity.users SET role='SELLER',seller_id=v WHERE id=u.id;
  PERFORM flow.audit(u.id,'MEMBER_SHOP_OPENED','sellers',v,jsonb_build_object('shop',btrim(p_shop)));
  RETURN v;
END $$;


CREATE OR REPLACE FUNCTION flow.redeem_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_bal bigint;
BEGIN
  SELECT loyalty_points INTO v_bal FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') FOR UPDATE;
  IF v_bal IS NULL THEN RAISE EXCEPTION 'Tài khoản khách % không hợp lệ', p_customer; END IF;
  IF p_points > v_bal THEN RAISE EXCEPTION 'Chỉ còn % điểm, không đủ %', v_bal, p_points; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points - p_points WHERE id = p_customer;
  PERFORM flow.audit(p_customer, 'POINTS_REDEEMED', 'orders', p_order, jsonb_build_object('points', p_points));
END $$;

CREATE OR REPLACE FUNCTION flow.restore_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_RESTORED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points, 'restore', true)); END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.credit_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_EARNED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points)); END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.web_request(p_id uuid,p_customer uuid,p_kind text,p_product uuid,p_kiosk uuid,p_bouquet uuid,p_pickup timestamptz,p_instructions text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE pr catalog.flower_products; w ordering.web_requests; v_checkout uuid; v_order uuid; v_end timestamptz;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:'||p_id::text,0));
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND w.request_payload IS DISTINCT FROM jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) THEN RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác'; END IF;
    IF w.customer_id<>p_customer OR w.product_id<>p_product OR w.kiosk_id<>p_kiosk OR w.kind<>p_kind OR w.instructions<>p_instructions THEN RAISE EXCEPTION 'Ma yeu cau da duoc su dung'; END IF;
    RETURN w.id;
  END IF;
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=p_customer AND role IN ('CUSTOMER','SELLER') AND status='ACTIVE') THEN RAISE EXCEPTION 'Tai khoan khong hop le'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id=p_product AND status='ACTIVE';
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id=pr.seller_id AND status='ACTIVE') THEN RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don'; END IF;
  IF p_kind NOT IN ('STOCK','CUSTOM') OR length(p_instructions)>2000 OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE id=p_kiosk AND status NOT IN ('DISABLED','MAINTENANCE')) THEN RAISE EXCEPTION 'Thong tin dat hoa khong hop le'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=pr.seller_id AND status NOT IN ('FREE','PENDING_RELEASE')) THEN RAISE EXCEPTION 'Shop khong phuc vu tu nay'; END IF;
  IF p_kind='CUSTOM' THEN
    IF length(btrim(p_instructions))<10 OR p_pickup<public.app_now()+interval '2 hours' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Mo ta it nhat 10 ky tu, hen nhan tu 2 gio den 30 ngay'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,p_pickup,p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),'REQUESTED');
  ELSE
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id=b.id WHERE b.id=p_bouquet AND b.product_id=pr.id AND s.kiosk_id=p_kiosk) THEN RAISE EXCEPTION 'Bo hoa khong thuoc tu nay'; END IF;
    v_checkout:=flow.kiosk_checkout(p_kiosk,ARRAY[p_bouquet],p_customer);
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id=v_checkout;
    SELECT least(sellable_until,public.app_now()+make_interval(hours=>flow.cfg('web_pickup_hours')::int)) INTO v_end FROM kiosk_ops.bouquets WHERE id=p_bouquet;
    IF v_end<=public.app_now()+flow.cfg_min('hold_minutes') THEN RAISE EXCEPTION 'Hoa khong con du thoi gian de dat truoc'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,quoted_price,state,order_id)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,public.app_now(),v_end,(SELECT total_amount FROM ordering.orders WHERE id=v_order),'ORDERED',v_order);
  END IF;
  UPDATE ordering.web_requests SET request_payload=jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) WHERE id=p_id;
  UPDATE ordering.web_requests SET display_snapshot=(SELECT jsonb_build_object('product',pr.name,'shop',s.shop_name,'kiosk',k.name,'address',k.address) FROM identity.sellers s CROSS JOIN kiosk_ops.kiosks k WHERE s.id=pr.seller_id AND k.id=p_kiosk) WHERE id=p_id;
  PERFORM flow.audit(p_customer,'WEB_REQUEST_CREATED','web_requests',p_id,jsonb_build_object('kind',p_kind));
  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION flow.kiosk_checkout(p_kiosk uuid, p_bouquets uuid[], p_customer uuid DEFAULT NULL, p_accessories jsonb DEFAULT '[]',
                                               p_points bigint DEFAULT 0, p_ecard text DEFAULT NULL, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_checkout uuid := public.uuid_v7(); r record; v_order uuid; v_total bigint;
        v_kstatus text; v_target uuid; v_sub bigint; v_sellers uuid[]; v_accs jsonb; v_cap bigint;
BEGIN
  p_bouquets := coalesce(p_bouquets, '{}'); p_points := coalesce(p_points, 0);
  SELECT status INTO v_kstatus FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  IF v_kstatus IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk không hoạt động (%)', v_kstatus; END IF;
  IF cardinality(p_bouquets) = 0 AND jsonb_array_length(coalesce(p_accessories, '[]')) = 0 THEN RAISE EXCEPTION 'Giỏ hàng trống'; END IF;
  IF p_ecard IS NOT NULL AND length(p_ecard) > 150 THEN RAISE EXCEPTION 'Lời thiệp tối đa 150 ký tự'; END IF;
  IF p_customer IS NOT NULL AND NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tài khoản khách % không hợp lệ hoặc đã bị xóa', p_customer; END IF;
  -- khóa ô và bó theo thứ tự id để tránh deadlock khi 2 khách cùng bấm
  FOR r IN SELECT b.id AS bouquet_id, b.status AS bstatus, b.sellable_until, s.id AS slot_id, s.kiosk_id, s.status AS sstatus, s.current_seller_id, p.name, sl.status AS seller_status
           FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id = b.id JOIN catalog.flower_products p ON p.id = b.product_id
           JOIN identity.sellers sl ON sl.id = b.seller_id
           WHERE b.id = ANY(p_bouquets) ORDER BY s.id FOR UPDATE OF s, b
  LOOP
    IF r.kiosk_id <> p_kiosk OR r.sstatus <> 'STOCKED' OR r.bstatus <> 'STOCKED' THEN RAISE EXCEPTION 'Bó % không còn sẵn để bán', r.name; END IF;
    IF r.sellable_until <= v_now THEN RAISE EXCEPTION 'Bó % đã quá hạn bán', r.name; END IF;
    IF r.seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller của bó % đang % (tạm ngưng bán)', r.name, r.seller_status; END IF;
    IF NOT flow.slot_sellable(r.slot_id, r.current_seller_id) THEN RAISE EXCEPTION 'Gói của seller bán bó % đã hết hạn, không bán', r.name; END IF;
  END LOOP;
  IF (SELECT count(*) FROM kiosk_ops.slots WHERE bouquet_id = ANY(p_bouquets) AND status = 'STOCKED') <> cardinality(p_bouquets) THEN
    RAISE EXCEPTION 'Có bó không còn trong ô'; END IF;
  v_accs := flow.take_accessories(p_kiosk, p_accessories);

  SELECT array_agg(DISTINCT x ORDER BY x) INTO v_sellers FROM (
    SELECT seller_id AS x FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets)
    UNION SELECT (e->>'seller_id')::uuid FROM jsonb_array_elements(v_accs) e) z;
  FOR r IN SELECT unnest(v_sellers) AS seller_id LOOP
    v_sub := coalesce((SELECT sum(price_snapshot) FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets) AND seller_id = r.seller_id), 0)
           + coalesce((SELECT sum((e->>'price')::bigint * (e->>'qty')::int) FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id), 0);
    INSERT INTO ordering.orders (order_code, checkout_id, customer_id, seller_id, kiosk_id, subtotal, total_amount, tracking_token, ecard_content)
    VALUES (flow.new_order_code(), v_checkout, p_customer, r.seller_id, p_kiosk, v_sub, v_sub, flow.new_tracking_token(), p_ecard)
    RETURNING id INTO v_order;
    INSERT INTO ordering.order_items (order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'BOUQUET', b.id, s.id, p.name, 1, b.price_snapshot, b.price_snapshot
      FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id = b.id JOIN catalog.flower_products p ON p.id = b.product_id
     WHERE b.id = ANY(p_bouquets) AND b.seller_id = r.seller_id;
    INSERT INTO ordering.order_items (order_id, item_type, accessory_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'ACCESSORY', (e->>'id')::uuid, e->>'name', (e->>'qty')::int, (e->>'price')::bigint, (e->>'qty')::int * (e->>'price')::bigint
      FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id;
    PERFORM flow.audit(p_kiosk, 'ORDER_STATUS_CHANGED', 'orders', v_order, jsonb_build_object('from', NULL, 'to', 'AWAITING_PAYMENT'), 'KIOSK');
  END LOOP;

  -- đổi điểm: chỉ khách đã đăng nhập; trừ ngay (trả lại nếu đơn hết hạn/hủy); nền tảng chịu phần giảm; áp cho đơn lớn nhất
  IF p_points > 0 THEN
    IF p_customer IS NULL THEN RAISE EXCEPTION 'Đổi điểm cần đăng nhập'; END IF;
    SELECT id, subtotal INTO v_target, v_sub FROM ordering.orders WHERE checkout_id = v_checkout ORDER BY subtotal DESC, id LIMIT 1;
    v_cap := floor(v_sub * flow.cfg('points_max_redeem_percent') / 100);
    IF p_points > v_cap THEN RAISE EXCEPTION 'Đổi điểm tối đa % đồng (%%% giá trị hàng)', v_cap, flow.cfg('points_max_redeem_percent'); END IF;
    UPDATE ordering.orders SET discount_amount = p_points, points_redeemed = p_points, total_amount = subtotal - p_points WHERE id = v_target;
    PERFORM flow.redeem_points(p_customer, p_points, v_target);   -- lệnh sang identity service (PointsRedeemed)
  END IF;
  -- điểm sẽ được cộng khi đơn COMPLETED
  UPDATE ordering.orders SET points_earned = CASE WHEN p_customer IS NULL THEN 0 ELSE floor(total_amount * flow.cfg('points_rate_percent') / 100) END
   WHERE checkout_id = v_checkout;

  -- giữ ô trong lúc khách quét mã thanh toán (BRD: TTL 7 phút)
  FOR r IN SELECT oi.slot_id, oi.bouquet_id, oi.order_id FROM ordering.order_items oi JOIN ordering.orders o ON o.id = oi.order_id
           WHERE o.checkout_id = v_checkout AND oi.item_type = 'BOUQUET'
  LOOP
    UPDATE kiosk_ops.slots SET status = 'HELD', hold_until = v_now + flow.cfg_min('hold_minutes'), hold_order_id = r.order_id, row_version = row_version + 1 WHERE id = r.slot_id;
    UPDATE kiosk_ops.bouquets SET status = 'HELD' WHERE id = r.bouquet_id;
    INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id, reason) VALUES (r.bouquet_id, r.slot_id, 'HOLD', r.order_id, 'Giữ chờ thanh toán');
  END LOOP;
  SELECT sum(total_amount) INTO v_total FROM ordering.orders WHERE checkout_id = v_checkout;
  IF v_total <= 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không tạo được khoản thu'; END IF;
  INSERT INTO payment.payments (kind, purpose, checkout_id, gateway, idempotency_key, amount)
  VALUES ('CHARGE','ORDER_CHECKOUT', v_checkout, 'PAYOS', 'co-' || v_checkout, v_total);
  RETURN v_checkout;
END $$;

COMMIT;
