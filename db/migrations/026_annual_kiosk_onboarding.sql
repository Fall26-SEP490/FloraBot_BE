-- Migration 026: Annual kiosk onboarding lifecycle, whole-kiosk slot zones, and handover promotion
-- Forward-only additive migration preserving existing data and historical records. Idempotent.
BEGIN;

-- 1. Extend identity.subscription_packages with annual billing and slot zone capacities
ALTER TABLE identity.subscription_packages
  ADD COLUMN IF NOT EXISTS annual_fee bigint NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS walk_in_slots integer NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS preorder_slots integer NOT NULL DEFAULT 0,
  ADD COLUMN IF NOT EXISTS billing_cycle text NOT NULL DEFAULT 'MONTHLY';

ALTER TABLE identity.subscription_packages DROP CONSTRAINT IF EXISTS subscription_packages_monthly_fee_check;
ALTER TABLE identity.subscription_packages ADD CONSTRAINT subscription_packages_monthly_fee_check
  CHECK (monthly_fee >= 0);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_subscription_packages_billing_cycle') THEN
    ALTER TABLE identity.subscription_packages
      ADD CONSTRAINT chk_subscription_packages_billing_cycle
      CHECK (billing_cycle IN ('MONTHLY', 'ANNUAL'));
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_subscription_packages_fees') THEN
    ALTER TABLE identity.subscription_packages
      ADD CONSTRAINT chk_subscription_packages_fees
      CHECK (
        (billing_cycle = 'MONTHLY' AND monthly_fee > 0) OR
        (billing_cycle = 'ANNUAL' AND annual_fee > 0)
      );
  END IF;
END $$;

-- Seed / update the 3 approved annual onboarding tiers
INSERT INTO identity.subscription_packages (id, name, monthly_fee, annual_fee, walk_in_slots, preorder_slots, max_slots, billing_cycle, status)
VALUES
  ('20000000-0000-0000-0000-00000000000c', 'Trial', 0, 10000, 1, 1, 2, 'ANNUAL', 'ACTIVE'),
  ('20000000-0000-0000-0000-00000000000d', 'Pro',   0, 15000, 4, 4, 8, 'ANNUAL', 'ACTIVE'),
  ('20000000-0000-0000-0000-00000000000e', 'Max',   0, 20000, 6, 6, 12, 'ANNUAL', 'ACTIVE')
ON CONFLICT (id) DO UPDATE SET
  name = EXCLUDED.name,
  annual_fee = EXCLUDED.annual_fee,
  walk_in_slots = EXCLUDED.walk_in_slots,
  preorder_slots = EXCLUDED.preorder_slots,
  max_slots = EXCLUDED.max_slots,
  billing_cycle = EXCLUDED.billing_cycle,
  status = 'ACTIVE';

-- 2. Extend kiosk_ops.slots with explicit zone (WALK_IN, PREORDER)
ALTER TABLE kiosk_ops.slots
  ADD COLUMN IF NOT EXISTS zone text;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_slots_zone') THEN
    ALTER TABLE kiosk_ops.slots
      ADD CONSTRAINT chk_slots_zone
      CHECK (zone IS NULL OR zone IN ('WALK_IN', 'PREORDER'));
  END IF;
END $$;

-- 3. Extend payment.payments purpose and ledger_entries ref_type
ALTER TABLE payment.payments DROP CONSTRAINT IF EXISTS payments_purpose_check;
ALTER TABLE payment.payments ADD CONSTRAINT payments_purpose_check
  CHECK (purpose IN ('ORDER_CHECKOUT', 'SUBSCRIPTION', 'KIOSK_APPLICATION'));

ALTER TABLE payment.ledger_entries DROP CONSTRAINT IF EXISTS ledger_entries_ref_type_check;
ALTER TABLE payment.ledger_entries ADD CONSTRAINT ledger_entries_ref_type_check
  CHECK (ref_type IN ('ORDER', 'REFUND', 'REFUND_PAYOUT', 'SUBSCRIPTION', 'WITHDRAWAL', 'SETTLEMENT', 'KIOSK_APPLICATION'));

-- 4. Extend identity.sellers for contact communication and exact anniversary timestamp
ALTER TABLE identity.sellers
  ADD COLUMN IF NOT EXISTS contact_email text,
  ADD COLUMN IF NOT EXISTS package_expires_at_ts timestamptz;

-- Update flow.package_valid: enforces package_expires_at_ts > now when present, else fallback to legacy DateOnly
CREATE OR REPLACE FUNCTION flow.package_valid(p_seller uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
    SELECT 1 FROM identity.sellers
    WHERE id = p_seller
      AND (
        (package_expires_at_ts IS NOT NULL AND package_expires_at_ts > public.app_now())
        OR
        (package_expires_at_ts IS NULL AND package_expires_at IS NOT NULL AND package_expires_at > public.app_now()::date)
      )
  );
$$;

-- Update flow.subscribe: prevent selling annual tiers with monthly semantics
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
  IF v_pkg.billing_cycle <> 'MONTHLY' THEN RAISE EXCEPTION 'Gói thuê bao hàng năm không thể gia hạn theo tháng' USING ERRCODE = 'P0001'; END IF;
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

-- Update flow.refresh_seller_package: exact timestamp comparison for annual leases, preserving monthly DateOnly
CREATE OR REPLACE FUNCTION flow.refresh_seller_package(p_seller uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_today date := public.app_now()::date;
  v_now timestamptz := public.app_now();
  v_seller identity.sellers;
  v_exp date;
  v_next date;
  v_new text;
BEGIN
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF v_seller.status NOT IN ('APPROVED','ACTIVE','PAST_DUE') THEN RETURN v_seller.status; END IF;

  IF v_seller.package_expires_at_ts IS NOT NULL THEN
    IF v_seller.package_expires_at_ts > v_now THEN
      v_new := 'ACTIVE';
    ELSE
      v_new := CASE WHEN v_seller.status = 'APPROVED' THEN 'APPROVED' ELSE 'PAST_DUE' END;
    END IF;
    v_exp := v_seller.package_expires_at;
  ELSE
    SELECT max(period_to) INTO v_exp FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from <= v_today AND v_today < period_to;
    IF v_exp IS NOT NULL THEN
      LOOP
        SELECT max(period_to) INTO v_next FROM identity.subscriptions WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from = v_exp;
        EXIT WHEN v_next IS NULL; v_exp := v_next;
      END LOOP;
      v_new := 'ACTIVE';
    ELSE
      v_exp := v_seller.package_expires_at;
      v_new := CASE WHEN v_seller.status = 'APPROVED' THEN 'APPROVED' ELSE 'PAST_DUE' END;
    END IF;
  END IF;

  UPDATE identity.sellers SET status = v_new, package_expires_at = v_exp WHERE id = p_seller;
  IF v_new <> v_seller.status THEN
    PERFORM flow.audit(NULL, CASE v_new WHEN 'ACTIVE' THEN CASE v_seller.status WHEN 'PAST_DUE' THEN 'SELLER_REACTIVATED' ELSE 'SELLER_ACTIVATED' END ELSE 'SELLER_PAST_DUE' END,
                       'sellers', p_seller, jsonb_build_object('expires', v_exp, 'expires_ts', v_seller.package_expires_at_ts, 'from', v_seller.status));
  END IF;
  RETURN v_new;
END $$;

-- Update flow.roll_subscriptions: exact anniversary timestamp for annual leases, preserving monthly DateOnly
CREATE OR REPLACE FUNCTION flow.roll_subscriptions(p_today date DEFAULT NULL, p_seller uuid DEFAULT NULL, p_now timestamptz DEFAULT NULL) RETURNS int LANGUAGE plpgsql AS $$
DECLARE
  v_today date;
  v_now timestamptz;
  r record;
  n int := 0;
  v_grace int := flow.cfg('past_due_grace_days')::int;
BEGIN
  IF p_now IS NOT NULL THEN PERFORM flow.tick(p_now);
  ELSIF p_today IS NOT NULL THEN PERFORM flow.tick(p_today::timestamptz + interval '1 hour'); END IF;
  v_today := public.app_now()::date;
  v_now := public.app_now();

  FOR r IN SELECT * FROM identity.subscriptions
           WHERE status = 'ACTIVE'
             AND (p_seller IS NULL OR seller_id = p_seller)
             AND (
               (period_to_ts IS NOT NULL AND period_to_ts <= v_now)
               OR
               (period_to_ts IS NULL AND period_to <= v_today)
             )
           ORDER BY id FOR UPDATE LOOP
    UPDATE identity.subscriptions SET status = 'EXPIRED' WHERE id = r.id;
    n := n + 1;
  END LOOP;

  FOR r IN SELECT id, status, package_expires_at, package_expires_at_ts FROM identity.sellers
           WHERE status IN ('APPROVED','ACTIVE','PAST_DUE') AND (p_seller IS NULL OR id = p_seller) ORDER BY id LOOP
    IF flow.refresh_seller_package(r.id) <> r.status THEN n := n + 1; END IF;
    IF (SELECT status FROM identity.sellers WHERE id = r.id) = 'PAST_DUE'
       AND (
         (r.package_expires_at_ts IS NOT NULL AND r.package_expires_at_ts + make_interval(days => v_grace) <= v_now)
         OR
         (r.package_expires_at_ts IS NULL AND r.package_expires_at + v_grace <= v_today)
       )
       AND EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE seller_id = r.id AND status = 'ACTIVE') THEN
      PERFORM flow.audit(NULL, 'SELLER_SLOTS_RELEASED', 'sellers', r.id,
                         jsonb_build_object('expired', r.package_expires_at, 'expired_ts', r.package_expires_at_ts, 'grace_days', v_grace));
      n := n + flow.release_seller_slots(r.id, 'PAST_DUE_GRACE');
    END IF;
  END LOOP;
  RETURN n;
