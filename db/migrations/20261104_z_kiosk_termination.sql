-- Migration 20261104_z_kiosk_termination.sql
-- Kiosk Termination workflow: requests table, idempotency, strict global lock order,
-- actor audit, robust new commerce guards with trigger row-locking, and fail-closed termination.
BEGIN;

-- 1. Create kiosk_ops.kiosk_terminations table
CREATE TABLE IF NOT EXISTS kiosk_ops.kiosk_terminations (
  id                  uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  kiosk_id            uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  seller_id           uuid NOT NULL REFERENCES identity.sellers(id),
  requested_by        uuid NOT NULL REFERENCES identity.users(id),
  reason              text NOT NULL CHECK (length(btrim(reason)) BETWEEN 10 AND 2000),
  status              text NOT NULL DEFAULT 'REQUESTED' CHECK (status IN ('REQUESTED', 'ACCEPTED', 'REJECTED', 'COMPLETED', 'CANCELLED')),
  version             integer NOT NULL DEFAULT 1,
  requested_at        timestamptz NOT NULL DEFAULT public.app_now(),
  review_action       text CHECK (review_action IS NULL OR review_action IN ('ACCEPT', 'REJECT')),
  review_reason       text CHECK (review_reason IS NULL OR length(btrim(review_reason)) BETWEEN 10 AND 2000),
  reviewed_by         uuid REFERENCES identity.users(id),
  reviewed_at         timestamptz,
  correlated_task_id  uuid REFERENCES kiosk_ops.staff_tasks(id),
  completed_by        uuid REFERENCES identity.users(id),
  completed_at        timestamptz,
  completion_notes    text,
  created_at          timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at          timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE INDEX IF NOT EXISTS ix_kiosk_terminations_kiosk_status
  ON kiosk_ops.kiosk_terminations (kiosk_id, status);

CREATE INDEX IF NOT EXISTS ix_kiosk_terminations_seller
  ON kiosk_ops.kiosk_terminations (seller_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_kiosk_terminations_status
  ON kiosk_ops.kiosk_terminations (status, created_at DESC);

-- Ensure only one in-flight termination request per kiosk
CREATE UNIQUE INDEX IF NOT EXISTS uq_kiosk_termination_inflight
  ON kiosk_ops.kiosk_terminations (kiosk_id)
  WHERE status IN ('REQUESTED', 'ACCEPTED');

-- 2. Predicate: check whether a kiosk has an accepted termination
CREATE OR REPLACE FUNCTION flow.is_kiosk_terminating(p_kiosk uuid)
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_terminations
    WHERE kiosk_id = p_kiosk AND status = 'ACCEPTED'
  );
$$;

-- 3. Stored function: request kiosk termination (Seller initiated, records intent only)
CREATE OR REPLACE FUNCTION flow.request_kiosk_termination(
  p_kiosk uuid,
  p_seller uuid,
  p_actor uuid,
  p_reason text
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_id uuid;
  v_actor_role text;
  v_actor_status text;
  v_actor_seller uuid;
  v_seller_status text;
  v_kiosk_seller uuid;
  v_existing_id uuid;
  v_existing_status text;
  v_existing_reason text;
BEGIN
  -- Consistent global lock order: Kiosk -> Seller
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = p_seller FOR UPDATE;

  -- Validate actor role, status, and tenant match
  SELECT role, status, seller_id INTO v_actor_role, v_actor_status, v_actor_seller
  FROM identity.users WHERE id = p_actor FOR SHARE;
  IF NOT FOUND OR v_actor_status <> 'ACTIVE' OR v_actor_role <> 'SELLER' OR v_actor_seller IS DISTINCT FROM p_seller THEN
    RAISE EXCEPTION 'Chỉ tài khoản Seller đang hoạt động của shop mới được yêu cầu chấm dứt' USING ERRCODE = '42501';
  END IF;

  -- Validate seller status
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = p_seller FOR SHARE;
  IF NOT FOUND OR v_seller_status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Hồ sơ Seller không tồn tại hoặc đã bị khóa' USING ERRCODE = '22023';
  END IF;

  -- Validate kiosk ownership
  SELECT seller_id INTO v_kiosk_seller FROM kiosk_ops.kiosks WHERE id = p_kiosk FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Kiosk không tồn tại' USING ERRCODE = 'P0002';
  END IF;
  IF v_kiosk_seller IS DISTINCT FROM p_seller THEN
    RAISE EXCEPTION 'Kiosk không thuộc quyền sở hữu của Seller này' USING ERRCODE = '42501';
  END IF;

  -- Validate reason length
  IF p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Lý do chấm dứt phải từ 10 đến 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  -- Check concurrent duplicate or in-flight request
  SELECT id, status, reason INTO v_existing_id, v_existing_status, v_existing_reason
  FROM kiosk_ops.kiosk_terminations
  WHERE kiosk_id = p_kiosk AND status IN ('REQUESTED', 'ACCEPTED')
  FOR SHARE;
  IF FOUND THEN
    IF v_existing_status = 'REQUESTED' AND v_existing_reason = btrim(p_reason) THEN
      RETURN v_existing_id;
    END IF;
    RAISE EXCEPTION 'Kiosk đã có yêu cầu chấm dứt đang xử lý' USING ERRCODE = '40001';
  END IF;

  v_id := public.uuid_v7();
  INSERT INTO kiosk_ops.kiosk_terminations (
    id, kiosk_id, seller_id, requested_by, reason, status, version, requested_at, created_at, updated_at
  ) VALUES (
    v_id, p_kiosk, p_seller, p_actor, btrim(p_reason), 'REQUESTED', 1, v_now, v_now, v_now
  );

  PERFORM flow.audit(p_actor, 'KIOSK_TERMINATION_REQUESTED', 'kiosk_terminations', v_id,
    jsonb_build_object('kiosk_id', p_kiosk, 'seller_id', p_seller, 'reason', btrim(p_reason)));

  RETURN v_id;
END $$;

-- 4. Stored function: Operations Manager reviews termination request
CREATE OR REPLACE FUNCTION flow.review_kiosk_termination(
  p_id uuid,
  p_manager uuid,
  p_action text,
  p_reason text
) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_role text;
  v_status text;
  v_kiosk_id uuid;
  v_seller_id uuid;
  rec kiosk_ops.kiosk_terminations%ROWTYPE;
  v_task_id uuid;
BEGIN
  -- Validate manager active role
  SELECT role, status INTO v_role, v_status FROM identity.users WHERE id = p_manager FOR SHARE;
  IF NOT FOUND OR v_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'Chỉ Operations Manager đang hoạt động mới được duyệt yêu cầu' USING ERRCODE = '42501';
  END IF;

  IF p_action IS NULL OR p_action NOT IN ('ACCEPT', 'REJECT') THEN
    RAISE EXCEPTION 'Hành động duyệt không hợp lệ (chỉ chấp nhận ACCEPT hoặc REJECT)' USING ERRCODE = '22023';
  END IF;

  IF p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Lý do duyệt phải từ 10 đến 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  -- 1. Initial nonlocking lookup to discover kiosk_id and seller_id
  SELECT kiosk_id, seller_id INTO v_kiosk_id, v_seller_id
  FROM kiosk_ops.kiosk_terminations WHERE id = p_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Yêu cầu chấm dứt không tồn tại' USING ERRCODE = 'P0002';
  END IF;

  -- 2. Consistent lock order: Kiosk -> Seller -> Request
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = v_kiosk_id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = v_seller_id FOR UPDATE;
  SELECT * INTO rec FROM kiosk_ops.kiosk_terminations WHERE id = p_id FOR UPDATE;

  -- Recheck that relations did not mutate
  IF rec.kiosk_id IS DISTINCT FROM v_kiosk_id OR rec.seller_id IS DISTINCT FROM v_seller_id THEN
    RAISE EXCEPTION 'Xung đột thông tin hồ sơ' USING ERRCODE = '40001';
  END IF;

  -- Idempotency check: if already reviewed with same action and reason
  IF rec.status = (CASE WHEN p_action = 'ACCEPT' THEN 'ACCEPTED' ELSE 'REJECTED' END)
     AND rec.review_reason = btrim(p_reason) THEN
    RETURN rec.status;
  END IF;

  IF rec.status <> 'REQUESTED' THEN
    RAISE EXCEPTION 'Yêu cầu không ở trạng thái chờ duyệt (status: %)', rec.status USING ERRCODE = '40001';
  END IF;

  IF p_action = 'REJECT' THEN
    UPDATE kiosk_ops.kiosk_terminations
    SET status = 'REJECTED',
        review_action = 'REJECT',
        review_reason = btrim(p_reason),
        reviewed_by = p_manager,
        reviewed_at = v_now,
        version = version + 1,
        updated_at = v_now
    WHERE id = p_id;

    PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_REJECTED', 'kiosk_terminations', p_id,
      jsonb_build_object('kiosk_id', rec.kiosk_id, 'reason', btrim(p_reason)));

    RETURN 'REJECTED';
  END IF;

  -- Action is ACCEPT: correlate existing pickup/incident task if any is open on this kiosk
  SELECT id INTO v_task_id FROM kiosk_ops.staff_tasks
  WHERE kiosk_id = rec.kiosk_id AND status NOT IN ('COMPLETED', 'CANCELLED')
  ORDER BY created_at DESC LIMIT 1;

  UPDATE kiosk_ops.kiosk_terminations
  SET status = 'ACCEPTED',
      review_action = 'ACCEPT',
      review_reason = btrim(p_reason),
      reviewed_by = p_manager,
      reviewed_at = v_now,
      correlated_task_id = v_task_id,
      version = version + 1,
      updated_at = v_now
  WHERE id = p_id;

  PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_ACCEPTED', 'kiosk_terminations', p_id,
    jsonb_build_object('kiosk_id', rec.kiosk_id, 'reason', btrim(p_reason), 'correlated_task_id', v_task_id));

  RETURN 'ACCEPTED';
