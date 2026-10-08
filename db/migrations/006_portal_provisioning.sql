-- Administrative CLI only; never expose this flow through the HTTP flow allowlist.
CREATE OR REPLACE FUNCTION flow.provision_portal_user(p_user uuid, p_email text, p_hash text, p_allow_demo boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE u identity.users; v_email text := lower(trim(p_email));
BEGIN
  IF v_email IS NULL OR length(v_email) > 254 OR v_email !~ '^[^[:space:]@]+@[^[:space:]@]+$' THEN
    RAISE EXCEPTION 'Email không hợp lệ';
  END IF;
  IF p_hash IS NULL OR p_hash !~ '^\$2[aby]\$12\$[./A-Za-z0-9]{53}$' THEN
    RAISE EXCEPTION 'Mật khẩu phải được băm bcrypt với cost 12';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtext('provision-email:' || v_email));
  SELECT * INTO u FROM identity.users WHERE id=p_user FOR UPDATE;
  IF NOT FOUND OR u.role NOT IN ('ADMIN','SELLER') OR u.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Tài khoản không đủ điều kiện cấp mật khẩu';
  END IF;
  IF u.password_hash IS DISTINCT FROM '!unprovisioned' AND NOT (
    p_allow_demo IS TRUE AND u.password_hash IN ('$2b$12$demoadmin','$2b$12$demoops','$2b$12$demoseller1',
      '$2b$12$demoseller2','$2b$12$demostaff','$2b$12$demostaff1','$2b$12$demotech','$2b$12$demostaff2')) THEN
    RAISE EXCEPTION 'Tài khoản đã có mật khẩu hoặc không phải tài khoản chờ cấp';
  END IF;
  IF u.email IS NOT NULL AND lower(trim(u.email)) <> v_email THEN
    RAISE EXCEPTION 'Email không khớp tài khoản đã chọn';
  END IF;
  IF EXISTS(SELECT 1 FROM identity.users WHERE lower(trim(email))=v_email AND id<>p_user) THEN
    RAISE EXCEPTION 'Email đã được dùng cho tài khoản khác';
  END IF;
  UPDATE identity.users SET email=v_email,password_hash=p_hash WHERE id=p_user;
  PERFORM flow.audit(NULL,'PORTAL_USER_PROVISIONED','users',p_user,jsonb_build_object('role',u.role,'channel','operator_cli'));
END $$;
