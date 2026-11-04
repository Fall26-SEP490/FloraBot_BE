-- Manager-owned annual unit prices and renter-requested kiosk layouts.
-- Existing applications, paid amounts, leases and slot zones are preserved.
BEGIN;

ALTER TABLE kiosk_ops.kiosk_applications
  ADD COLUMN IF NOT EXISTS pricing_mode text NOT NULL DEFAULT 'PACKAGE',
  ADD COLUMN IF NOT EXISTS walk_in_annual_price bigint,
  ADD COLUMN IF NOT EXISTS preorder_annual_price bigint,
  ADD COLUMN IF NOT EXISTS pricing_version integer;
ALTER TABLE kiosk_ops.kiosk_applications DROP CONSTRAINT IF EXISTS chk_requested_kiosk_layout;
ALTER TABLE kiosk_ops.kiosk_applications ADD CONSTRAINT chk_requested_kiosk_layout CHECK (
  pricing_mode IN ('PACKAGE','SLOT_PRICING') AND (pricing_mode <> 'SLOT_PRICING' OR (
    walk_in_slots IS NOT NULL AND preorder_slots IS NOT NULL AND total_slots IS NOT NULL
    AND walk_in_slots BETWEEN 0 AND 50 AND preorder_slots BETWEEN 0 AND 50
    AND total_slots BETWEEN 1 AND 50 AND walk_in_slots + preorder_slots = total_slots
    AND walk_in_annual_price IS NOT NULL AND walk_in_annual_price > 0
    AND preorder_annual_price IS NOT NULL AND preorder_annual_price > 0
    AND pricing_version IS NOT NULL AND pricing_version > 0
    AND annual_fee IS NOT NULL AND annual_fee > 0)));
-- Deliberately do not tie the fee to the CURRENT layout with a CHECK: an agreed
-- post-payment zone change keeps the original fee for the current rental term.

ALTER TABLE kiosk_ops.kiosks ADD COLUMN IF NOT EXISTS requested_application_id uuid
  REFERENCES kiosk_ops.kiosk_applications(id);
CREATE UNIQUE INDEX IF NOT EXISTS uq_requested_kiosk_application
  ON kiosk_ops.kiosks(requested_application_id) WHERE requested_application_id IS NOT NULL;


CREATE TABLE IF NOT EXISTS kiosk_ops.kiosk_slot_pricing (
    singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
    walk_in_annual_price bigint NOT NULL CHECK (walk_in_annual_price > 0),
    preorder_annual_price bigint NOT NULL CHECK (preorder_annual_price > 0),
    version integer NOT NULL CHECK (version > 0),
    updated_at timestamptz NOT NULL DEFAULT public.app_now(),
    updated_by uuid NOT NULL REFERENCES identity.users(id)
);

CREATE OR REPLACE FUNCTION flow.set_kiosk_slot_pricing(
    p_actor uuid,
    p_walk bigint,
    p_pre bigint,
    p_expected integer
) RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    previous kiosk_ops.kiosk_slot_pricing;
    old_prices jsonb;
    next_version integer;
BEGIN
    PERFORM 1 FROM identity.users
    WHERE id = p_actor AND role = 'OPERATIONS_MANAGER' AND status = 'ACTIVE'
    FOR SHARE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Only an active Operations Manager can update kiosk pricing' USING ERRCODE = '42501';
    END IF;
    IF p_walk IS NULL OR p_pre IS NULL OR p_expected IS NULL
       OR p_walk NOT BETWEEN 1 AND 9007199254740991
       OR p_pre NOT BETWEEN 1 AND 9007199254740991
       OR p_expected < 0 THEN
        RAISE EXCEPTION 'Invalid slot price or expected version' USING ERRCODE = '22023';
    END IF;

    -- Serializes the initial insert as well as subsequent versioned updates.
    PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-slot-pricing', 0));
    SELECT * INTO previous FROM kiosk_ops.kiosk_slot_pricing WHERE singleton FOR UPDATE;
    IF FOUND THEN
        IF previous.version <> p_expected THEN
            RAISE EXCEPTION 'Kiosk pricing version changed' USING ERRCODE = '40001';
        END IF;
        old_prices := jsonb_build_object(
            'walk_in_annual_price', previous.walk_in_annual_price,
            'preorder_annual_price', previous.preorder_annual_price,
            'version', previous.version);
        next_version := previous.version + 1;
        UPDATE kiosk_ops.kiosk_slot_pricing
        SET walk_in_annual_price = p_walk,
            preorder_annual_price = p_pre,
            version = next_version,
            updated_at = public.app_now(),
            updated_by = p_actor
        WHERE singleton;
    ELSE
        IF p_expected <> 0 THEN
            RAISE EXCEPTION 'Kiosk pricing version changed' USING ERRCODE = '40001';
        END IF;
        next_version := 1;
        INSERT INTO kiosk_ops.kiosk_slot_pricing
            (singleton, walk_in_annual_price, preorder_annual_price, version, updated_at, updated_by)
        VALUES (true, p_walk, p_pre, next_version, public.app_now(), p_actor);
    END IF;

    PERFORM flow.audit(p_actor, 'KIOSK_SLOT_PRICING_UPDATED', 'kiosk_slot_pricing', NULL,
        jsonb_build_object('old', old_prices, 'new', jsonb_build_object(
            'walk_in_annual_price', p_walk,
            'preorder_annual_price', p_pre,
            'version', next_version)));
