-- Các "lệnh nghiệp vụ" mô phỏng service (v3). Trong hệ thống thật mỗi bước là API của một service
-- và các bước chéo service đi qua sự kiện (RabbitMQ + CAP outbox, xem hợp đồng sự kiện ở đầu 01_schema.sql).
-- Ở demo gom vào hàm SQL để chạy lại được trọn luồng và kiểm tra ràng buộc dữ liệu.
-- Quy ước thời gian: mọi hàm nhận p_now (NULL = giờ hiện tại, hoặc giờ đã "tua" trước đó trong cùng giao dịch).
-- Thông báo realtime (SignalR/email) KHÔNG lưu DB: chỗ nào v2 gửi notification, v3 ghi audit hoặc không làm gì.
CREATE SCHEMA IF NOT EXISTS flow;

-- ===================== tiện ích chung =====================
CREATE OR REPLACE FUNCTION flow.tick(p_now timestamptz) RETURNS timestamptz LANGUAGE plpgsql AS $$
BEGIN
  IF p_now IS NOT NULL THEN PERFORM set_config('app.now', p_now::text, true); END IF;
  RETURN public.app_now();
END $$;
CREATE OR REPLACE FUNCTION flow.reset_clock() RETURNS void LANGUAGE sql AS $$ SELECT set_config('app.now', '', true); $$;
CREATE OR REPLACE FUNCTION flow.sys_user() RETURNS uuid LANGUAGE sql IMMUTABLE AS $$ SELECT '10000000-0000-0000-0000-0000000000ff'::uuid $$;

