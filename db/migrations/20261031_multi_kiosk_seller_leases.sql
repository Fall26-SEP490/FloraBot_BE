BEGIN;

-- Give every annual lease a physical kiosk as its entitlement boundary.
ALTER TABLE identity.subscriptions
  ADD COLUMN IF NOT EXISTS kiosk_id uuid REFERENCES kiosk_ops.kiosks(id);

-- Existing annual onboarding created one kiosk per Seller. Backfill that kiosk
-- for each existing annual lease without guessing when a Seller has multiple.
WITH single_kiosk_sellers AS (
  SELECT seller_id, (array_agg(id))[1] AS kiosk_id
  FROM kiosk_ops.kiosks
  WHERE seller_id IS NOT NULL
  GROUP BY seller_id
  HAVING count(*) = 1
)
UPDATE identity.subscriptions sub
SET kiosk_id = owners.kiosk_id,
    updated_at = public.app_now()
FROM single_kiosk_sellers owners,
     identity.subscription_packages pkg
WHERE sub.seller_id = owners.seller_id
  AND sub.package_id = pkg.id
  AND pkg.billing_cycle = 'ANNUAL'
  AND sub.status IN ('ACTIVE', 'EXPIRED')
  AND sub.kiosk_id IS NULL;

CREATE INDEX IF NOT EXISTS ix_subscriptions_kiosk_period
  ON identity.subscriptions(kiosk_id, status, period_to_ts)
  WHERE kiosk_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS uq_kiosk_applications_seller_inflight
  ON kiosk_ops.kiosk_applications(seller_id)
  WHERE seller_id IS NOT NULL
    AND status IN ('AWAITING_PAYMENT', 'PAID_REVIEW', 'REFUND_PENDING', 'CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER');

-- New lease requests reuse the existing application, payment, Operations,
-- technician, evidence, and handover lifecycle. Shared Seller details are
-- snapshotted from the existing Seller row; only location and package differ.
CREATE OR REPLACE FUNCTION flow.create_seller_kiosk_application(
  p_user uuid,
  p_seller uuid,
  p_package_id uuid,
  p_installation_address text,
  p_lat double precision,
  p_lon double precision
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_app_id uuid := public.uuid_v7();
  v_payment_id uuid := public.uuid_v7();
  u identity.users;
  s identity.sellers;
  pkg identity.subscription_packages;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('seller-kiosk-application:' || p_seller::text, 0));

  SELECT * INTO u FROM identity.users WHERE id = p_user FOR UPDATE;
  IF NOT FOUND OR u.status <> 'ACTIVE' OR u.role <> 'SELLER' OR u.seller_id IS DISTINCT FROM p_seller THEN
    RAISE EXCEPTION 'Tài khoản không còn thuộc Seller đang hoạt động' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO s FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND OR s.status <> 'ACTIVE' OR s.bank_account_no_enc IS NULL
     OR nullif(btrim(s.bank_name), '') IS NULL OR nullif(btrim(s.bank_holder), '') IS NULL
     OR nullif(btrim(s.phone), '') IS NULL OR nullif(btrim(s.contact_email), '') IS NULL THEN
    RAISE EXCEPTION 'Hồ sơ Seller chưa hoạt động hoặc chưa có thông tin ngân hàng' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO pkg FROM identity.subscription_packages WHERE id = p_package_id;
  IF NOT FOUND OR pkg.status <> 'ACTIVE' OR pkg.billing_cycle <> 'ANNUAL' THEN
    RAISE EXCEPTION 'Gói thuê bao hàng năm không hợp lệ' USING ERRCODE = '22023';
  END IF;

  IF p_installation_address IS NULL OR length(btrim(p_installation_address)) = 0
     OR length(btrim(p_installation_address)) > 200
     OR p_lat IS NULL OR p_lat < -90 OR p_lat > 90
     OR p_lon IS NULL OR p_lon < -180 OR p_lon > 180 THEN
    RAISE EXCEPTION 'Địa chỉ hoặc tọa độ lắp đặt không hợp lệ' USING ERRCODE = '22023';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE (seller_id = p_seller OR customer_id = p_user)
      AND status IN ('AWAITING_PAYMENT', 'PAID_REVIEW', 'REFUND_PENDING', 'CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER')
  ) THEN
    RAISE EXCEPTION 'Seller đang có một hồ sơ thuê kiosk được xử lý' USING ERRCODE = 'P0001';
  END IF;

  INSERT INTO payment.payments (
    id, kind, purpose, amount, gateway, idempotency_key, status, created_at, updated_at
  ) VALUES (
    v_payment_id, 'CHARGE', 'KIOSK_APPLICATION', pkg.annual_fee, 'PAYOS',
    'kiosk-app-' || v_app_id::text, 'PENDING', v_now, v_now
  );

  INSERT INTO kiosk_ops.kiosk_applications (
    id, customer_id, seller_id, package_id, shop_name, phone, contact_email,
    installation_address, latitude, longitude, bank_name, bank_holder,
    bank_account_no_enc, status, payment_id, package_name, annual_fee,
    walk_in_slots, preorder_slots, total_slots, created_at, updated_at
  ) VALUES (
    v_app_id, p_user, p_seller, pkg.id, s.shop_name, s.phone,
    coalesce(s.contact_email, ''), btrim(p_installation_address), p_lat, p_lon,
    coalesce(s.bank_name, ''), coalesce(s.bank_holder, ''), s.bank_account_no_enc,
    'AWAITING_PAYMENT', v_payment_id, pkg.name, pkg.annual_fee,
    coalesce(pkg.walk_in_slots, 0), coalesce(pkg.preorder_slots, 0), pkg.max_slots,
    v_now, v_now
  );

  PERFORM flow.audit(p_user, 'ADDITIONAL_KIOSK_APPLICATION_CREATED', 'kiosk_applications', v_app_id,
    jsonb_build_object('seller', p_seller, 'package', pkg.name, 'amount', pkg.annual_fee));
  RETURN v_app_id;
