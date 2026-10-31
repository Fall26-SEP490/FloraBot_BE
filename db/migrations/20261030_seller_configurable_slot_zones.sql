-- The Operations Manager enrolls physical capacity only. Sellers choose the
-- commercial zone for each empty slot after the kiosk has been handed over.
BEGIN;

-- Preserve the old W/P naming convention for existing kiosks, where it records
-- the previously fixed zone. New generic slots are deliberately left unclassified.
UPDATE kiosk_ops.slots
SET zone = CASE
      WHEN slot_code ~ '^W[0-9]+$' THEN 'WALK_IN'
      WHEN slot_code ~ '^P[0-9]+$' THEN 'PREORDER'
    END,
    row_version = row_version + 1,
    updated_at = public.app_now()
WHERE zone IS NULL
  AND (slot_code ~ '^W[0-9]+$' OR slot_code ~ '^P[0-9]+$');

CREATE OR REPLACE FUNCTION flow.enroll_kiosk(
  p_code text,
  p_name text,
  p_hardware_id text,
  p_mqtt_client_id text,
  p_region text,
  p_slot_count integer,
  p_api_key_hash text,
  p_actor uuid
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_kiosk_id uuid := public.uuid_v7();
  i integer;
BEGIN
  IF p_code IS NULL OR length(btrim(p_code)) < 2 OR length(btrim(p_code)) > 50 THEN
    RAISE EXCEPTION 'Mã kiosk không hợp lệ (2-50 ký tự)' USING ERRCODE = '22023';
  END IF;
  IF p_name IS NULL OR length(btrim(p_name)) < 2 OR length(btrim(p_name)) > 120 THEN
    RAISE EXCEPTION 'Tên kiosk không hợp lệ (2-120 ký tự)' USING ERRCODE = '22023';
  END IF;
  IF p_hardware_id IS NULL OR length(btrim(p_hardware_id)) < 2 THEN
    RAISE EXCEPTION 'Hardware ID không được để trống' USING ERRCODE = '22023';
  END IF;
  IF p_slot_count IS NULL OR p_slot_count < 1 OR p_slot_count > 50 THEN
    RAISE EXCEPTION 'Tổng số ô phải từ 1 đến 50' USING ERRCODE = '22023';
  END IF;

  INSERT INTO kiosk_ops.kiosks (
    id, code, name, address, region, hardware_id, mqtt_client_id, status,
    last_heartbeat_at, api_key_hash
  ) VALUES (
    v_kiosk_id, btrim(p_code), btrim(p_name), '', coalesce(p_region, 'HCM'),
    btrim(p_hardware_id), coalesce(btrim(p_mqtt_client_id), 'kiosk-' || lower(btrim(p_code))),
    'OFFLINE', NULL, p_api_key_hash
  );

  FOR i IN 1..p_slot_count LOOP
    INSERT INTO kiosk_ops.slots (id, kiosk_id, slot_code, zone, relay_channel, status)
    VALUES (public.uuid_v7(), v_kiosk_id, 'S' || lpad(i::text, 2, '0'), NULL, i - 1, 'FREE');
  END LOOP;

  PERFORM flow.audit(p_actor, 'KIOSK_ENROLLED', 'kiosks', v_kiosk_id,
    jsonb_build_object('code', btrim(p_code), 'slot_count', p_slot_count, 'zone', NULL));

  RETURN v_kiosk_id;
END $$;

-- Keep the previous database signature available for an IIS rollback to the
-- previous API build. The old per-zone numbers are treated only as a total;
-- they never assign slot zones.
CREATE OR REPLACE FUNCTION flow.enroll_kiosk(
  p_code text,
  p_name text,
  p_hardware_id text,
  p_mqtt_client_id text,
  p_region text,
  p_walk_in integer,
  p_preorder integer,
  p_api_key_hash text,
  p_actor uuid
) RETURNS uuid LANGUAGE plpgsql AS $$
BEGIN
  IF p_walk_in IS NULL OR p_preorder IS NULL OR p_walk_in < 0 OR p_preorder < 0
     OR p_walk_in + p_preorder < 1 OR p_walk_in + p_preorder > 50 THEN
    RAISE EXCEPTION 'Tổng số ô phải từ 1 đến 50' USING ERRCODE = '22023';
  END IF;
  RETURN flow.enroll_kiosk(
    p_code, p_name, p_hardware_id, p_mqtt_client_id, p_region,
    p_walk_in + p_preorder, p_api_key_hash, p_actor
  );
END $$;

CREATE OR REPLACE FUNCTION flow.seller_set_slot_zone(
  p_kiosk_id uuid,
  p_slot_id uuid,
  p_actor uuid,
  p_zone text
) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_seller_id uuid;
  s kiosk_ops.slots;
BEGIN
  IF p_zone IS DISTINCT FROM 'WALK_IN' AND p_zone IS DISTINCT FROM 'PREORDER' THEN
    RAISE EXCEPTION 'Loại ô chỉ có thể là WALK_IN hoặc PREORDER' USING ERRCODE = '22023';
  END IF;

  SELECT u.seller_id INTO v_seller_id
  FROM identity.users u
  JOIN identity.sellers seller ON seller.id = u.seller_id AND seller.status = 'ACTIVE'
  JOIN kiosk_ops.kiosks k ON k.id = p_kiosk_id AND k.seller_id = u.seller_id
  WHERE u.id = p_actor AND u.role = 'SELLER' AND u.status = 'ACTIVE'
  FOR SHARE OF u, seller, k;

  IF v_seller_id IS NULL THEN
    RAISE EXCEPTION 'Seller không có quyền quản lý kiosk này' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot_id AND kiosk_id = p_kiosk_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không tìm thấy ô trong kiosk này' USING ERRCODE = 'P0002';
  END IF;

  IF s.current_seller_id IS DISTINCT FROM v_seller_id
     OR s.status <> 'RENTED_EMPTY'
     OR s.bouquet_id IS NOT NULL
     OR s.hold_order_id IS NOT NULL
     OR s.hold_until IS NOT NULL THEN
    RAISE EXCEPTION 'Chỉ đổi loại ô trống đang được shop thuê và chưa giữ cho đơn hàng' USING ERRCODE = '22023';
  END IF;

  UPDATE kiosk_ops.slots
  SET zone = p_zone, row_version = row_version + 1, updated_at = public.app_now()
  WHERE id = p_slot_id;

  PERFORM flow.audit(p_actor, 'SELLER_SLOT_ZONE_CHANGED', 'slots', p_slot_id,
    jsonb_build_object('kiosk', p_kiosk_id, 'from', s.zone, 'to', p_zone));
END $$;

-- Capacity is validated by total count. Snapshot the actual pre-existing zone
-- counts so the existing handover flow can still verify the installation.
CREATE OR REPLACE FUNCTION flow.kiosk_application_schedule(
  p_app_id uuid, p_actor uuid, p_tech_id uuid, p_kiosk_id uuid,
  p_scheduled_date date, p_time_window text, p_now timestamptz DEFAULT NULL
) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  u identity.users;
  k kiosk_ops.kiosks;
  v_w_count integer;
  v_p_count integer;
  v_total_slots integer;
  v_start_time time;
  v_end_time time;
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status NOT IN ('CONFIRMED', 'SCHEDULED') THEN
    RAISE EXCEPTION 'Chỉ lên lịch cho đơn đã xác nhận (CONFIRMED) hoặc đang đổi lịch (SCHEDULED)' USING ERRCODE = 'P0001';
  END IF;
  IF p_scheduled_date < v_now::date THEN
    RAISE EXCEPTION 'Không thể lên lịch cho ngày trong quá khứ' USING ERRCODE = '22023';
  END IF;
  IF p_time_window IS NULL OR p_time_window !~ '^([01][0-9]|2[0-3]):[0-5][0-9]\s*-\s*([01][0-9]|2[0-3]):[0-5][0-9]$' THEN
    RAISE EXCEPTION 'Khung giờ không hợp lệ (yêu cầu định dạng HH:mm - HH:mm)' USING ERRCODE = '22023';
  END IF;
  v_start_time := btrim(split_part(p_time_window, '-', 1))::time;
  v_end_time := btrim(split_part(p_time_window, '-', 2))::time;
  IF v_start_time >= v_end_time THEN
    RAISE EXCEPTION 'Khung giờ không hợp lệ (giờ kết thúc phải sau giờ bắt đầu)' USING ERRCODE = '22023';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('tech-schedule:' || p_tech_id::text, 0));
  SELECT * INTO u FROM identity.users WHERE id = p_tech_id FOR UPDATE;
  IF NOT FOUND OR u.role <> 'TECHNICIAN' OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Kỹ thuật viên không tồn tại hoặc không ở trạng thái hoạt động' USING ERRCODE = 'P0001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_technician_id = p_tech_id AND scheduled_date = p_scheduled_date
      AND status IN ('SCHEDULED', 'INSTALLING') AND id <> p_app_id
      AND (
        (start_time IS NOT NULL AND end_time IS NOT NULL AND start_time < v_end_time AND v_start_time < end_time)
        OR time_window = btrim(p_time_window)
      )
  ) THEN
    RAISE EXCEPTION 'Kỹ thuật viên đã có lịch trùng hoặc chồng chéo vào khung giờ này' USING ERRCODE = 'P0001';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-reserve:' || p_kiosk_id::text, 0));
  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = p_kiosk_id FOR UPDATE;
  IF NOT FOUND OR k.status IN ('DISABLED', 'MAINTENANCE') OR k.seller_id IS NOT NULL THEN
    RAISE EXCEPTION 'Kiosk không tồn tại, đã bị vô hiệu hóa hoặc đang bảo trì' USING ERRCODE = 'P0001';
  END IF;

  SELECT count(*) FILTER (WHERE zone = 'WALK_IN'),
         count(*) FILTER (WHERE zone = 'PREORDER'),
         count(*)
  INTO v_w_count, v_p_count, v_total_slots
  FROM kiosk_ops.slots WHERE kiosk_id = k.id;
  IF v_total_slots <> app.total_slots THEN
    RAISE EXCEPTION 'Kiosk cần có đúng tổng cộng % ô (hiện có %)', app.total_slots, v_total_slots USING ERRCODE = '22023';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots s
    WHERE s.kiosk_id = k.id
      AND (s.status <> 'FREE' OR s.current_seller_id IS NOT NULL OR s.bouquet_id IS NOT NULL
        OR s.hold_until IS NOT NULL OR s.hold_order_id IS NOT NULL
        OR EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a WHERE a.slot_id = s.id AND a.status = 'ACTIVE'))
  ) THEN
    RAISE EXCEPTION 'Kiosk % có ô không ở trạng thái sẵn sàng để gán (phải hoàn toàn FREE và chưa có shop thuê/chứa hoa)', k.id USING ERRCODE = 'P0001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_kiosk_id = p_kiosk_id AND id <> p_app_id
      AND status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER', 'ACTIVE')
  ) THEN
    RAISE EXCEPTION 'Kiosk % đã được giữ hoặc bàn giao cho đơn khác', k.id USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET assigned_technician_id = p_tech_id, assigned_kiosk_id = p_kiosk_id,
      scheduled_date = p_scheduled_date,
      time_window = to_char(v_start_time, 'HH24:MI') || ' - ' || to_char(v_end_time, 'HH24:MI'),
      start_time = v_start_time, end_time = v_end_time,
      walk_in_slots = v_w_count, preorder_slots = v_p_count,
      status = 'SCHEDULED', updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_SCHEDULED', 'kiosk_applications', app.id,
    jsonb_build_object('tech', p_tech_id, 'kiosk', p_kiosk_id, 'date', p_scheduled_date,
      'window', to_char(v_start_time, 'HH24:MI') || ' - ' || to_char(v_end_time, 'HH24:MI'),
      'slot_count', v_total_slots, 'walk_in_slots', v_w_count, 'preorder_slots', v_p_count));
