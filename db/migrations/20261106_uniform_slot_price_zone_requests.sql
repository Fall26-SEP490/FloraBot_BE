-- One annual price per slot; a seller may request a new layout without changing the lease fee.
-- Apply after 20261104_requested_kiosk_layout_pricing.sql. Existing contracts retain their fee snapshots.
BEGIN;

ALTER TABLE kiosk_ops.kiosk_slot_pricing ADD COLUMN IF NOT EXISTS annual_slot_price bigint;
UPDATE kiosk_ops.kiosk_slot_pricing SET annual_slot_price=walk_in_annual_price
WHERE annual_slot_price IS NULL AND walk_in_annual_price=preorder_annual_price;
ALTER TABLE kiosk_ops.kiosk_slot_pricing DROP CONSTRAINT IF EXISTS chk_uniform_slot_price;
ALTER TABLE kiosk_ops.kiosk_slot_pricing ADD CONSTRAINT chk_uniform_slot_price CHECK (
 annual_slot_price IS NULL OR (annual_slot_price BETWEEN 1 AND 9007199254740991
 AND walk_in_annual_price=annual_slot_price AND preorder_annual_price=annual_slot_price));

CREATE OR REPLACE FUNCTION flow.uniform_slot_price_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.walk_in_annual_price IS DISTINCT FROM NEW.preorder_annual_price THEN
  RAISE EXCEPTION 'Walk-in and Preorder must have the same annual slot price' USING ERRCODE='22023';
 END IF;
 NEW.annual_slot_price := NEW.walk_in_annual_price;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_uniform_slot_price ON kiosk_ops.kiosk_slot_pricing;
CREATE TRIGGER trg_uniform_slot_price BEFORE INSERT OR UPDATE OF walk_in_annual_price,preorder_annual_price
 ON kiosk_ops.kiosk_slot_pricing FOR EACH ROW EXECUTE FUNCTION flow.uniform_slot_price_guard();

CREATE OR REPLACE FUNCTION flow.require_uniform_application_price() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.pricing_mode='SLOT_PRICING' AND NOT EXISTS(
  SELECT 1 FROM kiosk_ops.kiosk_slot_pricing p WHERE p.singleton AND p.annual_slot_price IS NOT NULL
  AND p.version=NEW.pricing_version AND NEW.walk_in_annual_price=p.annual_slot_price
  AND NEW.preorder_annual_price=p.annual_slot_price
  AND NEW.annual_fee=NEW.total_slots::bigint*p.annual_slot_price) THEN
  RAISE EXCEPTION 'Configure one shared slot price before creating a new rental' USING ERRCODE='55000';
 END IF;
 RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS trg_uniform_application_price ON kiosk_ops.kiosk_applications;
CREATE TRIGGER trg_uniform_application_price BEFORE INSERT ON kiosk_ops.kiosk_applications
 FOR EACH ROW EXECUTE FUNCTION flow.require_uniform_application_price();

CREATE TABLE IF NOT EXISTS kiosk_ops.slot_layout_requests (
 id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
 kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
 seller_id uuid NOT NULL REFERENCES identity.sellers(id),
 requested_by uuid NOT NULL REFERENCES identity.users(id),
 walk_in_slots integer NOT NULL CHECK (walk_in_slots>=0),
 preorder_slots integer NOT NULL CHECK (preorder_slots>=0),
 reason text NOT NULL CHECK (length(btrim(reason)) BETWEEN 1 AND 500),
 status text NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING','APPLYING','APPROVED','REJECTED')),
 reviewed_by uuid REFERENCES identity.users(id),
 review_note text,
 created_at timestamptz NOT NULL DEFAULT public.app_now(),
 reviewed_at timestamptz
);
CREATE UNIQUE INDEX IF NOT EXISTS uq_slot_layout_pending ON kiosk_ops.slot_layout_requests(kiosk_id) WHERE status='PENDING';
CREATE INDEX IF NOT EXISTS idx_slot_layout_seller ON kiosk_ops.slot_layout_requests(seller_id,created_at DESC);