END $$;

-- Update screen.v_kiosk_catalog: uses flow.package_valid and restricts to WALK_IN or unzoned slots
CREATE OR REPLACE VIEW screen.v_kiosk_catalog AS
SELECT k.id AS kiosk_id, k.code AS kiosk, sl.slot_code, b.id AS bouquet_id, fp.id AS product_id, fp.name, fp.tags, b.price_snapshot AS gia, b.sellable_until, s.shop_name
FROM kiosk_ops.slots sl JOIN kiosk_ops.kiosks k ON k.id = sl.kiosk_id JOIN kiosk_ops.bouquets b ON b.id = sl.bouquet_id
JOIN catalog.flower_products fp ON fp.id = b.product_id JOIN identity.sellers s ON s.id = b.seller_id
WHERE sl.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND s.status = 'ACTIVE' AND flow.package_valid(s.id)
  AND (sl.zone IS NULL OR sl.zone = 'WALK_IN');

-- Update flow.ai_candidates: uses flow.package_valid and restricts to WALK_IN or unzoned slots
CREATE OR REPLACE FUNCTION flow.ai_candidates(p_kiosk uuid, p_budget bigint, p_occasion text)
RETURNS TABLE (bouquet_id uuid, product_id uuid, seller_id uuid, name text, price bigint, tags text[], description text, brand_tone text) LANGUAGE sql STABLE AS $$
  SELECT b.id, p.id, p.seller_id, p.name, b.price_snapshot, p.tags, p.description, sl.brand_tone
  FROM kiosk_ops.slots s JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id JOIN catalog.flower_products p ON p.id = b.product_id
  JOIN identity.sellers sl ON sl.id = p.seller_id
  WHERE s.kiosk_id = p_kiosk AND s.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() AND sl.status = 'ACTIVE' AND flow.package_valid(sl.id)
    AND (s.zone IS NULL OR s.zone = 'WALK_IN')
    AND (p_budget IS NULL OR b.price_snapshot <= p_budget)
    AND NOT (p_occasion IN ('BIRTHDAY','CONGRATS','ANNIVERSARY','VALENTINE') AND p.tags && ARRAY['WHITE','SYMPATHY'])
    AND NOT (p_occasion = 'SYMPATHY' AND p.tags && ARRAY['ROMANTIC','FUNNY','RED'])
$$;

-- Update screen.v_web_catalog: uses PREORDER or unzoned slots and flow.package_valid
CREATE OR REPLACE VIEW screen.v_web_catalog AS
SELECT p.id AS product_id,p.seller_id,s.shop_name,p.name,p.description,p.price,k.id AS kiosk_id,k.name AS kiosk_name,k.address,
 (SELECT a.file_url FROM notify.attachments a WHERE a.owner_service='catalog' AND a.owner_type='flower_product' AND a.owner_id=p.id ORDER BY a.created_at LIMIT 1) AS photo,
 ARRAY(SELECT b.id FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots sl ON sl.bouquet_id=b.id
       WHERE b.product_id=p.id AND sl.kiosk_id=k.id AND sl.status='STOCKED' AND b.status='STOCKED' AND b.sellable_until>public.app_now()+flow.cfg_min('hold_minutes')
         AND flow.slot_sellable(sl.id,s.id)
         AND (sl.zone IS NULL OR sl.zone = 'PREORDER')
       ORDER BY b.sellable_until,b.id) AS bouquets
FROM catalog.flower_products p JOIN identity.sellers s ON s.id=p.seller_id
JOIN kiosk_ops.kiosks k ON k.status NOT IN ('DISABLED','MAINTENANCE')
  AND length(btrim(coalesce(k.address, ''))) > 0
  AND k.latitude IS NOT NULL
  AND k.longitude IS NOT NULL
WHERE p.status='ACTIVE' AND s.status='ACTIVE' AND flow.package_valid(s.id)
AND EXISTS(SELECT 1 FROM kiosk_ops.slots sl WHERE sl.kiosk_id=k.id AND sl.current_seller_id=s.id AND flow.slot_sellable(sl.id,s.id) AND (sl.zone IS NULL OR sl.zone = 'PREORDER'));

-- Update flow.kiosk_checkout: enforce zone boundaries (walk-in cannot purchase PREORDER slots)
DROP FUNCTION IF EXISTS flow.kiosk_checkout(uuid, uuid[], uuid, jsonb, bigint, text, timestamptz, boolean);
CREATE OR REPLACE FUNCTION flow.kiosk_checkout(
  p_kiosk uuid, p_bouquets uuid[], p_customer uuid DEFAULT NULL, p_accessories jsonb DEFAULT '[]',
  p_points bigint DEFAULT 0, p_ecard text DEFAULT NULL, p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); v_checkout uuid := public.uuid_v7(); r record; v_order uuid; v_total bigint;
        v_kstatus text; v_target uuid; v_sub bigint; v_sellers uuid[]; v_accs jsonb; v_cap bigint;
        v_is_web boolean;
