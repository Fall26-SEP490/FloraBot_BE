-- Annual renewal is scoped to the physical kiosk. Existing monthly leases
-- keep their seller-wide exclusion; different kiosks may overlap in time.
BEGIN;

ALTER TABLE identity.subscriptions
  DROP CONSTRAINT IF EXISTS subscriptions_seller_id_daterange_excl;

DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='subscriptions_monthly_seller_period_excl') THEN
    ALTER TABLE identity.subscriptions ADD CONSTRAINT subscriptions_monthly_seller_period_excl
      EXCLUDE USING gist (seller_id WITH =, daterange(period_from, period_to) WITH &&)
      WHERE (kiosk_id IS NULL AND status IN ('PENDING_PAYMENT','ACTIVE'));
  END IF;
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname='subscriptions_kiosk_period_excl') THEN
    ALTER TABLE identity.subscriptions ADD CONSTRAINT subscriptions_kiosk_period_excl
      EXCLUDE USING gist (kiosk_id WITH =, daterange(period_from, period_to) WITH &&)
      WHERE (kiosk_id IS NOT NULL AND status IN ('PENDING_PAYMENT','ACTIVE'));
  END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.request_kiosk_renewal(p_actor uuid, p_kiosk uuid)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_seller uuid;
  v_latest identity.subscriptions;
  v_price bigint;
  v_slots int;
  v_amount numeric;
  v_from timestamptz;
  v_to timestamptz;
  v_sub uuid := public.uuid_v7();
  v_payment uuid := public.uuid_v7();
BEGIN
  SELECT u.seller_id INTO v_seller FROM identity.users u
  JOIN identity.sellers s ON s.id=u.seller_id
  WHERE u.id=p_actor AND u.role='SELLER' AND u.status='ACTIVE'
    AND s.status IN ('ACTIVE','PAST_DUE') FOR SHARE OF u,s;
  IF v_seller IS NULL THEN RAISE EXCEPTION 'Seller not eligible for renewal' USING ERRCODE='42501'; END IF;

  PERFORM 1 FROM kiosk_ops.kiosks k WHERE k.id=p_kiosk AND k.seller_id=v_seller
    AND k.status<>'DISABLED' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk does not belong to Seller' USING ERRCODE='42501'; END IF;
  IF EXISTS (SELECT 1 FROM identity.subscriptions WHERE kiosk_id=p_kiosk AND status='PENDING_PAYMENT') THEN
    RAISE EXCEPTION 'Renewal is already awaiting payment' USING ERRCODE='23505';
  END IF;
  SELECT * INTO v_latest FROM identity.subscriptions
    WHERE kiosk_id=p_kiosk AND seller_id=v_seller AND status IN ('ACTIVE','EXPIRED')
    ORDER BY coalesce(period_to_ts,period_to::timestamp AT TIME ZONE 'UTC') DESC, id DESC LIMIT 1;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk has no previous lease' USING ERRCODE='22023'; END IF;

  SELECT annual_slot_price INTO v_price FROM kiosk_ops.kiosk_slot_pricing WHERE singleton FOR SHARE;
  IF v_price IS NULL OR v_price<=0 THEN
    RAISE EXCEPTION 'Current kiosk slot price is not configured' USING ERRCODE='55000';
  END IF;
  SELECT count(*) INTO v_slots FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk;
  IF v_slots NOT BETWEEN 1 AND 50 THEN RAISE EXCEPTION 'Invalid kiosk slot count' USING ERRCODE='22023'; END IF;
  v_amount := v_price::numeric * v_slots;
  IF v_amount > 9007199254740991 THEN RAISE EXCEPTION 'Renewal amount too large' USING ERRCODE='22023'; END IF;

  v_from := greatest(v_now, coalesce(v_latest.period_to_ts,v_latest.period_to::timestamp AT TIME ZONE 'UTC'));
  v_to := v_from + interval '1 year';
  INSERT INTO identity.subscriptions
    (id,seller_id,kiosk_id,package_id,period_from,period_to,period_from_ts,period_to_ts,price,status,payment_id)
  VALUES (v_sub,v_seller,p_kiosk,v_latest.package_id,v_from::date,v_to::date,v_from,v_to,
          v_amount::bigint,'PENDING_PAYMENT',v_payment);
  INSERT INTO payment.payments
    (id,kind,purpose,subscription_id,gateway,idempotency_key,amount,status)
  VALUES (v_payment,'CHARGE','SUBSCRIPTION',v_sub,'PAYOS','kiosk-renewal-'||v_sub::text,
          v_amount::bigint,'PENDING');
  PERFORM flow.audit(p_actor,'KIOSK_RENEWAL_REQUESTED','subscriptions',v_sub,
    jsonb_build_object('kiosk_id',p_kiosk,'seller_id',v_seller,'slot_count',v_slots,
      'annual_slot_price',v_price,'amount',v_amount,'period_from',v_from,'period_to',v_to));
  RETURN v_sub;
END $$;

COMMIT;