END $$;

-- A Seller is active while at least one of its kiosk leases is active. A
-- kiosk-specific check below still blocks an expired kiosk independently.
CREATE OR REPLACE FUNCTION flow.package_valid(p_seller uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT CASE
    WHEN EXISTS (
      SELECT 1 FROM identity.subscriptions WHERE seller_id = p_seller AND kiosk_id IS NOT NULL
    ) THEN EXISTS (
      SELECT 1 FROM identity.subscriptions sub
      WHERE sub.seller_id = p_seller AND sub.kiosk_id IS NOT NULL AND sub.status = 'ACTIVE'
        AND (
          (sub.period_to_ts IS NOT NULL AND sub.period_to_ts > public.app_now()
            AND coalesce(sub.period_from_ts, sub.period_from::timestamp AT TIME ZONE 'UTC') <= public.app_now())
          OR (sub.period_to_ts IS NULL AND sub.period_to > public.app_now()::date
            AND sub.period_from <= public.app_now()::date)
        )
    )
    ELSE EXISTS (
      SELECT 1 FROM identity.sellers
      WHERE id = p_seller
        AND ((package_expires_at_ts IS NOT NULL AND package_expires_at_ts > public.app_now())
          OR (package_expires_at_ts IS NULL AND package_expires_at IS NOT NULL
            AND package_expires_at > public.app_now()::date))
    )
  END;
$$;

-- Legacy admin assignment also checks capacity against the package attached to
-- this kiosk, not a Seller-wide total that would prevent a second lease.
CREATE OR REPLACE FUNCTION flow.assign_kiosk(
  p_kiosk uuid, p_seller uuid, p_admin uuid, p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  k kiosk_ops.kiosks;
  v_seller identity.sellers;
  v_package identity.subscription_packages;
  v_slot_count integer;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) IS DISTINCT FROM 'ADMIN' THEN
    RAISE EXCEPTION 'Chỉ admin được gán kiosk';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-owner:' || p_kiosk::text, 0));
  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = p_kiosk FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  IF k.status IN ('DISABLED', 'MAINTENANCE') THEN
    RAISE EXCEPTION 'Kiosk % đang bị vô hiệu hóa hoặc bảo trì', k.code;
  END IF;

  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status <> 'ACTIVE' OR NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Seller % chưa có gói hiệu lực (status %), không gán kiosk được', v_seller.shop_name, v_seller.status;
  END IF;

  SELECT pkg.* INTO v_package
  FROM identity.subscriptions sub
  JOIN identity.subscription_packages pkg ON pkg.id = sub.package_id
  WHERE sub.seller_id = p_seller AND sub.kiosk_id = p_kiosk AND sub.status = 'ACTIVE'
    AND ((sub.period_to_ts IS NOT NULL AND sub.period_to_ts > v_now)
      OR (sub.period_to_ts IS NULL AND sub.period_to > v_now::date))
  ORDER BY coalesce(sub.period_to_ts, sub.period_to::timestamp AT TIME ZONE 'UTC') DESC
  LIMIT 1;
  IF NOT FOUND THEN
    IF EXISTS (SELECT 1 FROM identity.subscriptions WHERE seller_id = p_seller AND kiosk_id IS NOT NULL) THEN
      RAISE EXCEPTION 'Gói thuê của kiosk này không còn hiệu lực';
    END IF;
    SELECT * INTO v_package FROM identity.subscription_packages WHERE id = v_seller.package_id;
  END IF;
  IF NOT FOUND OR v_package.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Gói của kiosk không còn hoạt động'; END IF;

  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk ORDER BY id FOR UPDATE;
  SELECT count(*) INTO v_slot_count FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk;
  IF v_slot_count = 0 THEN RAISE EXCEPTION 'Kiosk % chưa có ô nào', k.code; END IF;
  IF v_slot_count > v_package.max_slots THEN
    RAISE EXCEPTION 'Gói % chỉ cho phép tối đa % ô trên kiosk này (hiện có %)',
      v_package.name, v_package.max_slots, v_slot_count;
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id = p_kiosk
      AND (sl.status NOT IN ('FREE', 'RENTED_EMPTY') OR sl.bouquet_id IS NOT NULL
        OR sl.hold_until IS NOT NULL OR sl.hold_order_id IS NOT NULL)
  ) THEN
    RAISE EXCEPTION 'Chỉ chuyển kiosk khi mọi ô đều trống, không giữ đơn và không khóa/bảo trì';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_kiosk_id = p_kiosk AND status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER')
  ) THEN
    RAISE EXCEPTION 'Kiosk đang được giữ cho quy trình bàn giao seller';
  END IF;

  IF k.seller_id = p_seller AND NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id = p_kiosk
      AND (sl.current_seller_id IS DISTINCT FROM p_seller OR NOT EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        WHERE a.slot_id = sl.id AND a.seller_id = p_seller AND a.status = 'ACTIVE'
      ))
  ) THEN RETURN p_kiosk; END IF;

  UPDATE kiosk_ops.slot_assignments a
  SET status = 'RELEASED', released_at = v_now, release_reason = 'ADMIN'
  FROM kiosk_ops.slots sl
  WHERE sl.id = a.slot_id AND sl.kiosk_id = p_kiosk AND a.status = 'ACTIVE';
  UPDATE kiosk_ops.kiosks SET seller_id = p_seller, updated_at = v_now WHERE id = p_kiosk;
  UPDATE kiosk_ops.slots
  SET current_seller_id = p_seller, status = 'RENTED_EMPTY', hold_until = NULL,
      hold_order_id = NULL, row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = p_kiosk;
  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at, status)
  SELECT sl.id, p_seller, p_admin, v_now, 'ACTIVE'
  FROM kiosk_ops.slots sl WHERE sl.kiosk_id = p_kiosk ORDER BY sl.slot_code;

  PERFORM flow.audit(p_admin, 'KIOSK_ASSIGNED', 'kiosks', p_kiosk,
    jsonb_build_object('seller', p_seller, 'previous_seller', k.seller_id, 'slot_count', v_slot_count));
  RETURN p_kiosk;