BEGIN
  v_is_web := nullif(current_setting('flow.is_web_request', true), '') IS NOT NULL;
  p_bouquets := coalesce(p_bouquets, '{}'); p_points := coalesce(p_points, 0);
  SELECT status INTO v_kstatus FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  IF v_kstatus IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk không hoạt động (%)', v_kstatus; END IF;
  IF cardinality(p_bouquets) = 0 AND jsonb_array_length(coalesce(p_accessories, '[]')) = 0 THEN RAISE EXCEPTION 'Giỏ hàng trống'; END IF;
  IF p_ecard IS NOT NULL AND length(p_ecard) > 150 THEN RAISE EXCEPTION 'Lời thiệp tối đa 150 ký tự'; END IF;
  IF p_customer IS NOT NULL AND NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tài khoản khách % không hợp lệ hoặc đã bị xóa', p_customer; END IF;
  -- khóa ô và bó theo thứ tự id để tránh deadlock khi 2 khách cùng bấm
  FOR r IN SELECT b.id AS bouquet_id, b.status AS bstatus, b.sellable_until, s.id AS slot_id, s.kiosk_id, s.status AS sstatus, s.current_seller_id, p.name, sl.status AS seller_status, s.zone, s.slot_code
           FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id = b.id JOIN catalog.flower_products p ON p.id = b.product_id
           JOIN identity.sellers sl ON sl.id = b.seller_id
           WHERE b.id = ANY(p_bouquets) ORDER BY s.id FOR UPDATE OF s, b
  LOOP
    IF r.kiosk_id <> p_kiosk OR r.sstatus <> 'STOCKED' OR r.bstatus <> 'STOCKED' THEN RAISE EXCEPTION 'Bó % không còn sẵn để bán', r.name; END IF;
    IF r.sellable_until <= v_now THEN RAISE EXCEPTION 'Bó % đã quá hạn bán', r.name; END IF;
    IF r.seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller của bó % đang % (tạm ngưng bán)', r.name, r.seller_status; END IF;
    IF NOT flow.slot_sellable(r.slot_id, r.current_seller_id) THEN RAISE EXCEPTION 'Gói của seller bán bó % đã hết hạn, không bán', r.name; END IF;

    -- Zoneguard: walk-in checkout at kiosk cannot purchase PREORDER slots
    IF r.zone = 'PREORDER' AND NOT v_is_web THEN
      RAISE EXCEPTION 'Ô % thuộc vùng đặt trước (PREORDER), không thể mua trực tiếp tại kiosk', r.slot_code;
    END IF;
    IF r.zone = 'WALK_IN' AND v_is_web THEN
      RAISE EXCEPTION 'Không thể đặt trước bó hoa trong ô vãng lai (WALK_IN)';
    END IF;
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
    PERFORM flow.redeem_points(p_customer, p_points, v_target);
  END IF;
  UPDATE ordering.orders SET points_earned = CASE WHEN p_customer IS NULL THEN 0 ELSE floor(total_amount * flow.cfg('points_rate_percent') / 100) END
   WHERE checkout_id = v_checkout;

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
  PERFORM flow.audit(p_kiosk, 'CHECKOUT_STARTED', 'orders', v_checkout, jsonb_build_object('total', v_total, 'bouquets', cardinality(p_bouquets)));
  RETURN v_checkout;
END $$;

-- Update flow.web_request: enforce zone boundaries for preorders
CREATE OR REPLACE FUNCTION flow.web_request(p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid, p_bouquet uuid, p_pickup timestamp with time zone, p_instructions text)
 RETURNS uuid
 LANGUAGE plpgsql
AS $function$
DECLARE pr catalog.flower_products; w ordering.web_requests; v_checkout uuid; v_order uuid; v_end timestamptz; v_slot_zone text;
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
  IF p_kind NOT IN ('STOCK','CUSTOM') OR length(p_instructions)>2000 OR NOT EXISTS(
    SELECT 1 FROM kiosk_ops.kiosks
    WHERE id=p_kiosk
      AND status NOT IN ('DISABLED','MAINTENANCE')
      AND length(btrim(coalesce(address,'')))>0
      AND latitude IS NOT NULL
      AND longitude IS NOT NULL
  ) THEN RAISE EXCEPTION 'Thong tin dat hoa khong hop le'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=pr.seller_id AND status NOT IN ('FREE','PENDING_RELEASE')) THEN RAISE EXCEPTION 'Shop khong phuc vu tu nay'; END IF;
  IF p_kind='CUSTOM' THEN
    IF length(btrim(p_instructions))<10 OR p_pickup<public.app_now()+interval '2 hours' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Mo ta it nhat 10 ky tu, hen nhan tu 2 gio den 30 ngay'; END IF;
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=pr.seller_id AND (zone IS NULL OR zone = 'PREORDER')) THEN
      RAISE EXCEPTION 'Kiosk không có ô đặt trước (PREORDER) cho shop này';
    END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,p_pickup,p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),'REQUESTED');
  ELSE
    SELECT s.zone INTO v_slot_zone FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id=b.id WHERE b.id=p_bouquet AND b.product_id=pr.id AND s.kiosk_id=p_kiosk;
    IF NOT FOUND THEN RAISE EXCEPTION 'Bo hoa khong thuoc tu nay'; END IF;
    IF v_slot_zone = 'WALK_IN' THEN
      RAISE EXCEPTION 'Không thể đặt trước bó hoa trong ô vãng lai (WALK_IN)';
    END IF;
    PERFORM set_config('flow.is_web_request', 'true', true);
    v_checkout:=flow.kiosk_checkout(p_kiosk,ARRAY[p_bouquet],p_customer);
    PERFORM set_config('flow.is_web_request', '', true);
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
END $function$;

-- Update flow.web_fulfill: custom preorder stocking requires PREORDER zone
CREATE OR REPLACE FUNCTION flow.web_fulfill(p_id uuid,p_seller uuid,p_actor uuid,p_slot uuid,p_qr text) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; o ordering.orders; s kiosk_ops.slots; b uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id=w.order_id FOR UPDATE;
  IF w.kind<>'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id=o.id AND item_type='BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF public.app_now()<w.pickup_at-interval '2 hours' OR public.app_now()>=w.pickup_before THEN RAISE EXCEPTION 'Nap hoa trong 2 gio truoc hen va truoc han nhan'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id=p_slot AND current_seller_id=p_seller AND kiosk_id=w.kiosk_id FOR UPDATE;
  IF NOT FOUND OR s.status<>'RENTED_EMPTY' THEN RAISE EXCEPTION 'Can o trong cua shop tai dung tu nhan'; END IF;
  IF s.zone = 'WALK_IN' THEN
    RAISE EXCEPTION 'Đơn đặt riêng phải nạp vào ô đặt trước (PREORDER)';
  END IF;
  b:=flow.stock_bouquet(flow.new_batch(),w.product_id,p_slot,p_actor,p_qr);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id=b)<w.pickup_before THEN RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan'; END IF;
  UPDATE kiosk_ops.bouquets SET status='SOLD',price_snapshot=o.total_amount WHERE id=b;
  UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=o.id,hold_until=w.pickup_before,row_version=row_version+1 WHERE id=p_slot;
  INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
    VALUES(o.id,'BOUQUET',b,p_slot,'Hoa dat rieng: '||(SELECT name FROM catalog.flower_products WHERE id=w.product_id),1,o.total_amount,o.total_amount);
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,order_id,performed_by) VALUES(b,p_slot,'SALE_OUT',o.id,p_actor);
  PERFORM flow.audit(p_actor,'WEB_ORDER_READY','orders',o.id,jsonb_build_object('request',p_id,'slot',p_slot));
  RETURN b;
END $$;

-- 5. Create kiosk_ops.kiosk_applications table and add snapshot columns
CREATE TABLE IF NOT EXISTS kiosk_ops.kiosk_applications (
  id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  customer_id uuid NOT NULL REFERENCES identity.users(id),
  package_id uuid NOT NULL REFERENCES identity.subscription_packages(id),
  shop_name text NOT NULL,
  phone text NOT NULL,
  contact_email text NOT NULL,
  installation_address text NOT NULL,
  latitude double precision NOT NULL,
  longitude double precision NOT NULL,
  bank_name text NOT NULL,
  bank_holder text NOT NULL,
  bank_account_no_enc text NOT NULL,
  status text NOT NULL DEFAULT 'AWAITING_PAYMENT',
  payment_id uuid REFERENCES payment.payments(id),
  assigned_technician_id uuid REFERENCES identity.users(id),
  assigned_kiosk_id uuid REFERENCES kiosk_ops.kiosks(id),
  scheduled_date date,
  time_window text,
  review_note text,
  confirmed_at timestamptz,
  technician_report text,
  rework_reason text,
  handover_at timestamptz,
  handover_by uuid REFERENCES identity.users(id),
  seller_id uuid REFERENCES identity.sellers(id),
  refund_approved_by uuid REFERENCES identity.users(id),
  refund_paid_by uuid REFERENCES identity.users(id),
  refund_reference text,
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at timestamptz NOT NULL DEFAULT public.app_now(),
  CONSTRAINT chk_kiosk_app_status CHECK (status IN (
    'AWAITING_PAYMENT', 'PAID_REVIEW', 'CONFIRMED', 'SCHEDULED',
    'INSTALLING', 'AWAITING_HANDOVER', 'ACTIVE',
    'CANCELLED', 'REFUND_PENDING', 'REFUNDED', 'REJECTED'
  )),
  CONSTRAINT chk_kiosk_app_coordinates CHECK (latitude >= -90.0 AND latitude <= 90.0 AND longitude >= -180.0 AND longitude <= 180.0)
);

