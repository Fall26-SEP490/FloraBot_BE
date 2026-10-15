-- =============================================================================
-- FloraBot Local Demo: Team Accounts Seeding Logic (Bounded Namespace: df000000-*)
-- Scope: Local Demonstration and Visual Verification for 7 Team Accounts (6 Authenticated Roles) only.
-- Invocation: Included within same transaction before seed-demo-data.sql in seed-local-demo.ps1.
-- Requires temporary input tables:
--   - demo_input_config (key text PRIMARY KEY, value text NOT NULL)
--   - demo_input_team_accounts (id uuid PRIMARY KEY, email text NOT NULL, role text NOT NULL,
--                               full_name text NOT NULL, seller_id uuid, raw_password text NOT NULL)
-- Safety Invariants:
--   - 7 reserved UUIDs in namespace df000000-* (2 ADMIN, 1 OPERATIONS_MANAGER, 1 TECHNICIAN, 1 SELLER, 1 SELLER_STAFF, 1 CUSTOMER).
--   - Assigned to same baseline Hoa Sai Gon seller (20000000-0000-0000-0000-000000000001).
--   - Passwords hashed with bcrypt cost 12 via pgcrypto crypt/gen_salt.
--   - Never overwrite existing passwords, roles, or status on rerun.
--   - Fails immediately on reserved ID or email collisions with non-team accounts.
--   - Advisory transaction lock serializes execution for the same namespace.
-- =============================================================================

DO $$
DECLARE
  v_team_mode boolean := false;
  v_seller_id uuid := '20000000-0000-0000-0000-000000000001'::uuid;
  v_admin_id uuid := 'df000000-0000-0000-0000-000000000001'::uuid;
  v_admin2_id uuid := 'df000000-0000-0000-0000-000000000007'::uuid;
  v_mgr_id uuid := 'df000000-0000-0000-0000-000000000002'::uuid;
  v_tech_id uuid := 'df000000-0000-0000-0000-000000000003'::uuid;
  v_seller_user_id uuid := 'df000000-0000-0000-0000-000000000004'::uuid;
  v_staff_id uuid := 'df000000-0000-0000-0000-000000000005'::uuid;
  v_customer_id uuid := 'df000000-0000-0000-0000-000000000006'::uuid;
BEGIN
  SELECT (value = 'true') INTO v_team_mode
  FROM demo_input_config
  WHERE key = 'enable_team_accounts';

  IF v_team_mode IS NULL OR NOT v_team_mode THEN
    RETURN;
  END IF;

  -- 1. Serialize concurrent team seed attempts for this namespace
  PERFORM pg_advisory_xact_lock(hashtext('florabot:team_demo_accounts:df000000'));

  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id::text LIKE 'df000000-%')
     AND EXISTS (SELECT 1 FROM kiosk_ops.kiosks WHERE id = 'de000000-0000-0000-0001-000000000001'::uuid) THEN
    RAISE EXCEPTION 'Existing demo namespace belongs to a non-team setup. Preserve its progress and use a separate local database for team seeding.';
  END IF;

  -- 2. NULL-safe collision detection: ensure no existing accounts conflict with reserved IDs or emails
  IF EXISTS (
    SELECT 1 FROM identity.users u
    JOIN demo_input_team_accounts a ON (u.id = a.id OR lower(trim(u.email)) = lower(trim(a.email)))
    WHERE u.id IS DISTINCT FROM a.id
       OR lower(trim(u.email)) IS DISTINCT FROM lower(trim(a.email))
       OR u.role IS DISTINCT FROM a.role
       OR u.seller_id IS DISTINCT FROM a.seller_id
       OR u.status IS DISTINCT FROM 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'Collision detected: existing user account conflicts with reserved team account ID, email, role, seller binding, or status.'
      USING ERRCODE = '23505';
  END IF;

  -- 3. Validate password hashes for any already-existing team rows before ON CONFLICT DO NOTHING (fails on mismatch)
  IF EXISTS (
    SELECT 1 FROM identity.users u
    JOIN demo_input_team_accounts a ON u.id = a.id
    WHERE u.password_hash IS NULL OR public.crypt(a.raw_password, u.password_hash) IS DISTINCT FROM u.password_hash
  ) THEN
    RAISE EXCEPTION 'Existing database team account password hash does not match private fixture. Credentials will not be overwritten or reset.'
      USING ERRCODE = '28000';
  END IF;

  -- 4. Insert the seven team accounts if not already present.
  -- ON CONFLICT (id) DO NOTHING ensures we NEVER overwrite existing passwords, roles, or status.
  INSERT INTO identity.users (
    id, email, phone, password_hash, full_name, role, seller_id, status, loyalty_points, created_at, updated_at
  )
  SELECT
    a.id,
    lower(trim(a.email)),
    NULL,
    public.crypt(a.raw_password, public.gen_salt('bf', 12)),
    a.full_name,
    a.role,
    a.seller_id,
    'ACTIVE',
    0,
    public.app_now(),
    public.app_now()
  FROM demo_input_team_accounts a
  ON CONFLICT (id) DO NOTHING;

  -- 5. Verify all seven accounts exist and are ACTIVE in identity.users
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_admin_id AND role = 'ADMIN' AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_admin2_id AND role = 'ADMIN' AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_mgr_id AND role = 'OPERATIONS_MANAGER' AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_tech_id AND role = 'TECHNICIAN' AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_seller_user_id AND role = 'SELLER' AND seller_id = v_seller_id AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_staff_id AND role = 'SELLER_STAFF' AND seller_id = v_seller_id AND status = 'ACTIVE') OR
     NOT EXISTS (SELECT 1 FROM identity.users WHERE id = v_customer_id AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Team accounts insertion failed: all seven accounts must be active in identity.users.'
      USING ERRCODE = '22023';
  END IF;

  RAISE NOTICE 'Team accounts seeded/verified successfully in bounded namespace df000000-*.';
END $$;