END $$;

CREATE OR REPLACE FUNCTION flow.kiosk_package_valid(p_kiosk uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT CASE
    WHEN k.seller_id IS NULL THEN false
    WHEN EXISTS (
      SELECT 1 FROM identity.subscriptions any_lease
      WHERE any_lease.seller_id = k.seller_id AND any_lease.kiosk_id IS NOT NULL
    ) THEN EXISTS (
      SELECT 1 FROM identity.subscriptions sub
      WHERE sub.seller_id = k.seller_id AND sub.kiosk_id = k.id AND sub.status = 'ACTIVE'
        AND (
          (sub.period_to_ts IS NOT NULL AND sub.period_to_ts > public.app_now()
            AND coalesce(sub.period_from_ts, sub.period_from::timestamp AT TIME ZONE 'UTC') <= public.app_now())
          OR (sub.period_to_ts IS NULL AND sub.period_to > public.app_now()::date
            AND sub.period_from <= public.app_now()::date)
        )
    )
    ELSE flow.package_valid(k.seller_id)
  END
  FROM kiosk_ops.kiosks k WHERE k.id = p_kiosk;
$$;

-- Accessory inventory is kiosk-owned too. Do not allow a kiosk with an expired
-- lease to sell accessories while another kiosk under the same Seller remains active.
CREATE OR REPLACE FUNCTION flow.take_accessories(p_kiosk uuid, p_items jsonb) RETURNS jsonb LANGUAGE plpgsql AS $$
DECLARE a jsonb; v record; v_out jsonb := '[]'; v_qty int;
BEGIN
  IF NOT flow.kiosk_package_valid(p_kiosk) THEN
    RAISE EXCEPTION 'Gói thuê của kiosk này không còn hiệu lực';
  END IF;
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

-- Release only one expired kiosk after the established grace period.
CREATE OR REPLACE FUNCTION flow.release_kiosk_slots(p_kiosk uuid, p_seller uuid, p_reason text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  a kiosk_ops.slot_assignments;
  s kiosk_ops.slots;
  v_count integer := 0;
BEGIN
  FOR a IN
    SELECT a.* FROM kiosk_ops.slot_assignments a
    JOIN kiosk_ops.slots sl ON sl.id = a.slot_id
    WHERE sl.kiosk_id = p_kiosk AND a.seller_id = p_seller AND a.status = 'ACTIVE'
    ORDER BY a.slot_id FOR UPDATE OF a
  LOOP
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = a.slot_id FOR UPDATE;
    CONTINUE WHEN s.status = 'HELD';

    UPDATE kiosk_ops.slot_assignments
    SET status = 'RELEASED', released_at = public.app_now(), release_reason = p_reason
    WHERE id = a.id;

    IF s.status = 'RENTED_EMPTY' THEN
      UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL,
        row_version = row_version + 1, updated_at = public.app_now() WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_RELEASED', 'slots', s.id,
        jsonb_build_object('seller', p_seller, 'kiosk', p_kiosk, 'reason', p_reason));
    ELSIF s.status = 'STOCKED' THEN
      UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE',
        row_version = row_version + 1, updated_at = public.app_now() WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_PENDING_RELEASE', 'slots', s.id,
        jsonb_build_object('seller', p_seller, 'kiosk', p_kiosk, 'reason', p_reason));
    END IF;
    v_count := v_count + 1;
  END LOOP;
  RETURN v_count;
END $$;

