-- The supplied demo function creates a fake bcrypt hash. Keep its exact signature,
-- but leave newly registered contacts unable to log in until securely provisioned.
CREATE OR REPLACE FUNCTION flow.register_seller(p_shop text, p_phone text, p_address text, p_owner_name text, p_owner_email text, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v uuid; v_now timestamptz := flow.tick(p_now);
BEGIN
  IF length(trim(p_shop)) NOT BETWEEN 2 AND 120 OR p_phone !~ '^(0[35789][0-9]{8}|\+84[35789][0-9]{8})$' THEN
    RAISE EXCEPTION 'Tên shop hoặc số điện thoại không hợp lệ';
  END IF;
  INSERT INTO identity.sellers (shop_name, phone, address) VALUES (trim(p_shop), p_phone, p_address) RETURNING id INTO v;
  INSERT INTO identity.users (email, phone, password_hash, full_name, role, seller_id)
  VALUES (p_owner_email, p_phone, '!unprovisioned', coalesce(nullif(trim(p_owner_name), ''), trim(p_shop)), 'SELLER', v);
  PERFORM flow.audit(NULL, 'SELLER_REGISTERED', 'sellers', v, jsonb_build_object('shop', p_shop));
  RETURN v;
END $$;