END $$;

CREATE OR REPLACE FUNCTION flow.stock_bouquet(
  p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); pr catalog.flower_products; s kiosk_ops.slots; v_b uuid; v_seller_status text;
BEGIN
  IF p_batch IS NULL THEN RAISE EXCEPTION 'Thiếu mã lô nạp hàng (batch_id)'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product;
  IF NOT FOUND THEN RAISE EXCEPTION 'Sản phẩm % không tồn tại', p_product; END IF;
  IF pr.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Sản phẩm % chưa được bật bán', pr.name; END IF;
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = pr.seller_id;
  IF v_seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller đang % (gói quá hạn hoặc bị khóa), không nạp được', v_seller_status; END IF;
  PERFORM flow.check_seller_user(p_staff, pr.seller_id);
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status <> 'RENTED_EMPTY' OR s.current_seller_id IS DISTINCT FROM pr.seller_id THEN
    RAISE EXCEPTION 'Ô % không phải ô trống do seller này thuê (status %)', s.slot_code, s.status;
  END IF;
  IF s.zone IS DISTINCT FROM 'WALK_IN' THEN
    RAISE EXCEPTION 'Nạp bán sẵn chỉ được dùng ô WALK_IN đã được seller phân loại';
  END IF;
  IF NOT flow.slot_sellable(p_slot, pr.seller_id) THEN
    RAISE EXCEPTION 'Gói của seller đã hết hạn ngày % (hoặc ô đã bị thu hồi), không nạp được vào ô %', v_now::date, s.slot_code;
  END IF;
  INSERT INTO kiosk_ops.bouquets (product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, p_qr, pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now)
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, p_slot, 'STOCK_IN', p_batch, p_staff);
  RETURN v_b;