CREATE OR REPLACE FUNCTION flow.refresh_seller_package(p_seller uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_today date := public.app_now()::date;
  v_seller identity.sellers;
  v_exp date;
  v_next date;
  v_exp_ts timestamptz;
  v_new text;
  v_has_kiosk_leases boolean;
  v_is_valid boolean;
BEGIN
  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RETURN NULL; END IF;
  IF v_seller.status NOT IN ('APPROVED', 'ACTIVE', 'PAST_DUE') THEN RETURN v_seller.status; END IF;

  SELECT EXISTS (
    SELECT 1 FROM identity.subscriptions WHERE seller_id = p_seller AND kiosk_id IS NOT NULL
  ) INTO v_has_kiosk_leases;

  IF v_has_kiosk_leases THEN
    SELECT EXISTS (
      SELECT 1 FROM identity.subscriptions sub
      WHERE sub.seller_id = p_seller AND sub.kiosk_id IS NOT NULL AND sub.status = 'ACTIVE'
        AND ((sub.period_to_ts IS NOT NULL AND sub.period_to_ts > v_now
              AND coalesce(sub.period_from_ts, sub.period_from::timestamp AT TIME ZONE 'UTC') <= v_now)
          OR (sub.period_to_ts IS NULL AND sub.period_to > v_today AND sub.period_from <= v_today))
    ) INTO v_is_valid;

    SELECT sub.period_to, sub.period_to_ts INTO v_exp, v_exp_ts
    FROM identity.subscriptions sub
    WHERE sub.seller_id = p_seller AND sub.kiosk_id IS NOT NULL
    ORDER BY coalesce(sub.period_to_ts, sub.period_to::timestamp AT TIME ZONE 'UTC') DESC
    LIMIT 1;

    v_new := CASE WHEN v_is_valid THEN 'ACTIVE'
                  WHEN v_seller.status = 'APPROVED' THEN 'APPROVED'
                  ELSE 'PAST_DUE' END;
    UPDATE identity.sellers
    SET status = v_new, package_expires_at = v_exp, package_expires_at_ts = v_exp_ts,
        updated_at = v_now
    WHERE id = p_seller;
  ELSE
    IF v_seller.package_expires_at_ts IS NOT NULL THEN
      v_exp := v_seller.package_expires_at;
      v_exp_ts := v_seller.package_expires_at_ts;
      v_new := CASE WHEN v_exp_ts > v_now THEN 'ACTIVE'
                    WHEN v_seller.status = 'APPROVED' THEN 'APPROVED'
                    ELSE 'PAST_DUE' END;
    ELSE
      SELECT max(period_to) INTO v_exp FROM identity.subscriptions
      WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from <= v_today AND v_today < period_to;
      IF v_exp IS NOT NULL THEN
        LOOP
          SELECT max(period_to) INTO v_next FROM identity.subscriptions
          WHERE seller_id = p_seller AND status = 'ACTIVE' AND period_from = v_exp;
          EXIT WHEN v_next IS NULL;
          v_exp := v_next;
        END LOOP;
        v_new := 'ACTIVE';
      ELSE
        v_exp := v_seller.package_expires_at;
        v_new := CASE WHEN v_seller.status = 'APPROVED' THEN 'APPROVED' ELSE 'PAST_DUE' END;
      END IF;
    END IF;
    UPDATE identity.sellers SET status = v_new, package_expires_at = v_exp,
      package_expires_at_ts = coalesce(v_exp_ts, package_expires_at_ts), updated_at = v_now
    WHERE id = p_seller;
  END IF;

  IF v_new <> v_seller.status THEN
    PERFORM flow.audit(NULL,
      CASE v_new WHEN 'ACTIVE' THEN CASE v_seller.status WHEN 'PAST_DUE' THEN 'SELLER_REACTIVATED' ELSE 'SELLER_ACTIVATED' END ELSE 'SELLER_PAST_DUE' END,
      'sellers', p_seller,
      jsonb_build_object('expires', v_exp, 'expires_ts', v_exp_ts, 'from', v_seller.status));
  END IF;
  RETURN v_new;
END $$;

CREATE OR REPLACE FUNCTION flow.roll_subscriptions(
  p_today date DEFAULT NULL,
  p_seller uuid DEFAULT NULL,
  p_now timestamptz DEFAULT NULL
) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  v_today date;
  v_now timestamptz;
  v_grace integer := flow.cfg('past_due_grace_days')::integer;
  r record;
  v_count integer := 0;
BEGIN
  IF p_now IS NOT NULL THEN PERFORM flow.tick(p_now);
  ELSIF p_today IS NOT NULL THEN PERFORM flow.tick(p_today::timestamptz + interval '1 hour'); END IF;
  v_today := public.app_now()::date;
  v_now := public.app_now();

  FOR r IN
    SELECT * FROM identity.subscriptions
    WHERE status = 'ACTIVE' AND (p_seller IS NULL OR seller_id = p_seller)
      AND ((period_to_ts IS NOT NULL AND period_to_ts <= v_now)
        OR (period_to_ts IS NULL AND period_to <= v_today))
    ORDER BY id FOR UPDATE
  LOOP
    UPDATE identity.subscriptions SET status = 'EXPIRED', updated_at = v_now WHERE id = r.id;
    v_count := v_count + 1;
  END LOOP;

  FOR r IN
    SELECT id, status, package_expires_at, package_expires_at_ts
    FROM identity.sellers
    WHERE status IN ('APPROVED', 'ACTIVE', 'PAST_DUE') AND (p_seller IS NULL OR id = p_seller)
    ORDER BY id
  LOOP
    IF flow.refresh_seller_package(r.id) <> r.status THEN v_count := v_count + 1; END IF;

    IF NOT EXISTS (
      SELECT 1 FROM identity.subscriptions WHERE seller_id = r.id AND kiosk_id IS NOT NULL
    )
      AND (SELECT status FROM identity.sellers WHERE id = r.id) = 'PAST_DUE'
      AND ((r.package_expires_at_ts IS NOT NULL AND r.package_expires_at_ts + make_interval(days => v_grace) <= v_now)
        OR (r.package_expires_at_ts IS NULL AND r.package_expires_at + v_grace <= v_today))
      AND EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments WHERE seller_id = r.id AND status = 'ACTIVE') THEN
      PERFORM flow.audit(NULL, 'SELLER_SLOTS_RELEASED', 'sellers', r.id,
        jsonb_build_object('expired', r.package_expires_at, 'expired_ts', r.package_expires_at_ts, 'grace_days', v_grace));
      v_count := v_count + flow.release_seller_slots(r.id, 'PAST_DUE_GRACE');
    END IF;
  END LOOP;

  FOR r IN
    SELECT sub.seller_id, sub.kiosk_id,
      max(coalesce(sub.period_to_ts, sub.period_to::timestamp AT TIME ZONE 'UTC')) AS expires_at
    FROM identity.subscriptions sub
    WHERE sub.status = 'EXPIRED' AND sub.kiosk_id IS NOT NULL
      AND (p_seller IS NULL OR sub.seller_id = p_seller)
    GROUP BY sub.seller_id, sub.kiosk_id
    HAVING max(coalesce(sub.period_to_ts, sub.period_to::timestamp AT TIME ZONE 'UTC')) + make_interval(days => v_grace) <= v_now
      AND NOT EXISTS (
        SELECT 1 FROM identity.subscriptions current_lease
        WHERE current_lease.seller_id = sub.seller_id AND current_lease.kiosk_id = sub.kiosk_id
          AND current_lease.status = 'ACTIVE'
          AND ((current_lease.period_to_ts IS NOT NULL AND current_lease.period_to_ts > v_now
                AND coalesce(current_lease.period_from_ts, current_lease.period_from::timestamp AT TIME ZONE 'UTC') <= v_now)
            OR (current_lease.period_to_ts IS NULL AND current_lease.period_to > v_today
                AND current_lease.period_from <= v_today))
      )
      AND EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        JOIN kiosk_ops.slots sl ON sl.id = a.slot_id
        WHERE sl.kiosk_id = sub.kiosk_id AND a.seller_id = sub.seller_id AND a.status = 'ACTIVE'
      )
    ORDER BY sub.kiosk_id
  LOOP
    PERFORM flow.audit(NULL, 'KIOSK_LEASE_SLOTS_RELEASED', 'kiosks', r.kiosk_id,
      jsonb_build_object('seller', r.seller_id, 'expired_at', r.expires_at, 'grace_days', v_grace));
    v_count := v_count + flow.release_kiosk_slots(r.kiosk_id, r.seller_id, 'PAST_DUE_GRACE');
  END LOOP;

  RETURN v_count;