-- Snapshot columns for existing candidate tables
ALTER TABLE kiosk_ops.kiosk_applications
  ADD COLUMN IF NOT EXISTS package_name text,
  ADD COLUMN IF NOT EXISTS annual_fee bigint,
  ADD COLUMN IF NOT EXISTS walk_in_slots integer,
  ADD COLUMN IF NOT EXISTS preorder_slots integer,
  ADD COLUMN IF NOT EXISTS total_slots integer,
  ADD COLUMN IF NOT EXISTS start_time time,
  ADD COLUMN IF NOT EXISTS end_time time;

ALTER TABLE identity.subscriptions
  ADD COLUMN IF NOT EXISTS period_from_ts timestamptz,
  ADD COLUMN IF NOT EXISTS period_to_ts timestamptz;

CREATE UNIQUE INDEX IF NOT EXISTS uq_kiosk_applications_customer_inflight
  ON kiosk_ops.kiosk_applications (customer_id)
  WHERE status IN ('AWAITING_PAYMENT', 'PAID_REVIEW', 'CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER');

CREATE UNIQUE INDEX IF NOT EXISTS uq_kiosk_applications_reserved_kiosk
  ON kiosk_ops.kiosk_applications (assigned_kiosk_id)
  WHERE status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER', 'ACTIVE')
    AND assigned_kiosk_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_kiosk_applications_status ON kiosk_ops.kiosk_applications(status);
CREATE INDEX IF NOT EXISTS idx_kiosk_applications_customer ON kiosk_ops.kiosk_applications(customer_id);
CREATE INDEX IF NOT EXISTS idx_kiosk_applications_technician ON kiosk_ops.kiosk_applications(assigned_technician_id, status);
CREATE INDEX IF NOT EXISTS idx_kiosk_applications_kiosk ON kiosk_ops.kiosk_applications(assigned_kiosk_id);

-- 6. Close legacy open_member_shop from promoting Customers
CREATE OR REPLACE FUNCTION flow.open_member_shop(p_user uuid, p_shop text, p_phone text, p_address text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE u identity.users;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id = p_user FOR UPDATE;
  IF NOT FOUND OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Active member account required' USING ERRCODE = '42501';
  END IF;
  IF u.role = 'SELLER' AND u.seller_id IS NOT NULL THEN
    RETURN u.seller_id;
  END IF;
  RAISE EXCEPTION 'Đăng ký mở shop trực tiếp đã ngừng hỗ trợ. Vui lòng nộp hồ sơ thuê kiosk hàng năm qua /api/member/kiosk-applications.' USING ERRCODE = '42501';
END $$;

-- 7. Create kiosk application flow with immutable snapshots
CREATE OR REPLACE FUNCTION flow.create_kiosk_application(
  p_customer_id uuid,
  p_package_id uuid,
  p_shop_name text,
  p_phone text,
  p_contact_email text,
  p_installation_address text,
  p_lat double precision,
  p_lon double precision,
  p_bank_name text,
  p_bank_holder text,
  p_bank_account_no_enc text,
  p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  u identity.users;
  pkg identity.subscription_packages;
  v_app_id uuid := public.uuid_v7();
  v_payment_id uuid := public.uuid_v7();
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('customer-kiosk-app:' || p_customer_id::text, 0));

  SELECT * INTO u FROM identity.users WHERE id = p_customer_id FOR UPDATE;
  IF NOT FOUND OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Tài khoản khách không hợp lệ' USING ERRCODE = '42501';
  END IF;
  IF u.role = 'SELLER' THEN
    RAISE EXCEPTION 'Khách hàng đã sở hữu vai trò người bán (SELLER)' USING ERRCODE = 'P0001';
  END IF;
  IF u.role <> 'CUSTOMER' THEN
    RAISE EXCEPTION 'Chỉ tài khoản khách hàng (CUSTOMER) mới được đăng ký hồ sơ kiosk' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO pkg FROM identity.subscription_packages WHERE id = p_package_id;
  IF NOT FOUND OR pkg.status <> 'ACTIVE' OR pkg.billing_cycle <> 'ANNUAL' THEN
    RAISE EXCEPTION 'Gói thuê bao hàng năm không hợp lệ' USING ERRCODE = '22023';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE customer_id = p_customer_id
      AND status IN ('AWAITING_PAYMENT', 'PAID_REVIEW', 'CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER')
  ) THEN
    RAISE EXCEPTION 'Bạn đang có một hồ sơ đăng ký kiosk đang được xử lý' USING ERRCODE = 'P0001';
  END IF;

  -- Create pending charge payment
  INSERT INTO payment.payments (
    id, kind, purpose, amount, gateway, idempotency_key, status, created_at, updated_at
  ) VALUES (
    v_payment_id, 'CHARGE', 'KIOSK_APPLICATION', pkg.annual_fee, 'PAYOS',
    'kiosk-app-' || v_app_id::text, 'PENDING', v_now, v_now
  );

  -- Create kiosk application with immutable snapshots
  INSERT INTO kiosk_ops.kiosk_applications (
    id, customer_id, package_id, shop_name, phone, contact_email, installation_address,
    latitude, longitude, bank_name, bank_holder, bank_account_no_enc, status, payment_id,
    package_name, annual_fee, walk_in_slots, preorder_slots, total_slots,
    created_at, updated_at
  ) VALUES (
    v_app_id, p_customer_id, p_package_id, btrim(p_shop_name), btrim(p_phone),
    lower(btrim(p_contact_email)), btrim(p_installation_address), p_lat, p_lon,
    btrim(p_bank_name), btrim(p_bank_holder), p_bank_account_no_enc, 'AWAITING_PAYMENT',
    v_payment_id, pkg.name, pkg.annual_fee, pkg.walk_in_slots, pkg.preorder_slots, pkg.max_slots,
    v_now, v_now
  );

  PERFORM flow.audit(p_customer_id, 'KIOSK_APPLICATION_CREATED', 'kiosk_applications', v_app_id,
                     jsonb_build_object('package', pkg.name, 'amount', pkg.annual_fee));

  RETURN v_app_id;
END $$;