END $$;

-- 5. Robust commerce guards: DB triggers on new orders, checkouts, preorders
CREATE OR REPLACE FUNCTION flow.trg_guard_terminating_web_request()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  -- Lock kiosk row before reading current termination state to eliminate acceptance/insert race
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = NEW.kiosk_id FOR SHARE;
  IF flow.is_kiosk_terminating(NEW.kiosk_id) THEN
    RAISE EXCEPTION 'Kiosk % đang trong quá trình chấm dứt hợp đồng, không nhận đơn đặt trước mới', NEW.kiosk_id
      USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_guard_terminating_web_request ON ordering.web_requests;
CREATE TRIGGER trg_guard_terminating_web_request
  BEFORE INSERT ON ordering.web_requests
  FOR EACH ROW EXECUTE FUNCTION flow.trg_guard_terminating_web_request();

CREATE OR REPLACE FUNCTION flow.trg_guard_terminating_order()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  -- Lock kiosk row before reading current termination state
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = NEW.kiosk_id FOR SHARE;
  -- Reject all brand-new order inserts under ACCEPTED termination
  IF flow.is_kiosk_terminating(NEW.kiosk_id) THEN
    RAISE EXCEPTION 'Kiosk % đang trong quá trình chấm dứt hợp đồng, không nhận đơn hàng mới', NEW.kiosk_id
      USING ERRCODE = 'P0001';
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_guard_terminating_order ON ordering.orders;
CREATE TRIGGER trg_guard_terminating_order
  BEFORE INSERT ON ordering.orders
  FOR EACH ROW EXECUTE FUNCTION flow.trg_guard_terminating_order();