END $$;

-- Seller cancellation remains owner-only; existing CUSTOMER onboarding is
-- unchanged, while an additional kiosk request can be cancelled by its Seller.
CREATE OR REPLACE FUNCTION flow.kiosk_application_cancel(
  p_app_id uuid, p_actor uuid, p_reason text, p_now timestamptz DEFAULT NULL
) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  u identity.users;
  p payment.payments;
  j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_app_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_app_id; END IF;
  SELECT * INTO u FROM identity.users WHERE id = p_actor;

  IF NOT FOUND OR app.customer_id IS DISTINCT FROM p_actor OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Từ chối quyền truy cập' USING ERRCODE = '42501';
  END IF;

  IF app.seller_id IS NULL AND u.role <> 'CUSTOMER' THEN
    RAISE EXCEPTION 'Hồ sơ này thuộc tài khoản CUSTOMER' USING ERRCODE = '42501';
  ELSIF app.seller_id IS NOT NULL AND (u.role <> 'SELLER' OR u.seller_id IS DISTINCT FROM app.seller_id) THEN
    RAISE EXCEPTION 'Hồ sơ này thuộc Seller khác' USING ERRCODE = '42501';
  END IF;

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
        (j, 'PLATFORM_SUBSCRIPTION', NULL, p.amount, 'REFUND', p.id, 'Hoàn tiền khách hủy đơn trước xác nhận'),
        (j, 'REFUND_CLEARING', NULL, -p.amount, 'REFUND', p.id, 'Phải hoàn lại khách (CUSTOMER_VOLUNTARY_CANCEL)');
    END IF;
    PERFORM flow.audit(p_actor, 'KIOSK_APPLICATION_REFUND_REQUESTED', 'kiosk_applications', app.id, jsonb_build_object('reason', p_reason));
    RETURN 'REFUND_PENDING';
  ELSE
    RAISE EXCEPTION 'Đơn đăng ký đã được xác nhận, không thể tự hủy' USING ERRCODE = 'P0001';
  END IF;
END $$;