END $$;

CREATE OR REPLACE FUNCTION flow.staff_stock_bouquet(p_id uuid,p_task uuid,p_staff uuid,p_product uuid,p_slot uuid,p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks; previous kiosk_ops.staff_stock_actions; pr catalog.flower_products;
  s kiosk_ops.slots; v_b uuid; v_staff_role text; v_staff_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;
  IF v_staff_role IS NULL OR v_staff_role <> 'SELLER_STAFF' THEN
    RAISE EXCEPTION 'Active seller staff required' USING ERRCODE = '42501';
  END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id <> p_staff OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_staff_seller THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-stock:' || p_id::text, 0));
  SELECT * INTO previous FROM kiosk_ops.staff_stock_actions WHERE id = p_id;
  IF FOUND THEN
    IF previous.task_id <> p_task OR previous.actor_id <> p_staff OR previous.product_id <> p_product
       OR previous.slot_id <> p_slot OR previous.qr_code IS DISTINCT FROM btrim(p_qr) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN previous.bouquet_id;
  END IF;
  IF t.status <> 'IN_PROGRESS' THEN RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023'; END IF;
  IF p_id IS NULL OR p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN
    RAISE EXCEPTION 'Invalid stock reference' USING ERRCODE = '22023';
  END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR SHARE;
  IF NOT FOUND OR pr.seller_id IS DISTINCT FROM t.seller_id OR pr.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Product outside task scope' USING ERRCODE = '22023';
  END IF;
  PERFORM 1 FROM identity.sellers WHERE id = t.seller_id AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller inactive' USING ERRCODE = '22023'; END IF;
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = t.kiosk_id AND status NOT IN ('DISABLED', 'MAINTENANCE') FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE = '22023'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND OR s.kiosk_id <> t.kiosk_id OR s.current_seller_id IS DISTINCT FROM t.seller_id
     OR s.status <> 'RENTED_EMPTY' OR NOT flow.slot_sellable(p_slot, t.seller_id) THEN
    RAISE EXCEPTION 'Slot is unavailable or outside task scope' USING ERRCODE = '22023';
  END IF;
  IF s.zone IS DISTINCT FROM 'WALK_IN' THEN
    RAISE EXCEPTION 'Nạp bán sẵn chỉ được dùng ô WALK_IN đã được seller phân loại' USING ERRCODE = '22023';
  END IF;
  INSERT INTO kiosk_ops.bouquets(product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, btrim(p_qr), pr.price, 'STOCKED', public.app_now() + make_interval(hours => pr.shelf_life_hours), public.app_now())
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = s.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, s.id, 'STOCK_IN', p_task, p_staff);
  INSERT INTO kiosk_ops.staff_stock_actions(id, task_id, actor_id, product_id, slot_id, qr_code, bouquet_id)
  VALUES (p_id, p_task, p_staff, pr.id, s.id, btrim(p_qr), v_b);
  PERFORM flow.audit(p_staff, 'STAFF_TASK_STOCKED', 'staff_tasks', p_task,
    jsonb_build_object('operation', p_id, 'bouquet', v_b, 'slot', s.id));
  RETURN v_b;