-- 6. Guard walk-in stocking against terminating kiosks
CREATE OR REPLACE FUNCTION flow.trg_guard_terminating_stock()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_kiosk uuid;
  v_zone text;
BEGIN
  SELECT kiosk_id, zone INTO v_kiosk, v_zone FROM kiosk_ops.slots WHERE id = NEW.slot_id;
  IF v_kiosk IS NOT NULL THEN
    PERFORM 1 FROM kiosk_ops.kiosks WHERE id = v_kiosk FOR SHARE;
    IF flow.is_kiosk_terminating(v_kiosk) AND v_zone = 'WALK_IN' AND NEW.movement_type = 'STOCK_IN' THEN
      RAISE EXCEPTION 'Kiosk đang trong quá trình chấm dứt hợp đồng, không nhận nạp hàng bán sẵn'
        USING ERRCODE = 'P0001';
    END IF;
  END IF;
  RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_guard_terminating_stock ON kiosk_ops.inventory_logs;
CREATE TRIGGER trg_guard_terminating_stock
  BEFORE INSERT ON kiosk_ops.inventory_logs
  FOR EACH ROW EXECUTE FUNCTION flow.trg_guard_terminating_stock();

-- 7. Stored function: Operations Manager completes kiosk termination
CREATE OR REPLACE FUNCTION flow.complete_kiosk_termination(
  p_id uuid,
  p_manager uuid,
  p_notes text DEFAULT NULL,
  p_evidence_id uuid DEFAULT NULL
) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_role text;
  v_status text;
  v_kiosk_id uuid;
  v_seller_id uuid;
  rec kiosk_ops.kiosk_terminations%ROWTYPE;
  v_kiosk_seller uuid;
  v_evidence_count integer;