-- Keep the original initial-customer handover, but attach a second-kiosk
-- application to the existing Seller instead of creating/promoting a Seller.
CREATE OR REPLACE FUNCTION flow.kiosk_application_handover(
  p_application_id uuid, p_manager_id uuid, p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  app kiosk_ops.kiosk_applications;
  k kiosk_ops.kiosks;
  u identity.users;
  s identity.sellers;
  p payment.payments;
  v_total_slots integer;
  v_seller_id uuid;
  v_sub_id uuid;
  v_expiry_ts timestamptz;
  v_new_seller boolean;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-handover:' || p_application_id::text, 0));

  SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id = p_application_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn đăng ký % không tồn tại', p_application_id; END IF;
  IF app.status <> 'AWAITING_HANDOVER' THEN
    RAISE EXCEPTION 'Đơn đăng ký chưa sẵn sàng bàn giao (trạng thái: %)', app.status USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO p FROM payment.payments WHERE id = app.payment_id;
  IF p.id IS NULL OR p.status <> 'SUCCEEDED' OR p.amount <> app.annual_fee THEN
    RAISE EXCEPTION 'Khoản thanh toán chưa hợp lệ hoặc chưa thành công' USING ERRCODE = 'P0001';
  END IF;

  IF NOT EXISTS (
    SELECT 1 FROM notify.attachments
    WHERE owner_service = 'kiosk_ops' AND owner_type = 'kiosk_application' AND owner_id = app.id AND phase = 'EVIDENCE'
  ) THEN
    RAISE EXCEPTION 'Chưa có ảnh bằng chứng nghiệm thu lắp đặt từ kỹ thuật viên' USING ERRCODE = 'P0001';
  END IF;

  IF app.assigned_technician_id IS NULL OR NOT EXISTS (
    SELECT 1 FROM identity.users WHERE id = app.assigned_technician_id AND role = 'TECHNICIAN' AND status = 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'Kỹ thuật viên được phân công không hợp lệ hoặc đã ngừng hoạt động' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO u FROM identity.users WHERE id = app.customer_id FOR UPDATE;
  IF NOT FOUND OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Tài khoản gửi hồ sơ không hoạt động' USING ERRCODE = 'P0001';
  END IF;
  v_new_seller := app.seller_id IS NULL;
  IF v_new_seller THEN
    IF u.role <> 'CUSTOMER' THEN
      RAISE EXCEPTION 'Hồ sơ thuê kiosk đầu tiên yêu cầu tài khoản CUSTOMER' USING ERRCODE = 'P0001';
    END IF;
  ELSE
    IF u.role <> 'SELLER' OR u.seller_id IS DISTINCT FROM app.seller_id THEN
      RAISE EXCEPTION 'Tài khoản không còn thuộc Seller của hồ sơ thuê kiosk' USING ERRCODE = 'P0001';
    END IF;
    SELECT * INTO s FROM identity.sellers WHERE id = app.seller_id FOR UPDATE;
    IF NOT FOUND OR s.status IN ('SUSPENDED', 'CLOSED') THEN
      RAISE EXCEPTION 'Hồ sơ Seller đã bị khóa hoặc đóng' USING ERRCODE = 'P0001';
    END IF;
    v_seller_id := app.seller_id;
  END IF;

  IF app.assigned_kiosk_id IS NULL THEN
    RAISE EXCEPTION 'Chưa gán kiosk cho đơn đăng ký này' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = app.assigned_kiosk_id FOR UPDATE;
  IF NOT FOUND OR k.status IN ('DISABLED', 'MAINTENANCE') OR k.seller_id IS NOT NULL THEN
    RAISE EXCEPTION 'Kiosk không tồn tại, đã bị vô hiệu hóa hoặc đang bảo trì' USING ERRCODE = 'P0001';
  END IF;

  SELECT count(*)
  INTO v_total_slots
  FROM kiosk_ops.slots WHERE kiosk_id = k.id;

  IF v_total_slots <> app.total_slots THEN
    RAISE EXCEPTION 'Số ô kiosk (%) không khớp gói đăng ký (%)', v_total_slots, app.total_slots
      USING ERRCODE = 'P0001';
  END IF;

  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = k.id FOR UPDATE;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id = k.id
      AND (sl.status <> 'FREE' OR sl.current_seller_id IS NOT NULL OR sl.bouquet_id IS NOT NULL
        OR sl.hold_until IS NOT NULL OR sl.hold_order_id IS NOT NULL
        OR EXISTS (SELECT 1 FROM kiosk_ops.slot_assignments a WHERE a.slot_id = sl.id AND a.status = 'ACTIVE'))
  ) THEN
    RAISE EXCEPTION 'Kiosk % có ô chưa sẵn sàng bàn giao; mọi ô phải trống và chưa được thuê', k.id USING ERRCODE = 'P0001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications
    WHERE assigned_kiosk_id = k.id AND id <> app.id AND status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER', 'ACTIVE')
  ) THEN
    RAISE EXCEPTION 'Kiosk % đã được giữ hoặc bàn giao cho hồ sơ khác', k.id USING ERRCODE = 'P0001';
  END IF;

  v_expiry_ts := v_now + interval '1 year';
  IF v_new_seller THEN
    INSERT INTO identity.sellers (
      shop_name, phone, address, status, package_id, package_expires_at, package_expires_at_ts,
      contact_email, bank_name, bank_holder, bank_account_no_enc
    ) VALUES (
      app.shop_name, app.phone, app.installation_address, 'ACTIVE', app.package_id,
      v_expiry_ts::date, v_expiry_ts, app.contact_email,
      app.bank_name, app.bank_holder, app.bank_account_no_enc
    ) RETURNING id INTO v_seller_id;
  END IF;

  INSERT INTO identity.subscriptions (
    seller_id, kiosk_id, package_id, period_from, period_to, period_from_ts, period_to_ts,
    price, status, payment_id
  ) VALUES (
    v_seller_id, k.id, app.package_id, v_now::date, v_expiry_ts::date, v_now, v_expiry_ts,
    app.annual_fee, 'ACTIVE', app.payment_id
  ) RETURNING id INTO v_sub_id;

  UPDATE kiosk_ops.kiosks
  SET seller_id = v_seller_id, address = app.installation_address,
      latitude = app.latitude, longitude = app.longitude, updated_at = v_now
  WHERE id = k.id;

  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at, status)
  SELECT sl.id, v_seller_id, p_manager_id, v_now, 'ACTIVE'
  FROM kiosk_ops.slots sl WHERE sl.kiosk_id = k.id ORDER BY sl.slot_code;

  UPDATE kiosk_ops.slots
  SET current_seller_id = v_seller_id, status = 'RENTED_EMPTY',
      row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = k.id;

  IF v_new_seller THEN
    UPDATE identity.users SET role = 'SELLER', seller_id = v_seller_id, updated_at = v_now WHERE id = u.id;
  END IF;

  UPDATE kiosk_ops.kiosk_applications
  SET status = 'ACTIVE', seller_id = v_seller_id, handover_at = v_now,
      handover_by = p_manager_id, updated_at = v_now
  WHERE id = app.id;

  PERFORM flow.refresh_seller_package(v_seller_id);
  PERFORM flow.audit(p_manager_id, 'KIOSK_APPLICATION_HANDOVER_COMPLETED', 'kiosk_applications', app.id,
    jsonb_build_object('seller', v_seller_id, 'kiosk', k.id, 'subscription', v_sub_id,
      'package', app.package_name, 'expires_at', v_expiry_ts, 'additional_kiosk', NOT v_new_seller));

  RETURN v_seller_id;
END $$;

-- Per-kiosk gates for stocked bouquets, checkout, and product availability.
CREATE OR REPLACE FUNCTION flow.slot_sellable(p_slot uuid, p_seller uuid)
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    JOIN kiosk_ops.kiosks k ON k.id = sl.kiosk_id
    JOIN kiosk_ops.slot_assignments a ON a.slot_id = sl.id
    WHERE sl.id = p_slot AND k.seller_id = p_seller
      AND a.seller_id = p_seller AND a.status = 'ACTIVE'
      AND flow.kiosk_package_valid(k.id)
  )
$$;

