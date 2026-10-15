-- =============================================================================
-- FloraBot Local Demo: Onboarding Kiosk Assets Seeding Logic (Namespace: da000000-*)
-- Scope: Local Demonstration and Visual Verification for Annual Kiosk Onboarding.
-- Invocation: Executed via scripts/local-demo/seed-onboarding-assets.ps1.
--
-- Business Rules & Safety Invariants:
--   - Exactly 3 fictional offline whole kiosks matching Trial (1+1), Pro (4+4), Max (6+6).
--   - Explicit slot zones: WALK_IN and PREORDER.
--   - All 22 slots initialized as status 'FREE' with current_seller_id = NULL, bouquet_id = NULL.
--   - No seller lease, no slot assignments, no paid entitlements, no simulated ledger entries.
--   - Devices are explicitly OFFLINE with no hardware heartbeat (last_heartbeat_at = NULL).
--   - Authentication disabled / api_key_hash = NULL until Operations Manager explicitly provisions key via API.
--   - Fixed reserved namespace da000000-*; preserves existing progress on rerun via ON CONFLICT DO NOTHING.
--   - Advisory transaction lock serializes execution for this namespace.
-- =============================================================================

DO $$
DECLARE
  v_kiosk_trial uuid := 'da000000-0000-0000-0001-000000000001'::uuid;
  v_kiosk_pro   uuid := 'da000000-0000-0000-0001-000000000002'::uuid;
  v_kiosk_max   uuid := 'da000000-0000-0000-0001-000000000003'::uuid;
  v_kiosk_count int;
  v_slot_count  int;
  v_trial_w     int;
  v_trial_p     int;
  v_pro_w       int;
  v_pro_p       int;
  v_max_w       int;
  v_max_p       int;
  v_unfree      int;
  v_assigned    int;
