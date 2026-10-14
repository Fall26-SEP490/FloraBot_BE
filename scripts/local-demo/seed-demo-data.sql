-- =============================================================================
-- FloraBot Local Demo Data Core Seeding Logic (Bounded Namespace: de000000-*)
-- Scope: Local Demonstration and Visual Verification only.
-- Invocation: Called via scripts/local-demo/seed-local-demo.ps1 within a transaction.
-- Requires temporary input tables:
--   - demo_input_config (key text PRIMARY KEY, value text NOT NULL)
--   - demo_input_manifest (slug text PRIMARY KEY, name text, url text, public_id text,
--                          sha256 text, size_bytes bigint, mime_type text)
--
-- Business Rules & Safety Invariants:
--   - Reuses existing Seller Hoa Sai Gon (20000000-0000-0000-0000-000000000001).
--   - Preserves all pre-existing user accounts without credential modification.
--   - No developer Cloudinary URLs hardcoded; consumed purely from demo_input_manifest.
--   - One Seller whole kiosk: all 8 slots assigned to Hoa Sai Gon; no FREE unassigned slots.
--   - Simulated device explicitly labeled OFFLINE and no-hardware.
--   - Manager mapped ONLY to the demo kiosk in our bounded namespace.
--   - Rerun preserves demo progress/hold/order states via ON CONFLICT DO NOTHING.
--   - No outbound notifications or paid ledger entries fabricated.
--   - Optional subscription lease opt-in available via enable_subscription config.
-- =============================================================================

DO $$
DECLARE
  v_admin_id uuid;
  v_mgr_id uuid;
  v_tech_id uuid;
  v_seller_user_id uuid;
  v_staff_id uuid;
  v_customer_id uuid;
  v_seller_id uuid := '20000000-0000-0000-0000-000000000001'::uuid;

  v_kiosk_id uuid := 'de000000-0000-0000-0001-000000000001'::uuid;
  v_prod1 uuid := 'de000000-0000-0000-0003-000000000001'::uuid;
  v_prod2 uuid := 'de000000-0000-0000-0003-000000000002'::uuid;
  v_prod3 uuid := 'de000000-0000-0000-0003-000000000003'::uuid;
  v_prod4 uuid := 'de000000-0000-0000-0003-000000000004'::uuid;
  v_prod5 uuid := 'de000000-0000-0000-0003-000000000005'::uuid;
  v_prod6 uuid := 'de000000-0000-0000-0003-000000000006'::uuid;

  v_bq1 uuid := 'de000000-0000-0000-0004-000000000001'::uuid;
  v_bq2 uuid := 'de000000-0000-0000-0004-000000000002'::uuid;
  v_bq3 uuid := 'de000000-0000-0000-0004-000000000003'::uuid;
  v_bq4 uuid := 'de000000-0000-0000-0004-000000000004'::uuid;

  v_att1 uuid := 'de000000-0000-0000-0005-000000000001'::uuid;
  v_att2 uuid := 'de000000-0000-0000-0005-000000000002'::uuid;
  v_att3 uuid := 'de000000-0000-0000-0005-000000000003'::uuid;
  v_att4 uuid := 'de000000-0000-0000-0005-000000000004'::uuid;

  v_slot_w1 uuid := 'de000000-0000-0000-0002-000000000001'::uuid;
  v_slot_w2 uuid := 'de000000-0000-0000-0002-000000000002'::uuid;
  v_slot_w3 uuid := 'de000000-0000-0000-0002-000000000003'::uuid;
  v_slot_w4 uuid := 'de000000-0000-0000-0002-000000000004'::uuid;
  v_slot_p1 uuid := 'de000000-0000-0000-0002-000000000005'::uuid;
  v_slot_p2 uuid := 'de000000-0000-0000-0002-000000000006'::uuid;
  v_slot_p3 uuid := 'de000000-0000-0000-0002-000000000007'::uuid;
  v_slot_p4 uuid := 'de000000-0000-0000-0002-000000000008'::uuid;

  v_disp1 uuid := 'de000000-0000-0000-0007-000000000001'::uuid;
  v_disp2 uuid := 'de000000-0000-0000-0007-000000000002'::uuid;
  v_disp3 uuid := 'de000000-0000-0000-0007-000000000003'::uuid;

  v_task1 uuid := 'de000000-0000-0000-0006-000000000001'::uuid;
  v_task2 uuid := 'de000000-0000-0000-0006-000000000002'::uuid;
  v_task3 uuid := 'de000000-0000-0000-0006-000000000003'::uuid;
  v_task4 uuid := 'de000000-0000-0000-0006-000000000004'::uuid;

  v_req1 uuid := 'de000000-0000-0000-0008-000000000001'::uuid;
  v_req2 uuid := 'de000000-0000-0000-0008-000000000002'::uuid;

  v_order1 uuid := 'de000000-0000-0000-0009-000000000001'::uuid;
  v_chk1 uuid := 'de000000-0000-0000-0009-0000000000c1'::uuid;
  v_order_item1 uuid := 'de000000-0000-0000-0009-000000000011'::uuid;

  v_sub1 uuid := 'de000000-0000-0000-000b-000000000001'::uuid;

  v_key_hash text;
  v_opt_in boolean := false;
  v_reset_state boolean := false;