BEGIN
  -- Validate manager active role
  SELECT role, status INTO v_role, v_status FROM identity.users WHERE id = p_manager FOR SHARE;
  IF NOT FOUND OR v_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'Chỉ Operations Manager đang hoạt động mới được hoàn tất chấm dứt' USING ERRCODE = '42501';
  END IF;

  -- 1. Initial nonlocking lookup
  SELECT kiosk_id, seller_id INTO v_kiosk_id, v_seller_id
  FROM kiosk_ops.kiosk_terminations WHERE id = p_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Yêu cầu chấm dứt không tồn tại' USING ERRCODE = 'P0002';
  END IF;

  -- 2. Strict consistent global lock order: Kiosk -> Slots -> Seller -> Request
  SELECT seller_id INTO v_kiosk_seller FROM kiosk_ops.kiosks WHERE id = v_kiosk_id FOR UPDATE;
  IF v_kiosk_seller IS DISTINCT FROM v_seller_id THEN
    RAISE EXCEPTION 'Kiosk không còn thuộc về người bán ban đầu của hồ sơ chấm dứt' USING ERRCODE = '40001';
  END IF;

  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_id ORDER BY id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = v_seller_id FOR UPDATE;
  SELECT * INTO rec FROM kiosk_ops.kiosk_terminations WHERE id = p_id FOR UPDATE;

  -- Duplicate complete idempotent: recheck actor and return without consuming new evidence
  IF rec.status = 'COMPLETED' THEN
    RETURN 'COMPLETED';
  END IF;

  IF rec.status <> 'ACCEPTED' THEN
    RAISE EXCEPTION 'Chỉ hoàn tất hồ sơ đã được chấp thuận (ACCEPTED), trạng thái hiện tại: %', rec.status USING ERRCODE = '40001';
  END IF;

  -- 1. Evidence check: must have verified private evidence attached by authorized Operations Manager
  -- or from a completed inspection task on the same kiosk
  SELECT count(*) INTO v_evidence_count
  FROM notify.attachments a
  JOIN identity.users u ON u.id = a.uploaded_by
  WHERE a.owner_service = 'kiosk_ops' AND a.owner_type = 'kiosk_termination'
    AND a.owner_id = p_id AND a.phase = 'EVIDENCE'
    AND a.sha256 IS NOT NULL AND length(a.sha256) > 0
    AND a.size_bytes > 0
    AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
    AND u.role = 'OPERATIONS_MANAGER' AND u.status = 'ACTIVE';

  IF v_evidence_count = 0 AND p_evidence_id IS NOT NULL THEN
    SELECT count(*) INTO v_evidence_count
    FROM notify.attachments a
    JOIN identity.users u ON u.id = a.uploaded_by
    WHERE a.id = p_evidence_id AND a.owner_service = 'kiosk_ops' AND a.owner_type = 'kiosk_termination'
      AND a.phase = 'EVIDENCE'
      AND a.sha256 IS NOT NULL AND length(a.sha256) > 0
      AND a.size_bytes > 0
      AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
      AND u.role = 'OPERATIONS_MANAGER' AND u.status = 'ACTIVE';
  END IF;

  IF v_evidence_count = 0 THEN
    SELECT count(*) INTO v_evidence_count
    FROM notify.attachments a
    JOIN kiosk_ops.staff_tasks st ON st.id = a.owner_id
    WHERE a.owner_service = 'kiosk_ops' AND a.owner_type = 'staff_task'
      AND st.kiosk_id = rec.kiosk_id AND st.status = 'COMPLETED'
      AND a.phase = 'EVIDENCE'
      AND a.sha256 IS NOT NULL AND length(a.sha256) > 0;
  END IF;

  IF v_evidence_count = 0 THEN
    RAISE EXCEPTION 'Yêu cầu minh chứng kiểm tra/bàn giao thực tế hợp lệ trước khi hoàn tất' USING ERRCODE = '40001';
  END IF;

  -- 2. Stock and slot ambiguity: no stocked bouquets, no holds, no locked slots
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = rec.kiosk_id
      AND (bouquet_id IS NOT NULL OR status NOT IN ('FREE', 'RENTED_EMPTY')
        OR hold_until IS NOT NULL OR hold_order_id IS NOT NULL)
  ) THEN
    RAISE EXCEPTION 'Vẫn còn hoa tồn hoặc ô đang bị giữ tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 3. Accessory inventory: must have zero stock
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.accessories
    WHERE kiosk_id = rec.kiosk_id AND stock_quantity > 0
  ) THEN
    RAISE EXCEPTION 'Vẫn còn phụ kiện tồn kho tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 4. Open incidents or disputes
  IF EXISTS (
    SELECT 1 FROM ordering.disputes
    WHERE kiosk_id = rec.kiosk_id AND status = 'OPEN'
  ) THEN
    RAISE EXCEPTION 'Vẫn còn sự cố hoặc khiếu nại chưa giải quyết tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 5. Open staff tasks
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.staff_tasks
    WHERE kiosk_id = rec.kiosk_id AND status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED')
  ) THEN
    RAISE EXCEPTION 'Vẫn còn công việc nhân viên chưa kết thúc tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 6. Pending orders
  IF EXISTS (
    SELECT 1 FROM ordering.orders
    WHERE kiosk_id = rec.kiosk_id AND status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING')
  ) THEN
    RAISE EXCEPTION 'Vẫn còn đơn hàng chưa kết thúc hoặc đang nhận tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 7. Pending preorder requests
  IF EXISTS (
    SELECT 1 FROM ordering.web_requests
    WHERE kiosk_id = rec.kiosk_id AND state IN ('REQUESTED', 'QUOTED', 'ORDERED')
  ) THEN
    RAISE EXCEPTION 'Vẫn còn đơn đặt trước chưa hoàn tất tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 8. Pending payments
  IF EXISTS (
    SELECT 1 FROM payment.payments p
    JOIN ordering.orders o ON o.checkout_id = p.checkout_id
    WHERE o.kiosk_id = rec.kiosk_id AND p.status = 'PENDING'
  ) THEN
    RAISE EXCEPTION 'Vẫn còn thanh toán đang chờ xử lý tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- 9. Unclosed device commands
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.unlock_tokens
    WHERE kiosk_id = rec.kiosk_id AND status IN ('ISSUED', 'SENT', 'ACKED', 'OPENED')
  ) THEN
    RAISE EXCEPTION 'Vẫn còn lệnh điều khiển thiết bị chưa đóng tại kiosk' USING ERRCODE = '40001';
  END IF;

  -- All verified: finalize termination
  UPDATE kiosk_ops.kiosk_terminations
  SET status = 'COMPLETED',
      completed_by = p_manager,
      completed_at = v_now,
      completion_notes = btrim(p_notes),
      version = version + 1,
      updated_at = v_now
  WHERE id = p_id;

  -- Expire active lease subscription on this kiosk for this seller
  UPDATE identity.subscriptions
  SET status = 'EXPIRED', updated_at = v_now
  WHERE kiosk_id = rec.kiosk_id AND seller_id = rec.seller_id AND status = 'ACTIVE';

  -- Release slot assignments for this seller and reset slots to FREE
  UPDATE kiosk_ops.slot_assignments
  SET status = 'RELEASED', released_at = v_now, release_reason = 'TERMINATION'
  WHERE slot_id IN (SELECT id FROM kiosk_ops.slots WHERE kiosk_id = rec.kiosk_id)
    AND seller_id = rec.seller_id
    AND status = 'ACTIVE';

  UPDATE kiosk_ops.slots
  SET current_seller_id = NULL, status = 'FREE', row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = rec.kiosk_id AND current_seller_id = rec.seller_id;

  -- Unbind seller from physical kiosk without auto-disposing hardware
  UPDATE kiosk_ops.kiosks
  SET seller_id = NULL, updated_at = v_now
  WHERE id = rec.kiosk_id AND seller_id = rec.seller_id;

  PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_COMPLETED', 'kiosk_terminations', p_id,
    jsonb_build_object('kiosk_id', rec.kiosk_id, 'seller_id', rec.seller_id));

  RETURN 'COMPLETED';
END $$;

COMMIT;
