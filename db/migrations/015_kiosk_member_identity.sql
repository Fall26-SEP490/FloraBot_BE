BEGIN;

CREATE OR REPLACE FUNCTION flow.customer_by_phone(p_phone text, p_name text DEFAULT NULL, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE u identity.users; v_now timestamptz := flow.tick(p_now);
BEGIN
  IF p_phone IS NULL OR p_phone !~ '^0[35789][0-9]{8}$' THEN
    RAISE EXCEPTION 'Invalid phone' USING ERRCODE='22023';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('member-phone:'||p_phone,0));
  SELECT * INTO u FROM identity.users WHERE phone=p_phone FOR UPDATE;
  IF FOUND THEN
    IF u.role NOT IN ('CUSTOMER','SELLER') OR u.status<>'ACTIVE' THEN
      RAISE EXCEPTION 'Member unavailable' USING ERRCODE='42501';
    END IF;
    RETURN u.id;
  END IF;
  INSERT INTO identity.users(phone,full_name,role)
  VALUES(p_phone,coalesce(p_name,'Khách '||right(p_phone,4)),'CUSTOMER') RETURNING * INTO u;
  PERFORM flow.audit(u.id,'CUSTOMER_REGISTERED','users',u.id);
  RETURN u.id;
END $$;

COMMIT;