CREATE OR REPLACE FUNCTION flow.cfg(p_key text) RETURNS numeric LANGUAGE plpgsql STABLE AS $$
DECLARE v jsonb;
BEGIN
  SELECT value INTO v FROM kiosk_ops.system_settings WHERE key = p_key;
  IF NOT FOUND THEN RAISE EXCEPTION 'Thiếu tham số hệ thống %', p_key; END IF;
  RETURN (v #>> '{}')::numeric;
END $$;
CREATE OR REPLACE FUNCTION flow.cfg_min(p_key text) RETURNS interval LANGUAGE sql STABLE AS $$ SELECT make_interval(mins => flow.cfg(p_key)::int) $$;
CREATE OR REPLACE FUNCTION flow.cfg_hour(p_key text) RETURNS interval LANGUAGE sql STABLE AS $$ SELECT make_interval(hours => flow.cfg(p_key)::int) $$;
CREATE OR REPLACE FUNCTION flow.set_cfg(p_key text, p_value numeric, p_admin uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE kiosk_ops.system_settings SET value = to_jsonb(p_value), updated_by = p_admin, updated_at = public.app_now() WHERE key = p_key;
  IF NOT FOUND THEN RAISE EXCEPTION 'Tham số % không tồn tại', p_key; END IF;
  PERFORM flow.audit(p_admin, 'SETTING_CHANGED', 'system_settings', NULL, jsonb_build_object('key', p_key, 'value', p_value));
END $$;

CREATE OR REPLACE FUNCTION flow.audit(p_actor uuid, p_action text, p_entity text, p_id uuid, p_payload jsonb DEFAULT NULL, p_actor_type text DEFAULT NULL)
RETURNS void LANGUAGE sql AS $$
  INSERT INTO notify.audit_logs (actor_type, actor_id, action, entity_type, entity_id, payload)
  VALUES (coalesce(p_actor_type, CASE WHEN p_actor IS NULL OR p_actor = flow.sys_user() THEN 'SYSTEM' ELSE 'USER' END), p_actor, p_action, p_entity, p_id, p_payload);
$$;
CREATE OR REPLACE FUNCTION flow.attach(p_service text, p_owner_type text, p_owner uuid, p_url text, p_phase text, p_user uuid) RETURNS uuid LANGUAGE sql AS $$
  INSERT INTO notify.attachments (owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by)
  VALUES (p_service, p_owner_type, p_owner, p_url,
          CASE WHEN p_url ILIKE '%.pdf' THEN 'application/pdf' WHEN p_url ILIKE '%.png' THEN 'image/png' WHEN p_url ILIKE '%.webp' THEN 'image/webp' ELSE 'image/jpeg' END,
          100000 + abs(hashtext(p_url)) % 900000, encode(sha256(convert_to(p_url, 'UTF8')), 'hex'), p_phase, p_user)
  RETURNING id;
$$;
-- mã e-receipt 8 ký tự (bảng chữ không có O/0/I/1 để gõ được trên màn kiosk), ~1e12 tổ hợp, in kèm QR
CREATE OR REPLACE FUNCTION flow.new_tracking_token() RETURNS text LANGUAGE plpgsql VOLATILE AS $$
DECLARE a text := 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789'; b bytea := gen_random_bytes(8); r text := ''; i int;
BEGIN
  FOR i IN 0..7 LOOP r := r || substr(a, 1 + get_byte(b, i) % 32, 1); END LOOP;
  RETURN r;
END $$;
CREATE OR REPLACE FUNCTION flow.new_order_code() RETURNS text LANGUAGE sql VOLATILE AS $$
  SELECT 'FB' || to_char(public.app_now(), 'YYMMDD') || '-' || upper(substr(md5(random()::text || clock_timestamp()::text), 1, 6)) $$;

-- máy trạng thái đơn: cạnh hợp lệ kiểm ở trigger public.guard_transition; ở đây ghi lịch sử vào audit (thay order_events)
CREATE OR REPLACE FUNCTION flow.set_order_status(p_order uuid, p_to text, p_actor_type text, p_actor uuid DEFAULT NULL, p_note text DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_from text;
BEGIN
  SELECT status INTO v_from FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  UPDATE ordering.orders SET status = p_to,
         completed_at = CASE WHEN p_to = 'COMPLETED' AND completed_at IS NULL THEN public.app_now() ELSE completed_at END
   WHERE id = p_order;
  PERFORM flow.audit(p_actor, 'ORDER_STATUS_CHANGED', 'orders', p_order, jsonb_build_object('from', v_from, 'to', p_to, 'note', p_note),
                     CASE p_actor_type WHEN 'KIOSK' THEN 'KIOSK' WHEN 'SYSTEM' THEN 'SYSTEM' ELSE 'USER' END);
END $$;
-- lịch sử trạng thái của một đơn (màn hình chi tiết đơn)
CREATE OR REPLACE FUNCTION flow.order_history(p_order uuid) RETURNS TABLE (at timestamptz, from_status text, to_status text, note text) LANGUAGE sql STABLE AS $$
  SELECT created_at, payload->>'from', payload->>'to', payload->>'note' FROM notify.audit_logs
   WHERE entity_type = 'orders' AND entity_id = p_order AND action = 'ORDER_STATUS_CHANGED' ORDER BY id $$;

-- ô trống trở lại: ô vẫn đang gán cho seller (slot_assignments ACTIVE) thì RENTED_EMPTY, không thì trả về FREE
CREATE OR REPLACE FUNCTION flow.release_slot_empty(p_slot uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE v_has boolean; v_status text;
BEGIN
  SELECT EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a JOIN kiosk_ops.slots s ON s.id = a.slot_id
                 WHERE a.slot_id = p_slot AND a.seller_id = s.current_seller_id AND a.status = 'ACTIVE') INTO v_has;
  UPDATE kiosk_ops.slots SET status = CASE WHEN v_has THEN 'RENTED_EMPTY' ELSE 'FREE' END,
         current_seller_id = CASE WHEN v_has THEN current_seller_id END,
         bouquet_id = NULL, hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1
   WHERE id = p_slot RETURNING status INTO v_status;
  RETURN v_status;
END $$;
-- gói của seller còn hạn theo NGÀY (không chờ job): hết hạn là khóa bán/nạp ngay, job roll_subscriptions chỉ đổi status + ẩn catalog
CREATE OR REPLACE FUNCTION flow.package_valid(p_seller uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM identity.sellers WHERE id = p_seller AND package_expires_at IS NOT NULL AND package_expires_at > public.app_now()::date) $$;
-- ô bán được cho seller: ô đang gán cho seller đó VÀ gói seller còn hạn
CREATE OR REPLACE FUNCTION flow.slot_sellable(p_slot uuid, p_seller uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE slot_id = p_slot AND seller_id = p_seller AND status = 'ACTIVE')
         AND flow.package_valid(p_seller) $$;
-- người thao tác phải là tài khoản của seller (hoặc admin)
CREATE OR REPLACE FUNCTION flow.check_seller_user(p_user uuid, p_seller uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE u identity.users;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id = p_user;
  IF NOT FOUND THEN RAISE EXCEPTION 'Người dùng % không tồn tại', p_user; END IF;
  IF u.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Tài khoản % đang %', u.full_name, u.status; END IF;
  IF u.role <> 'ADMIN' AND u.seller_id IS DISTINCT FROM p_seller THEN RAISE EXCEPTION 'Người dùng % không thuộc seller này', u.full_name; END IF;
END $$;

-- ===================== quản trị danh mục =====================
-- khách đăng nhập tại kiosk bằng SĐT + OTP (OTP kiểm ở tầng ứng dụng/Valkey); DB chỉ tìm-hoặc-tạo tài khoản
CREATE OR REPLACE FUNCTION flow.customer_by_phone(p_phone text, p_name text DEFAULT NULL, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  IF p_phone !~ '^0\d{9}$' THEN RAISE EXCEPTION 'Số điện thoại % không hợp lệ', p_phone; END IF;
  SELECT id INTO v FROM identity.users WHERE phone = p_phone;
  IF FOUND THEN
    IF (SELECT role FROM identity.users WHERE id = v) <> 'CUSTOMER' THEN RAISE EXCEPTION 'SĐT thuộc tài khoản nội bộ, không dùng tích điểm'; END IF;
    RETURN v;
  END IF;
  INSERT INTO identity.users (phone, full_name, role) VALUES (p_phone, coalesce(p_name, 'Khách ' || right(p_phone, 4)), 'CUSTOMER') RETURNING id INTO v;
  PERFORM flow.audit(v, 'CUSTOMER_REGISTERED', 'users', v);
  RETURN v;
END $$;
-- khách tự xóa tài khoản (NĐ 13/2023): xóa PII, giữ id để đơn cũ không gãy
CREATE OR REPLACE FUNCTION flow.forget_customer(p_customer uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE identity.users SET phone = NULL, email = 'deleted-' || id || '@florabot.invalid', full_name = 'Khách đã xóa', status = 'DISABLED', loyalty_points = 0
   WHERE id = p_customer AND role = 'CUSTOMER';
  IF NOT FOUND THEN RAISE EXCEPTION 'Khách % không tồn tại', p_customer; END IF;
  PERFORM flow.audit(p_customer, 'CUSTOMER_FORGOTTEN', 'users', p_customer);
END $$;
CREATE OR REPLACE FUNCTION flow.register_seller(p_shop text, p_phone text, p_address text, p_owner_name text, p_owner_email text, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  INSERT INTO identity.sellers (shop_name, phone, address) VALUES (p_shop, p_phone, p_address) RETURNING id INTO v;
  INSERT INTO identity.users (email, phone, password_hash, full_name, role, seller_id)
  VALUES (p_owner_email, p_phone, '$2b$12$demo-' || left(md5(p_owner_email), 8), p_owner_name, 'SELLER', v);
  PERFORM flow.audit(NULL, 'SELLER_REGISTERED', 'sellers', v, jsonb_build_object('shop', p_shop));
  RETURN v;
END $$;
CREATE OR REPLACE FUNCTION flow.add_seller_user(p_seller uuid, p_name text, p_email text, p_phone text, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  INSERT INTO identity.users (email, phone, password_hash, full_name, role, seller_id)
  VALUES (p_email, p_phone, '$2b$12$demo-' || left(md5(p_email), 8), p_name, 'SELLER', p_seller) RETURNING id INTO v;
  RETURN v;
END $$;
-- admin duyệt hồ sơ seller (FR-ADM-02): gán gói, tài khoản nhận tiền, giọng thương hiệu cho AI
-- seller tự khai tài khoản nhận tiền (không phải admin nhập hộ); đổi tài khoản có audit, yêu cầu rút đang chờ giữ số cũ (đã chụp)
CREATE OR REPLACE FUNCTION flow.set_seller_bank(p_seller uuid, p_user uuid, p_bank text, p_account_enc text, p_holder text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF p_bank IS NULL OR p_account_enc IS NULL OR p_holder IS NULL THEN RAISE EXCEPTION 'Thiếu thông tin tài khoản ngân hàng'; END IF;
  UPDATE identity.sellers SET bank_name = p_bank, bank_account_no_enc = p_account_enc, bank_holder = upper(p_holder) WHERE id = p_seller;
  PERFORM flow.audit(p_user, 'SELLER_BANK_CHANGED', 'sellers', p_seller, jsonb_build_object('bank', p_bank, 'holder', upper(p_holder)));
END $$;
-- admin duyệt hồ sơ (BRD FR-ADM-02): PENDING -> APPROVED, gán gói + giọng thương hiệu; seller trả phí gói (flow.subscribe) mới thành ACTIVE
CREATE OR REPLACE FUNCTION flow.approve_seller(p_seller uuid, p_admin uuid, p_package uuid, p_tone text DEFAULT NULL, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now);
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin được duyệt seller'; END IF;
  IF NOT EXISTS (SELECT 1 FROM identity.subscription_packages WHERE id = p_package AND status = 'ACTIVE') THEN RAISE EXCEPTION 'Gói % không tồn tại hoặc đã ngừng', p_package; END IF;
  UPDATE identity.sellers SET status = 'APPROVED', package_id = p_package, brand_tone = coalesce(p_tone, brand_tone)
   WHERE id = p_seller AND status = 'PENDING';
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không ở trạng thái chờ duyệt', p_seller; END IF;
  PERFORM flow.audit(p_admin, 'SELLER_APPROVED', 'sellers', p_seller, jsonb_build_object('package', p_package));
END $$;
-- admin khóa/mở/đóng seller. Mở lại (ACTIVE) thì tính lại theo gói: gói hết hạn -> PAST_DUE chứ không ACTIVE. CLOSED thu hồi mọi ô.
CREATE OR REPLACE FUNCTION flow.set_seller_status(p_seller uuid, p_status text, p_admin uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_old text;
BEGIN
  IF p_status NOT IN ('ACTIVE','SUSPENDED','CLOSED') THEN RAISE EXCEPTION 'Admin chỉ đặt ACTIVE/SUSPENDED/CLOSED (PAST_DUE do hệ thống)'; END IF;
  SELECT status INTO v_old FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_old = 'CLOSED' THEN RAISE EXCEPTION 'Seller đã đóng, không đổi được'; END IF;
  IF v_old = 'PENDING' THEN RAISE EXCEPTION 'Seller chưa duyệt hồ sơ (approve_seller)'; END IF;
  IF p_status = 'ACTIVE' THEN
    UPDATE identity.sellers SET status = 'APPROVED' WHERE id = p_seller;   -- tạm, refresh quyết định ACTIVE hay PAST_DUE
    PERFORM flow.refresh_seller_package(p_seller);
  ELSE
    UPDATE identity.sellers SET status = p_status WHERE id = p_seller;
    IF p_status = 'CLOSED' THEN PERFORM flow.release_seller_slots(p_seller, 'SELLER_CLOSED'); END IF;
  END IF;
  PERFORM flow.audit(p_admin, 'SELLER_STATUS_' || p_status, 'sellers', p_seller, jsonb_build_object('from', v_old));
END $$;
CREATE OR REPLACE FUNCTION flow.set_seller_tone(p_seller uuid, p_user uuid, p_tone text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF length(p_tone) > 200 OR p_tone ~* '(ignore|bỏ qua|system prompt|hướng dẫn trước)' THEN
    RAISE EXCEPTION 'Giọng thương hiệu không hợp lệ (quá dài hoặc chứa từ bị chặn)'; END IF;   -- text seller cũng đi vào prompt: chống injection gián tiếp
  UPDATE identity.sellers SET brand_tone = p_tone WHERE id = p_seller;
  PERFORM flow.audit(p_user, 'SELLER_TONE_CHANGED', 'sellers', p_seller, jsonb_build_object('tone', p_tone));
END $$;
CREATE OR REPLACE FUNCTION flow.set_kiosk_status(p_kiosk uuid, p_status text, p_actor uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE kiosk_ops.kiosks SET status = p_status, last_heartbeat_at = CASE WHEN p_status = 'ONLINE' THEN public.app_now() ELSE last_heartbeat_at END WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  PERFORM flow.audit(p_actor, 'KIOSK_STATUS_' || p_status, 'kiosks', p_kiosk);
END $$;
-- heartbeat MQTT từ tủ; job đánh OFFLINE khi quá heartbeat_offline_seconds
CREATE OR REPLACE FUNCTION flow.kiosk_heartbeat(p_kiosk uuid, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now);
BEGIN
  UPDATE kiosk_ops.kiosks SET last_heartbeat_at = v_now, status = CASE WHEN status = 'OFFLINE' THEN 'ONLINE' ELSE status END WHERE id = p_kiosk;
END $$;
CREATE OR REPLACE FUNCTION flow.mark_offline_kiosks(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); n int;
BEGIN
  UPDATE kiosk_ops.kiosks SET status = 'OFFLINE' WHERE status = 'ONLINE' AND coalesce(last_heartbeat_at, created_at) < v_now - make_interval(secs => flow.cfg('heartbeat_offline_seconds'));
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;
CREATE OR REPLACE FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE catalog.flower_products p SET status = p_status
   WHERE id = p_product AND seller_id = (SELECT seller_id FROM identity.users WHERE id = p_user);
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa sản phẩm %', p_product; END IF;
END $$;
CREATE OR REPLACE FUNCTION flow.add_product_photo(p_product uuid, p_user uuid, p_url text) RETURNS uuid LANGUAGE sql AS $$
  SELECT flow.attach('catalog', 'flower_product', p_product, p_url, 'PRODUCT', p_user) $$;
-- đổi giá: trigger ghi audit (giá cũ/mới) — người sửa lấy từ app.actor
CREATE OR REPLACE FUNCTION flow.update_product_price(p_product uuid, p_user uuid, p_price bigint) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM set_config('app.actor', p_user::text, true);
  UPDATE catalog.flower_products SET price = p_price WHERE id = p_product AND seller_id = (SELECT seller_id FROM identity.users WHERE id = p_user);
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa giá sản phẩm %', p_product; END IF;
END $$;
CREATE OR REPLACE FUNCTION flow.trg_price_audit() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.price IS DISTINCT FROM OLD.price THEN
    PERFORM flow.audit(nullif(current_setting('app.actor', true), '')::uuid, 'PRODUCT_PRICE_CHANGED', 'flower_products', NEW.id,
                       jsonb_build_object('old', OLD.price, 'new', NEW.price));
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER price_audit AFTER UPDATE OF price ON catalog.flower_products FOR EACH ROW EXECUTE FUNCTION flow.trg_price_audit();
-- seller nạp thêm phụ kiện tại kiosk
CREATE OR REPLACE FUNCTION flow.restock_accessory(p_accessory uuid, p_user uuid, p_qty int) RETURNS void LANGUAGE plpgsql AS $$
DECLARE a kiosk_ops.accessories;
BEGIN
  IF p_qty <= 0 THEN RAISE EXCEPTION 'Số lượng nạp phải > 0'; END IF;
  SELECT * INTO a FROM kiosk_ops.accessories WHERE id = p_accessory FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Phụ kiện % không tồn tại', p_accessory; END IF;
  PERFORM flow.check_seller_user(p_user, a.seller_id);
  UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + p_qty WHERE id = a.id;
  PERFORM flow.audit(p_user, 'ACCESSORY_RESTOCKED', 'accessories', a.id, jsonb_build_object('qty', p_qty));
END $$;

-- ===================== 1. Gói thuê bao (BRD §7.1) và gán ô (FR-ADM-01) =====================
-- Seller đăng ký kỳ N tháng theo gói đã được duyệt; kỳ mới nối tiếp ngày hết hạn hiện tại (gia hạn) hoặc bắt đầu hôm nay (lần đầu / đã PAST_DUE).
CREATE OR REPLACE FUNCTION flow.subscribe(p_seller uuid, p_user uuid, p_months int, p_from date DEFAULT NULL, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_seller identity.sellers; v_pkg identity.subscription_packages; v_from date; v_to date; v_sub uuid; v_pay uuid;
BEGIN
  IF p_months IS NULL OR p_months NOT BETWEEN 1 AND 12 THEN RAISE EXCEPTION 'Số tháng đăng ký phải từ 1 đến 12'; END IF;
  PERFORM flow.check_seller_user(p_user, p_seller);
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status = 'PENDING' THEN RAISE EXCEPTION 'Seller chưa được duyệt hồ sơ, chưa đăng ký gói được'; END IF;
  IF v_seller.status IN ('SUSPENDED','CLOSED') THEN RAISE EXCEPTION 'Seller đang % — liên hệ admin', v_seller.status; END IF;
  SELECT * INTO v_pkg FROM identity.subscription_packages WHERE id = v_seller.package_id;
  IF v_pkg.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Gói % đã ngừng cung cấp, admin cần đổi gói', v_pkg.name; END IF;
  v_from := coalesce(p_from, greatest(v_now::date, coalesce((SELECT max(period_to) FROM identity.subscriptions WHERE seller_id = p_seller AND status IN ('PENDING_PAYMENT','ACTIVE')), v_now::date)));
  v_to := (v_from + make_interval(months => p_months))::date;
  IF v_to <= v_now::date THEN RAISE EXCEPTION 'Kỳ đăng ký đã kết thúc'; END IF;
  INSERT INTO identity.subscriptions (seller_id, package_id, period_from, period_to, price)
  VALUES (p_seller, v_pkg.id, v_from, v_to, v_pkg.monthly_fee * p_months) RETURNING id INTO v_sub;
  INSERT INTO payment.payments (kind, purpose, subscription_id, gateway, idempotency_key, amount)
  VALUES ('CHARGE','SUBSCRIPTION', v_sub, 'PAYOS', 'sub-' || v_sub, v_pkg.monthly_fee * p_months) RETURNING id INTO v_pay;
  UPDATE identity.subscriptions SET payment_id = v_pay WHERE id = v_sub;
  PERFORM flow.audit(p_user, 'SUBSCRIPTION_REQUESTED', 'subscriptions', v_sub, jsonb_build_object('package', v_pkg.name, 'from', v_from, 'to', v_to, 'price', v_pkg.monthly_fee * p_months));
  RETURN v_sub;
END $$;

-- tính lại trạng thái gói của seller theo ngày hiện tại: có kỳ ACTIVE phủ hôm nay -> ACTIVE, hạn = cuối chuỗi kỳ liên tục; không -> PAST_DUE (nếu từng ACTIVE)
CREATE OR REPLACE FUNCTION flow.refresh_seller_package(p_seller uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE v_today date := public.app_now()::date; v_seller identity.sellers; v_exp date; v_next date; v_new text;
BEGIN
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF v_seller.status NOT IN ('APPROVED','ACTIVE','PAST_DUE') THEN RETURN v_seller.status; END IF;
  SELECT max(period_to) INTO v_exp FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from <= v_today AND v_today < period_to;
  IF v_exp IS NOT NULL THEN
    LOOP   -- nối các kỳ đã trả liên tục
      SELECT max(period_to) INTO v_next FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from = v_exp;
      EXIT WHEN v_next IS NULL; v_exp := v_next;
    END LOOP;
    v_new := 'ACTIVE';
  ELSE
    v_exp := v_seller.package_expires_at;
    v_new := CASE WHEN v_seller.status = 'APPROVED' THEN 'APPROVED' ELSE 'PAST_DUE' END;
  END IF;
  UPDATE identity.sellers SET status = v_new, package_expires_at = v_exp WHERE id = p_seller;
  IF v_new <> v_seller.status THEN
    PERFORM flow.audit(NULL, CASE v_new WHEN 'ACTIVE' THEN CASE v_seller.status WHEN 'PAST_DUE' THEN 'SELLER_REACTIVATED' ELSE 'SELLER_ACTIVATED' END ELSE 'SELLER_PAST_DUE' END,
                       'sellers', p_seller, jsonb_build_object('expires', v_exp, 'from', v_seller.status));
  END IF;
  RETURN v_new;
END $$;

-- webhook payOS cho phí gói (idempotent theo payment): kỳ ACTIVE; phủ hôm nay thì seller ACTIVE ngay (mở khóa PAST_DUE)
CREATE OR REPLACE FUNCTION flow.subscription_paid(p_sub uuid, p_txn text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r identity.subscriptions; p payment.payments; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO r FROM identity.subscriptions WHERE id = p_sub FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đăng ký gói % không tồn tại', p_sub; END IF;
  SELECT * INTO p FROM payment.payments WHERE id = r.payment_id FOR UPDATE;
  IF p.status = 'SUCCEEDED' THEN RETURN; END IF;      -- webhook gọi lại lần 2: bỏ qua
  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code','00','orderCode',p_txn) WHERE id = p.id;
  IF r.status = 'CANCELLED' THEN
    -- tiền về sau khi đăng ký đã hết hạn: ghi nhận tiền, lập lệnh hoàn (admin chi tay về STK seller), không kích hoạt
    INSERT INTO payment.payments (kind, purpose, subscription_id, parent_payment_id, order_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND','SUBSCRIPTION', r.id, p.id, NULL, 'MANUAL', 'late-sub-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_SUBSCRIPTION_PAYMENT') ON CONFLICT DO NOTHING;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', p.id, 'Phí gói về muộn sau khi đăng ký hết hạn'),
     (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại seller (LATE_SUBSCRIPTION_PAYMENT)');
    PERFORM flow.audit(NULL, 'LATE_SUBSCRIPTION_PAYMENT', 'subscriptions', r.id, jsonb_build_object('amount', p.amount));
    RETURN;
  END IF;
  UPDATE identity.subscriptions SET status = 'ACTIVE' WHERE id = r.id;
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
   (j,'GATEWAY_CLEARING',      NULL, p.amount,'SUBSCRIPTION',r.id,'Thu phí gói'),
   (j,'PLATFORM_SUBSCRIPTION', NULL,-p.amount,'SUBSCRIPTION',r.id,'Doanh thu phí gói');
  PERFORM flow.audit(NULL, 'SUBSCRIPTION_PAID', 'subscriptions', r.id, jsonb_build_object('amount', p.amount, 'to', r.period_to));
  PERFORM flow.refresh_seller_package(r.seller_id);
END $$;

-- job: đăng ký gói chưa thanh toán quá subscription_payment_hours thì hủy (mở lại kỳ cho lần đăng ký sau)
CREATE OR REPLACE FUNCTION flow.expire_pending_subscriptions(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r record; n int := 0;
BEGIN
  FOR r IN SELECT * FROM identity.subscriptions WHERE status = 'PENDING_PAYMENT' AND created_at + flow.cfg_hour('subscription_payment_hours') < v_now ORDER BY id FOR UPDATE LOOP
    UPDATE payment.payments SET status = 'EXPIRED' WHERE id = r.payment_id AND status = 'PENDING';
    CONTINUE WHEN NOT FOUND;   -- webhook đã ghi SUCCEEDED: subscription_paid sẽ kích hoạt, không hủy
    UPDATE identity.subscriptions SET status = 'CANCELLED' WHERE id = r.id;
    PERFORM flow.audit(NULL, 'SUBSCRIPTION_EXPIRED_UNPAID', 'subscriptions', r.id);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- thu hồi mọi ô của seller (quá ân hạn PAST_DUE / đóng seller): ô trống -> FREE, ô còn hoa -> PENDING_RELEASE (seller đến lấy, hoặc admin thanh lý sau 48h)
CREATE OR REPLACE FUNCTION flow.release_seller_slots(p_seller uuid, p_reason text) RETURNS int LANGUAGE plpgsql AS $$
DECLARE a record; s kiosk_ops.slots; n int := 0;
BEGIN
  FOR a IN SELECT * FROM kiosk_ops.slot_assignments WHERE seller_id = p_seller AND status = 'ACTIVE' ORDER BY slot_id FOR UPDATE LOOP
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = a.slot_id FOR UPDATE;
    CONTINUE WHEN s.status = 'HELD';   -- khách đang thanh toán: đợi lượt sau
    UPDATE kiosk_ops.slot_assignments SET status = 'RELEASED', released_at = public.app_now(), release_reason = p_reason WHERE id = a.id;
    IF s.status = 'RENTED_EMPTY' THEN
      UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL, row_version = row_version + 1 WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_RELEASED', 'slots', s.id, jsonb_build_object('seller', p_seller, 'reason', p_reason));
    ELSIF s.status = 'STOCKED' THEN
      UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE', row_version = row_version + 1 WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_PENDING_RELEASE', 'slots', s.id, jsonb_build_object('seller', p_seller, 'reason', p_reason));
    END IF;   -- FAULT/LOCKED/PENDING_REMOVAL: giữ nguyên, release_slot_empty sẽ trả FREE khi ô trống
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- job hằng ngày: kết thúc kỳ hết hạn, tính lại trạng thái gói từng seller (ACTIVE <-> PAST_DUE), quá ân hạn thì thu hồi ô.
-- p_seller để chạy riêng một seller khi demo. Lưu ý: khóa bán theo NGÀY đã có trong slot_sellable/package_valid, job chỉ đổi status.
CREATE OR REPLACE FUNCTION flow.roll_subscriptions(p_today date DEFAULT NULL, p_seller uuid DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_today date; r record; n int := 0; v_grace int := flow.cfg('past_due_grace_days')::int;
BEGIN
  IF p_today IS NOT NULL THEN PERFORM flow.tick(p_today::timestamptz + interval '1 hour'); END IF;
  v_today := public.app_now()::date;
  FOR r IN SELECT * FROM identity.subscriptions WHERE status = 'ACTIVE' AND period_to <= v_today AND (p_seller IS NULL OR seller_id = p_seller) ORDER BY id FOR UPDATE LOOP
    UPDATE identity.subscriptions SET status = 'EXPIRED' WHERE id = r.id; n := n + 1;
  END LOOP;
  FOR r IN SELECT id, status, package_expires_at FROM identity.sellers WHERE status IN ('APPROVED','ACTIVE','PAST_DUE') AND (p_seller IS NULL OR id = p_seller) ORDER BY id LOOP
    IF flow.refresh_seller_package(r.id) <> r.status THEN n := n + 1; END IF;
    IF (SELECT status FROM identity.sellers WHERE id = r.id) = 'PAST_DUE' AND r.package_expires_at + v_grace <= v_today
       AND EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE seller_id = r.id AND status = 'ACTIVE') THEN
      PERFORM flow.audit(NULL, 'SELLER_SLOTS_RELEASED', 'sellers', r.id, jsonb_build_object('expired', r.package_expires_at, 'grace_days', v_grace));
      n := n + flow.release_seller_slots(r.id, 'PAST_DUE_GRACE');
    END IF;
  END LOOP;
  RETURN n;
END $$;

-- admin gán ô cho seller (FR-ADM-01): seller ACTIVE (gói còn hạn), ô FREE, trong hạn mức max_slots của gói
CREATE OR REPLACE FUNCTION flow.assign_slot(p_slot uuid, p_seller uuid, p_admin uuid, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_seller identity.sellers; v_pkg identity.subscription_packages; s kiosk_ops.slots; v_used int; v uuid;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) IS DISTINCT FROM 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin được gán ô'; END IF;
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status <> 'ACTIVE' OR NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Seller % chưa có gói hiệu lực (status %), không gán ô được', v_seller.shop_name, v_seller.status; END IF;
  SELECT * INTO v_pkg FROM identity.subscription_packages WHERE id = v_seller.package_id;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));   -- đếm max_slots không bị vượt khi 2 admin gán song song
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status IN ('FAULT','LOCKED') THEN RAISE EXCEPTION 'Ô % đang bảo trì/khóa, không gán được', s.slot_code; END IF;
  IF s.status <> 'FREE' THEN RAISE EXCEPTION 'Ô % đang do seller khác sử dụng (%)', s.slot_code, s.status; END IF;
  SELECT count(*) INTO v_used FROM kiosk_ops.slot_assignments WHERE seller_id = p_seller AND status = 'ACTIVE';
  IF v_used + 1 > v_pkg.max_slots THEN RAISE EXCEPTION 'Vượt hạn mức % ô của gói %', v_pkg.max_slots, v_pkg.name; END IF;
  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at) VALUES (p_slot, p_seller, p_admin, v_now) RETURNING id INTO v;
  UPDATE kiosk_ops.slots SET status = 'RENTED_EMPTY', current_seller_id = p_seller, row_version = row_version + 1 WHERE id = p_slot;
  PERFORM flow.audit(p_admin, 'SLOT_ASSIGNED', 'slots', p_slot, jsonb_build_object('seller', p_seller, 'slot', s.slot_code, 'assignment', v));
  RETURN v;
END $$;

-- thu hồi một ô (admin) hoặc seller tự trả ô: ô trống -> FREE ngay; còn hoa -> PENDING_RELEASE cho seller lấy về
CREATE OR REPLACE FUNCTION flow.release_slot(p_slot uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; a kiosk_ops.slot_assignments; v_role text;
BEGIN
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  SELECT * INTO a FROM kiosk_ops.slot_assignments WHERE slot_id = p_slot AND status = 'ACTIVE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % chưa gán cho seller nào', s.slot_code; END IF;
  SELECT role INTO v_role FROM identity.users WHERE id = p_actor;
  IF v_role <> 'ADMIN' THEN PERFORM flow.check_seller_user(p_actor, a.seller_id); END IF;
  IF s.status = 'HELD' THEN RAISE EXCEPTION 'Ô % đang có khách thanh toán, thu hồi sau', s.slot_code; END IF;
  UPDATE kiosk_ops.slot_assignments SET status = 'RELEASED', released_at = v_now, release_reason = CASE WHEN v_role = 'ADMIN' THEN 'ADMIN' ELSE 'SELLER' END WHERE id = a.id;
  IF s.status = 'RENTED_EMPTY' THEN
    UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL, row_version = row_version + 1 WHERE id = s.id;
  ELSIF s.status = 'STOCKED' THEN
    UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE', row_version = row_version + 1 WHERE id = s.id;
  END IF;
  PERFORM flow.audit(p_actor, 'SLOT_RELEASED', 'slots', s.id, jsonb_build_object('seller', a.seller_id, 'reason', p_reason, 'slot', s.slot_code));
END $$;

-- ===================== 2. Nạp hoa theo lô (batch) =====================
-- seller đến kiosk, mở cửa kỹ thuật bằng mã riêng (token SELLER_ACCESS), xếp hoa vào các ô; cùng một lần = cùng batch_id
CREATE OR REPLACE FUNCTION flow.new_batch() RETURNS uuid LANGUAGE sql VOLATILE AS $$ SELECT public.uuid_v7() $$;
CREATE OR REPLACE FUNCTION flow.stock_bouquet(p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
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
    RAISE EXCEPTION 'Ô % không phải ô trống do seller này thuê (status %)', s.slot_code, s.status; END IF;
  IF NOT flow.slot_sellable(p_slot, pr.seller_id) THEN
    RAISE EXCEPTION 'Gói của seller đã hết hạn ngày % (hoặc ô đã bị thu hồi), không nạp được vào ô %', v_now::date, s.slot_code; END IF;
  INSERT INTO kiosk_ops.bouquets (product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, p_qr, pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now)
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, p_slot, 'STOCK_IN', p_batch, p_staff);
  RETURN v_b;
END $$;

-- trừ kho phụ kiện nguyên tử; trả về danh sách dòng [{id,seller_id,name,price,qty}]
CREATE OR REPLACE FUNCTION flow.take_accessories(p_kiosk uuid, p_items jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE a jsonb; v record; v_out jsonb := '[]'; v_qty int;
BEGIN
  FOR a IN SELECT * FROM jsonb_array_elements(coalesce(p_items, '[]')) LOOP
    v_qty := (a->>'qty')::int;
    IF v_qty IS NULL OR v_qty <= 0 THEN RAISE EXCEPTION 'Số lượng phụ kiện không hợp lệ'; END IF;
    UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity - v_qty
     WHERE id = (a->>'id')::uuid AND kiosk_id = p_kiosk AND status = 'ACTIVE' AND stock_quantity >= v_qty
       AND seller_id IN (SELECT id FROM identity.sellers WHERE status = 'ACTIVE')
     RETURNING id, seller_id, name, price INTO v;
    IF NOT FOUND THEN RAISE EXCEPTION 'Phụ kiện % không đủ tồn kho tại kiosk hoặc seller đang tạm ngưng (cần %)', a->>'id', v_qty; END IF;
    v_out := v_out || jsonb_build_object('id', v.id, 'seller_id', v.seller_id, 'name', v.name, 'price', v.price, 'qty', v_qty);
  END LOOP;
  RETURN v_out;
END $$;

-- identity service: trừ điểm khi đổi (khóa dòng khách để 2 phiên không đổi quá số dư)
CREATE OR REPLACE FUNCTION flow.redeem_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_bal bigint;
BEGIN
  SELECT loyalty_points INTO v_bal FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' FOR UPDATE;
  IF v_bal IS NULL THEN RAISE EXCEPTION 'Tài khoản khách % không hợp lệ', p_customer; END IF;
  IF p_points > v_bal THEN RAISE EXCEPTION 'Chỉ còn % điểm, không đủ %', v_bal, p_points; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points - p_points WHERE id = p_customer;
  PERFORM flow.audit(p_customer, 'POINTS_REDEEMED', 'orders', p_order, jsonb_build_object('points', p_points));
END $$;

-- ===================== 3. Mua tại kiosk (luồng chính, BRD §7.3) =====================
-- giữ ô TTL hold_minutes, tách đơn theo seller, 1 khoản thanh toán VietQR cho cả giỏ; khách đã đăng nhập có thể đổi điểm
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
  IF p_customer IS NOT NULL AND NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
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

-- số tiền seller còn "chờ đối soát" của một đơn (gồm bút toán hoàn tiền của đơn)
CREATE OR REPLACE FUNCTION flow.order_seller_pending(p_order uuid) RETURNS bigint LANGUAGE sql STABLE AS $$
  SELECT coalesce(-sum(amount), 0)::bigint FROM payment.ledger_entries
   WHERE account = 'SELLER_PENDING'
     AND (ref_id = p_order OR ref_id IN (SELECT id FROM payment.payments WHERE order_id = p_order AND kind = 'REFUND')) $$;

-- ghi sổ cái cho một đơn đã thanh toán (idempotent nhờ unique ledger_once). Không hoa hồng: seller nhận trọn subtotal; nền tảng chịu phần đổi điểm.
CREATE OR REPLACE FUNCTION flow.post_order_ledger(p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE o ordering.orders; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.status IN ('AWAITING_PAYMENT','EXPIRED','CANCELLED') OR NOT EXISTS (SELECT 1 FROM payment.payments WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED') THEN
    RAISE EXCEPTION 'Chỉ ghi sổ đơn đã thu tiền thành công (đơn % đang %)', o.order_code, o.status; END IF;
  BEGIN
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
    SELECT j, a, s, amt, 'ORDER', o.id, m FROM (VALUES
      ('GATEWAY_CLEARING', NULL::uuid, o.total_amount, 'Tiền khách trả'),
      ('PLATFORM_LOYALTY', NULL::uuid, o.discount_amount, 'Nền tảng chịu phần đổi điểm'),
      ('SELLER_PENDING',   o.seller_id, -o.subtotal, 'Phần seller, chờ đối soát')
    ) v(a, s, amt, m) WHERE amt <> 0;
  EXCEPTION WHEN unique_violation THEN
    RETURN;   -- đã ghi sổ trước đó (sự kiện lặp): coi là đã xử lý
  END;
END $$;

-- phát token mở hộc cho mọi ô đang giữ của đơn (thu hồi token cũ còn sống); TTL token_minutes (BRD: 60 giây)
CREATE OR REPLACE FUNCTION flow.issue_pickup_tokens(p_order uuid) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE s record; v_cmd uuid; v_first uuid; v_now timestamptz := public.app_now();
BEGIN
  FOR s IN SELECT id, kiosk_id FROM kiosk_ops.slots WHERE hold_order_id = p_order AND status = 'HELD' ORDER BY id FOR UPDATE LOOP
    UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
    INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, order_id, token_hash, issued_at, expires_at)
    VALUES (s.kiosk_id, s.id, 'CUSTOMER_PICKUP', p_order, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes'))
    RETURNING cmd_id INTO v_cmd;
    v_first := coalesce(v_first, v_cmd);
  END LOOP;
  RETURN v_first;
END $$;

-- webhook thanh toán đơn: kiểm số tiền, idempotent theo gateway_txn_id, xử lý tiền về muộn
CREATE OR REPLACE FUNCTION flow.checkout_paid(p_checkout uuid, p_txn text, p_success boolean, p_amount bigint, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); p payment.payments; o record; it record; v_ref uuid; v_order uuid; j uuid := public.uuid_v7(); v_online boolean;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE checkout_id = p_checkout AND kind = 'CHARGE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy khoản thu cho checkout %', p_checkout; END IF;
  IF p_amount IS DISTINCT FROM p.amount THEN RAISE EXCEPTION 'Số tiền webhook % khác số tiền cần thu %', p_amount, p.amount; END IF;
  IF p.status = 'SUCCEEDED' THEN
    IF p.gateway_txn_id = p_txn THEN RETURN; END IF;   -- webhook gọi lại lần 2: bỏ qua
    RAISE EXCEPTION 'Checkout % đã thanh toán bằng giao dịch khác %', p_checkout, p.gateway_txn_id;
  END IF;
  IF NOT p_success THEN
    IF p.status = 'PENDING' THEN
      UPDATE payment.payments SET status = 'FAILED', gateway_txn_id = p_txn WHERE id = p.id;
      PERFORM flow.release_checkout(p_checkout, 'CANCELLED');
    END IF;
    RETURN;
  END IF;
  IF p.status IN ('EXPIRED','CANCELLED','FAILED') THEN
    -- tiền về sau khi đơn đã hết hạn: ghi nhận tiền, lập lệnh hoàn tự động, đơn giữ nguyên EXPIRED/CANCELLED
    UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
           raw_payload = jsonb_build_object('code','00','desc','late','orderCode',p_txn) WHERE id = p.id;
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY total_amount DESC, id LIMIT 1;
    INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND','ORDER_CHECKOUT', p_checkout, v_order, p.id, 'PAYOS', 'late-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_PAYMENT')
    RETURNING id INTO v_ref;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', v_ref, 'Tiền về muộn sau khi đơn hết hạn'),
     (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', v_ref, 'Phải hoàn lại khách (LATE_PAYMENT)');
    PERFORM flow.audit(NULL, 'LATE_PAYMENT_REFUND_CREATED', 'payments', v_ref, jsonb_build_object('checkout', p_checkout, 'amount', p.amount));
    RETURN;
  END IF;
  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code','00','desc','success','orderCode',p_txn) WHERE id = p.id;
  FOR o IN SELECT * FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY id FOR UPDATE LOOP
    CONTINUE WHEN o.status <> 'AWAITING_PAYMENT';
    PERFORM flow.set_order_status(o.id, 'PAID', 'SYSTEM');
    PERFORM flow.post_order_ledger(o.id);
    FOR it IN SELECT * FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET' AND line_status = 'ACTIVE' LOOP
      UPDATE kiosk_ops.bouquets SET status = 'SOLD' WHERE id = it.bouquet_id;
      INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id) VALUES (it.bouquet_id, it.slot_id, 'SALE_OUT', o.id);
      UPDATE kiosk_ops.slots SET hold_until = v_now + flow.cfg_min('kiosk_pickup_minutes') WHERE id = it.slot_id; -- giữ tới khi khách lấy
    END LOOP;
    -- kiosk mất kết nối: vẫn ghi nhận tiền, không phát token; khách gọi flow.request_pickup khi kiosk online lại
    SELECT status = 'ONLINE' INTO v_online FROM kiosk_ops.kiosks WHERE id = o.kiosk_id;
    IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id) THEN
      -- đơn chỉ có phụ kiện (không giữ ô nào): giao ngay tại ngăn phụ kiện
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
      PERFORM flow.complete_order(o.id, 'KIOSK', 'Đơn chỉ có phụ kiện: giao ngay');
    ELSIF EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status <> 'HELD') THEN
      -- ô đã vào FAULT trong lúc khách thanh toán: không nhả được, hoàn 100% ngay
      PERFORM flow.dispense_failed(o.id, 'Ô gặp sự cố trong lúc thanh toán');
    ELSIF v_online THEN
      PERFORM flow.issue_pickup_tokens(o.id);
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
    END IF;   -- offline: đơn ở PAID chờ kiosk online (request_pickup) hoặc expire_pickups hoàn tiền
  END LOOP;
END $$;

-- đơn hoàn tất: đóng cửa xong (hoặc giao phụ kiện); cộng điểm cho khách; mốc tính hạn khiếu nại
CREATE OR REPLACE FUNCTION flow.complete_order(p_order uuid, p_actor_type text, p_note text DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF o.status <> 'DISPENSING' THEN RAISE EXCEPTION 'Chỉ hoàn tất đơn đang nhả hàng (đơn đang %)', o.status; END IF;
  PERFORM flow.set_order_status(o.id, 'COMPLETED', p_actor_type, NULL, p_note);
  IF o.customer_id IS NOT NULL AND o.points_earned > 0 THEN PERFORM flow.credit_points(o.customer_id, o.points_earned, o.id); END IF;
END $$;
-- identity service: trả lại điểm đã đổi khi đơn không thành; tài khoản đã xóa/khóa thì bỏ qua (ghi audit)
CREATE OR REPLACE FUNCTION flow.restore_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF p_points IS NULL OR p_points <= 0 THEN RETURN; END IF;
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_RESTORED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points, 'restore', true)); END IF;
END $$;
-- identity service: cộng điểm khi đơn hoàn tất; tài khoản đã xóa/khóa thì bỏ qua (ghi audit)
CREATE OR REPLACE FUNCTION flow.credit_points(p_customer uuid, p_points bigint, p_order uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE identity.users SET loyalty_points = loyalty_points + p_points WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE';
  IF FOUND THEN PERFORM flow.audit(p_customer, 'POINTS_EARNED', 'orders', p_order, jsonb_build_object('points', p_points));
  ELSE PERFORM flow.audit(p_customer, 'POINTS_SKIPPED_INACTIVE', 'orders', p_order, jsonb_build_object('points', p_points)); END IF;
END $$;

-- hủy giữ ô / trả kho / trả điểm khi thanh toán thất bại hoặc hết hạn (chỉ xử lý đơn còn AWAITING_PAYMENT)
CREATE OR REPLACE FUNCTION flow.release_checkout(p_checkout uuid, p_to text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE it record; orec record;
BEGIN
  FOR orec IN SELECT * FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY id FOR UPDATE LOOP
    CONTINUE WHEN orec.status <> 'AWAITING_PAYMENT';
    FOR it IN SELECT * FROM ordering.order_items WHERE order_id = orec.id AND line_status = 'ACTIVE' LOOP
      IF it.item_type = 'BOUQUET' THEN
        UPDATE kiosk_ops.slots SET status = CASE WHEN flow.slot_sellable(id, current_seller_id) THEN 'STOCKED' ELSE 'PENDING_RELEASE' END,
               hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1
         WHERE id = it.slot_id AND status = 'HELD' AND hold_order_id = orec.id;
        UPDATE kiosk_ops.bouquets SET status = 'STOCKED' WHERE id = it.bouquet_id AND status = 'HELD';
        INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id, reason) VALUES (it.bouquet_id, it.slot_id, 'RELEASE', orec.id, 'Thanh toán không thành công');
      ELSIF it.item_type = 'ACCESSORY' THEN
        UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + it.quantity WHERE id = it.accessory_id;
      END IF;
      UPDATE ordering.order_items SET line_status = 'CANCELLED' WHERE id = it.id;
    END LOOP;
    IF orec.points_redeemed > 0 THEN PERFORM flow.restore_points(orec.customer_id, orec.points_redeemed, orec.id); END IF;
    PERFORM flow.set_order_status(orec.id, p_to, 'SYSTEM');
  END LOOP;
END $$;

-- job: hết hạn giữ ô chưa thanh toán (BRD: quá 7 phút tự nhả, đơn EXPIRED, màn kiosk reset)
CREATE OR REPLACE FUNCTION flow.expire_pending_payments(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); c record; n int := 0; v uuid;
BEGIN
  FOR c IN SELECT DISTINCT o.checkout_id FROM ordering.orders o
           LEFT JOIN kiosk_ops.slots s ON s.hold_order_id = o.id AND s.status = 'HELD'
           WHERE o.status = 'AWAITING_PAYMENT' AND coalesce(s.hold_until, o.created_at + flow.cfg_min('hold_minutes')) < v_now
  LOOP
    -- chỉ thắng khi payment còn PENDING: webhook đã ghi SUCCEEDED trước thì bỏ qua (chống race với checkout_paid)
    UPDATE payment.payments SET status = 'EXPIRED' WHERE checkout_id = c.checkout_id AND kind = 'CHARGE' AND status = 'PENDING' RETURNING id INTO v;
    CONTINUE WHEN NOT FOUND;
    PERFORM flow.release_checkout(c.checkout_id, 'EXPIRED');
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- ===================== 4. Lấy hàng & thiết bị (MQTT) =====================
-- khách xin mở lại hộc bằng mã trên e-receipt (token hết hạn, kiosk vừa online lại); mã rỗng/sai không phát token
CREATE OR REPLACE FUNCTION flow.request_pickup(p_order uuid, p_tracking text, p_kiosk uuid, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_k text; v_cmd uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.kiosk_id <> p_kiosk THEN RAISE EXCEPTION 'Đơn không nhận tại kiosk này'; END IF;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION 'Đơn không ở trạng thái chờ nhận (%)', o.status; END IF;
  SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF v_k IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk đang không trực tuyến, thử lại sau'; END IF;
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status = 'HELD' AND hold_until >= v_now) THEN
    RAISE EXCEPTION 'Hết thời gian giữ hàng tại tủ'; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN
    PERFORM flow.audit(p_kiosk, 'PICKUP_CODE_REJECTED', 'orders', o.id, NULL, 'KIOSK');
    RAISE NOTICE 'Sai mã e-receipt';
    RETURN NULL;   -- không RAISE để audit được lưu
  END IF;
  v_cmd := flow.issue_pickup_tokens(o.id);
  IF o.status = 'PAID' THEN PERFORM flow.set_order_status(o.id, 'DISPENSING', 'KIOSK', NULL, 'Kiosk online lại, phát token'); END IF;
  RETURN v_cmd;
END $$;

-- tủ không nhả được hàng (BRD 7.3 "Sự cố cửa"): mở ticket DISPENSE_FAILED, lập lệnh hoàn chờ chi, ô FAULT/PENDING_REMOVAL.
-- Chỉ hoàn phần KHÔNG giao được: bó đã PICKED_UP (cửa đã đóng) không hoàn. Cửa đang mở (token OPENED) thì phải đợi cảm biến đóng.
CREATE OR REPLACE FUNCTION flow.dispense_failed(p_order uuid, p_reason text, p_slot uuid DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE o ordering.orders; v_ref uuid; v_d uuid; s kiosk_ops.slots; it record; v_refund bigint; v_lines bigint; v_delivered boolean; v_earn bigint;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION 'Đơn % không ở trạng thái nhả hàng', o.order_code; END IF;
  IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE order_id = o.id AND status = 'OPENED') THEN
    RAISE EXCEPTION 'Cửa của đơn % đang mở, đợi cảm biến đóng cửa rồi mới xử lý', o.order_code; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || o.seller_id::text));
  -- phần chưa giao: bó chưa PICKED_UP; phụ kiện coi là đã giao nếu đã có ít nhất một ô mở
  v_delivered := EXISTS (SELECT 1 FROM ordering.order_items oi JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id WHERE oi.order_id = o.id AND b.status = 'PICKED_UP');
  SELECT coalesce(sum(oi.line_total), 0) INTO v_refund FROM ordering.order_items oi LEFT JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
   WHERE oi.order_id = o.id AND oi.line_status = 'ACTIVE' AND ((oi.item_type = 'BOUQUET' AND b.status <> 'PICKED_UP') OR (oi.item_type = 'ACCESSORY' AND NOT v_delivered));
  v_lines := v_refund;                            -- giá trị hàng chưa giao: seller chịu đúng phần này
  v_refund := least(v_refund, o.total_amount);   -- khách nhận lại tối đa số tiền đã trả
  IF v_refund <= 0 THEN RAISE EXCEPTION 'Đơn % đã giao đủ hàng, không có gì để hoàn', o.order_code; END IF;
  v_ref := flow.create_refund(o.id, v_refund, 'AUTO_REFUND: ' || p_reason, flow.sys_user(), 'MANUAL');
  PERFORM flow.refund_allocation(v_ref, 'REFUND_CLEARING', v_lines);
  INSERT INTO ordering.disputes (kind, order_id, kiosk_id, slot_id, reason) VALUES ('DISPENSE_FAILED', o.id, o.kiosk_id, p_slot, p_reason) RETURNING id INTO v_d;
  -- các ô đang giữ cho đơn: ô lỗi -> FAULT (giữ bó để kỹ thuật kiểm), ô khác -> PENDING_REMOVAL (bó đã bán, chờ seller lấy về)
  FOR s IN SELECT * FROM kiosk_ops.slots WHERE hold_order_id = o.id ORDER BY id FOR UPDATE LOOP
    UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
    IF s.id = p_slot OR s.status = 'FAULT' THEN
      UPDATE kiosk_ops.slots SET status = 'FAULT', hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
    ELSE
      UPDATE kiosk_ops.slots SET status = 'PENDING_REMOVAL', hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
    END IF;
  END LOOP;
  FOR it IN SELECT oi.* FROM ordering.order_items oi LEFT JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
            WHERE oi.order_id = o.id AND oi.line_status = 'ACTIVE' AND ((oi.item_type = 'BOUQUET' AND b.status <> 'PICKED_UP') OR (oi.item_type = 'ACCESSORY' AND NOT v_delivered)) LOOP
    IF it.item_type = 'ACCESSORY' THEN UPDATE kiosk_ops.accessories SET stock_quantity = stock_quantity + it.quantity WHERE id = it.accessory_id; END IF;
    UPDATE ordering.order_items SET line_status = 'REFUNDED' WHERE id = it.id;
  END LOOP;
  IF v_delivered THEN
    -- giao được một phần: đơn hoàn tất, phần thiếu hoàn tiền (bút toán điều chỉnh); điểm tích trên phần đã trả thật, điểm đổi trả lại phần nền tảng thu hồi
    v_earn := CASE WHEN o.customer_id IS NULL THEN 0 ELSE floor((o.total_amount - v_refund) * flow.cfg('points_rate_percent') / 100) END;
    UPDATE ordering.orders SET points_earned = v_earn WHERE id = o.id;
    PERFORM flow.set_order_status(o.id, 'COMPLETED', 'SYSTEM', NULL, 'Giao thiếu, hoàn ' || v_refund || 'đ: ' || p_reason);
    IF v_earn > 0 THEN PERFORM flow.credit_points(o.customer_id, v_earn, o.id); END IF;
    IF v_lines - v_refund > 0 THEN PERFORM flow.restore_points(o.customer_id, least(v_lines - v_refund, o.points_redeemed), o.id); END IF;
  ELSE
    IF o.points_redeemed > 0 THEN PERFORM flow.restore_points(o.customer_id, o.points_redeemed, o.id); END IF;
    PERFORM flow.set_order_status(o.id, 'DISPENSE_FAILED', 'SYSTEM', NULL, p_reason);
  END IF;
  PERFORM flow.audit(NULL, 'DISPENSE_FAILED', 'orders', o.id, jsonb_build_object('reason', p_reason, 'refund', v_ref, 'refund_amount', v_refund, 'dispute', v_d, 'partial', v_delivered));
  RETURN v_d;
END $$;

CREATE OR REPLACE FUNCTION flow.device_event(p_cmd uuid, p_event text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); t kiosk_ops.unlock_tokens; v_bq uuid;
BEGIN
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id = p_cmd FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh % không tồn tại', p_cmd; END IF;
  -- token quá hạn: ghi EXPIRED và dừng (không RAISE để trạng thái EXPIRED được lưu)
  IF p_event IN ('SENT','ACK','OPENED') AND t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at < v_now THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'EXPIRED' WHERE id = t.id;
    PERFORM flow.audit(t.kiosk_id, 'DEVICE_TOKEN_EXPIRED', 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd, 'event', p_event), 'KIOSK');
    RETURN;
  END IF;
  IF p_event = 'SENT' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'SENT', attempts = attempts + 1 WHERE id = t.id AND status IN ('ISSUED','SENT');
  ELSIF p_event = 'ACK' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'ACKED' WHERE id = t.id AND status = 'SENT';
  ELSIF p_event = 'OPENED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'OPENED', door_opened_at = v_now WHERE id = t.id AND status = 'ACKED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Cửa mở khi token không ở trạng thái ACKED (status %)', t.status; END IF;
  ELSIF p_event = 'CLOSED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'CLOSED', door_closed_at = v_now WHERE id = t.id AND status = 'OPENED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Đóng cửa khi chưa mở'; END IF;
    IF t.purpose = 'CUSTOMER_PICKUP' THEN
      SELECT bouquet_id INTO v_bq FROM kiosk_ops.slots WHERE id = t.slot_id;
      UPDATE kiosk_ops.bouquets SET status = 'PICKED_UP' WHERE id = v_bq;
      PERFORM flow.release_slot_empty(t.slot_id);
      -- đơn hoàn tất khi không còn ô nào giữ cho đơn (BRD: cảm biến cửa đóng mới hoàn tất)
      IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = t.order_id)
         AND (SELECT status FROM ordering.orders WHERE id = t.order_id) = 'DISPENSING' THEN
        PERFORM flow.complete_order(t.order_id, 'KIOSK', 'Cửa đã đóng, khách đã lấy hàng');
      END IF;
    END IF;
  ELSIF p_event = 'FAILED' THEN
    -- chỉ áp cho token còn sống; đủ số lần thất bại (BRD: 2) thì khóa token và DISPENSE_FAILED
    IF t.status IN ('ISSUED','SENT','ACKED') AND t.attempts >= flow.cfg('door_max_attempts') THEN
      UPDATE kiosk_ops.unlock_tokens SET status = 'FAILED' WHERE id = t.id;
      IF t.purpose = 'CUSTOMER_PICKUP' THEN
        PERFORM flow.dispense_failed(t.order_id, 'Khóa không phản hồi sau ' || t.attempts || ' lần gửi lệnh', t.slot_id);
      ELSE
        PERFORM flow.report_device_fault(t.kiosk_id, t.slot_id, NULL, 'Cửa kỹ thuật không mở sau ' || t.attempts || ' lần', NULL);
      END IF;
    END IF;
  ELSE RAISE EXCEPTION 'Sự kiện thiết bị lạ %', p_event; END IF;
  PERFORM flow.audit(t.kiosk_id, 'DEVICE_' || p_event, 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd), 'KIOSK');
END $$;

-- cửa kẹt mở (cảm biến không gửi CLOSED): kỹ thuật xác nhận tại chỗ cửa đã đóng -> ghi nhận như sự kiện CLOSED, có audit người xác nhận
CREATE OR REPLACE FUNCTION flow.admin_close_door(p_cmd uuid, p_admin uuid, p_note text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); t kiosk_ops.unlock_tokens;
BEGIN
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id = p_cmd;
  IF NOT FOUND OR t.status <> 'OPENED' THEN RAISE EXCEPTION 'Lệnh % không ở trạng thái cửa đang mở', p_cmd; END IF;
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin/kỹ thuật được xác nhận đóng cửa tay'; END IF;
  PERFORM flow.audit(p_admin, 'DOOR_CLOSED_MANUALLY', 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd, 'note', p_note));
  PERFORM flow.device_event(p_cmd, 'CLOSED');
END $$;

-- job: token quá hạn chưa dùng
CREATE OR REPLACE FUNCTION flow.expire_tokens(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); n int;
BEGIN
  UPDATE kiosk_ops.unlock_tokens SET status = 'EXPIRED' WHERE status IN ('ISSUED','SENT','ACKED') AND expires_at < v_now;
  GET DIAGNOSTICS n = ROW_COUNT;
  RETURN n;
END $$;

-- job: đơn đã trả tiền mà quá thời gian giữ ô vẫn chưa lấy được hàng (kiosk offline, khách bỏ đi) -> hoàn 100% (BRD 7.8: tiền đi trước cửa)
CREATE OR REPLACE FUNCTION flow.expire_pickups(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o record; n int := 0; v_k text;
BEGIN
  FOR o IN SELECT od.* FROM ordering.orders od WHERE od.status IN ('PAID','DISPENSING')
             AND EXISTS (SELECT 1 FROM kiosk_ops.slots s WHERE s.hold_order_id = od.id AND s.status = 'HELD' AND s.hold_until < v_now)
             AND NOT EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.order_id = od.id AND t.status = 'OPENED')   -- cửa đang mở: đợi đóng
           ORDER BY od.id FOR UPDATE LOOP
    SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = o.kiosk_id;
    PERFORM flow.dispense_failed(o.id, CASE WHEN v_k <> 'ONLINE' THEN 'Kiosk không trực tuyến trong thời gian nhận hàng' ELSE 'Hết thời gian giữ hàng, khách không mở hộc' END);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- ===================== 5. Đối soát =====================
CREATE OR REPLACE FUNCTION flow.move_pending_to_available(p_order uuid, p_memo text) RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE o ordering.orders; v bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF o.status NOT IN ('COMPLETED','REFUNDED') THEN RAISE EXCEPTION 'Chỉ đối soát đơn đã giao xong (đơn % đang %)', o.order_code, o.status; END IF;
  v := flow.order_seller_pending(p_order);
  IF v > 0 THEN
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j,'SELLER_PENDING',  o.seller_id,  v,'SETTLEMENT',o.id, p_memo || ' ' || o.order_code),
     (j,'SELLER_AVAILABLE',o.seller_id, -v,'SETTLEMENT',o.id, p_memo || ' ' || o.order_code);
  END IF;
  RETURN v;
END $$;

-- hết thời gian khiếu nại -> chuyển tiền seller sang khả dụng (đơn vẫn COMPLETED)
CREATE OR REPLACE FUNCTION flow.settle_order(p_order uuid, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || o.seller_id::text));
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Chỉ đối soát đơn đã hoàn tất, đơn đang %', o.status; END IF;
  IF v_now < o.completed_at + flow.cfg_hour('dispute_window_hours') THEN
    RAISE EXCEPTION 'Chưa hết thời gian khiếu nại (đến %)', o.completed_at + flow.cfg_hour('dispute_window_hours'); END IF;
  IF EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Đơn % còn lệnh hoàn chờ chi, chưa đối soát', o.order_code; END IF;
  PERFORM flow.move_pending_to_available(o.id, 'Đối soát đơn');
END $$;

CREATE OR REPLACE FUNCTION flow.settle_due_orders(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o record; n int := 0;
BEGIN
  FOR o IN SELECT id FROM ordering.orders od
           WHERE od.status = 'COMPLETED' AND od.completed_at + flow.cfg_hour('dispute_window_hours') <= v_now AND flow.order_seller_pending(od.id) > 0
             AND NOT EXISTS (SELECT 1 FROM payment.payments p WHERE p.order_id = od.id AND p.kind = 'REFUND' AND p.status = 'PENDING')
           ORDER BY od.completed_at, od.id LOOP
    PERFORM flow.settle_order(o.id);
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- ===================== 6. Hoa quá hạn bán / trả seller =====================
-- hoa quá hạn: bó vẫn nằm trong ô (PENDING_REMOVAL, ẩn khỏi catalog) cho tới khi seller lấy ra
CREATE OR REPLACE FUNCTION flow.expire_bouquets(p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); b record; n int := 0;
BEGIN
  FOR b IN SELECT bq.*, s.id AS slot_id FROM kiosk_ops.bouquets bq JOIN kiosk_ops.slots s ON s.bouquet_id = bq.id
           WHERE bq.status = 'STOCKED' AND s.status = 'STOCKED' AND bq.sellable_until <= v_now ORDER BY s.id FOR UPDATE OF bq, s LOOP
    UPDATE kiosk_ops.bouquets SET status = 'EXPIRED' WHERE id = b.id;
    UPDATE kiosk_ops.slots SET status = 'PENDING_REMOVAL', row_version = row_version + 1 WHERE id = b.slot_id;
    INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, reason) VALUES (b.id, b.slot_id, 'EXPIRE_OUT', 'Quá hạn bán');
    n := n + 1;
  END LOOP;
  RETURN n;
END $$;

-- seller lấy hoa ra khỏi ô (quá hạn / đơn hoàn tiền / hết hợp đồng): mở cửa kỹ thuật bằng token SELLER_ACCESS
CREATE OR REPLACE FUNCTION flow.return_to_seller(p_slot uuid, p_staff uuid, p_photo_url text, p_reason text, p_damaged boolean DEFAULT false, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; v_tok uuid; v_log uuid; v_seller uuid;
BEGIN
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status NOT IN ('PENDING_REMOVAL','PENDING_RELEASE') THEN RAISE EXCEPTION 'Ô % không ở trạng thái chờ trả hàng (%)', s.slot_code, s.status; END IF;
  SELECT seller_id INTO v_seller FROM kiosk_ops.bouquets WHERE id = s.bouquet_id;
  PERFORM flow.check_seller_user(p_staff, v_seller);
  -- token SELLER_ACCESS; người đứng tại tủ nên mô phỏng luôn chuỗi sự kiện mở/đóng cửa
  UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
  INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
  VALUES (s.kiosk_id, s.id, 'SELLER_ACCESS', p_staff, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes'))
  RETURNING cmd_id INTO v_tok;
  PERFORM flow.device_event(v_tok, 'SENT'); PERFORM flow.device_event(v_tok, 'ACK');
  PERFORM flow.device_event(v_tok, 'OPENED'); PERFORM flow.device_event(v_tok, 'CLOSED');
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, performed_by, reason)
  VALUES (s.bouquet_id, s.id, CASE WHEN p_damaged THEN 'DAMAGE' ELSE 'RETURN_SELLER' END, p_staff, p_reason) RETURNING id INTO v_log;
  IF p_photo_url IS NOT NULL THEN PERFORM flow.attach('kiosk_ops', 'inventory_log', v_log, p_photo_url, 'RETURN', p_staff); END IF;
  UPDATE kiosk_ops.bouquets SET status = CASE WHEN p_damaged THEN 'DAMAGED' ELSE 'RETURNED' END WHERE id = s.bouquet_id;
  PERFORM flow.release_slot_empty(s.id);
END $$;

-- seller không đến lấy hoa (PENDING_REMOVAL/PENDING_RELEASE) quá return_deadline_hours: admin/kỹ thuật thanh lý, ô trống lại; seller chịu (bó DAMAGED)
CREATE OR REPLACE FUNCTION flow.admin_dispose_slot(p_slot uuid, p_admin uuid, p_note text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); s kiosk_ops.slots; v_since timestamptz; v_tok uuid; v_log uuid;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) <> 'ADMIN' THEN RAISE EXCEPTION 'Chỉ admin/kỹ thuật được thanh lý'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND OR s.status NOT IN ('PENDING_REMOVAL','PENDING_RELEASE') THEN RAISE EXCEPTION 'Ô % không ở trạng thái chờ trả hàng', p_slot; END IF;
  SELECT max(created_at) INTO v_since FROM notify.audit_logs WHERE entity_type = 'slots' AND entity_id = s.id AND action = 'SLOT_PENDING_RELEASE';
  v_since := coalesce(v_since, (SELECT max(created_at) FROM kiosk_ops.inventory_logs WHERE slot_id = s.id), s.updated_at);
  IF v_now < v_since + flow.cfg_hour('return_deadline_hours') THEN
    RAISE EXCEPTION 'Chưa quá hạn % giờ để seller tự lấy (đến %)', flow.cfg('return_deadline_hours'), v_since + flow.cfg_hour('return_deadline_hours'); END IF;
  UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');
  INSERT INTO kiosk_ops.unlock_tokens (kiosk_id, slot_id, purpose, issued_to_user_id, token_hash, issued_at, expires_at)
  VALUES (s.kiosk_id, s.id, 'SELLER_ACCESS', p_admin, encode(sha256(convert_to(public.uuid_v7()::text, 'UTF8')), 'hex'), v_now, v_now + flow.cfg_min('token_minutes')) RETURNING cmd_id INTO v_tok;
  PERFORM flow.device_event(v_tok, 'SENT'); PERFORM flow.device_event(v_tok, 'ACK'); PERFORM flow.device_event(v_tok, 'OPENED'); PERFORM flow.device_event(v_tok, 'CLOSED');
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, performed_by, reason) VALUES (s.bouquet_id, s.id, 'DAMAGE', p_admin, 'Thanh lý: ' || p_note) RETURNING id INTO v_log;
  UPDATE kiosk_ops.bouquets SET status = 'DAMAGED' WHERE id = s.bouquet_id;
  PERFORM flow.release_slot_empty(s.id);
  PERFORM flow.audit(p_admin, 'SLOT_DISPOSED', 'slots', s.id, jsonb_build_object('note', p_note, 'log', v_log));
END $$;

-- ===================== 7. Hoàn tiền, khiếu nại, sự cố =====================
-- lập lệnh hoàn (PENDING) cho một đơn; trần theo từng đơn, trần theo khoản thu kiểm ở trigger
CREATE OR REPLACE FUNCTION flow.create_refund(p_order uuid, p_amount bigint, p_reason text, p_approver uuid, p_gateway text DEFAULT 'MANUAL') RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE o ordering.orders; v_charge uuid; v_done bigint; v uuid;
BEGIN
  IF p_amount IS NULL OR p_amount <= 0 THEN RAISE EXCEPTION 'Số tiền hoàn phải > 0'; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  SELECT id INTO v_charge FROM payment.payments WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % chưa có khoản thu thành công', o.order_code; END IF;
  SELECT coalesce(sum(amount), 0) INTO v_done FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status IN ('PENDING','SUCCEEDED');
  IF v_done + p_amount > o.total_amount THEN
    RAISE EXCEPTION 'Tổng hoàn cho đơn % (% đ) vượt giá trị đơn % đ', o.order_code, v_done + p_amount, o.total_amount; END IF;
  INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
  VALUES ('REFUND','ORDER_CHECKOUT', o.checkout_id, o.id, v_charge, p_gateway, 'refund-' || public.uuid_v7(), p_amount, 'PENDING', p_approver, p_reason)
  RETURNING id INTO v;
  RETURN v;
END $$;

-- phân bổ một khoản hoàn: seller chịu theo tỷ lệ subtotal/total; phần đổi điểm nền tảng đã chịu được hoàn lại tương ứng.
-- trừ SELLER_PENDING trước, thiếu thì trừ SELLER_AVAILABLE (được phép âm = công nợ).
-- p_seller_share: phần seller chịu; mặc định pro-rata subtotal/total; giao thiếu thì truyền đúng giá trị hàng chưa giao.
CREATE OR REPLACE FUNCTION flow.refund_allocation(p_refund uuid, p_credit_account text, p_seller_share bigint DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r payment.payments; o ordering.orders; v_sp bigint; v_loy bigint; v_pend bigint; v_from_pend bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO r FROM payment.payments WHERE id = p_refund;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id;
  IF o.total_amount = 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không hoàn được'; END IF;
  v_sp  := coalesce(p_seller_share, round(r.amount::numeric * o.subtotal / o.total_amount));   -- seller chịu (có thể > r.amount khi đơn có đổi điểm)
  IF v_sp < r.amount OR v_sp > o.subtotal THEN RAISE EXCEPTION 'Phần seller chịu % không hợp lệ (hoàn %, hàng %)', v_sp, r.amount, o.subtotal; END IF;
  v_loy := v_sp - r.amount;                                          -- nền tảng được hoàn lại phần đổi điểm đã chịu
  v_pend := flow.order_seller_pending(o.id);
  v_from_pend := least(greatest(v_pend, 0), v_sp);
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
  SELECT j, a, s, amt, 'REFUND', r.id, m FROM (VALUES
    ('SELLER_PENDING',   o.seller_id, v_from_pend,        'Seller chịu hoàn (trừ tiền chờ đối soát)'),
    ('SELLER_AVAILABLE', o.seller_id, v_sp - v_from_pend, 'Seller chịu hoàn (trừ ví khả dụng, có thể thành công nợ)'),
    ('PLATFORM_LOYALTY', NULL::uuid,  -v_loy,             'Hoàn lại phần đổi điểm nền tảng đã chịu'),
    (p_credit_account,   NULL::uuid,  -r.amount,          'Chi hoàn ' || coalesce(r.reason, ''))
  ) v(a, s, amt, m) WHERE amt <> 0;
END $$;

-- khách khiếu nại từ e-receipt (BRD 7.6): quét QR -> tracking_token là chìa khóa; trong dispute_window_hours; mỗi đơn 1 lần
CREATE OR REPLACE FUNCTION flow.open_dispute(p_order uuid, p_tracking text, p_reason text, p_photo text, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_d uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN RAISE EXCEPTION 'Mã e-receipt không đúng: chỉ người giữ biên nhận mới được khiếu nại'; END IF;
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Chỉ khiếu nại đơn đã nhận hàng (đơn đang %); tủ không mở được thì hệ thống đã tự hoàn', o.status; END IF;
  IF EXISTS (SELECT 1 FROM ordering.disputes WHERE order_id = o.id AND kind = 'COMPLAINT') THEN RAISE EXCEPTION 'Đơn đã có khiếu nại (mỗi đơn chỉ khiếu nại 1 lần)'; END IF;
  IF v_now > o.completed_at + flow.cfg_hour('dispute_window_hours') THEN
    RAISE EXCEPTION 'Quá % giờ kể từ lúc nhận hàng, hết hạn khiếu nại', flow.cfg('dispute_window_hours'); END IF;
  IF p_photo IS NULL THEN RAISE EXCEPTION 'Khiếu nại bắt buộc kèm ảnh'; END IF;
  INSERT INTO ordering.disputes (kind, order_id, kiosk_id, reason, reported_by) VALUES ('COMPLAINT', o.id, o.kiosk_id, p_reason, o.customer_id) RETURNING id INTO v_d;
  PERFORM flow.attach('ordering', 'dispute', v_d, p_photo, 'EVIDENCE', o.customer_id);
  PERFORM flow.set_order_status(o.id, 'DISPUTED', 'CUSTOMER', o.customer_id, p_reason);
  RETURN v_d;
END $$;

-- admin đối chiếu log (đơn — webhook — sự kiện cửa) rồi quyết định: từ chối (đối soát luôn) hoặc lập lệnh hoàn MANUAL chờ chi (confirm_refund)
CREATE OR REPLACE FUNCTION flow.resolve_dispute(p_dispute uuid, p_admin uuid, p_refund bigint, p_decision text, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); d ordering.disputes; o ordering.orders; v_ref uuid;
BEGIN
  SELECT * INTO d FROM ordering.disputes WHERE id = p_dispute AND kind = 'COMPLAINT' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khiếu nại % không tồn tại', p_dispute; END IF;
  IF d.status <> 'OPEN' THEN RAISE EXCEPTION 'Khiếu nại đã đóng (%)', d.status; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = d.order_id FOR UPDATE;
  IF p_refund IS NULL OR p_refund = 0 THEN
    UPDATE ordering.disputes SET status = 'RESOLVED_REJECT', decision = p_decision, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
    PERFORM flow.set_order_status(o.id, 'COMPLETED', 'ADMIN', p_admin, p_decision);
    PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
    PERFORM flow.audit(p_admin, 'DISPUTE_REJECTED', 'disputes', d.id, jsonb_build_object('order', o.order_code));
    RETURN NULL;
  END IF;
  v_ref := flow.create_refund(o.id, p_refund, p_decision, p_admin, 'MANUAL');
  UPDATE ordering.disputes SET status = 'RESOLVED_REFUND', refund_amount = p_refund, decision = p_decision, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
  PERFORM flow.audit(p_admin, 'DISPUTE_REFUND_APPROVED', 'disputes', d.id, jsonb_build_object('order', o.order_code, 'refund', p_refund, 'refund_payment', v_ref));
  RETURN v_ref;
END $$;

-- hoàn tiền sau đối soát (khách gọi hotline, đơn đã COMPLETED và không còn khiếu nại): trừ thẳng ví khả dụng của seller
CREATE OR REPLACE FUNCTION flow.admin_refund(p_order uuid, p_admin uuid, p_amount bigint, p_reason text) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.status <> 'COMPLETED' THEN RAISE EXCEPTION 'Hoàn thủ công chỉ áp dụng cho đơn đã hoàn tất'; END IF;
  PERFORM flow.audit(p_admin, 'ADMIN_REFUND', 'orders', o.id, jsonb_build_object('amount', p_amount, 'reason', p_reason));
  RETURN flow.create_refund(o.id, p_amount, p_reason, p_admin, 'MANUAL');
END $$;

-- A1: VietQR không có refund API -> hoàn bằng chuyển khoản tay. Khách (kể cả vãng lai) quét e-receipt, bấm "Nhận hoàn tiền", tự khai STK.
-- Chỉ nhận khi đơn đang có lệnh hoàn chờ chi; mã e-receipt là chìa khóa; STK mã hóa ở tầng ứng dụng; audit không ghi PII.
CREATE OR REPLACE FUNCTION flow.submit_refund_info(p_order uuid, p_tracking text, p_bank text, p_account_enc text, p_holder text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN RAISE EXCEPTION 'Mã e-receipt không đúng: chỉ người giữ biên nhận mới khai được tài khoản nhận hoàn'; END IF;
  IF NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Đơn % không có lệnh hoàn nào đang chờ, không thu tài khoản ngân hàng', o.order_code; END IF;
  IF coalesce(p_bank, '') = '' OR coalesce(p_account_enc, '') = '' OR length(coalesce(p_holder, '')) < 2 THEN RAISE EXCEPTION 'Thiếu ngân hàng / số tài khoản / tên chủ tài khoản'; END IF;
  IF p_holder !~ '^[A-Za-zÀ-ỹ ]+$' THEN RAISE EXCEPTION 'Tên chủ tài khoản chỉ gồm chữ cái và khoảng trắng'; END IF;
  UPDATE ordering.orders SET refund_bank_name = p_bank, refund_bank_account_enc = p_account_enc, refund_bank_holder = upper(p_holder) WHERE id = o.id;
  PERFORM flow.audit(o.customer_id, 'REFUND_INFO_SUBMITTED', 'orders', o.id, jsonb_build_object('bank', p_bank), 'USER');
END $$;

-- admin xác nhận đã chi hoàn (chứng từ): lúc này mới ghi sổ và đổi trạng thái đơn
CREATE OR REPLACE FUNCTION flow.confirm_refund(p_refund uuid, p_admin uuid, p_proof_url text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r payment.payments; o ordering.orders; j uuid := public.uuid_v7(); v_refunded bigint;
BEGIN
  SELECT * INTO r FROM payment.payments WHERE id = p_refund AND kind = 'REFUND' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh hoàn % không tồn tại', p_refund; END IF;
  IF r.status <> 'PENDING' THEN RAISE EXCEPTION 'Lệnh hoàn không ở trạng thái chờ (%)', r.status; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id FOR UPDATE;
  IF r.purpose = 'ORDER_CHECKOUT' AND o.refund_bank_name IS NULL THEN
    RAISE EXCEPTION 'Đơn % chưa có tài khoản nhận hoàn: khách khai trên e-receipt (submit_refund_info) rồi admin mới chuyển khoản được', o.order_code; END IF;
  IF p_proof_url IS NULL THEN RAISE EXCEPTION 'Chi hoàn phải kèm chứng từ chuyển khoản'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || coalesce(o.seller_id::text, 'none')));
  PERFORM flow.attach('payment', 'refund', r.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = v_now,
         gateway_txn_id = coalesce(gateway_txn_id, r.gateway || '-RF-' || replace(r.id::text, '-', '')) WHERE id = r.id;
  IF EXISTS (SELECT 1 FROM payment.ledger_entries WHERE ref_type = 'REFUND' AND ref_id = r.id AND account = 'REFUND_CLEARING') THEN
    -- đã phân bổ lúc lập lệnh (tiền về muộn / tủ không nhả hàng): chỉ còn bút toán chi tiền ra
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'REFUND_CLEARING',  NULL,  r.amount, 'REFUND_PAYOUT', r.id, 'Đã chi hoàn cho khách'),
     (j, 'GATEWAY_CLEARING', NULL, -r.amount, 'REFUND_PAYOUT', r.id, 'Chi hoàn qua ' || r.gateway);
  ELSE
    PERFORM flow.refund_allocation(r.id, 'GATEWAY_CLEARING');
  END IF;
  SELECT coalesce(sum(amount), 0) INTO v_refunded FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';
  IF o.status = 'DISPUTED' AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    -- chỉ đóng khiếu nại và đối soát khi KHÔNG còn lệnh hoàn nào chờ chi (tiền seller bị giữ tới lúc đó)
    PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, 'Đã chi hoàn ' || r.amount || 'đ');
    PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
  ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% sau đối soát');
  ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% do tủ không nhả hàng');
    -- ticket DISPENSE_FAILED vẫn OPEN cho tới khi kỹ thuật xử lý ô (resolve_device_fault)
  END IF;
  PERFORM flow.audit(p_admin, 'REFUND_CONFIRMED', 'payments', r.id, jsonb_build_object('amount', r.amount, 'order', o.order_code));
  -- đã chi hết lệnh hoàn của đơn -> xóa STK khách (chỉ giữ đúng thời gian cần, NĐ 13/2023)
  IF o.id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    UPDATE ordering.orders SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL WHERE id = o.id AND refund_bank_name IS NOT NULL;
    IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
  END IF;
END $$;

-- seller/kỹ thuật báo sự cố thiết bị (cửa không đóng, nhiệt độ…): ô vào FAULT (bó vẫn trong ô), ticket DEVICE_FAULT
CREATE OR REPLACE FUNCTION flow.report_device_fault(p_kiosk uuid, p_slot uuid, p_reporter uuid, p_desc text, p_photo text, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v uuid; s kiosk_ops.slots; o ordering.orders;
BEGIN
  IF p_reporter IS NOT NULL AND p_photo IS NULL THEN RAISE EXCEPTION 'Bắt buộc ảnh minh chứng khi người báo sự cố'; END IF;
  INSERT INTO ordering.disputes (kind, kiosk_id, slot_id, reason, reported_by) VALUES ('DEVICE_FAULT', p_kiosk, p_slot, p_desc, p_reporter) RETURNING id INTO v;
  IF p_photo IS NOT NULL THEN PERFORM flow.attach('ordering', 'dispute', v, p_photo, 'EVIDENCE', p_reporter); END IF;
  IF p_slot IS NOT NULL THEN
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
    IF NOT FOUND OR s.kiosk_id <> p_kiosk THEN RAISE EXCEPTION 'Ô % không thuộc kiosk %', p_slot, p_kiosk; END IF;
    IF s.status NOT IN ('FAULT','LOCKED') THEN
      IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id = p_slot AND status = 'OPENED') THEN
        RAISE EXCEPTION 'Cửa ô % đang mở, đợi cảm biến đóng cửa rồi mới báo sự cố', s.slot_code; END IF;
      UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED' WHERE slot_id = p_slot AND status IN ('ISSUED','SENT','ACKED');
      IF s.status = 'HELD' AND s.hold_order_id IS NOT NULL THEN
        SELECT * INTO o FROM ordering.orders WHERE id = s.hold_order_id;
        IF o.status IN ('PAID','DISPENSING') THEN
          PERFORM flow.dispense_failed(o.id, 'Sự cố thiết bị tại ô ' || s.slot_code || ': ' || p_desc, p_slot);   -- đơn đang nhận hàng: hoàn tiền luôn
        ELSIF o.status = 'AWAITING_PAYMENT' THEN
          -- khách chưa trả tiền: hủy giỏ, khoản thu PENDING -> CANCELLED (tiền về muộn sẽ đi đường hoàn tự động)
          UPDATE payment.payments SET status = 'CANCELLED' WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'PENDING';
          PERFORM flow.release_checkout(o.checkout_id, 'CANCELLED');
          UPDATE kiosk_ops.slots SET status = 'FAULT', row_version = row_version + 1 WHERE id = p_slot;
        END IF;
      ELSE
        UPDATE kiosk_ops.slots SET status = 'FAULT', row_version = row_version + 1 WHERE id = p_slot;
      END IF;
    END IF;
  END IF;
  PERFORM flow.audit(p_reporter, 'DEVICE_FAULT_REPORTED', 'disputes', v, jsonb_build_object('kiosk', p_kiosk, 'slot', p_slot), CASE WHEN p_reporter IS NULL THEN 'KIOSK' END);
  RETURN v;
END $$;

-- kỹ thuật xử lý xong: ô FAULT trở lại trạng thái suy từ dữ liệu (còn bó -> STOCKED/PENDING_REMOVAL, trống -> theo hợp đồng thuê)
CREATE OR REPLACE FUNCTION flow.resolve_device_fault(p_dispute uuid, p_admin uuid, p_note text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); d ordering.disputes; s kiosk_ops.slots; v_bstatus text;
BEGIN
  SELECT * INTO d FROM ordering.disputes WHERE id = p_dispute AND kind IN ('DEVICE_FAULT','DISPENSE_FAILED') FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Sự cố % không tồn tại', p_dispute; END IF;
  IF d.status <> 'OPEN' THEN RAISE EXCEPTION 'Sự cố đã đóng (%)', d.status; END IF;
  IF d.kind = 'DISPENSE_FAILED' AND EXISTS (SELECT 1 FROM payment.payments WHERE order_id = d.order_id AND kind = 'REFUND' AND status = 'PENDING') THEN
    RAISE EXCEPTION 'Phải chi hoàn cho khách (confirm_refund) trước khi đóng sự cố nhả hàng'; END IF;
  IF d.slot_id IS NOT NULL THEN
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = d.slot_id FOR UPDATE;
    IF s.status = 'FAULT' THEN
      IF s.bouquet_id IS NULL THEN
        PERFORM flow.release_slot_empty(s.id);
      ELSE
        SELECT status INTO v_bstatus FROM kiosk_ops.bouquets WHERE id = s.bouquet_id;
        UPDATE kiosk_ops.slots SET status = CASE WHEN v_bstatus <> 'STOCKED' THEN 'PENDING_REMOVAL'
                                                 WHEN flow.slot_sellable(id, current_seller_id) THEN 'STOCKED' ELSE 'PENDING_RELEASE' END,
               hold_until = NULL, hold_order_id = NULL, row_version = row_version + 1 WHERE id = s.id;
      END IF;
    END IF;
  END IF;
  UPDATE ordering.disputes SET status = 'RESOLVED_FIXED', decision = p_note, decided_by = p_admin, resolved_at = v_now WHERE id = d.id;
  PERFORM flow.audit(p_admin, 'DEVICE_FAULT_RESOLVED', 'disputes', d.id, jsonb_build_object('note', p_note));
END $$;

-- ===================== 8. Rút tiền =====================
-- BR ví âm: số dư khả dụng có thể âm (công nợ) do hoàn tiền sau đối soát; khi âm/không đủ thì không rút được
CREATE OR REPLACE FUNCTION flow.request_withdrawal(p_seller uuid, p_user uuid, p_amount bigint, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_avail bigint; s identity.sellers; v_w uuid;
BEGIN
  PERFORM flow.check_seller_user(p_user, p_seller);
  IF (SELECT role FROM identity.users WHERE id = p_user) <> 'SELLER' THEN RAISE EXCEPTION 'Chỉ tài khoản của shop được yêu cầu rút tiền'; END IF;
  IF p_amount < flow.cfg('withdraw_min') THEN RAISE EXCEPTION 'Số tiền rút tối thiểu % đồng', flow.cfg('withdraw_min'); END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));
  SELECT available_balance INTO v_avail FROM payment.v_seller_balance WHERE seller_id = p_seller;
  v_avail := coalesce(v_avail, 0);
  IF v_avail <= 0 OR v_avail < p_amount THEN RAISE EXCEPTION 'Số dư khả dụng % < số tiền rút %', v_avail, p_amount; END IF;
  SELECT * INTO s FROM identity.sellers WHERE id = p_seller;
  IF s.bank_account_no_enc IS NULL THEN RAISE EXCEPTION 'Seller chưa khai báo tài khoản nhận tiền'; END IF;
  INSERT INTO payment.withdrawal_requests (seller_id, amount, bank_name, bank_account_no_enc, bank_holder)
  VALUES (p_seller, p_amount, s.bank_name, s.bank_account_no_enc, s.bank_holder) RETURNING id INTO v_w;
  PERFORM flow.audit(p_user, 'WITHDRAWAL_REQUESTED', 'withdrawal_requests', v_w, jsonb_build_object('amount', p_amount));
  RETURN v_w;
END $$;

CREATE OR REPLACE FUNCTION flow.pay_withdrawal(p_w uuid, p_admin uuid, p_proof_url text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); w payment.withdrawal_requests; v_avail bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO w FROM payment.withdrawal_requests WHERE id = p_w FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Yêu cầu rút % không tồn tại', p_w; END IF;
  IF w.status NOT IN ('PENDING','APPROVED') THEN RAISE EXCEPTION 'Yêu cầu rút không ở trạng thái chờ'; END IF;
  IF p_proof_url IS NULL THEN RAISE EXCEPTION 'Chi tiền phải kèm chứng từ chuyển khoản'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || w.seller_id::text));
  SELECT available_balance INTO v_avail FROM payment.v_seller_balance WHERE seller_id = w.seller_id;
  v_avail := coalesce(v_avail, 0);
  IF v_avail <= 0 OR v_avail < w.amount THEN RAISE EXCEPTION 'Số dư khả dụng không đủ tại thời điểm chi (% < %)', v_avail, w.amount; END IF;
  PERFORM flow.attach('payment', 'withdrawal', w.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.withdrawal_requests SET status = 'PAID', approved_by = p_admin, paid_at = v_now WHERE id = w.id;
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
   (j,'SELLER_AVAILABLE',w.seller_id, w.amount,'WITHDRAWAL',w.id,'Chi trả seller'),
   (j,'GATEWAY_CLEARING',NULL,-w.amount,'WITHDRAWAL',w.id,'Chuyển khoản ra ngân hàng');
  PERFORM flow.audit(p_admin, 'WITHDRAWAL_PAID', 'withdrawal_requests', w.id, jsonb_build_object('amount', w.amount));
END $$;
CREATE OR REPLACE FUNCTION flow.reject_withdrawal(p_w uuid, p_admin uuid, p_reason text) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE payment.withdrawal_requests SET status = 'REJECTED', reject_reason = p_reason, approved_by = NULL WHERE id = p_w AND status IN ('PENDING','APPROVED');
  IF NOT FOUND THEN RAISE EXCEPTION 'Yêu cầu rút % không ở trạng thái chờ', p_w; END IF;
  PERFORM flow.audit(p_admin, 'WITHDRAWAL_REJECTED', 'withdrawal_requests', p_w, jsonb_build_object('reason', p_reason));
END $$;

-- ===================== 9. AI Gift Advisor =====================
-- Phase 0 (RuleBasedAdvisor): backend lọc ứng viên CÒN HÀNG TẠI KIOSK, khớp ngân sách, loại hoa kỵ dịp; chấm điểm theo tag; trả 3 gợi ý.
-- LLM (ngoài DB) chỉ xếp lại 3 combo + viết lý do/lời thiệp từ đúng tập ứng viên này; kết quả validate rồi ghi qua flow.ai_record_llm.
CREATE OR REPLACE FUNCTION flow.ai_candidates(p_kiosk uuid, p_budget bigint, p_occasion text)
RETURNS TABLE (bouquet_id uuid, product_id uuid, seller_id uuid, name text, price bigint, tags text[], description text, brand_tone text) LANGUAGE sql STABLE AS $$
  SELECT b.id, p.id, p.seller_id, p.name, b.price_snapshot, p.tags, p.description, sl.brand_tone
  FROM kiosk_ops.slots s JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id JOIN catalog.flower_products p ON p.id = b.product_id
  JOIN identity.sellers sl ON sl.id = p.seller_id
  WHERE s.kiosk_id = p_kiosk AND s.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND sl.status = 'ACTIVE' AND sl.package_expires_at > public.app_now()::date
    AND (p_budget IS NULL OR b.price_snapshot <= p_budget)
    -- luật văn hóa cứng để trong code, không giao LLM: hoa trắng/cúc trắng không gợi ý cho sinh nhật, chúc mừng, khai trương
    AND NOT (p_occasion IN ('BIRTHDAY','CONGRATS','ANNIVERSARY','VALENTINE') AND p.tags && ARRAY['WHITE','SYMPATHY'])
    AND NOT (p_occasion = 'SYMPATHY' AND p.tags && ARRAY['ROMANTIC','FUNNY','RED'])
$$;
CREATE OR REPLACE FUNCTION flow.ai_suggest(p_session text, p_customer uuid, p_kiosk uuid, p_recipient text, p_age text, p_occasion text, p_tone text, p_budget bigint, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_tags text[]; v uuid; v_t0 timestamptz := clock_timestamp();
BEGIN
  IF p_kiosk IS NULL THEN RAISE EXCEPTION 'Khảo sát AI phải gắn với một kiosk'; END IF;
  v_tags := ARRAY[p_recipient, p_occasion] || CASE WHEN p_tone IS NULL THEN '{}'::text[] ELSE ARRAY[p_tone] END;
  INSERT INTO ai.gift_surveys (session_id, customer_id, kiosk_id, recipient_type, age_group, occasion, message_tone, budget_max, model_version, source, results, latency_ms)
  SELECT p_session, p_customer, p_kiosk, p_recipient, p_age, p_occasion, p_tone, p_budget, 'rules-v1', 'FALLBACK',
         coalesce(jsonb_agg(jsonb_build_object('product_id', x.product_id, 'bouquet_id', x.bouquet_id, 'rank', x.rn, 'score', x.sc, 'reason', x.description) ORDER BY x.rn), '[]'),
         (extract(epoch FROM clock_timestamp() - v_t0) * 1000)::int
  FROM (SELECT c.*, row_number() OVER (ORDER BY cardinality(ARRAY(SELECT unnest(c.tags) INTERSECT SELECT unnest(v_tags))) DESC, c.price DESC) rn,
               round(cardinality(ARRAY(SELECT unnest(c.tags) INTERSECT SELECT unnest(v_tags)))::numeric / cardinality(v_tags), 3) sc
          FROM flow.ai_candidates(p_kiosk, p_budget, p_occasion) c) x
  WHERE x.rn <= 3
  RETURNING id INTO v;
  RETURN v;
END $$;
-- backend ghi kết quả LLM đã validate (mọi bouquet_id phải thuộc tập ứng viên của kiosk; sai thì giữ FALLBACK)
CREATE OR REPLACE FUNCTION flow.ai_record_llm(p_survey uuid, p_results jsonb, p_model text, p_latency_ms int) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE s ai.gift_surveys; e jsonb; v_ok boolean := true;
BEGIN
  SELECT * INTO s FROM ai.gift_surveys WHERE id = p_survey FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khảo sát % không tồn tại', p_survey; END IF;
  IF p_results IS NULL OR jsonb_typeof(p_results) <> 'array' OR jsonb_array_length(p_results) NOT BETWEEN 1 AND 3 THEN v_ok := false; END IF;   -- NULL/không phải mảng: từ chối (giữ FALLBACK)
  IF v_ok AND (SELECT count(DISTINCT x->>'bouquet_id') FROM jsonb_array_elements(p_results) x) <> jsonb_array_length(p_results) THEN v_ok := false; END IF;   -- không lặp bó
  FOR e IN SELECT * FROM jsonb_array_elements(coalesce(p_results, '[]')) LOOP
    IF (e->>'bouquet_id') IS NULL OR NOT EXISTS (SELECT 1 FROM flow.ai_candidates(s.kiosk_id, s.budget_max, s.occasion) c WHERE c.bouquet_id = (e->>'bouquet_id')::uuid)
       OR length(coalesce(e->>'reason','')) > 300 OR length(coalesce(e->>'card_message','')) > 200
       OR coalesce(e->>'reason','') || ' ' || coalesce(e->>'card_message','') ~* '(https?://|www\.|ignore|bỏ qua hướng dẫn|system prompt|<[a-z/])' THEN
      v_ok := false;
    END IF;
  END LOOP;
  IF v_ok THEN
    UPDATE ai.gift_surveys SET results = p_results, model_version = p_model, source = 'LLM', latency_ms = p_latency_ms WHERE id = p_survey;
  ELSE
    PERFORM flow.audit(NULL, 'AI_LLM_RESULT_REJECTED', 'gift_surveys', p_survey, jsonb_build_object('model', p_model));
  END IF;
  RETURN v_ok;
END $$;
CREATE OR REPLACE FUNCTION flow.ai_mark_purchased(p_survey uuid, p_order uuid, p_product uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  IF p_order IS NOT NULL AND NOT EXISTS (SELECT 1 FROM ordering.orders WHERE id = p_order) THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF p_product IS NOT NULL AND NOT EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = p_product) THEN RAISE EXCEPTION 'Sản phẩm % không tồn tại', p_product; END IF;
  UPDATE ai.gift_surveys SET purchased_order_id = p_order, selected_product_id = p_product WHERE id = p_survey;
END $$;

-- ===================== 9b. Đối soát (A1: hoàn tiền tay nên phải đối soát hằng ngày) =====================
-- (1) Đối soát với sao kê cổng VietQR: p_statement = [{"txn": "...", "amount": 350000}] các giao dịch TIỀN VÀO ngày p_date do payOS/SePay trả về.
--     So với payment.payments (CHARGE, PAYOS, SUCCEEDED, paid_at trong ngày). Trả về từng chênh lệch; audit RECONCILED kèm số liệu.
CREATE OR REPLACE FUNCTION flow.reconcile_gateway(p_date date, p_statement jsonb, p_admin uuid)
RETURNS TABLE (loai text, gateway_txn_id text, he_thong bigint, sao_ke bigint, ghi_chu text) LANGUAGE plpgsql AS $$
DECLARE n_match int; n_diff int;
BEGIN
  IF jsonb_typeof(p_statement) <> 'array' THEN RAISE EXCEPTION 'Sao kê phải là mảng JSON [{txn, amount}]'; END IF;
  CREATE TEMP TABLE IF NOT EXISTS _stmt (txn text PRIMARY KEY, amount bigint) ON COMMIT DROP;
  DELETE FROM _stmt;
  INSERT INTO _stmt SELECT e->>'txn', (e->>'amount')::bigint FROM jsonb_array_elements(p_statement) e ON CONFLICT (txn) DO UPDATE SET amount = _stmt.amount + EXCLUDED.amount;
  RETURN QUERY
  WITH db AS (SELECT p.gateway_txn_id AS txn, p.amount, p.purpose FROM payment.payments p
              WHERE p.kind = 'CHARGE' AND p.gateway = 'PAYOS' AND p.status = 'SUCCEEDED' AND (p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date)
  SELECT 'MISSING_IN_DB', st.txn, NULL::bigint, st.amount, 'Ngân hàng có tiền vào nhưng hệ thống không có khoản thu (webhook rớt?) -> tra soát, tạo khoản thu tay hoặc hoàn'
    FROM _stmt st LEFT JOIN db ON db.txn = st.txn WHERE db.txn IS NULL
  UNION ALL
  SELECT 'MISSING_IN_STATEMENT', db.txn, db.amount, NULL::bigint, 'Hệ thống ghi SUCCEEDED nhưng sao kê không có -> webhook giả/ghi nhầm, khóa đơn, tra soát ngay'
    FROM db LEFT JOIN _stmt st ON st.txn = db.txn WHERE st.txn IS NULL
  UNION ALL
  SELECT 'AMOUNT_MISMATCH', db.txn, db.amount, st.amount, 'Số tiền lệch -> tra soát'
    FROM db JOIN _stmt st ON st.txn = db.txn WHERE st.amount <> db.amount;
  SELECT count(*) INTO n_match FROM _stmt st JOIN payment.payments p ON p.gateway_txn_id = st.txn AND p.amount = st.amount AND p.kind = 'CHARGE' AND p.status = 'SUCCEEDED';
  n_diff := (SELECT count(*) FROM _stmt) + (SELECT count(*) FROM payment.payments p WHERE p.kind = 'CHARGE' AND p.gateway = 'PAYOS' AND p.status = 'SUCCEEDED' AND (p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date) - 2 * n_match;
  PERFORM flow.audit(p_admin, 'RECONCILED', 'payments', NULL, jsonb_build_object('date', p_date, 'matched', n_match, 'diff', n_diff));
END $$;

-- (2) Báo cáo đối soát nội bộ cuối ngày: tiền ghi nhận (payments / withdrawals) phải khớp sổ cái GATEWAY_CLEARING; lệnh hoàn chờ lâu; tiền chờ hoàn.
CREATE OR REPLACE FUNCTION flow.reconcile_daily(p_date date)
RETURNS TABLE (muc text, chung_tu bigint, so_cai bigint, lech bigint, ghi_chu text) LANGUAGE sql STABLE AS $$
  WITH d AS (SELECT p_date AS d),
  l AS (SELECT * FROM payment.ledger_entries WHERE (created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date),
  p AS (SELECT * FROM payment.payments WHERE (paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date AND status = 'SUCCEEDED'),
  w AS (SELECT * FROM payment.withdrawal_requests WHERE (paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date = p_date AND status = 'PAID')
  SELECT 'Tiền vào VietQR (đơn + phí gói)', coalesce((SELECT sum(amount) FROM p WHERE kind = 'CHARGE'), 0),
         coalesce((SELECT sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount > 0), 0),
         coalesce((SELECT sum(amount) FROM p WHERE kind = 'CHARGE'), 0) - coalesce((SELECT sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount > 0), 0),
         'Khoản thu SUCCEEDED trong ngày phải = nợ GATEWAY_CLEARING trong ngày'
  UNION ALL
  SELECT 'Chi hoàn khách/seller (chuyển khoản tay)', coalesce((SELECT sum(amount) FROM p WHERE kind = 'REFUND'), 0),
         coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount < 0 AND ref_type IN ('REFUND','REFUND_PAYOUT')), 0),
         coalesce((SELECT sum(amount) FROM p WHERE kind = 'REFUND'), 0) - coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND amount < 0 AND ref_type IN ('REFUND','REFUND_PAYOUT')), 0),
         'Lệnh hoàn đã chi trong ngày phải = có GATEWAY_CLEARING (REFUND/REFUND_PAYOUT) trong ngày'
  UNION ALL
  SELECT 'Chi rút tiền seller', coalesce((SELECT sum(amount) FROM w), 0),
         coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND ref_type = 'WITHDRAWAL'), 0),
         coalesce((SELECT sum(amount) FROM w), 0) - coalesce((SELECT -sum(amount) FROM l WHERE account = 'GATEWAY_CLEARING' AND ref_type = 'WITHDRAWAL'), 0),
         'Rút tiền PAID trong ngày phải = có GATEWAY_CLEARING (WITHDRAWAL) trong ngày'
  UNION ALL
  SELECT 'Sổ cái cân (tổng mọi bút toán)', 0, coalesce((SELECT sum(amount) FROM payment.ledger_entries), 0), coalesce((SELECT sum(amount) FROM payment.ledger_entries), 0), 'Phải = 0'
  UNION ALL
  SELECT 'Lệnh hoàn chờ chi quá 48 giờ (số lệnh)', (SELECT count(*) FROM payment.payments WHERE kind = 'REFUND' AND status = 'PENDING' AND created_at < p_date::timestamptz - interval '48 hours'),
         0, 0, 'Cảnh báo SLA hoàn tiền: khách chưa khai STK hoặc admin chưa chi'
  UNION ALL
  SELECT 'Tiền đang chờ hoàn (REFUND_CLEARING)', 0, coalesce((SELECT -sum(amount) FROM payment.ledger_entries WHERE account = 'REFUND_CLEARING'), 0), 0,
         'Phải = tổng lệnh hoàn PENDING đã phân bổ; về 0 khi chi hết'
$$;

-- ===================== 10. Màn hình (view) dùng cho portal / kiosk =====================
CREATE SCHEMA IF NOT EXISTS screen;
CREATE OR REPLACE VIEW screen.v_kiosk_catalog AS          -- màn hình kiosk: hoa đang bán (tồn thực, còn hạn, seller đang hoạt động)
SELECT k.id AS kiosk_id, k.code AS kiosk, sl.slot_code, b.id AS bouquet_id, fp.id AS product_id, fp.name, fp.tags, b.price_snapshot AS gia, b.sellable_until, s.shop_name
FROM kiosk_ops.slots sl JOIN kiosk_ops.kiosks k ON k.id = sl.kiosk_id JOIN kiosk_ops.bouquets b ON b.id = sl.bouquet_id
JOIN catalog.flower_products fp ON fp.id = b.product_id JOIN identity.sellers s ON s.id = b.seller_id
WHERE sl.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND s.status = 'ACTIVE' AND s.package_expires_at > public.app_now()::date;
CREATE OR REPLACE VIEW screen.v_expiring_bouquets AS      -- FR-SEL-05: cảnh báo lô sắp hết hạn (thay cho thông báo lưu DB)
SELECT b.seller_id, k.code AS kiosk, sl.slot_code, b.id AS bouquet_id, fp.name, b.sellable_until, b.sellable_until - public.app_now() AS con_lai
FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots sl ON sl.bouquet_id = b.id JOIN kiosk_ops.kiosks k ON k.id = sl.kiosk_id JOIN catalog.flower_products fp ON fp.id = b.product_id
WHERE b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND b.sellable_until <= public.app_now() + flow.cfg_hour('expiry_warn_hours');
CREATE OR REPLACE VIEW screen.v_revenue_daily AS          -- FR-ADM-05: doanh thu theo ngày/kiosk (GMV đơn); doanh thu nền tảng = v_platform_revenue
SELECT (p.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS ngay, k.code AS kiosk, count(DISTINCT o.id) AS so_don, sum(o.total_amount) AS gmv, sum(o.discount_amount) AS giam_do_diem
FROM payment.payments p JOIN ordering.orders o ON o.checkout_id = p.checkout_id JOIN kiosk_ops.kiosks k ON k.id = o.kiosk_id
WHERE p.kind = 'CHARGE' AND p.purpose = 'ORDER_CHECKOUT' AND p.status = 'SUCCEEDED' AND o.status NOT IN ('EXPIRED','CANCELLED')
GROUP BY 1, 2;
CREATE OR REPLACE VIEW screen.v_platform_revenue AS       -- doanh thu nền tảng theo tháng = phí gói thuê bao
SELECT date_trunc('month', created_at)::date AS thang, -sum(amount) AS doanh_thu_phi_goi FROM payment.ledger_entries WHERE account = 'PLATFORM_SUBSCRIPTION' GROUP BY 1;
CREATE OR REPLACE VIEW screen.v_admin_queue AS            -- hàng chờ admin
SELECT 'Rút tiền chờ duyệt' AS hang_cho, count(*) AS so_luong FROM payment.withdrawal_requests WHERE status = 'PENDING'
UNION ALL SELECT 'Khiếu nại đang mở', count(*) FROM ordering.disputes WHERE kind = 'COMPLAINT' AND status = 'OPEN'
UNION ALL SELECT 'Sự cố thiết bị đang mở', count(*) FROM ordering.disputes WHERE kind IN ('DEVICE_FAULT','DISPENSE_FAILED') AND status = 'OPEN'
UNION ALL SELECT 'Seller chờ duyệt', count(*) FROM identity.sellers WHERE status = 'PENDING'
UNION ALL SELECT 'Seller quá hạn gói', count(*) FROM identity.sellers WHERE status = 'PAST_DUE'
UNION ALL SELECT 'Đơn đang nhả hàng', count(*) FROM ordering.orders WHERE status = 'DISPENSING'
UNION ALL SELECT 'Bó sắp hết hạn', count(*) FROM screen.v_expiring_bouquets
UNION ALL SELECT 'Lệnh hoàn chờ chi', count(*) FROM payment.payments WHERE kind = 'REFUND' AND status = 'PENDING';
CREATE OR REPLACE VIEW screen.v_refund_queue AS           -- màn hình admin "Lệnh hoàn chờ chi": chi được ngay hay còn chờ khách khai STK
SELECT p.id AS refund_id, p.created_at, p.amount, p.reason, p.purpose, o.order_code, o.customer_id IS NOT NULL AS khach_dang_nhap, s.shop_name,
       o.refund_bank_name IS NOT NULL AS da_co_stk, o.refund_bank_name, o.refund_bank_holder,     -- không lộ số tài khoản ra màn hình danh sách
       CASE WHEN p.purpose = 'SUBSCRIPTION' THEN 'Hoàn seller (STK seller)' WHEN o.refund_bank_name IS NOT NULL THEN 'Sẵn sàng chi' ELSE 'Chờ khách khai STK trên e-receipt' END AS trang_thai,
       extract(epoch FROM public.app_now() - p.created_at) / 3600 AS cho_gio
FROM payment.payments p LEFT JOIN ordering.orders o ON o.id = p.order_id LEFT JOIN identity.sellers s ON s.id = o.seller_id
WHERE p.kind = 'REFUND' AND p.status = 'PENDING';
CREATE OR REPLACE VIEW screen.v_seller_wallet AS          -- FR-SEL-07: sao kê ví seller
SELECT s.shop_name, l.created_at, l.account, l.amount, l.ref_type, l.memo
FROM payment.ledger_entries l JOIN identity.sellers s ON s.id = l.seller_id;
CREATE OR REPLACE VIEW screen.v_customer_history AS       -- lịch sử mua + điểm của khách (chỉ khách đã đăng nhập)
SELECT o.customer_id, o.order_code, o.created_at, o.status, o.total_amount, o.points_earned, o.points_redeemed, s.shop_name,
       (SELECT string_agg(name_snapshot, ', ') FROM ordering.order_items i WHERE i.order_id = o.id) AS hang
FROM ordering.orders o JOIN identity.sellers s ON s.id = o.seller_id WHERE o.customer_id IS NOT NULL;
CREATE OR REPLACE VIEW screen.v_ai_effectiveness AS        -- báo cáo eval AI: tỷ lệ khảo sát dẫn tới mua, theo nguồn
SELECT kiosk_id, source, count(*) AS so_khao_sat, count(purchased_order_id) AS so_mua, round(100.0 * count(purchased_order_id) / count(*), 1) AS ty_le_mua, round(avg(latency_ms)) AS latency_tb_ms
FROM ai.gift_surveys GROUP BY 1, 2;