BEGIN
  -- 1. Serialize concurrent seed executions for onboarding assets namespace
  PERFORM pg_advisory_xact_lock(hashtext('florabot:onboarding_assets:da000000'));
  SELECT count(*) INTO v_assigned FROM kiosk_ops.slot_assignments WHERE slot_id::text LIKE 'da000000-%';

  -- 2. Collision detection: ensure no existing non-demo entities conflict with reserved codes/hardware IDs
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosks
    WHERE (code IN ('K-ONBOARD-TRIAL', 'K-ONBOARD-PRO', 'K-ONBOARD-MAX')
       OR hardware_id IN ('SIM-ONBOARD-TRIAL01', 'SIM-ONBOARD-PRO01', 'SIM-ONBOARD-MAX01')
       OR mqtt_client_id IN ('sim-onboard-trial01', 'sim-onboard-pro01', 'sim-onboard-max01'))
      AND id::text NOT LIKE 'da000000-%'
  ) THEN
    RAISE EXCEPTION 'Collision detected: existing kiosk conflicts with reserved onboarding asset code, hardware ID, or MQTT client ID.'
      USING ERRCODE = '23505';
  END IF;

  -- 3. Insert the 3 fictional unassigned whole kiosks (OFFLINE, no hardware heartbeat, disabled auth)
  INSERT INTO kiosk_ops.kiosks (
    id, code, name, address, region, hardware_id, mqtt_client_id, status, last_heartbeat_at, api_key_hash, created_at, updated_at
  ) VALUES
  (
    v_kiosk_trial, 'K-ONBOARD-TRIAL', 'Kiosk Onboarding Demo - Trial (1W+1P)',
    'Kho hàng trung tâm (Mô phỏng)', 'HCM', 'SIM-ONBOARD-TRIAL01', 'sim-onboard-trial01',
    'OFFLINE', NULL, NULL, public.app_now(), public.app_now()
  ),
  (
    v_kiosk_pro, 'K-ONBOARD-PRO', 'Kiosk Onboarding Demo - Pro (4W+4P)',
    'Kho hàng trung tâm (Mô phỏng)', 'HCM', 'SIM-ONBOARD-PRO01', 'sim-onboard-pro01',
    'OFFLINE', NULL, NULL, public.app_now(), public.app_now()
  ),
  (
    v_kiosk_max, 'K-ONBOARD-MAX', 'Kiosk Onboarding Demo - Max (6W+6P)',
    'Kho hàng trung tâm (Mô phỏng)', 'HCM', 'SIM-ONBOARD-MAX01', 'sim-onboard-max01',
    'OFFLINE', NULL, NULL, public.app_now(), public.app_now()
  )
  ON CONFLICT (id) DO NOTHING;

  -- 4. Insert slots for the 3 kiosks matching annual onboarding packages
  -- Trial Kiosk (1 WALK_IN + 1 PREORDER = 2 slots)
  INSERT INTO kiosk_ops.slots (
    id, kiosk_id, slot_code, zone, relay_channel, status, current_seller_id, bouquet_id, hold_until, hold_order_id, created_at, updated_at
  ) VALUES
  ('da000000-0000-0000-0002-000000000101'::uuid, v_kiosk_trial, 'W01', 'WALK_IN',  0, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000102'::uuid, v_kiosk_trial, 'P01', 'PREORDER', 1, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now())
  ON CONFLICT (id) DO NOTHING;

  -- Pro Kiosk (4 WALK_IN + 4 PREORDER = 8 slots)
  INSERT INTO kiosk_ops.slots (
    id, kiosk_id, slot_code, zone, relay_channel, status, current_seller_id, bouquet_id, hold_until, hold_order_id, created_at, updated_at
  ) VALUES
  ('da000000-0000-0000-0002-000000000201'::uuid, v_kiosk_pro, 'W01', 'WALK_IN',  0, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000202'::uuid, v_kiosk_pro, 'W02', 'WALK_IN',  1, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000203'::uuid, v_kiosk_pro, 'W03', 'WALK_IN',  2, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000204'::uuid, v_kiosk_pro, 'W04', 'WALK_IN',  3, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000205'::uuid, v_kiosk_pro, 'P01', 'PREORDER', 4, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000206'::uuid, v_kiosk_pro, 'P02', 'PREORDER', 5, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000207'::uuid, v_kiosk_pro, 'P03', 'PREORDER', 6, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000208'::uuid, v_kiosk_pro, 'P04', 'PREORDER', 7, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now())
  ON CONFLICT (id) DO NOTHING;

  -- Max Kiosk (6 WALK_IN + 6 PREORDER = 12 slots)
  INSERT INTO kiosk_ops.slots (
    id, kiosk_id, slot_code, zone, relay_channel, status, current_seller_id, bouquet_id, hold_until, hold_order_id, created_at, updated_at
  ) VALUES
  ('da000000-0000-0000-0002-000000000301'::uuid, v_kiosk_max, 'W01', 'WALK_IN',  0, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000302'::uuid, v_kiosk_max, 'W02', 'WALK_IN',  1, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000303'::uuid, v_kiosk_max, 'W03', 'WALK_IN',  2, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000304'::uuid, v_kiosk_max, 'W04', 'WALK_IN',  3, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000305'::uuid, v_kiosk_max, 'W05', 'WALK_IN',  4, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000306'::uuid, v_kiosk_max, 'W06', 'WALK_IN',  5, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000307'::uuid, v_kiosk_max, 'P01', 'PREORDER', 6, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000308'::uuid, v_kiosk_max, 'P02', 'PREORDER', 7, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000309'::uuid, v_kiosk_max, 'P03', 'PREORDER', 8, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000310'::uuid, v_kiosk_max, 'P04', 'PREORDER', 9, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000311'::uuid, v_kiosk_max, 'P05', 'PREORDER', 10, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now()),
  ('da000000-0000-0000-0002-000000000312'::uuid, v_kiosk_max, 'P06', 'PREORDER', 11, 'FREE', NULL, NULL, NULL, NULL, public.app_now(), public.app_now())
  ON CONFLICT (id) DO NOTHING;

  -- 5. Strict Invariant Verification
  SELECT count(*) INTO v_kiosk_count FROM kiosk_ops.kiosks WHERE id::text LIKE 'da000000-%';
  IF v_kiosk_count <> 3 THEN
    RAISE EXCEPTION 'Onboarding kiosks assertion failed: expected 3 kiosks, found %', v_kiosk_count USING ERRCODE = '22023';
  END IF;

  SELECT count(*) INTO v_slot_count FROM kiosk_ops.slots WHERE id::text LIKE 'da000000-%';
  IF v_slot_count <> 22 THEN
    RAISE EXCEPTION 'Onboarding slots assertion failed: expected 22 slots, found %', v_slot_count USING ERRCODE = '22023';
  END IF;

  SELECT count(*) FILTER (WHERE zone = 'WALK_IN'), count(*) FILTER (WHERE zone = 'PREORDER')
  INTO v_trial_w, v_trial_p
  FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_trial;
  IF v_trial_w <> 1 OR v_trial_p <> 1 THEN
    RAISE EXCEPTION 'Trial kiosk zone assertion failed: expected 1W+1P, found %W+%P', v_trial_w, v_trial_p USING ERRCODE = '22023';
  END IF;

  SELECT count(*) FILTER (WHERE zone = 'WALK_IN'), count(*) FILTER (WHERE zone = 'PREORDER')
  INTO v_pro_w, v_pro_p
  FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_pro;
  IF v_pro_w <> 4 OR v_pro_p <> 4 THEN
    RAISE EXCEPTION 'Pro kiosk zone assertion failed: expected 4W+4P, found %W+%P', v_pro_w, v_pro_p USING ERRCODE = '22023';
  END IF;

  SELECT count(*) FILTER (WHERE zone = 'WALK_IN'), count(*) FILTER (WHERE zone = 'PREORDER')
  INTO v_max_w, v_max_p
  FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_max;
  IF v_max_w <> 6 OR v_max_p <> 6 THEN
    RAISE EXCEPTION 'Max kiosk zone assertion failed: expected 6W+6P, found %W+%P', v_max_w, v_max_p USING ERRCODE = '22023';
  END IF;

  -- Creating fixtures must not create leases; reruns preserve leases that were
  -- subsequently granted through the real onboarding flow.
  IF (SELECT count(*) FROM kiosk_ops.slot_assignments WHERE slot_id::text LIKE 'da000000-%') <> v_assigned THEN
    RAISE EXCEPTION 'Onboarding asset seed changed existing slot assignments' USING ERRCODE = '22023';
  END IF;

  RAISE NOTICE 'Onboarding kiosk assets seeded/verified successfully in bounded namespace da000000-*.';
END $$;
