-- Migration 021: Google auth support and account hierarchy rules
BEGIN;

-- 1. Safely replace password check to allow OAuth customers and sellers to have password_hash IS NULL
DO $$
DECLARE
  r RECORD;
BEGIN
  FOR r IN (
    SELECT conname
    FROM pg_constraint
    WHERE conrelid = 'identity.users'::regclass
      AND contype = 'c'
      AND pg_get_constraintdef(oid) LIKE '%password_hash IS NOT NULL%'
  ) LOOP
    EXECUTE 'ALTER TABLE identity.users DROP CONSTRAINT IF EXISTS ' || quote_ident(r.conname);
  END LOOP;
END $$;

ALTER TABLE identity.users DROP CONSTRAINT IF EXISTS users_password_check;
ALTER TABLE identity.users ADD CONSTRAINT users_password_check
  CHECK (role IN ('CUSTOMER', 'SELLER') OR password_hash IS NOT NULL);

-- 2. Update flow.open_member_shop to allow Google Auth accounts (no password_hash required)
CREATE OR REPLACE FUNCTION flow.open_member_shop(p_user uuid, p_shop text, p_phone text, p_address text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE u identity.users; v uuid;
BEGIN
  SELECT * INTO u FROM identity.users WHERE id=p_user FOR UPDATE;
  IF NOT FOUND OR u.status<>'ACTIVE' OR u.role NOT IN ('CUSTOMER','SELLER') THEN
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

COMMIT;