END $$;

-- Web preorders require at least one explicitly configured PREORDER slot.
CREATE OR REPLACE FUNCTION flow.web_request(p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid, p_bouquet uuid, p_pickup timestamp with time zone, p_instructions text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE pr catalog.flower_products; w ordering.web_requests;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:' || p_id::text, 0));
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tai khoan khong hop le';
  END IF;
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND w.request_payload IS DISTINCT FROM jsonb_build_object('bouquet', p_bouquet, 'pickup', p_pickup) THEN
      RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác';
    END IF;
    IF w.customer_id <> p_customer OR w.product_id <> p_product OR w.kiosk_id <> p_kiosk OR w.kind <> p_kind OR w.instructions <> p_instructions THEN
      RAISE EXCEPTION 'Ma yeu cau da duoc su dung';
    END IF;
    RETURN w.id;
  END IF;
  IF p_kind IS DISTINCT FROM 'CUSTOM' THEN RAISE EXCEPTION 'Web chỉ nhận đơn đặt hoa theo mẫu'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product AND status = 'ACTIVE';
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id = pr.seller_id AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don';
  END IF;
  IF length(p_instructions) > 2000 OR NOT EXISTS(
    SELECT 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk AND status NOT IN ('DISABLED', 'MAINTENANCE')
      AND length(btrim(coalesce(address, ''))) > 0 AND latitude IS NOT NULL AND longitude IS NOT NULL
  ) THEN RAISE EXCEPTION 'Thong tin dat hoa khong hop le'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND status NOT IN ('FREE', 'PENDING_RELEASE')) THEN
    RAISE EXCEPTION 'Shop khong phuc vu tu nay';
  END IF;
  IF length(btrim(p_instructions)) < 10 OR p_pickup < public.app_now() + interval '2 hours' OR p_pickup > public.app_now() + interval '30 days' THEN
    RAISE EXCEPTION 'Mo ta it nhat 10 ky tu, hen nhan tu 2 gio den 30 ngay';
  END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND zone = 'PREORDER') THEN
    RAISE EXCEPTION 'Kiosk chưa có ô PREORDER do seller phân loại';
  END IF;
  INSERT INTO ordering.web_requests(id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, state)
  VALUES(p_id, p_customer, pr.seller_id, pr.id, p_kiosk, 'CUSTOM', p_instructions, p_pickup,
    p_pickup + make_interval(hours => flow.cfg('web_pickup_hours')::int), 'REQUESTED');
  UPDATE ordering.web_requests SET request_payload = jsonb_build_object('bouquet', NULL, 'pickup', p_pickup) WHERE id = p_id;
  UPDATE ordering.web_requests SET display_snapshot = (
    SELECT jsonb_build_object('product', pr.name, 'shop', s.shop_name, 'kiosk', k.name, 'address', k.address)
    FROM identity.sellers s CROSS JOIN kiosk_ops.kiosks k WHERE s.id = pr.seller_id AND k.id = p_kiosk
  ) WHERE id = p_id;
  PERFORM flow.audit(p_customer, 'WEB_REQUEST_CREATED', 'web_requests', p_id, jsonb_build_object('kind', 'CUSTOM'));
  RETURN p_id;
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
  IF v_now < w.pickup_at - interval '2 hours' OR v_now >= w.pickup_before THEN
    RAISE EXCEPTION 'Nap hoa trong 2 gio truoc hen va truoc han nhan';
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

CREATE OR REPLACE FUNCTION flow.staff_fulfill_preorder(p_task uuid, p_request uuid, p_actor uuid, p_slot uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks; v_role text; v_seller uuid; w ordering.web_requests;
BEGIN
  SELECT role, seller_id INTO v_role, v_seller FROM identity.users WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  IF v_role IS DISTINCT FROM 'SELLER_STAFF' THEN RAISE EXCEPTION 'Active seller staff required' USING ERRCODE = '42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id <> p_actor OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_seller THEN
    RAISE EXCEPTION 'Assigned delivery task not found' USING ERRCODE = 'P0002';
  END IF;
  IF t.status <> 'IN_PROGRESS' THEN RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023'; END IF;
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_request AND seller_id = t.seller_id AND kiosk_id = t.kiosk_id;
  IF NOT FOUND THEN RAISE EXCEPTION 'Preorder is outside this task' USING ERRCODE = 'P0002'; END IF;
  RETURN flow.web_fulfill(p_request, t.seller_id, p_actor, p_slot, p_qr);
END $$;

COMMIT;