BEGIN
  -- 1. Read required device key hash from temp config table (fails closed)
  SELECT value INTO v_key_hash FROM demo_input_config WHERE key = 'device_api_key_hash';
  IF v_key_hash IS NULL OR length(btrim(v_key_hash)) <> 64 OR v_key_hash !~ '^[0-9a-f]{64}$' THEN
    RAISE EXCEPTION 'A valid 64-hex SHA-256 device key hash is required in demo_input_config. Fallback or empty hashes are prohibited.' USING ERRCODE = '22023';
  END IF;

  SELECT (value = 'true') INTO v_opt_in FROM demo_input_config WHERE key = 'enable_subscription';
  IF v_opt_in IS NULL THEN v_opt_in := false; END IF;

  SELECT (value = 'true') INTO v_reset_state FROM demo_input_config WHERE key = 'reset_state';
  IF v_reset_state IS NULL THEN v_reset_state := false; END IF;

  -- 2. Non-secret identity lookup (preferring known local test accounts)
  SELECT id INTO v_admin_id FROM identity.users
  WHERE role = 'ADMIN' AND status = 'ACTIVE'
  ORDER BY (id = '01a135a0-f838-770d-97dd-a30e419f7d51'::uuid) DESC, id LIMIT 1;

  SELECT id INTO v_mgr_id FROM identity.users
  WHERE role = 'OPERATIONS_MANAGER' AND status = 'ACTIVE'
  ORDER BY (id = '01a1390e-10be-7da1-8748-48678b0065a9'::uuid) DESC, id LIMIT 1;

  SELECT id INTO v_tech_id FROM identity.users
  WHERE role = 'TECHNICIAN' AND status = 'ACTIVE'
  ORDER BY (id = '01a1390e-1174-7a25-8cd9-1edc73e2ff1f'::uuid) DESC, id LIMIT 1;

  SELECT id INTO v_seller_user_id FROM identity.users
  WHERE role = 'SELLER' AND seller_id = v_seller_id AND status = 'ACTIVE'
  ORDER BY (id = '01a1390e-1241-7103-a2fe-61229b5e2046'::uuid) DESC, id LIMIT 1;

  SELECT id INTO v_staff_id FROM identity.users
  WHERE role = 'SELLER_STAFF' AND seller_id = v_seller_id AND status = 'ACTIVE'
  ORDER BY (id = '01a1390e-1301-78c6-a05c-8a9e1d97f225'::uuid) DESC, id LIMIT 1;
  IF v_staff_id IS NULL THEN v_staff_id := v_seller_user_id; END IF;

  SELECT id INTO v_customer_id FROM identity.users
  WHERE role = 'CUSTOMER' AND status = 'ACTIVE'
  ORDER BY (id = '01a135ad-4d12-771a-bbdd-0559a5018c5e'::uuid) DESC, id LIMIT 1;

  IF v_admin_id IS NULL OR v_seller_user_id IS NULL OR v_customer_id IS NULL THEN
    RAISE EXCEPTION 'Missing fundamental active accounts in database (ADMIN, SELLER, CUSTOMER required)';
  END IF;

  -- 3. Create or update Simulated Kiosk Device (explicitly OFFLINE and no hardware)
  INSERT INTO kiosk_ops.kiosks (
    id, code, name, address, region, hardware_id, mqtt_client_id, status, last_heartbeat_at, api_key_hash, created_at, updated_at
  ) VALUES (
    v_kiosk_id, 'K-DEMO-SIM', 'Kiosk Demo (Mô phỏng - Không phần cứng)',
    'Khu Trải Nghiệm Demo (Mô phỏng)', 'HCM', 'SIM-NOHW-DEMO01', 'sim-nohw-demo01',
    'OFFLINE', NULL, v_key_hash, public.app_now(), public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    name = EXCLUDED.name,
    address = EXCLUDED.address,
    api_key_hash = EXCLUDED.api_key_hash,
    status = 'OFFLINE',
    last_heartbeat_at = NULL,
    updated_at = public.app_now();

  -- 4. Link Operations Manager to Kiosk (strictly bounded to our demo kiosk only)
  IF v_mgr_id IS NOT NULL THEN
    INSERT INTO kiosk_ops.manager_kiosks (manager_id, kiosk_id, created_at)
    VALUES (v_mgr_id, v_kiosk_id, public.app_now())
    ON CONFLICT (manager_id, kiosk_id) DO NOTHING;
  END IF;

  -- 5. Artificial Products for Hoa Sai Gon (4 Active from verified manifest + 2 Drafts)
  INSERT INTO catalog.flower_products (
    id, seller_id, name, description, price, tags, shelf_life_hours, status, inventory_kind, length_cm, width_cm, height_cm, created_at, updated_at
  ) VALUES
  (
    v_prod1, v_seller_id, 'Hồng lụa dịu dàng',
    'Bó hoa hồng lụa nhân tạo màu hồng pastel phối ruy băng voan cao cấp, lưu giữ kỷ niệm bền lâu.',
    350000, '{LOVER,ANNIVERSARY,ROMANTIC,PINK,ARTIFICIAL}', 720, 'ACTIVE', 'ARTIFICIAL', 30.00, 25.00, 45.00, public.app_now(), public.app_now()
  ),
  (
    v_prod2, v_seller_id, 'Tulip vải pastel',
    'Bó tulip nhân tạo gam màu tươi sáng, tinh tế và thanh lịch phù hợp trưng bày hoặc tặng bạn bè.',
    280000, '{FRIEND,BIRTHDAY,PASTEL,ELEGANT,ARTIFICIAL}', 720, 'ACTIVE', 'ARTIFICIAL', 25.00, 20.00, 40.00, public.app_now(), public.app_now()
  ),
  (
    v_prod3, v_seller_id, 'Hướng dương lụa',
    'Bó hoa hướng dương nhân tạo rực rỡ mang năng lượng tích cực, chúc mừng thành công và may mắn.',
    320000, '{CONGRATS,FRIEND,YELLOW,WARM,ARTIFICIAL}', 720, 'ACTIVE', 'ARTIFICIAL', 30.00, 30.00, 50.00, public.app_now(), public.app_now()
  ),
  (
    v_prod4, v_seller_id, 'Bó hồng lụa mini',
    'Bó hồng lụa mini nhỏ gọn, thiết kế tinh xảo, thích hợp để bàn làm việc hoặc góc học tập.',
    180000, '{MINI,DESK,GIFT,RED,ARTIFICIAL}', 720, 'ACTIVE', 'ARTIFICIAL', 18.00, 15.00, 28.00, public.app_now(), public.app_now()
  ),
  (
    v_prod5, v_seller_id, 'Oải hương nhân tạo vintage',
    'Mẫu hoa lụa thiết kế phong cách vintage cổ điển đang soạn thảo mẫu trưng bày.',
    220000, '{PURPLE,VINTAGE,DRAFT,ARTIFICIAL}', 720, 'DRAFT', 'ARTIFICIAL', 20.00, 15.00, 35.00, public.app_now(), public.app_now()
  ),
  (
    v_prod6, v_seller_id, 'Cẩm tú cầu lụa xanh',
    'Chậu cẩm tú cầu vải lụa nghệ thuật — trang trọng, lịch thiệp thích hợp tặng văn phòng.',
    390000, '{BLUE,FORMAL,ELEGANT,DRAFT,ARTIFICIAL}', 720, 'DRAFT', 'ARTIFICIAL', 35.00, 30.00, 45.00, public.app_now(), public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    name = CASE WHEN v_reset_state THEN EXCLUDED.name ELSE catalog.flower_products.name END,
    description = CASE WHEN v_reset_state THEN EXCLUDED.description ELSE catalog.flower_products.description END,
    price = CASE WHEN v_reset_state THEN EXCLUDED.price ELSE catalog.flower_products.price END,
    tags = CASE WHEN v_reset_state THEN EXCLUDED.tags ELSE catalog.flower_products.tags END,
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE catalog.flower_products.status END,
    inventory_kind = EXCLUDED.inventory_kind,
    length_cm = EXCLUDED.length_cm,
    width_cm = EXCLUDED.width_cm,
    height_cm = EXCLUDED.height_cm,
    updated_at = public.app_now();

  -- 6. Attach Verified Media Manifest to Products (consumed parameterized from demo_input_manifest)
  INSERT INTO notify.attachments (id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by, created_at)
  SELECT v_att1, 'catalog', 'flower_product', v_prod1, mf.url, mf.mime_type, mf.size_bytes, mf.sha256, 'PRODUCT', v_seller_user_id, public.app_now()
  FROM demo_input_manifest mf WHERE mf.slug = 'rose'
  ON CONFLICT (id) DO UPDATE SET
    file_url = EXCLUDED.file_url,
    mime_type = EXCLUDED.mime_type,
    size_bytes = EXCLUDED.size_bytes,
    sha256 = EXCLUDED.sha256;

  INSERT INTO notify.attachments (id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by, created_at)
  SELECT v_att2, 'catalog', 'flower_product', v_prod2, mf.url, mf.mime_type, mf.size_bytes, mf.sha256, 'PRODUCT', v_seller_user_id, public.app_now()
  FROM demo_input_manifest mf WHERE mf.slug = 'pastel'
  ON CONFLICT (id) DO UPDATE SET
    file_url = EXCLUDED.file_url,
    mime_type = EXCLUDED.mime_type,
    size_bytes = EXCLUDED.size_bytes,
    sha256 = EXCLUDED.sha256;

  INSERT INTO notify.attachments (id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by, created_at)
  SELECT v_att3, 'catalog', 'flower_product', v_prod3, mf.url, mf.mime_type, mf.size_bytes, mf.sha256, 'PRODUCT', v_seller_user_id, public.app_now()
  FROM demo_input_manifest mf WHERE mf.slug = 'sunflower'
  ON CONFLICT (id) DO UPDATE SET
    file_url = EXCLUDED.file_url,
    mime_type = EXCLUDED.mime_type,
    size_bytes = EXCLUDED.size_bytes,
    sha256 = EXCLUDED.sha256;

  INSERT INTO notify.attachments (id, owner_service, owner_type, owner_id, file_url, mime_type, size_bytes, sha256, phase, uploaded_by, created_at)
  SELECT v_att4, 'catalog', 'flower_product', v_prod4, mf.url, mf.mime_type, mf.size_bytes, mf.sha256, 'PRODUCT', v_seller_user_id, public.app_now()
  FROM demo_input_manifest mf WHERE mf.slug = 'mini'
  ON CONFLICT (id) DO UPDATE SET
    file_url = EXCLUDED.file_url,
    mime_type = EXCLUDED.mime_type,
    size_bytes = EXCLUDED.size_bytes,
    sha256 = EXCLUDED.sha256;

  -- 7. Bouquets (Stocked artificial bouquets; trigger sets sellable_until = 'infinity')
  INSERT INTO kiosk_ops.bouquets (
    id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at, created_at, updated_at
  ) VALUES
  (v_bq1, v_prod1, v_seller_id, 'BQ-DEMO-ROSE-001', 350000, 'STOCKED', 'infinity'::timestamptz, public.app_now() - interval '2 days', public.app_now(), public.app_now()),
  (v_bq2, v_prod2, v_seller_id, 'BQ-DEMO-PASTEL-002', 280000, 'STOCKED', 'infinity'::timestamptz, public.app_now() - interval '2 days', public.app_now(), public.app_now()),
  (v_bq3, v_prod3, v_seller_id, 'BQ-DEMO-SUNFLOWER-003', 320000, 'STOCKED', 'infinity'::timestamptz, public.app_now() - interval '1 day', public.app_now(), public.app_now()),
  (v_bq4, v_prod4, v_seller_id, 'BQ-DEMO-MINI-004', 180000, 'STOCKED', 'infinity'::timestamptz, public.app_now() - interval '1 day', public.app_now(), public.app_now())
  ON CONFLICT (id) DO UPDATE SET
    price_snapshot = CASE WHEN v_reset_state THEN EXCLUDED.price_snapshot ELSE kiosk_ops.bouquets.price_snapshot END,
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE kiosk_ops.bouquets.status END,
    updated_at = public.app_now();

  -- 8. Slots in WALK_IN and PREORDER zones (relay_channels 0..7)
  -- One Seller Whole Kiosk: all 8 slots belong to Hoa Sai Gon; no FREE unassigned slots.
  -- Uses ON CONFLICT DO NOTHING by default to preserve demo progress/holds on rerun.
  INSERT INTO kiosk_ops.slots (
    id, kiosk_id, slot_code, relay_channel, status, current_seller_id, bouquet_id, created_at, updated_at
  ) VALUES
  -- WALK_IN Zone (W01 - W04): all leased to Hoa Sai Gon
  (v_slot_w1, v_kiosk_id, 'W01', 0, 'STOCKED', v_seller_id, v_bq1, public.app_now(), public.app_now()),
  (v_slot_w2, v_kiosk_id, 'W02', 1, 'STOCKED', v_seller_id, v_bq2, public.app_now(), public.app_now()),
  (v_slot_w3, v_kiosk_id, 'W03', 2, 'RENTED_EMPTY', v_seller_id, NULL, public.app_now(), public.app_now()),
  (v_slot_w4, v_kiosk_id, 'W04', 3, 'RENTED_EMPTY', v_seller_id, NULL, public.app_now(), public.app_now()),
  -- PREORDER Zone (P01 - P04): all leased to Hoa Sai Gon
  (v_slot_p1, v_kiosk_id, 'P01', 4, 'STOCKED', v_seller_id, v_bq3, public.app_now(), public.app_now()),
  (v_slot_p2, v_kiosk_id, 'P02', 5, 'FAULT', v_seller_id, NULL, public.app_now(), public.app_now()),
  (v_slot_p3, v_kiosk_id, 'P03', 6, 'RENTED_EMPTY', v_seller_id, NULL, public.app_now(), public.app_now()),
  (v_slot_p4, v_kiosk_id, 'P04', 7, 'RENTED_EMPTY', v_seller_id, NULL, public.app_now(), public.app_now())
  ON CONFLICT (id) DO UPDATE SET
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE kiosk_ops.slots.status END,
    current_seller_id = EXCLUDED.current_seller_id,
    bouquet_id = CASE WHEN v_reset_state THEN EXCLUDED.bouquet_id ELSE kiosk_ops.slots.bouquet_id END,
    updated_at = public.app_now();

  -- 9. Slot Assignments (all 8 slots assigned to Hoa Sai Gon in whole-kiosk lease)
  INSERT INTO kiosk_ops.slot_assignments (
    id, slot_id, seller_id, assigned_by, status, assigned_at
  ) VALUES
  ('de000000-0000-0000-000a-000000000001'::uuid, v_slot_w1, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000002'::uuid, v_slot_w2, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000003'::uuid, v_slot_w3, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000004'::uuid, v_slot_w4, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000005'::uuid, v_slot_p1, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000006'::uuid, v_slot_p2, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000007'::uuid, v_slot_p3, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days'),
  ('de000000-0000-0000-000a-000000000008'::uuid, v_slot_p4, v_seller_id, v_admin_id, 'ACTIVE', public.app_now() - interval '5 days')
  ON CONFLICT (id) DO NOTHING;

  -- 10. Disputes / Incidents (Representative Hardware Incidents)
  INSERT INTO ordering.disputes (
    id, kind, kiosk_id, slot_id, reason, reported_by, status, decision, refund_amount, decided_by, resolved_at, created_at, updated_at
  ) VALUES
  (
    v_disp1, 'DEVICE_FAULT', v_kiosk_id, v_slot_p2,
    'Khóa nam châm hộc P02 không đóng khớp, cảm biến báo hở liên tục.',
    v_mgr_id, 'OPEN', NULL, NULL, NULL, NULL, public.app_now() - interval '18 hours', public.app_now()
  ),
  (
    v_disp2, 'DEVICE_FAULT', v_kiosk_id, v_slot_w2,
    'Đèn LED chiếu sáng buồng hộc W02 chập chờn khi mở cửa.',
    v_tech_id, 'OPEN', NULL, NULL, NULL, NULL, public.app_now() - interval '10 hours', public.app_now()
  ),
  (
    v_disp3, 'DEVICE_FAULT', v_kiosk_id, v_slot_w1,
    'Cảm biến hành trình hộc W01 bị lệch 2mm sau khi nạp hàng.',
    v_mgr_id, 'RESOLVED_FIXED', 'Kỹ thuật viên đã cân chỉnh lại ốc định vị và test chu trình mở đóng 5 lần đạt chuẩn.',
    NULL, v_mgr_id, public.app_now() - interval '6 hours', public.app_now() - interval '2 days', public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE ordering.disputes.status END,
    reason = CASE WHEN v_reset_state THEN EXCLUDED.reason ELSE ordering.disputes.reason END,
    decision = CASE WHEN v_reset_state THEN EXCLUDED.decision ELSE ordering.disputes.decision END,
    updated_at = public.app_now();

  -- 11. Staff Tasks (Incident Tasks for Technician & Delivery Tasks for Seller Staff)
  IF v_tech_id IS NOT NULL THEN
    INSERT INTO kiosk_ops.staff_tasks (
      id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions, status, report, version, created_at, updated_at
    ) VALUES
    (
      v_task1, 'INCIDENT', v_tech_id, coalesce(v_mgr_id, v_admin_id), v_kiosk_id, NULL, v_disp1,
      'Kiểm tra chốt khóa điện từ và tiếp điểm hộc P02 tại Kiosk Demo.',
      'IN_PROGRESS', NULL, 2, public.app_now() - interval '18 hours', public.app_now()
    ),
    (
      v_task2, 'INCIDENT', v_tech_id, coalesce(v_mgr_id, v_admin_id), v_kiosk_id, NULL, v_disp2,
      'Kiểm tra nguồn dây cấp LED chiếu sáng cho hộc W02.',
      'ASSIGNED', NULL, 1, public.app_now() - interval '10 hours', public.app_now()
    )
    ON CONFLICT (id) DO UPDATE SET
      status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE kiosk_ops.staff_tasks.status END,
      instructions = CASE WHEN v_reset_state THEN EXCLUDED.instructions ELSE kiosk_ops.staff_tasks.instructions END,
      updated_at = public.app_now();
  END IF;

  INSERT INTO kiosk_ops.staff_tasks (
    id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions, status, report, version, created_at, updated_at
  ) VALUES
  (
    v_task3, 'DELIVERY', v_staff_id, v_seller_user_id, v_kiosk_id, v_seller_id, NULL,
    'Chuẩn bị 2 bó hoa hồng lụa cho các ô trống W03 và W04 tại Kiosk Demo; chỉ nạp khi thiết bị sẵn sàng.',
    'ASSIGNED', NULL, 1, public.app_now() - interval '1 day', public.app_now()
  ),
  (
    v_task4, 'DELIVERY', v_staff_id, v_seller_user_id, v_kiosk_id, v_seller_id, NULL,
    'Kiểm tra vệ sinh mặt kính và chuẩn bị ô P01 cho đơn đặt trước.',
    'COMPLETED', 'Đã lau sạch kính bảo vệ và kiểm tra nhiệt độ buồng tủ bình thường.',
    3, public.app_now() - interval '2 days', public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE kiosk_ops.staff_tasks.status END,
    instructions = CASE WHEN v_reset_state THEN EXCLUDED.instructions ELSE kiosk_ops.staff_tasks.instructions END,
    report = CASE WHEN v_reset_state THEN EXCLUDED.report ELSE kiosk_ops.staff_tasks.report END,
    updated_at = public.app_now();

  -- 12. Representative Order (Awaiting Payment, truthful: no forged settled money or false pickup)
  INSERT INTO ordering.orders (
    id, order_code, checkout_id, customer_id, seller_id, kiosk_id, status, subtotal, discount_amount,
    total_amount, points_redeemed, points_earned, tracking_token, ecard_content, created_at, updated_at
  ) VALUES (
    v_order1, 'FBDEMO-000001', v_chk1, v_customer_id, v_seller_id, v_kiosk_id,
    'AWAITING_PAYMENT', 180000, 0, 180000, 0, 0,
    'DEMOTOK1', 'Chúc bạn một ngày tràn ngập niềm vui và thành công!',
    public.app_now() - interval '4 hours', public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    status = CASE WHEN v_reset_state THEN EXCLUDED.status ELSE ordering.orders.status END,
    updated_at = public.app_now();

  INSERT INTO ordering.order_items (
    id, order_id, item_type, bouquet_id, accessory_id, slot_id, name_snapshot, quantity, unit_price, line_total, line_status, created_at
  ) VALUES (
    v_order_item1, v_order1, 'BOUQUET', v_bq4, NULL, v_slot_p1,
    'Bó hồng lụa mini', 1, 180000, 180000, 'ACTIVE', public.app_now() - interval '4 hours'
  )
  ON CONFLICT (id) DO NOTHING;

  -- 13. Web Requests / Preorders
  INSERT INTO ordering.web_requests (
    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, quoted_price, quote_note, quote_expires_at, state, created_at, updated_at
  ) VALUES
  (
    v_req1, v_customer_id, v_seller_id, v_prod1, v_kiosk_id, 'CUSTOM',
    'Bó hoa hồng lụa kèm ruy băng đỏ thắt nơ đôi, viết thiệp mừng tốt nghiệp.',
    public.app_now() + interval '2 days', public.app_now() + interval '2 days 2 hours',
    380000, 'Shop nhận làm hoa hồng lụa tone đỏ rực rỡ, kèm thiệp in ép nhũ cao cấp.',
    public.app_now() + interval '1 day', 'QUOTED',
    public.app_now() - interval '6 hours', public.app_now()
  ),
  (
    v_req2, v_customer_id, v_seller_id, v_prod2, v_kiosk_id, 'CUSTOM',
    'Bó tulip pastel yêu cầu giấy gói trắng mờ kiểu Hàn Quốc, nhận tại ô P01.',
    public.app_now() + interval '3 days', public.app_now() + interval '3 days 2 hours',
    NULL, NULL, NULL, 'REQUESTED',
    public.app_now() - interval '2 hours', public.app_now()
  )
  ON CONFLICT (id) DO UPDATE SET
    state = CASE WHEN v_reset_state THEN EXCLUDED.state ELSE ordering.web_requests.state END,
    quoted_price = CASE WHEN v_reset_state THEN EXCLUDED.quoted_price ELSE ordering.web_requests.quoted_price END,
    quote_note = CASE WHEN v_reset_state THEN EXCLUDED.quote_note ELSE ordering.web_requests.quote_note END,
    updated_at = public.app_now();

  -- 14. Optional Test Subscription & Lease Opt-in
  -- Creates PENDING_PAYMENT subscription (truthful: does NOT pretend payment verified)
  IF v_opt_in THEN
    INSERT INTO identity.subscriptions (
      id, seller_id, package_id, period_from, period_to, price, status, payment_id, created_at, updated_at
    ) VALUES (
      v_sub1, v_seller_id, '20000000-0000-0000-0000-00000000000a'::uuid,
      CURRENT_DATE, CURRENT_DATE + interval '30 days', 400000, 'PENDING_PAYMENT', NULL, public.app_now(), public.app_now()
    )
    ON CONFLICT (id) DO UPDATE SET
      period_to = EXCLUDED.period_to,
      status = EXCLUDED.status,
      updated_at = public.app_now();

    -- Pending payment never grants entitlement or changes the existing seller.
  END IF;

  RAISE NOTICE 'Demo seed completed successfully in bounded namespace de000000-*. Opt-in subscription: %', v_opt_in;
END $$;