CREATE OR REPLACE FUNCTION flow.request_slot_layout_change(p_actor uuid,p_kiosk uuid,p_walk integer,p_pre integer,p_reason text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_seller uuid; v_total integer; v_id uuid;
BEGIN
 SELECT k.seller_id INTO v_seller FROM kiosk_ops.kiosks k WHERE k.id=p_kiosk FOR UPDATE;
 IF v_seller IS NULL OR NOT EXISTS(SELECT 1 FROM identity.users u JOIN identity.sellers s ON s.id=v_seller
  WHERE u.id=p_actor AND u.role='SELLER' AND u.status='ACTIVE' AND u.seller_id=v_seller AND s.status='ACTIVE') THEN
  RAISE EXCEPTION 'Only the active kiosk Seller can request a layout change' USING ERRCODE='42501';
 END IF;
 SELECT count(*) INTO v_total FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk;
 IF p_walk IS NULL OR p_pre IS NULL OR p_walk<0 OR p_pre<0 OR p_walk+p_pre<>v_total
  OR length(btrim(coalesce(p_reason,''))) NOT BETWEEN 1 AND 500 THEN
  RAISE EXCEPTION 'Layout must preserve capacity and include a reason' USING ERRCODE='22023';
 END IF;
 IF NOT EXISTS(SELECT 1 FROM identity.subscriptions WHERE seller_id=v_seller AND kiosk_id=p_kiosk
  AND status='ACTIVE' AND ((period_to_ts IS NOT NULL AND period_to_ts>public.app_now()
    AND coalesce(period_from_ts,period_from::timestamp AT TIME ZONE 'UTC')<=public.app_now())
    OR (period_to_ts IS NULL AND period_from<=public.app_now()::date
    AND period_to>public.app_now()::date))) THEN
  RAISE EXCEPTION 'Kiosk lease is not active' USING ERRCODE='P0001';
 END IF;
 IF (SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND zone='WALK_IN')=p_walk
  AND (SELECT count(*) FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND zone='PREORDER')=p_pre THEN
  RAISE EXCEPTION 'Requested layout is already active' USING ERRCODE='P0001';
 END IF;
 INSERT INTO kiosk_ops.slot_layout_requests(kiosk_id,seller_id,requested_by,walk_in_slots,preorder_slots,reason)
 VALUES(p_kiosk,v_seller,p_actor,p_walk,p_pre,btrim(p_reason)) RETURNING id INTO v_id;
 PERFORM flow.audit(p_actor,'SLOT_LAYOUT_REQUESTED','slot_layout_requests',v_id,
  jsonb_build_object('kiosk',p_kiosk,'walk_in_slots',p_walk,'preorder_slots',p_pre));
 RETURN v_id;
END $$;

CREATE OR REPLACE FUNCTION flow.review_slot_layout_change(p_actor uuid,p_request uuid,p_preorder_ids uuid[],p_note text)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE r kiosk_ops.slot_layout_requests; s kiosk_ops.slots; v_total integer; v_changed integer:=0;
BEGIN
 PERFORM flow.require_operations_manager(p_actor);
 SELECT * INTO r FROM kiosk_ops.slot_layout_requests WHERE id=p_request FOR UPDATE;
 IF NOT FOUND OR r.status<>'PENDING' THEN RAISE EXCEPTION 'Request is not pending' USING ERRCODE='P0001'; END IF;
 IF p_preorder_ids IS NULL THEN
  IF length(btrim(coalesce(p_note,''))) NOT BETWEEN 1 AND 500 THEN
   RAISE EXCEPTION 'Rejection reason required' USING ERRCODE='22023'; END IF;
  UPDATE kiosk_ops.slot_layout_requests SET status='REJECTED',reviewed_by=p_actor,
   review_note=btrim(p_note),reviewed_at=public.app_now() WHERE id=r.id;
  PERFORM flow.audit(p_actor,'SLOT_LAYOUT_REJECTED','slot_layout_requests',r.id,jsonb_build_object('reason',p_note));
  RETURN;
 END IF;
 PERFORM 1 FROM kiosk_ops.kiosks WHERE id=r.kiosk_id AND seller_id=r.seller_id FOR UPDATE;
 IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM identity.subscriptions WHERE seller_id=r.seller_id AND kiosk_id=r.kiosk_id
  AND status='ACTIVE' AND ((period_to_ts IS NOT NULL AND period_to_ts>public.app_now()
    AND coalesce(period_from_ts,period_from::timestamp AT TIME ZONE 'UTC')<=public.app_now())
    OR (period_to_ts IS NULL AND period_from<=public.app_now()::date
    AND period_to>public.app_now()::date))) THEN
  RAISE EXCEPTION 'Kiosk ownership or lease changed' USING ERRCODE='P0001'; END IF;
 SELECT count(*) INTO v_total FROM kiosk_ops.slots WHERE kiosk_id=r.kiosk_id;
 IF r.walk_in_slots+r.preorder_slots<>v_total OR cardinality(p_preorder_ids)<>r.preorder_slots
  OR cardinality(p_preorder_ids)<>(SELECT count(DISTINCT x) FROM unnest(p_preorder_ids) x)
  OR EXISTS(SELECT 1 FROM unnest(p_preorder_ids) x WHERE x IS NULL OR NOT EXISTS(
   SELECT 1 FROM kiosk_ops.slots WHERE id=x AND kiosk_id=r.kiosk_id)) THEN
  RAISE EXCEPTION 'Select exactly the requested number of distinct kiosk slots' USING ERRCODE='22023';
 END IF;
 PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id=r.kiosk_id ORDER BY id FOR UPDATE;
 FOR s IN SELECT * FROM kiosk_ops.slots WHERE kiosk_id=r.kiosk_id ORDER BY id LOOP
  IF s.zone IS DISTINCT FROM (CASE WHEN s.id=ANY(p_preorder_ids) THEN 'PREORDER' ELSE 'WALK_IN' END) THEN
   IF s.status<>'RENTED_EMPTY' OR s.bouquet_id IS NOT NULL OR s.hold_order_id IS NOT NULL OR s.hold_until IS NOT NULL
    OR s.current_seller_id IS DISTINCT FROM r.seller_id THEN
    RAISE EXCEPTION 'Only empty, unheld slots belonging to this Seller can change zone' USING ERRCODE='P0001';
   END IF;
   v_changed:=v_changed+1;
  END IF;
 END LOOP;
 UPDATE kiosk_ops.slot_layout_requests SET status='APPLYING' WHERE id=r.id;
 PERFORM set_config('florabot.slot_layout_request',r.id::text,true);
 UPDATE kiosk_ops.slots SET zone=CASE WHEN id=ANY(p_preorder_ids) THEN 'PREORDER' ELSE 'WALK_IN' END,
  row_version=row_version+1,updated_at=public.app_now()
  WHERE kiosk_id=r.kiosk_id AND zone IS DISTINCT FROM (CASE WHEN id=ANY(p_preorder_ids) THEN 'PREORDER' ELSE 'WALK_IN' END);
 PERFORM set_config('florabot.slot_layout_request','',true);
 UPDATE kiosk_ops.slot_layout_requests SET status='APPROVED',reviewed_by=p_actor,
  review_note=nullif(btrim(p_note),''),reviewed_at=public.app_now() WHERE id=r.id;
 PERFORM flow.audit(p_actor,'SLOT_LAYOUT_APPROVED','slot_layout_requests',r.id,
  jsonb_build_object('changed_slots',v_changed,'preorder_slot_ids',p_preorder_ids,'annual_fee_unchanged',true));
END $$;

CREATE OR REPLACE FUNCTION flow.prevent_slot_zone_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF NEW.zone IS DISTINCT FROM OLD.zone AND NOT EXISTS(
  SELECT 1 FROM kiosk_ops.slot_layout_requests r
  WHERE r.id::text=nullif(current_setting('florabot.slot_layout_request',true),'')
   AND r.status='APPLYING' AND r.kiosk_id=OLD.kiosk_id
   AND OLD.status='RENTED_EMPTY' AND OLD.bouquet_id IS NULL
   AND OLD.hold_order_id IS NULL AND OLD.hold_until IS NULL
   AND OLD.current_seller_id=r.seller_id) THEN
  RAISE EXCEPTION 'Zone changes require an approved layout request and an empty slot' USING ERRCODE='42501';
 END IF;
 RETURN NEW;
END $$;

COMMIT;