-- 8. Payment settlement flow for Kiosk Applications (supports CANCELLED & REJECTED late payments)
CREATE OR REPLACE FUNCTION flow.kiosk_application_paid(p_payment uuid, p_txn text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  p payment.payments;
  app kiosk_ops.kiosk_applications;
  j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO p FROM payment.payments WHERE id = p_payment FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khoản thanh toán % không tồn tại', p_payment; END IF;
  IF p.status = 'SUCCEEDED' THEN RETURN; END IF;

  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code', '00', 'orderCode', p_txn) WHERE id = p.id;

  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE payment_id = p.id FOR UPDATE;
  IF NOT FOUND THEN RETURN; END IF;

  IF app.status IN ('CANCELLED', 'REJECTED') THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'REFUND_PENDING', updated_at = v_now WHERE id = app.id;
    INSERT INTO payment.payments (kind, purpose, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND', 'KIOSK_APPLICATION', p.id, 'MANUAL', 'late-app-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_PAYMENT_AFTER_CANCEL')
    ON CONFLICT DO NOTHING;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
      (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', p.id, 'Phí kiosk về muộn sau khi đơn bị hủy hoặc từ chối'),
      (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại khách (LATE_PAYMENT_AFTER_CANCEL)');
    PERFORM flow.audit(app.customer_id, 'LATE_PAYMENT_AFTER_CANCEL', 'kiosk_applications', app.id, jsonb_build_object('amount', p.amount));
    RETURN;
  END IF;

  IF app.status = 'AWAITING_PAYMENT' THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'PAID_REVIEW', updated_at = v_now WHERE id = app.id;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
      (j, 'GATEWAY_CLEARING',      NULL,  p.amount, 'KIOSK_APPLICATION', app.id, 'Thu phí hồ sơ thuê kiosk hàng năm'),
      (j, 'PLATFORM_SUBSCRIPTION', NULL, -p.amount, 'KIOSK_APPLICATION', app.id, 'Doanh thu tạm giữ phí kiosk');
    PERFORM flow.audit(app.customer_id, 'KIOSK_APPLICATION_PAID', 'kiosk_applications', app.id, jsonb_build_object('amount', p.amount));
  END IF;
END $$;

-- 9. Atomic state transition flows

-- Confirm contact
CREATE OR REPLACE FUNCTION flow.kiosk_application_confirm(p_app_id uuid, p_actor uuid, p_note text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status <> 'PAID_REVIEW' THEN
    RAISE EXCEPTION 'Chỉ xác nhận đơn ở trạng thái đã thanh toán chờ duyệt (PAID_REVIEW)' USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'CONFIRMED', review_note = btrim(p_note), confirmed_at = v_now, updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_CONFIRMED', 'kiosk_applications', app.id,
                     jsonb_build_object('note', btrim(p_note)));
END $$;

-- Schedule technician and assign kiosk
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

  -- Validate technician with concurrency lock
  PERFORM pg_advisory_xact_lock(hashtextextended('tech-schedule:' || p_tech_id::text, 0));
  SELECT * INTO u FROM identity.users WHERE id = p_tech_id FOR UPDATE;
  IF NOT FOUND OR u.role <> 'TECHNICIAN' OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Kỹ thuật viên không tồn tại hoặc không ở trạng thái hoạt động' USING ERRCODE = 'P0001';
  END IF;

  -- Reject overlapping jobs for same technician on that date
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_technician_id = p_tech_id
      AND scheduled_date = p_scheduled_date
      AND status IN ('SCHEDULED', 'INSTALLING')
      AND id <> p_app_id
      AND (
        (start_time IS NOT NULL AND end_time IS NOT NULL AND start_time < v_end_time AND v_start_time < end_time)
        OR
        (time_window = btrim(p_time_window))
      )
  ) THEN
    RAISE EXCEPTION 'Kỹ thuật viên đã có lịch trùng hoặc chồng chéo vào khung giờ này' USING ERRCODE = 'P0001';
  END IF;

  -- Validate kiosk
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-reserve:' || p_kiosk_id::text, 0));
  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = p_kiosk_id FOR UPDATE;
  IF NOT FOUND OR k.status = 'DISABLED' THEN
    RAISE EXCEPTION 'Kiosk không tồn tại hoặc đã bị vô hiệu hóa' USING ERRCODE = 'P0001';
  END IF;

  SELECT
    count(*) FILTER (WHERE zone = 'WALK_IN'),
    count(*) FILTER (WHERE zone = 'PREORDER'),
    count(*)
  INTO v_w_count, v_p_count, v_total_slots
  FROM kiosk_ops.slots WHERE kiosk_id = k.id;

  IF v_w_count <> app.walk_in_slots OR v_p_count <> app.preorder_slots OR v_total_slots <> app.total_slots THEN
    RAISE EXCEPTION 'Cấu hình ô của kiosk (W: %, P: %, tổng: %) không khớp với gói (W: %, P: %, tổng: %)',
      v_w_count, v_p_count, v_total_slots, app.walk_in_slots, app.preorder_slots, app.total_slots
      USING ERRCODE = '22023';
  END IF;

  -- Ensure all slots are unassigned, FREE, no bouquets, no holds, no active assignments, not FAULT/LOCKED
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots s
    WHERE s.kiosk_id = k.id
      AND (
        s.status <> 'FREE'
        OR s.current_seller_id IS NOT NULL
        OR s.bouquet_id IS NOT NULL
        OR s.hold_until IS NOT NULL
        OR s.hold_order_id IS NOT NULL
        OR EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a WHERE a.slot_id = s.id AND a.status = 'ACTIVE')
      )
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
      status = 'SCHEDULED', updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_SCHEDULED', 'kiosk_applications', app.id,
                     jsonb_build_object('tech', p_tech_id, 'kiosk', p_kiosk_id, 'date', p_scheduled_date, 'window', to_char(v_start_time, 'HH24:MI') || ' - ' || to_char(v_end_time, 'HH24:MI')));
END $$;

-- Require rework
CREATE OR REPLACE FUNCTION flow.kiosk_application_rework(p_app_id uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status <> 'AWAITING_HANDOVER' THEN
    RAISE EXCEPTION 'Chỉ yêu cầu làm lại khi đơn đang chờ nghiệm thu bàn giao (AWAITING_HANDOVER)' USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'INSTALLING', rework_reason = btrim(p_reason), updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REWORK_REQUIRED', 'kiosk_applications', app.id,
                     jsonb_build_object('reason', btrim(p_reason)));
END $$;

-- Start visit
CREATE OR REPLACE FUNCTION flow.kiosk_application_start(p_app_id uuid, p_actor uuid, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.assigned_technician_id IS DISTINCT FROM p_actor THEN
    RAISE EXCEPTION 'Bạn không được phân công cho đơn đăng ký này' USING ERRCODE = '42501';
  END IF;
  IF app.status <> 'SCHEDULED' THEN
    RAISE EXCEPTION 'Chỉ bắt đầu công việc khi ở trạng thái đã lên lịch (SCHEDULED)' USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'INSTALLING', updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_INSTALLATION_STARTED', 'kiosk_applications', app.id);
END $$;

-- Submit report
CREATE OR REPLACE FUNCTION flow.kiosk_application_submit(p_app_id uuid, p_actor uuid, p_report text, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.assigned_technician_id IS DISTINCT FROM p_actor THEN
    RAISE EXCEPTION 'Bạn không được phân công cho đơn đăng ký này' USING ERRCODE = '42501';
  END IF;
  IF app.status <> 'INSTALLING' THEN
    RAISE EXCEPTION 'Chỉ nộp báo cáo khi công việc đang thực hiện (INSTALLING)' USING ERRCODE = 'P0001';
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM notify.attachments
    WHERE owner_service = 'kiosk_ops' AND owner_type = 'kiosk_application' AND owner_id = app.id AND phase = 'EVIDENCE'
  ) THEN
    RAISE EXCEPTION 'Cần ít nhất một ảnh bằng chứng lắp đặt trước khi bàn giao' USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'AWAITING_HANDOVER', technician_report = btrim(p_report), updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_INSTALLATION_SUBMITTED', 'kiosk_applications', app.id,
                     jsonb_build_object('report', btrim(p_report)));
END $$;

-- Cancellation flow
CREATE OR REPLACE FUNCTION flow.kiosk_application_cancel(p_app_id uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  p payment.payments;
  j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.customer_id <> p_actor THEN RAISE EXCEPTION 'Từ chối quyền truy cập' USING ERRCODE = '42501'; END IF;

  IF app.status = 'AWAITING_PAYMENT' THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'CANCELLED', review_note = p_reason, updated_at = v_now WHERE id = app.id;
    IF app.payment_id IS NOT NULL THEN
      UPDATE payment.payments SET status = 'CANCELLED', updated_at = v_now WHERE id = app.payment_id AND status = 'PENDING';
    END IF;
    PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_CANCELLED_UNPAID', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
    RETURN 'CANCELLED';
  ELSIF app.status = 'PAID_REVIEW' THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'REFUND_PENDING', review_note = p_reason, updated_at = v_now WHERE id = app.id;
    SELECT * INTO p FROM payment.payments WHERE id = app.payment_id;
    IF p.id IS NOT NULL THEN
      INSERT INTO payment.payments (kind, purpose, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
      VALUES ('REFUND', 'KIOSK_APPLICATION', p.id, 'MANUAL', 'cancel-app-' || p.id, p.amount, 'PENDING', flow.sys_user(), coalesce(p_reason, 'CUSTOMER_VOLUNTARY_CANCEL'))
      ON CONFLICT DO NOTHING;
      INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
        (j, 'PLATFORM_SUBSCRIPTION', NULL,  p.amount, 'REFUND', p.id, 'Hoàn tiền khách hủy đơn trước xác nhận'),
        (j, 'REFUND_CLEARING',       NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại khách (CUSTOMER_VOLUNTARY_CANCEL)');
    END IF;
    PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REFUND_REQUESTED', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
    RETURN 'REFUND_PENDING';
  ELSE
    RAISE EXCEPTION 'Đơn đăng ký đã được xác nhận, không thể tự hủy' USING ERRCODE = 'P0001';
  END IF;
END $$;

-- Rejection flow
CREATE OR REPLACE FUNCTION flow.kiosk_application_reject(p_app_id uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  p payment.payments;
  j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;

  IF app.status = 'AWAITING_PAYMENT' THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'REJECTED', review_note = p_reason, updated_at = v_now WHERE id = app.id;
    IF app.payment_id IS NOT NULL THEN
      UPDATE payment.payments SET status = 'CANCELLED', updated_at = v_now WHERE id = app.payment_id AND status = 'PENDING';
    END IF;
    PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REJECTED_UNPAID', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
    RETURN 'REJECTED';
  ELSIF app.status = 'PAID_REVIEW' THEN
    UPDATE kiosk_ops.kiosk_applications SET status = 'REFUND_PENDING', review_note = p_reason, updated_at = v_now WHERE id = app.id;
    SELECT * INTO p FROM payment.payments WHERE id = app.payment_id;
    IF p.id IS NOT NULL THEN
      INSERT INTO payment.payments (kind, purpose, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
      VALUES ('REFUND', 'KIOSK_APPLICATION', p.id, 'MANUAL', 'reject-app-' || p.id, p.amount, 'PENDING', p_actor, coalesce(p_reason, 'MANAGER_REJECTED_PAID'))
      ON CONFLICT DO NOTHING;
      INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
        (j, 'PLATFORM_SUBSCRIPTION', NULL,  p.amount, 'REFUND', p.id, 'Hoàn tiền quản lý từ chối đơn đã thanh toán'),
        (j, 'REFUND_CLEARING',       NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại khách (MANAGER_REJECTED_PAID)');
    END IF;
    PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REJECTED_PAID', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
    RETURN 'REFUND_PENDING';
  ELSE
    RAISE EXCEPTION 'Không thể từ chối đơn ở trạng thái %', app.status USING ERRCODE = 'P0001';
  END IF;
END $$;

-- Platform failure flow
CREATE OR REPLACE FUNCTION flow.kiosk_application_platform_failure(p_app_id uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  p payment.payments;
  j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status NOT IN ('CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER') THEN
    RAISE EXCEPTION 'Không thể xử lý sự cố nền tảng cho đơn ở trạng thái %', app.status USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications SET status = 'REFUND_PENDING', rework_reason = p_reason, updated_at = v_now WHERE id = app.id;
  SELECT * INTO p FROM payment.payments WHERE id = app.payment_id;
  IF p.id IS NOT NULL THEN
    INSERT INTO payment.payments (kind, purpose, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND', 'KIOSK_APPLICATION', p.id, 'MANUAL', 'failure-app-' || p.id, p.amount, 'PENDING', p_actor, coalesce(p_reason, 'PLATFORM_FAILURE_REMEDIATION'))
    ON CONFLICT DO NOTHING;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
      (j, 'PLATFORM_SUBSCRIPTION', NULL,  p.amount, 'REFUND', p.id, 'Hoàn tiền do sự cố triển khai kỹ thuật/mặt bằng'),
      (j, 'REFUND_CLEARING',       NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại khách (PLATFORM_FAILURE_REMEDIATION)');
  END IF;
  PERFORM flow.audit(p_actor, 'PLATFORM_FAILURE_REMEDIATION', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
  RETURN 'REFUND_PENDING';
END $$;

-- Handover execution flow
CREATE OR REPLACE FUNCTION flow.kiosk_application_handover(p_application_id uuid, p_manager_id uuid, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  k kiosk_ops.kiosks;
  u identity.users;
  p payment.payments;
  v_w_count integer;
  v_p_count integer;
  v_total_slots integer;
  v_seller_id uuid;
  v_sub_id uuid;
  v_expiry_ts timestamptz;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-handover:' || p_application_id::text, 0));

  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_application_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_application_id; END IF;
  IF app.status <> 'AWAITING_HANDOVER' THEN
    RAISE EXCEPTION 'Đơn đăng ký chưa sẵn sàng bàn giao (trạng thái: %)', app.status USING ERRCODE = 'P0001';
  END IF;

  -- Verify succeeded payment
  SELECT * INTO p FROM payment.payments WHERE id = app.payment_id;
  IF p.id IS NULL OR p.status <> 'SUCCEEDED' OR p.amount <> app.annual_fee THEN
    RAISE EXCEPTION 'Khoản thanh toán chưa hợp lệ hoặc chưa thành công' USING ERRCODE = 'P0001';
  END IF;

  -- Verify photo evidence exists
  IF NOT EXISTS (
    SELECT 1 FROM notify.attachments
    WHERE owner_service = 'kiosk_ops' AND owner_type = 'kiosk_application' AND owner_id = app.id AND phase = 'EVIDENCE'
  ) THEN
    RAISE EXCEPTION 'Chưa có ảnh bằng chứng nghiệm thu lắp đặt từ kỹ thuật viên' USING ERRCODE = 'P0001';
  END IF;

  -- Verify assigned active technician
  IF app.assigned_technician_id IS NULL OR NOT EXISTS (
    SELECT 1 FROM identity.users WHERE id = app.assigned_technician_id AND role = 'TECHNICIAN' AND status = 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'Kỹ thuật viên được phân công không hợp lệ hoặc đã ngừng hoạt động' USING ERRCODE = 'P0001';
  END IF;

  -- Verify customer user
  SELECT * INTO u FROM identity.users WHERE id = app.customer_id FOR UPDATE;
  IF NOT FOUND OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Tài khoản khách không hoạt động' USING ERRCODE = 'P0001';
  END IF;
  IF u.role = 'SELLER' THEN
    RAISE EXCEPTION 'Khách hàng đã sở hữu vai trò người bán (SELLER)' USING ERRCODE = 'P0001';
  END IF;
  IF u.role <> 'CUSTOMER' THEN
    RAISE EXCEPTION 'Tài khoản người dùng không còn là CUSTOMER hoạt động' USING ERRCODE = 'P0001';
  END IF;

  -- Verify assigned kiosk
  IF app.assigned_kiosk_id IS NULL THEN
    RAISE EXCEPTION 'Chưa gán kiosk cho đơn đăng ký này' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = app.assigned_kiosk_id FOR UPDATE;
  IF NOT FOUND OR k.status = 'DISABLED' THEN
    RAISE EXCEPTION 'Kiosk không tồn tại hoặc đã bị vô hiệu hóa' USING ERRCODE = 'P0001';
  END IF;

  -- Check exact zone capacities against immutable snapshots
  SELECT
    count(*) FILTER (WHERE zone = 'WALK_IN'),
    count(*) FILTER (WHERE zone = 'PREORDER'),
    count(*)
  INTO v_w_count, v_p_count, v_total_slots
  FROM kiosk_ops.slots WHERE kiosk_id = k.id;

  IF v_w_count <> app.walk_in_slots OR v_p_count <> app.preorder_slots OR v_total_slots <> app.total_slots THEN
    RAISE EXCEPTION 'Cấu hình ô của kiosk (W: %, P: %, tổng: %) không khớp với gói (W: %, P: %, tổng: %)',
      v_w_count, v_p_count, v_total_slots, app.walk_in_slots, app.preorder_slots, app.total_slots
      USING ERRCODE = 'P0001';
  END IF;

  -- Lock and validate all slots of the kiosk
  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = k.id FOR UPDATE;

  -- Ensure all slots are unassigned, FREE, no bouquet, no holds, no active assignments, not FAULT/LOCKED
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots s
    WHERE s.kiosk_id = k.id
      AND (
        s.status <> 'FREE'
        OR s.current_seller_id IS NOT NULL
        OR s.bouquet_id IS NOT NULL
        OR s.hold_until IS NOT NULL
        OR s.hold_order_id IS NOT NULL
        OR EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a WHERE a.slot_id = s.id AND a.status = 'ACTIVE')
      )
  ) THEN
    RAISE EXCEPTION 'Kiosk % có ô không ở trạng thái sẵn sàng để gán (phải hoàn toàn FREE và chưa có shop thuê/chứa hoa)', k.id USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_kiosk_id = k.id AND id <> app.id AND status = 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'Kiosk % đã được bàn giao cho đơn đăng ký khác', k.id USING ERRCODE = 'P0001';
  END IF;

  v_expiry_ts := v_now + interval '1 year';

  -- 1. Create Seller record with business contact email and exact timestamp
  INSERT INTO identity.sellers (
    shop_name, phone, address, status, package_id, package_expires_at, package_expires_at_ts,
    contact_email, bank_name, bank_holder, bank_account_no_enc
  ) VALUES (
    app.shop_name, app.phone, app.installation_address, 'ACTIVE', app.package_id,
    v_expiry_ts::date, v_expiry_ts, app.contact_email,
    app.bank_name, app.bank_holder, app.bank_account_no_enc
  ) RETURNING id INTO v_seller_id;

  -- 2. Create 1-year active Subscription with exact timestamp
  INSERT INTO identity.subscriptions (
    seller_id, package_id, period_from, period_to, period_from_ts, period_to_ts, price, status, payment_id
  ) VALUES (
    v_seller_id, app.package_id, v_now::date, v_expiry_ts::date,
    v_now, v_expiry_ts,
    app.annual_fee, 'ACTIVE', app.payment_id
  ) RETURNING id INTO v_sub_id;

  -- 3. Update Kiosk location and assign slots (preserve OFFLINE status)
  UPDATE kiosk_ops.kiosks
  SET address = app.installation_address, latitude = app.latitude, longitude = app.longitude, updated_at = v_now
  WHERE id = k.id;

  -- Insert active whole-kiosk rental assignments
  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at, status)
  SELECT s.id, v_seller_id, p_manager_id, v_now, 'ACTIVE'
  FROM kiosk_ops.slots s
  WHERE s.kiosk_id = k.id
  ORDER BY s.slot_code;

  -- Update slots to RENTED_EMPTY
  UPDATE kiosk_ops.slots
  SET current_seller_id = v_seller_id, status = 'RENTED_EMPTY', row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = k.id;

  -- 4. Promote Customer to SELLER
  UPDATE identity.users
  SET role = 'SELLER', seller_id = v_seller_id, updated_at = v_now
  WHERE id = u.id;

  -- 5. Mark application ACTIVE
  UPDATE kiosk_ops.kiosk_applications
  SET status = 'ACTIVE', seller_id = v_seller_id, handover_at = v_now, handover_by = p_manager_id, updated_at = v_now
  WHERE id = app.id;

  -- 6. Audit
  PERFORM flow.audit(p_manager_id, 'KIOSK_APPLICATION_HANDOVER_COMPLETED', 'kiosk_applications', app.id,
                     jsonb_build_object('seller', v_seller_id, 'kiosk', k.id, 'subscription', v_sub_id, 'package', app.package_name, 'expires_at', v_expiry_ts));

  RETURN v_seller_id;
END $$;

-- 10. Kiosk enrollment flow (authorized explicit asset creation, initially OFFLINE, stores api_key_hash)
CREATE OR REPLACE FUNCTION flow.enroll_kiosk(
  p_code text,
  p_name text,
  p_hardware_id text,
  p_mqtt_client_id text,
  p_region text,
  p_walk_in int,
  p_preorder int,
  p_api_key_hash text,
  p_actor uuid
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_kiosk_id uuid := public.uuid_v7();
  i int;
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
  IF p_walk_in < 1 OR p_preorder < 1 OR (p_walk_in + p_preorder) > 50 THEN
    RAISE EXCEPTION 'Số lượng ô không hợp lệ' USING ERRCODE = '22023';
  END IF;

  INSERT INTO kiosk_ops.kiosks (
    id, code, name, address, region, hardware_id, mqtt_client_id, status, last_heartbeat_at, api_key_hash
  ) VALUES (
    v_kiosk_id, btrim(p_code), btrim(p_name), '', coalesce(p_region, 'HCM'),
    btrim(p_hardware_id), coalesce(btrim(p_mqtt_client_id), 'kiosk-' || lower(btrim(p_code))),
    'OFFLINE', NULL, p_api_key_hash
  );

  -- Generate Walk-in slots (W01, W02...)
  FOR i IN 1..p_walk_in LOOP
    INSERT INTO kiosk_ops.slots (id, kiosk_id, slot_code, zone, relay_channel, status)
    VALUES (public.uuid_v7(), v_kiosk_id, 'W' || lpad(i::text, 2, '0'), 'WALK_IN', i - 1, 'FREE');
  END LOOP;

  -- Generate Preorder slots (P01, P02...)
  FOR i IN 1..p_preorder LOOP
    INSERT INTO kiosk_ops.slots (id, kiosk_id, slot_code, zone, relay_channel, status)
    VALUES (public.uuid_v7(), v_kiosk_id, 'P' || lpad(i::text, 2, '0'), 'PREORDER', p_walk_in + i - 1, 'FREE');
  END LOOP;

  PERFORM flow.audit(p_actor, 'KIOSK_ENROLLED', 'kiosks', v_kiosk_id,
                     jsonb_build_object('code', btrim(p_code), 'walk_in', p_walk_in, 'preorder', p_preorder));

  RETURN v_kiosk_id;
END $$;

-- 11. Key provisioning / rotation
CREATE OR REPLACE FUNCTION flow.kiosk_provision_key(p_kiosk_id uuid, p_key_hash text, p_actor uuid, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
BEGIN
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk_id AND status <> 'DISABLED') THEN
    RAISE EXCEPTION 'Kiosk không tồn tại hoặc đã bị vô hiệu hóa' USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosks
  SET api_key_hash = p_key_hash, updated_at = v_now
  WHERE id = p_kiosk_id;

  PERFORM flow.audit(p_actor, 'KIOSK_KEY_PROVISIONED', 'kiosks', p_kiosk_id);
END $$;

-- 12. Admin refund remediation approval
CREATE OR REPLACE FUNCTION flow.kiosk_application_refund_approve(p_app_id uuid, p_actor uuid, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
BEGIN
  PERFORM flow.require_active_admin(p_actor);
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status <> 'REFUND_PENDING' THEN
    RAISE EXCEPTION 'Đơn đăng ký không ở trạng thái chờ hoàn tiền (hiện tại: %)', app.status USING ERRCODE = 'P0001';
  END IF;

  UPDATE kiosk_ops.kiosk_applications SET refund_approved_by = p_actor, updated_at = v_now WHERE id = app.id;
  UPDATE payment.payments SET approved_by = p_actor, updated_at = v_now WHERE parent_payment_id = app.payment_id AND kind = 'REFUND';
  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REFUND_APPROVED', 'kiosk_applications', app.id, jsonb_build_object('approved_by', p_actor));
END $$;

-- 13. Admin refund remediation payout confirmation with evidence proof
CREATE OR REPLACE FUNCTION flow.kiosk_application_refund_confirm(p_app_id uuid, p_actor uuid, p_reference text, p_attachment_id uuid, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  p payment.payments;
  j uuid := public.uuid_v7();
BEGIN
  PERFORM flow.require_active_admin(p_actor);
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  IF app.status <> 'REFUND_PENDING' THEN
    RAISE EXCEPTION 'Đơn đăng ký không ở trạng thái chờ hoàn tiền (hiện tại: %)', app.status USING ERRCODE = 'P0001';
  END IF;

  IF app.refund_approved_by IS NULL THEN
    RAISE EXCEPTION 'Khoản hoàn cần được quản trị viên duyệt trước khi chi' USING ERRCODE = 'P0001';
  END IF;

  -- Separation of duty: executor must differ from approver
  IF app.refund_approved_by = p_actor THEN
    RAISE EXCEPTION 'Người chi tiền phải khác người duyệt hoàn' USING ERRCODE = 'P0001';
  END IF;

  IF p_reference IS NULL OR length(btrim(p_reference)) < 3 THEN
    RAISE EXCEPTION 'Mã giao dịch chuyển khoản không hợp lệ' USING ERRCODE = '22023';
  END IF;

  -- Verify proof attachment
  IF p_attachment_id IS NULL OR NOT EXISTS (
    SELECT 1 FROM notify.attachments
    WHERE id = p_attachment_id AND owner_service = 'kiosk_ops' AND owner_type = 'kiosk_application' AND owner_id = app.id AND phase = 'PROOF'
  ) THEN
    RAISE EXCEPTION 'Cần đính kèm chứng từ chuyển khoản hợp lệ' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO p FROM payment.payments WHERE parent_payment_id = app.payment_id AND kind = 'REFUND' FOR UPDATE;
  IF p.id IS NOT NULL THEN
    UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = v_now, gateway_txn_id = btrim(p_reference), updated_at = v_now WHERE id = p.id;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
      (j, 'REFUND_CLEARING',  NULL,  p.amount, 'REFUND_PAYOUT', p.id, 'Đã chi tiền hoàn đơn đăng ký kiosk'),
      (j, 'GATEWAY_CLEARING', NULL, -p.amount, 'REFUND_PAYOUT', p.id, 'Chi tiền qua tài khoản ngân hàng');
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'REFUNDED', refund_paid_by = p_actor, refund_reference = btrim(p_reference), updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REFUND_CONFIRMED', 'kiosk_applications', app.id,
                     jsonb_build_object('reference', btrim(p_reference), 'proof', p_attachment_id));
END $$;

-- 14. Module boundary crossing helper functions
CREATE OR REPLACE FUNCTION flow.get_payment_order_code(p_payment_id uuid)
RETURNS bigint LANGUAGE sql AS $$
  SELECT order_code FROM payment.gateway_orders WHERE payment_id = p_payment_id;
$$;

CREATE OR REPLACE FUNCTION flow.set_payment_checkout_url(p_payment_id uuid, p_url text, p_actor uuid, p_app_id uuid)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE payment.gateway_orders SET checkout_url = p_url WHERE payment_id = p_payment_id;
  PERFORM flow.audit(p_actor, 'PAYMENT_LINK_REQUESTED', 'kiosk_applications', p_app_id);
END $$;

CREATE OR REPLACE FUNCTION flow.get_payment_status(p_payment_id uuid)
RETURNS text LANGUAGE plpgsql AS $$
DECLARE v_status text;
BEGIN
  SELECT status INTO v_status FROM payment.payments WHERE id = p_payment_id FOR UPDATE;
  RETURN v_status;
END $$;

CREATE OR REPLACE FUNCTION flow.attach_kiosk_evidence(
  p_id uuid, p_app_id uuid, p_url text, p_mime text, p_size bigint, p_hash text, p_actor uuid
) RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE
  v_app kiosk_ops.kiosk_applications%ROWTYPE;
  v_created timestamptz;
BEGIN
  SELECT * INTO v_app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Hồ sơ % không tồn tại', p_app_id USING ERRCODE = 'P0002';
  END IF;
  IF v_app.assigned_technician_id IS DISTINCT FROM p_actor THEN
    RAISE EXCEPTION 'Kỹ thuật viên không được phân công hồ sơ này' USING ERRCODE = '42501';
  END IF;
  IF v_app.status <> 'INSTALLING' THEN
    RAISE EXCEPTION 'Chỉ đính kèm ảnh khi công việc đang thực hiện (INSTALLING)' USING ERRCODE = 'P0001';
  END IF;

  INSERT INTO notify.attachments(id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by)
  VALUES (p_id, 'kiosk_ops', 'kiosk_application', p_app_id, p_url, p_mime, p_size, p_hash, 'EVIDENCE', p_actor)
  RETURNING created_at INTO v_created;

  PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_EVIDENCE_ATTACHED', 'kiosk_applications', p_app_id,
                     jsonb_build_object('attachment', p_id));
  RETURN v_created;
END $$;

CREATE OR REPLACE FUNCTION flow.count_kiosk_evidence(p_app_id uuid)
RETURNS integer LANGUAGE sql AS $$
  SELECT count(*)::int FROM notify.attachments
  WHERE owner_service = 'kiosk_ops' AND owner_type = 'kiosk_application' AND owner_id = p_app_id AND phase = 'EVIDENCE';
$$;

CREATE OR REPLACE FUNCTION flow.list_kiosk_evidence(p_app_id uuid)
RETURNS TABLE (id uuid, created_at timestamptz) LANGUAGE sql AS $$
  SELECT a.id, a.created_at FROM notify.attachments a
  WHERE a.owner_service = 'kiosk_ops' AND a.owner_type = 'kiosk_application' AND a.owner_id = p_app_id AND a.phase = 'EVIDENCE'
  ORDER BY a.created_at;
$$;

CREATE OR REPLACE FUNCTION flow.get_kiosk_attachment(p_attachment_id uuid, p_app_id uuid, p_phase text DEFAULT 'EVIDENCE')
RETURNS TABLE (file_url text, mime_type text, size_bytes bigint, sha256 text) LANGUAGE sql AS $$
  SELECT a.file_url, a.mime_type, a.size_bytes, a.sha256
  FROM notify.attachments a
  WHERE a.id = p_attachment_id AND a.owner_service = 'kiosk_ops' AND a.owner_type = 'kiosk_application' AND a.owner_id = p_app_id
    AND a.phase = p_phase;
$$;

CREATE OR REPLACE FUNCTION flow.attach_kiosk_refund_proof(
  p_id uuid, p_app_id uuid, p_url text, p_mime text, p_size bigint, p_hash text, p_actor uuid
) RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE
  v_created timestamptz;
BEGIN
  INSERT INTO notify.attachments(id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by)
  VALUES (p_id, 'kiosk_ops', 'kiosk_application', p_app_id, p_url, p_mime, p_size, p_hash, 'PROOF', p_actor)
  RETURNING created_at INTO v_created;

  PERFORM flow.audit(p_actor, 'KIOSK_REFUND_PROOF_ATTACHED', 'kiosk_applications', p_app_id,
                     jsonb_build_object('attachment', p_id));
  RETURN v_created;
END $$;

COMMIT;
