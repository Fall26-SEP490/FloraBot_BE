-- Migration 025: Additive kiosk location coordinates and preorder location readiness
-- Forward-only migration preserving existing kiosks. Legacy NULL coordinates permitted, no fabricated coordinates. Idempotent.
BEGIN;

ALTER TABLE kiosk_ops.kiosks
  ADD COLUMN IF NOT EXISTS latitude double precision;

ALTER TABLE kiosk_ops.kiosks
  ADD COLUMN IF NOT EXISTS longitude double precision;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_kiosks_coordinates_pair') THEN
    ALTER TABLE kiosk_ops.kiosks
      ADD CONSTRAINT chk_kiosks_coordinates_pair
      CHECK (
        (latitude IS NULL AND longitude IS NULL) OR
        (latitude IS NOT NULL AND longitude IS NOT NULL AND length(btrim(address)) > 0)
      );
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_kiosks_coordinates_range') THEN
    ALTER TABLE kiosk_ops.kiosks
      ADD CONSTRAINT chk_kiosks_coordinates_range
      CHECK (
        (latitude IS NULL AND longitude IS NULL) OR
        (latitude >= -90.0 AND latitude <= 90.0 AND longitude >= -180.0 AND longitude <= 180.0)
      );
  END IF;
END $$;

-- Update flow.web_request: new preorders require kiosk to have complete address and valid coordinates (pin).
-- Preserves source migration012 member personas (CUSTOMER/SELLER), idempotency and financial logic.
-- Historical preorders and idempotent retries for existing request IDs are preserved.
CREATE OR REPLACE FUNCTION flow.web_request(p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid, p_bouquet uuid, p_pickup timestamp with time zone, p_instructions text)
 RETURNS uuid
 LANGUAGE plpgsql
AS $function$
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
END $function$;

-- Update screen.v_web_catalog: filter to ready address + pin so customers browsing catalog
-- are not shown unselectable locations.
CREATE OR REPLACE VIEW screen.v_web_catalog AS
SELECT p.id AS product_id,p.seller_id,s.shop_name,p.name,p.description,p.price,k.id AS kiosk_id,k.name AS kiosk_name,k.address,
 (SELECT a.file_url FROM notify.attachments a WHERE a.owner_service='catalog' AND a.owner_type='flower_product' AND a.owner_id=p.id ORDER BY a.created_at LIMIT 1) AS photo,
 ARRAY(SELECT b.id FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots sl ON sl.bouquet_id=b.id
       WHERE b.product_id=p.id AND sl.kiosk_id=k.id AND sl.status='STOCKED' AND b.status='STOCKED' AND b.sellable_until>public.app_now()+flow.cfg_min('hold_minutes') AND flow.slot_sellable(sl.id,s.id) ORDER BY b.sellable_until,b.id) AS bouquets
FROM catalog.flower_products p JOIN identity.sellers s ON s.id=p.seller_id
JOIN kiosk_ops.kiosks k ON k.status NOT IN ('DISABLED','MAINTENANCE')
  AND length(btrim(coalesce(k.address, ''))) > 0
  AND k.latitude IS NOT NULL
  AND k.longitude IS NOT NULL
WHERE p.status='ACTIVE' AND s.status='ACTIVE' AND flow.package_valid(s.id)
AND EXISTS(SELECT 1 FROM kiosk_ops.slots sl WHERE sl.kiosk_id=k.id AND sl.current_seller_id=s.id AND flow.slot_sellable(sl.id,s.id));

COMMIT;