-- The database checkout boundary must reject an unclassified slot even if a
-- client calls it directly without using the catalog view.
CREATE OR REPLACE FUNCTION flow.kiosk_checkout(
  p_kiosk uuid,
  p_bouquets uuid[],
  p_customer uuid DEFAULT NULL,
  p_accessories jsonb DEFAULT '[]',
  p_points bigint DEFAULT 0,
  p_ecard text DEFAULT NULL,
  p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  v_checkout uuid := public.uuid_v7();
  r record;
  v_order uuid;
  v_total bigint;
  v_kstatus text;
  v_target uuid;
  v_sub bigint;
  v_sellers uuid[];
  v_accs jsonb;
  v_cap bigint;
  v_is_web boolean;
BEGIN
  v_is_web := nullif(current_setting('flow.is_web_request', true), '') IS NOT NULL;
  p_bouquets := coalesce(p_bouquets, '{}');
  p_points := coalesce(p_points, 0);

  SELECT status INTO v_kstatus FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  IF v_kstatus IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk không hoạt động (%)', v_kstatus; END IF;
  IF cardinality(p_bouquets) = 0 AND jsonb_array_length(coalesce(p_accessories, '[]')) = 0 THEN RAISE EXCEPTION 'Giỏ hàng trống'; END IF;
  IF p_ecard IS NOT NULL AND length(p_ecard) > 150 THEN RAISE EXCEPTION 'Lời thiệp tối đa 150 ký tự'; END IF;
  IF p_customer IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM identity.users WHERE id = p_customer AND role IN ('CUSTOMER','SELLER') AND status = 'ACTIVE'
  ) THEN RAISE EXCEPTION 'Tài khoản khách % không hợp lệ hoặc đã bị xóa', p_customer; END IF;

  FOR r IN
    SELECT b.id AS bouquet_id, b.status AS bstatus, b.sellable_until, s.id AS slot_id,
      s.kiosk_id, s.status AS sstatus, s.current_seller_id, p.name, sl.status AS seller_status,
      s.zone, s.slot_code
    FROM kiosk_ops.bouquets b
    JOIN kiosk_ops.slots s ON s.bouquet_id = b.id
    JOIN catalog.flower_products p ON p.id = b.product_id
    JOIN identity.sellers sl ON sl.id = b.seller_id
    WHERE b.id = ANY(p_bouquets)
    ORDER BY s.id FOR UPDATE OF s, b
  LOOP
    IF r.kiosk_id <> p_kiosk OR r.sstatus <> 'STOCKED' OR r.bstatus <> 'STOCKED' THEN
      RAISE EXCEPTION 'Bó % không còn sẵn để bán', r.name;
    END IF;
    IF r.sellable_until <= v_now THEN RAISE EXCEPTION 'Bó % đã quá hạn bán', r.name; END IF;
    IF r.seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller của bó % đang % (tạm ngưng bán)', r.name, r.seller_status; END IF;
    IF NOT flow.slot_sellable(r.slot_id, r.current_seller_id) THEN
      RAISE EXCEPTION 'Gói của seller bán bó % đã hết hạn, không bán', r.name;
    END IF;
    IF v_is_web AND r.zone IS DISTINCT FROM 'PREORDER' THEN
      RAISE EXCEPTION 'Đơn đặt trước chỉ được dùng bó trong ô PREORDER';
    ELSIF NOT v_is_web AND r.zone IS DISTINCT FROM 'WALK_IN' THEN
      RAISE EXCEPTION 'Ô % không thuộc vùng WALK_IN', r.slot_code;
    END IF;
  END LOOP;

  IF (SELECT count(*) FROM kiosk_ops.slots WHERE bouquet_id = ANY(p_bouquets) AND status = 'STOCKED') <> cardinality(p_bouquets) THEN
    RAISE EXCEPTION 'Có bó không còn trong ô';
  END IF;
  v_accs := flow.take_accessories(p_kiosk, p_accessories);

  SELECT array_agg(DISTINCT x ORDER BY x) INTO v_sellers FROM (
    SELECT seller_id AS x FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets)
    UNION SELECT (e->>'seller_id')::uuid FROM jsonb_array_elements(v_accs) e
  ) z;
  FOR r IN SELECT unnest(v_sellers) AS seller_id LOOP
    v_sub := coalesce((SELECT sum(price_snapshot) FROM kiosk_ops.bouquets WHERE id = ANY(p_bouquets) AND seller_id = r.seller_id), 0)
      + coalesce((SELECT sum((e->>'price')::bigint * (e->>'qty')::int)
                  FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id), 0);
    INSERT INTO ordering.orders (order_code, checkout_id, customer_id, seller_id, kiosk_id, subtotal, total_amount, tracking_token, ecard_content)
    VALUES (flow.new_order_code(), v_checkout, p_customer, r.seller_id, p_kiosk, v_sub, v_sub, flow.new_tracking_token(), p_ecard)
    RETURNING id INTO v_order;
    INSERT INTO ordering.order_items (order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'BOUQUET', b.id, s.id, p.name, 1, b.price_snapshot, b.price_snapshot
    FROM kiosk_ops.bouquets b
    JOIN kiosk_ops.slots s ON s.bouquet_id = b.id
    JOIN catalog.flower_products p ON p.id = b.product_id
    WHERE b.id = ANY(p_bouquets) AND b.seller_id = r.seller_id;
    INSERT INTO ordering.order_items (order_id, item_type, accessory_id, name_snapshot, quantity, unit_price, line_total)
    SELECT v_order, 'ACCESSORY', (e->>'id')::uuid, e->>'name', (e->>'qty')::int,
      (e->>'price')::bigint, (e->>'qty')::int * (e->>'price')::bigint
    FROM jsonb_array_elements(v_accs) e WHERE (e->>'seller_id')::uuid = r.seller_id;
    PERFORM flow.audit(p_kiosk, 'ORDER_STATUS_CHANGED', 'orders', v_order,
      jsonb_build_object('from', NULL, 'to', 'AWAITING_PAYMENT'), 'KIOSK');
  END LOOP;

  IF p_points > 0 THEN
    IF p_customer IS NULL THEN RAISE EXCEPTION 'Đổi điểm cần đăng nhập'; END IF;
    SELECT id, subtotal INTO v_target, v_sub FROM ordering.orders
      WHERE checkout_id = v_checkout ORDER BY subtotal DESC, id LIMIT 1;
    v_cap := floor(v_sub * flow.cfg('points_max_redeem_percent') / 100);
    IF p_points > v_cap THEN RAISE EXCEPTION 'Đổi điểm tối đa % đồng (%%% giá trị hàng)', v_cap, flow.cfg('points_max_redeem_percent'); END IF;
    UPDATE ordering.orders SET discount_amount = p_points, points_redeemed = p_points,
      total_amount = subtotal - p_points WHERE id = v_target;
    PERFORM flow.redeem_points(p_customer, p_points, v_target);
  END IF;
  UPDATE ordering.orders SET points_earned = CASE WHEN p_customer IS NULL THEN 0
    ELSE floor(total_amount * flow.cfg('points_rate_percent') / 100) END
  WHERE checkout_id = v_checkout;

  FOR r IN
    SELECT oi.slot_id, oi.bouquet_id, oi.order_id
    FROM ordering.order_items oi JOIN ordering.orders o ON o.id = oi.order_id
    WHERE o.checkout_id = v_checkout AND oi.item_type = 'BOUQUET'
  LOOP
    UPDATE kiosk_ops.slots SET status = 'HELD', hold_until = v_now + flow.cfg_min('hold_minutes'),
      hold_order_id = r.order_id, row_version = row_version + 1 WHERE id = r.slot_id;
    UPDATE kiosk_ops.bouquets SET status = 'HELD' WHERE id = r.bouquet_id;
    INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id, reason)
    VALUES (r.bouquet_id, r.slot_id, 'HOLD', r.order_id, 'Giữ chờ thanh toán');
  END LOOP;

  SELECT sum(total_amount) INTO v_total FROM ordering.orders WHERE checkout_id = v_checkout;
  IF v_total <= 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không tạo được khoản thu'; END IF;
  INSERT INTO payment.payments (kind, purpose, checkout_id, gateway, idempotency_key, amount)
  VALUES ('CHARGE','ORDER_CHECKOUT', v_checkout, 'PAYOS', 'co-' || v_checkout, v_total);
  PERFORM flow.audit(p_kiosk, 'CHECKOUT_STARTED', 'orders', v_checkout,
    jsonb_build_object('total', v_total, 'bouquets', cardinality(p_bouquets)));
  RETURN v_checkout;