END $$;


CREATE OR REPLACE FUNCTION flow.require_operations_manager(p_actor uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='OPERATIONS_MANAGER' AND status='ACTIVE') THEN
  RAISE EXCEPTION 'Only an active Operations Manager can perform this action' USING ERRCODE='42501';
 END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.request_kiosk_application(
 p_actor uuid,p_walk integer,p_pre integer,p_shop text,p_phone text,p_email text,p_address text,
 p_lat double precision,p_lon double precision,p_bank text,p_holder text,p_account text,
 p_pricing_version integer,p_seller uuid DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
 u identity.users; s identity.sellers; prices kiosk_ops.kiosk_slot_pricing;
 result uuid := public.uuid_v7(); v_package uuid; v_payment uuid := public.uuid_v7(); total numeric;
BEGIN
 IF p_walk IS NULL OR p_pre IS NULL OR p_walk NOT BETWEEN 0 AND 50 OR p_pre NOT BETWEEN 0 AND 50
 OR p_walk+p_pre NOT BETWEEN 1 AND 50 OR p_pricing_version IS NULL OR p_pricing_version < 1 THEN
  RAISE EXCEPTION 'Nonnegative slot counts (total 1..50) and pricing version are required' USING ERRCODE='22023';
 END IF;
 PERFORM pg_advisory_xact_lock(hashtextextended('requested-kiosk:' || coalesce(p_seller,p_actor)::text,0));
 SELECT * INTO u FROM identity.users WHERE id=p_actor FOR UPDATE;
 IF NOT FOUND OR u.status<>'ACTIVE' OR (p_seller IS NULL AND u.role<>'CUSTOMER')
 OR (p_seller IS NOT NULL AND (u.role<>'SELLER' OR u.seller_id IS DISTINCT FROM p_seller)) THEN
  RAISE EXCEPTION 'Application owner is not permitted' USING ERRCODE='42501';
 END IF;
 IF p_seller IS NOT NULL THEN
  SELECT * INTO s FROM identity.sellers WHERE id=p_seller FOR UPDATE;
  IF NOT FOUND OR s.status<>'ACTIVE' THEN RAISE EXCEPTION 'Seller is not active' USING ERRCODE='42501'; END IF;
  p_shop:=s.shop_name; p_phone:=s.phone; p_email:=s.contact_email;
  p_bank:=s.bank_name; p_holder:=s.bank_holder; p_account:=s.bank_account_no_enc;
 END IF;
 IF nullif(btrim(p_shop),'') IS NULL OR nullif(btrim(p_phone),'') IS NULL OR nullif(btrim(p_email),'') IS NULL
 OR nullif(btrim(p_address),'') IS NULL OR length(p_address)>200 OR p_lat IS NULL OR p_lon IS NULL
 OR NOT(p_lat BETWEEN -90 AND 90) OR NOT(p_lon BETWEEN -180 AND 180)
 OR nullif(btrim(p_bank),'') IS NULL OR nullif(btrim(p_holder),'') IS NULL OR nullif(p_account,'') IS NULL THEN
  RAISE EXCEPTION 'Contact, location or bank profile is incomplete' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM kiosk_ops.kiosk_applications WHERE (customer_id=p_actor OR (p_seller IS NOT NULL AND seller_id=p_seller))
  AND (status IN ('AWAITING_PAYMENT','PAID_REVIEW','CONFIRMED','SCHEDULED','INSTALLING','AWAITING_HANDOVER')
       OR (p_seller IS NOT NULL AND status='REFUND_PENDING'))) THEN
  RAISE EXCEPTION 'An application is already in progress' USING ERRCODE='P0001';
 END IF;
 -- A shared lock makes the accepted version/amount atomic with concurrent price edits.
 SELECT * INTO prices FROM kiosk_ops.kiosk_slot_pricing WHERE singleton FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Operations Manager has not configured slot prices' USING ERRCODE='55000'; END IF;
 IF prices.version <> p_pricing_version THEN
  RAISE EXCEPTION 'Slot prices changed; reload prices and confirm the new total' USING ERRCODE='P0001';
 END IF;
 total := p_walk::numeric * prices.walk_in_annual_price + p_pre::numeric * prices.preorder_annual_price;
 IF total NOT BETWEEN 1 AND 9007199254740991 THEN
  RAISE EXCEPTION 'Rental amount exceeds the supported payment limit' USING ERRCODE='22023';
 END IF;
 -- Preserve the subscription FK using a private immutable contract package.
 -- INACTIVE prevents reusing an old contract through legacy self-subscription.
 INSERT INTO identity.subscription_packages(name,monthly_fee,annual_fee,max_slots,walk_in_slots,preorder_slots,billing_cycle,status)
 VALUES('Kiosk pricing '||result::text,0,total::bigint,p_walk+p_pre,p_walk,p_pre,'ANNUAL','INACTIVE')
 RETURNING id INTO v_package;
 INSERT INTO payment.payments(id,kind,purpose,amount,gateway,idempotency_key,status)
 VALUES(v_payment,'CHARGE','KIOSK_APPLICATION',total::bigint,'PAYOS','kiosk-app-'||result::text,'PENDING');
 INSERT INTO kiosk_ops.kiosk_applications(id,customer_id,seller_id,package_id,shop_name,phone,contact_email,
 installation_address,latitude,longitude,bank_name,bank_holder,bank_account_no_enc,status,
 package_name,annual_fee,walk_in_slots,preorder_slots,total_slots,pricing_mode,
 walk_in_annual_price,preorder_annual_price,pricing_version,payment_id)
 VALUES(result,p_actor,p_seller,v_package,btrim(p_shop),btrim(p_phone),lower(btrim(p_email)),btrim(p_address),p_lat,p_lon,
 btrim(p_bank),btrim(p_holder),p_account,'AWAITING_PAYMENT','Kiosk theo nhu cau',total::bigint,p_walk,p_pre,p_walk+p_pre,
 'SLOT_PRICING',prices.walk_in_annual_price,prices.preorder_annual_price,prices.version,v_payment);
 PERFORM flow.audit(p_actor,'KIOSK_REQUIREMENTS_SUBMITTED','kiosk_applications',result,
 jsonb_build_object('seller',p_seller,'walk_in_slots',p_walk,'preorder_slots',p_pre,'total_slots',p_walk+p_pre,
 'walk_in_annual_price',prices.walk_in_annual_price,'preorder_annual_price',prices.preorder_annual_price,
 'pricing_version',prices.version,'annual_fee',total,'payment',v_payment));
 RETURN result;
