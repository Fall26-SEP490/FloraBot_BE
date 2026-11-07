-- When a Seller has no separate shop contact email, snapshot the authenticated
-- Seller user's login email into the additional-kiosk application.
BEGIN;

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
  -- PAST_DUE only reflects the expiry of current lease entitlements. It must
  -- not prevent an active Seller account from renting another kiosk.
  IF NOT FOUND OR s.status NOT IN ('ACTIVE','PAST_DUE') THEN
   RAISE EXCEPTION 'Seller is not eligible to request a kiosk' USING ERRCODE='42501';
  END IF;
  p_shop:=s.shop_name; p_phone:=s.phone;
  p_email:=coalesce(nullif(btrim(s.contact_email),''),nullif(btrim(u.email),''));
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
 SELECT * INTO prices FROM kiosk_ops.kiosk_slot_pricing WHERE singleton FOR SHARE;
 IF NOT FOUND THEN RAISE EXCEPTION 'Operations Manager has not configured slot prices' USING ERRCODE='55000'; END IF;
 IF prices.version <> p_pricing_version THEN
  RAISE EXCEPTION 'Slot prices changed; reload prices and confirm the new total' USING ERRCODE='P0001';
 END IF;
 total := p_walk::numeric * prices.walk_in_annual_price + p_pre::numeric * prices.preorder_annual_price;
 IF total NOT BETWEEN 1 AND 9007199254740991 THEN
  RAISE EXCEPTION 'Rental amount exceeds the supported payment limit' USING ERRCODE='22023';
 END IF;
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

COMMIT;