END $$;

CREATE OR REPLACE VIEW screen.v_kiosk_catalog AS
SELECT k.id AS kiosk_id, k.code AS kiosk, sl.slot_code, b.id AS bouquet_id,
       fp.id AS product_id, fp.name, fp.tags, b.price_snapshot AS gia, b.sellable_until, s.shop_name
FROM kiosk_ops.slots sl
JOIN kiosk_ops.kiosks k ON k.id = sl.kiosk_id
JOIN kiosk_ops.bouquets b ON b.id = sl.bouquet_id
JOIN catalog.flower_products fp ON fp.id = b.product_id
JOIN identity.sellers s ON s.id = b.seller_id
WHERE sl.status = 'STOCKED' AND b.status = 'STOCKED' AND b.sellable_until > public.app_now()
  AND s.status = 'ACTIVE' AND flow.kiosk_package_valid(k.id)
  AND sl.zone = 'WALK_IN';

CREATE OR REPLACE FUNCTION flow.ai_candidates(p_kiosk uuid, p_budget bigint, p_occasion text)
RETURNS TABLE (bouquet_id uuid, product_id uuid, seller_id uuid, name text, price bigint,
               tags text[], description text, brand_tone text) LANGUAGE sql STABLE AS $$
  SELECT b.id, p.id, p.seller_id, p.name, b.price_snapshot, p.tags, p.description, sl.brand_tone
  FROM kiosk_ops.slots s
  JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id
  JOIN catalog.flower_products p ON p.id = b.product_id
  JOIN identity.sellers sl ON sl.id = p.seller_id
  WHERE s.kiosk_id = p_kiosk AND s.status = 'STOCKED' AND b.status = 'STOCKED'
    AND b.sellable_until > public.app_now() AND sl.status = 'ACTIVE'
    AND flow.kiosk_package_valid(p_kiosk)
    AND s.zone = 'WALK_IN'
    AND (p_budget IS NULL OR b.price_snapshot <= p_budget)
    AND NOT (p_occasion IN ('BIRTHDAY','CONGRATS','ANNIVERSARY','VALENTINE') AND p.tags && ARRAY['WHITE','SYMPATHY'])
    AND NOT (p_occasion = 'SYMPATHY' AND p.tags && ARRAY['ROMANTIC','FUNNY','RED'])
$$;

CREATE OR REPLACE VIEW screen.v_web_catalog AS
SELECT p.id AS product_id, p.seller_id, s.shop_name, p.name, p.description, p.price,
       k.id AS kiosk_id, k.name AS kiosk_name, k.address,
       (SELECT a.file_url FROM notify.attachments a
        WHERE a.owner_service = 'catalog' AND a.owner_type = 'flower_product' AND a.owner_id = p.id
        ORDER BY a.created_at LIMIT 1) AS photo,
       ARRAY(
         SELECT b.id FROM kiosk_ops.bouquets b
         JOIN kiosk_ops.slots sl ON sl.bouquet_id = b.id
         WHERE b.product_id = p.id AND sl.kiosk_id = k.id AND sl.status = 'STOCKED'
           AND b.status = 'STOCKED' AND b.sellable_until > public.app_now() + flow.cfg_min('hold_minutes')
           AND flow.slot_sellable(sl.id, s.id)
           AND sl.zone = 'PREORDER'
         ORDER BY b.sellable_until, b.id
       ) AS bouquets
FROM catalog.flower_products p
JOIN identity.sellers s ON s.id = p.seller_id
JOIN kiosk_ops.kiosks k ON k.status NOT IN ('DISABLED', 'MAINTENANCE')
  AND length(btrim(coalesce(k.address, ''))) > 0 AND k.latitude IS NOT NULL AND k.longitude IS NOT NULL
WHERE p.status = 'ACTIVE' AND s.status = 'ACTIVE' AND flow.package_valid(s.id)
  AND EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id = k.id AND sl.current_seller_id = s.id
      AND flow.slot_sellable(sl.id, s.id) AND sl.zone = 'PREORDER'
  );

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
  IF length(btrim(p_instructions)) < 10 OR p_pickup < public.app_now() + interval '2 hours'
     OR p_pickup > public.app_now() + interval '30 days' THEN
    RAISE EXCEPTION 'Mô tả tối thiểu 10 ký tự, hẹn nhận từ 2 giờ đến 30 ngày';
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

COMMIT;