END $$;

CREATE OR REPLACE FUNCTION flow.request_seller_kiosk_application(
 p_actor uuid,p_seller uuid,p_walk integer,p_pre integer,p_address text,
 p_lat double precision,p_lon double precision,p_pricing_version integer
) RETURNS uuid LANGUAGE plpgsql AS $$
BEGIN
 IF p_seller IS NULL THEN RAISE EXCEPTION 'Seller is required' USING ERRCODE='42501'; END IF;
 RETURN flow.request_kiosk_application(p_actor,p_walk,p_pre,NULL,NULL,NULL,p_address,p_lat,p_lon,NULL,NULL,NULL,p_pricing_version,p_seller);
END $$;

CREATE OR REPLACE FUNCTION flow.enroll_requested_kiosk(p_app_id uuid,p_code text,p_name text,p_hw text,p_mqtt text,p_region text,p_hash text,p_actor uuid)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE app kiosk_ops.kiosk_applications; result uuid:=public.uuid_v7(); i integer;
BEGIN
 PERFORM flow.require_operations_manager(p_actor);
 SELECT * INTO app FROM kiosk_ops.kiosk_applications WHERE id=p_app_id FOR UPDATE;
 IF NOT FOUND OR app.status<>'CONFIRMED' THEN RAISE EXCEPTION 'Enroll a kiosk only for a confirmed, paid application' USING ERRCODE='P0001'; END IF;
 IF NOT EXISTS(SELECT 1 FROM payment.payments WHERE id=app.payment_id AND status='SUCCEEDED' AND amount=app.annual_fee) THEN
  RAISE EXCEPTION 'Application payment is not settled' USING ERRCODE='P0001';
 END IF;
 IF app.walk_in_slots IS NULL OR app.preorder_slots IS NULL OR app.walk_in_slots+app.preorder_slots<>app.total_slots THEN
  RAISE EXCEPTION 'Application needs an agreed slot layout before enrollment' USING ERRCODE='22023';
 END IF;
 IF EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE requested_application_id=app.id) THEN
  RAISE EXCEPTION 'A kiosk was already created for this application' USING ERRCODE='P0001';
 END IF;
 IF nullif(btrim(p_code),'') IS NULL OR length(btrim(p_code)) NOT BETWEEN 2 AND 50 OR nullif(btrim(p_name),'') IS NULL
 OR length(btrim(p_name))>120 OR length(btrim(p_hw))<2 OR p_hash IS NULL THEN RAISE EXCEPTION 'Invalid kiosk identity' USING ERRCODE='22023'; END IF;
 INSERT INTO kiosk_ops.kiosks(id,code,name,address,region,hardware_id,mqtt_client_id,status,api_key_hash,requested_application_id)
 VALUES(result,btrim(p_code),btrim(p_name),'',coalesce(p_region,'HCM'),btrim(p_hw),coalesce(nullif(btrim(p_mqtt),''),'kiosk-'||lower(btrim(p_code))),'OFFLINE',p_hash,app.id);
 FOR i IN 1..app.total_slots LOOP
  INSERT INTO kiosk_ops.slots(kiosk_id,slot_code,zone,relay_channel,status)
  VALUES(result,'S'||lpad(i::text,2,'0'),CASE WHEN i<=app.walk_in_slots THEN 'WALK_IN' ELSE 'PREORDER' END,i-1,'FREE');
 END LOOP;
 PERFORM flow.audit(p_actor,'KIOSK_ENROLLED','kiosks',result,
 jsonb_build_object('application',app.id,'walk_in_slots',app.walk_in_slots,'preorder_slots',app.preorder_slots,'total_slots',app.total_slots));
 RETURN result;
END $$;

CREATE OR REPLACE FUNCTION flow.seller_set_slot_zone(p_kiosk_id uuid,p_slot_id uuid,p_actor uuid,p_zone text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Slot types are fixed at rental and cannot be changed' USING ERRCODE='42501';
END $$;

-- Retired entry points cannot reclassify slots, even after an API rollback.
CREATE OR REPLACE FUNCTION flow.manager_set_slot_zone(p_kiosk_id uuid,p_slot_id uuid,p_actor uuid,p_zone text,p_reason text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
 RAISE EXCEPTION 'Slot types are fixed at rental and cannot be changed' USING ERRCODE='42501';
END $$;

CREATE OR REPLACE FUNCTION flow.prevent_slot_zone_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.zone IS DISTINCT FROM OLD.zone THEN
  RAISE EXCEPTION 'Slot types are fixed at creation and cannot be changed' USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_immutable_slot_zone ON kiosk_ops.slots;
CREATE TRIGGER trg_immutable_slot_zone BEFORE UPDATE OF zone ON kiosk_ops.slots
 FOR EACH ROW EXECUTE FUNCTION flow.prevent_slot_zone_change();

CREATE OR REPLACE FUNCTION flow.prevent_requested_layout_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD.pricing_mode='SLOT_PRICING' AND
 (NEW.walk_in_slots IS DISTINCT FROM OLD.walk_in_slots
  OR NEW.preorder_slots IS DISTINCT FROM OLD.preorder_slots
  OR NEW.total_slots IS DISTINCT FROM OLD.total_slots
  OR NEW.pricing_mode IS DISTINCT FROM OLD.pricing_mode) THEN
  RAISE EXCEPTION 'Requested slot counts are fixed when the rental application is submitted' USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_immutable_requested_layout ON kiosk_ops.kiosk_applications;
CREATE TRIGGER trg_immutable_requested_layout
 BEFORE UPDATE OF walk_in_slots,preorder_slots,total_slots,pricing_mode ON kiosk_ops.kiosk_applications
 FOR EACH ROW EXECUTE FUNCTION flow.prevent_requested_layout_change();


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
  PERFORM flow.require_operations_manager(p_actor);
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
  IF k.requested_application_id IS NOT NULL AND k.requested_application_id IS DISTINCT FROM app.id THEN
    RAISE EXCEPTION 'Kiosk is reserved for another application' USING ERRCODE='P0001';
  END IF;
  IF app.pricing_mode='SLOT_PRICING' AND (v_w_count<>app.walk_in_slots OR v_p_count<>app.preorder_slots OR v_w_count+v_p_count<>v_total_slots) THEN
    RAISE EXCEPTION 'Kiosk layout does not match requested Walk-in and Pre-order counts' USING ERRCODE='22023';
  END IF;
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
  PERFORM flow.require_operations_manager(p_manager_id);
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
  IF k.requested_application_id IS NOT NULL AND k.requested_application_id IS DISTINCT FROM app.id THEN
    RAISE EXCEPTION 'Kiosk is reserved for another application' USING ERRCODE='P0001';
  END IF;
  IF app.pricing_mode='SLOT_PRICING' AND (
    (SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=k.id AND zone='WALK_IN')<>app.walk_in_slots OR
    (SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=k.id AND zone='PREORDER')<>app.preorder_slots) THEN
    RAISE EXCEPTION 'Kiosk layout no longer matches the agreed request' USING ERRCODE='22023';
  END IF;

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

COMMIT;
