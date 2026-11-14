BEGIN;

-- A kiosk termination has a dedicated technician task. The kiosk connectivity
-- state remains ONLINE/OFFLINE; dismantling is represented by task/request state.
ALTER TABLE kiosk_ops.staff_tasks
  DROP CONSTRAINT IF EXISTS staff_tasks_kind_check,
  DROP CONSTRAINT IF EXISTS staff_tasks_check,
  DROP CONSTRAINT IF EXISTS staff_tasks_scope_check;

ALTER TABLE kiosk_ops.staff_tasks
  ADD CONSTRAINT staff_tasks_kind_check
    CHECK (kind IN ('INCIDENT', 'DELIVERY', 'KIOSK_REMOVAL')),
  ADD CONSTRAINT staff_tasks_scope_check
    CHECK (
      (kind = 'INCIDENT' AND incident_id IS NOT NULL AND seller_id IS NULL)
      OR (kind = 'DELIVERY' AND seller_id IS NOT NULL AND incident_id IS NULL)
      OR (kind = 'KIOSK_REMOVAL' AND seller_id IS NULL AND incident_id IS NULL)
    );

-- Older acceptance could correlate an unrelated open task on the same kiosk.
-- Keep that task and its audit history, but clear the misleading relationship.
UPDATE kiosk_ops.kiosk_terminations term
SET correlated_task_id = NULL
WHERE term.status = 'ACCEPTED'
  AND term.correlated_task_id IS NOT NULL
  AND NOT EXISTS (
    SELECT 1 FROM kiosk_ops.staff_tasks task
    WHERE task.id = term.correlated_task_id AND task.kind = 'KIOSK_REMOVAL'
  );

-- Accepting the request starts the dismantling lifecycle, but assignment is a
-- separate explicit Manager action. Do not attach an unrelated task by kiosk.
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
BEGIN
  SELECT role, status INTO v_role, v_status
  FROM identity.users WHERE id = p_manager FOR SHARE;
  IF NOT FOUND OR v_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'Chỉ Operations Manager đang hoạt động mới được duyệt yêu cầu' USING ERRCODE = '42501';
  END IF;
  IF p_action IS NULL OR p_action NOT IN ('ACCEPT', 'REJECT') THEN
    RAISE EXCEPTION 'Hành động duyệt không hợp lệ (chỉ chấp nhận ACCEPT hoặc REJECT)' USING ERRCODE = '22023';
  END IF;
  IF p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Lý do duyệt phải từ 10 đến 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  SELECT kiosk_id, seller_id INTO v_kiosk_id, v_seller_id
  FROM kiosk_ops.kiosk_terminations WHERE id = p_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Yêu cầu chấm dứt không tồn tại' USING ERRCODE = 'P0002';
  END IF;

  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = v_kiosk_id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = v_seller_id FOR UPDATE;
  SELECT * INTO rec FROM kiosk_ops.kiosk_terminations WHERE id = p_id FOR UPDATE;
  IF rec.kiosk_id IS DISTINCT FROM v_kiosk_id OR rec.seller_id IS DISTINCT FROM v_seller_id THEN
    RAISE EXCEPTION 'Xung đột thông tin hồ sơ' USING ERRCODE = '40001';
  END IF;

  IF rec.status = (CASE WHEN p_action = 'ACCEPT' THEN 'ACCEPTED' ELSE 'REJECTED' END)
     AND rec.review_reason = btrim(p_reason) THEN
    RETURN rec.status;
  END IF;
  IF rec.status <> 'REQUESTED' THEN
    RAISE EXCEPTION 'Yêu cầu không ở trạng thái chờ duyệt (status: %)', rec.status USING ERRCODE = '40001';
  END IF;

  IF p_action = 'REJECT' THEN
    UPDATE kiosk_ops.kiosk_terminations
    SET status = 'REJECTED', review_action = 'REJECT', review_reason = btrim(p_reason),
        reviewed_by = p_manager, reviewed_at = v_now, version = version + 1, updated_at = v_now
    WHERE id = p_id;
    PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_REJECTED', 'kiosk_terminations', p_id,
      jsonb_build_object('kiosk_id', rec.kiosk_id, 'reason', btrim(p_reason)));
    RETURN 'REJECTED';
  END IF;

  UPDATE kiosk_ops.kiosk_terminations
  SET status = 'ACCEPTED', review_action = 'ACCEPT', review_reason = btrim(p_reason),
      reviewed_by = p_manager, reviewed_at = v_now, correlated_task_id = NULL,
      version = version + 1, updated_at = v_now
  WHERE id = p_id;
  PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_ACCEPTED', 'kiosk_terminations', p_id,
    jsonb_build_object('kiosk_id', rec.kiosk_id, 'reason', btrim(p_reason), 'correlated_task_id', NULL));
  RETURN 'ACCEPTED';
END $$;

CREATE OR REPLACE FUNCTION flow.assign_kiosk_removal_task(
  p_termination_id uuid,
  p_manager uuid,
  p_staff uuid,
  p_task_id uuid,
  p_instructions text
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := public.app_now();
  v_role text;
  v_user_status text;
  v_kiosk_id uuid;
  v_seller_id uuid;
  v_kiosk_seller uuid;
  v_task kiosk_ops.staff_tasks%ROWTYPE;
  rec kiosk_ops.kiosk_terminations%ROWTYPE;
BEGIN
  SELECT role, status INTO v_role, v_user_status
  FROM identity.users WHERE id = p_manager FOR SHARE;
  IF NOT FOUND OR v_user_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'Chỉ Operations Manager đang hoạt động mới được phân công tháo dỡ' USING ERRCODE = '42501';
  END IF;
  IF p_task_id IS NULL OR p_staff IS NULL OR p_instructions IS NULL
     OR length(btrim(p_instructions)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Thông tin công việc tháo dỡ không hợp lệ' USING ERRCODE = '22023';
  END IF;

  SELECT kiosk_id, seller_id INTO v_kiosk_id, v_seller_id
  FROM kiosk_ops.kiosk_terminations WHERE id = p_termination_id;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Yêu cầu chấm dứt không tồn tại' USING ERRCODE = 'P0002';
  END IF;

  SELECT seller_id INTO v_kiosk_seller
  FROM kiosk_ops.kiosks WHERE id = v_kiosk_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk không tồn tại' USING ERRCODE = 'P0002'; END IF;
  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_id ORDER BY id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = v_seller_id FOR UPDATE;
  SELECT * INTO rec FROM kiosk_ops.kiosk_terminations WHERE id = p_termination_id FOR UPDATE;

  IF rec.status <> 'ACCEPTED' THEN
    RAISE EXCEPTION 'Chỉ phân công được hồ sơ đã được Manager chấp thuận' USING ERRCODE = '40001';
  END IF;
  IF v_kiosk_seller IS DISTINCT FROM v_seller_id THEN
    RAISE EXCEPTION 'Kiosk không còn gắn với Seller trong hồ sơ chấm dứt' USING ERRCODE = '40001';
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.manager_kiosks
    WHERE manager_id = p_manager AND kiosk_id = v_kiosk_id
  ) THEN
    RAISE EXCEPTION 'Manager chưa được phân công phụ trách kiosk này' USING ERRCODE = '42501';
  END IF;

  IF rec.correlated_task_id IS NOT NULL THEN
    SELECT * INTO v_task FROM kiosk_ops.staff_tasks
    WHERE id = rec.correlated_task_id FOR UPDATE;
    IF FOUND AND v_task.kind = 'KIOSK_REMOVAL'
       AND v_task.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED') THEN
      IF v_task.assignee_id = p_staff AND v_task.instructions = btrim(p_instructions) THEN
        RETURN v_task.id;
      END IF;
      RAISE EXCEPTION 'Đã có công việc tháo dỡ đang mở; hãy tải lại hồ sơ trước khi phân công lại' USING ERRCODE = '40001';
    END IF;
  END IF;

  PERFORM 1 FROM identity.users
  WHERE id = p_staff AND role = 'TECHNICIAN' AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Chỉ kỹ thuật viên đang hoạt động mới nhận công việc tháo dỡ kiosk' USING ERRCODE = '22023';
  END IF;
  PERFORM 1 FROM kiosk_ops.kiosks
  WHERE id = v_kiosk_id AND status <> 'DISABLED' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk không khả dụng' USING ERRCODE = '22023'; END IF;

  INSERT INTO kiosk_ops.staff_tasks(
    id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions
  ) VALUES (
    p_task_id, 'KIOSK_REMOVAL', p_staff, p_manager, v_kiosk_id, NULL, NULL, btrim(p_instructions)
  );
  UPDATE kiosk_ops.kiosk_terminations
  SET correlated_task_id = p_task_id, version = version + 1, updated_at = v_now
  WHERE id = p_termination_id;
  PERFORM flow.audit(p_manager, 'KIOSK_REMOVAL_TASK_ASSIGNED', 'staff_tasks', p_task_id,
    jsonb_build_object('termination_id', p_termination_id, 'kiosk_id', v_kiosk_id, 'assignee', p_staff));
  RETURN p_task_id;
END $$;

-- Technicians can perform removal tasks through the existing staff task APIs.
-- The Manager must use complete_kiosk_termination to mark such work complete.
CREATE OR REPLACE FUNCTION flow.transition_staff_task(
  p_id uuid,
  p_actor uuid,
  p_version integer,
  p_status text,
  p_report text
) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  v_actor_role text;
  v_actor_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER', 'TECHNICIAN', 'SELLER_STAFF') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002'; END IF;

  IF v_actor_role = 'TECHNICIAN' THEN
    IF t.assignee_id <> p_actor OR t.kind NOT IN ('INCIDENT', 'KIOSK_REMOVAL') THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER_STAFF' THEN
    IF t.assignee_id <> p_actor OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'OPERATIONS_MANAGER' THEN
    IF t.kind NOT IN ('INCIDENT', 'KIOSK_REMOVAL')
       OR NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = t.kiosk_id) THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER' THEN
    IF t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  END IF;

  IF p_version IS DISTINCT FROM t.version THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE = '40001';
  END IF;
  IF p_status = 'COMPLETED' AND t.kind = 'KIOSK_REMOVAL' AND EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_terminations WHERE correlated_task_id = t.id AND status = 'ACCEPTED'
  ) THEN
    RAISE EXCEPTION 'Manager must confirm completion through the kiosk termination request' USING ERRCODE = '22023';
  END IF;

  IF p_status IS NULL OR NOT (
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'ASSIGNED' AND p_status = 'IN_PROGRESS') OR
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'IN_PROGRESS' AND p_status = 'SUBMITTED') OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status = 'SUBMITTED' AND p_status IN ('COMPLETED', 'IN_PROGRESS')) OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED') AND p_status = 'CANCELLED')
  ) THEN
    RAISE EXCEPTION 'Invalid task transition' USING ERRCODE = '22023';
  END IF;
  IF p_report IS NOT NULL AND btrim(p_report) <> ''
     AND (length(btrim(p_report)) < 10 OR length(btrim(p_report)) > 2000) THEN
    RAISE EXCEPTION 'Provide a report of 10 to 2000 characters' USING ERRCODE = '22023';
  END IF;
  IF p_report IS NULL OR btrim(p_report) = '' THEN
    IF p_status <> 'SUBMITTED' OR t.kind <> 'KIOSK_REMOVAL' OR NOT EXISTS (
      SELECT 1 FROM notify.attachments a
      WHERE a.owner_service = 'kiosk_ops' AND a.owner_type = 'staff_task'
        AND a.owner_id = t.id AND a.phase = 'EVIDENCE'
        AND a.sha256 IS NOT NULL AND length(a.sha256) > 0 AND a.size_bytes > 0
        AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
    ) THEN
      RAISE EXCEPTION 'Provide a dismantling report or attach a valid photo before submitting' USING ERRCODE = '22023';
    END IF;
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET status = p_status, report = nullif(btrim(p_report), ''), version = version + 1, updated_at = public.app_now()
  WHERE id = p_id;
  PERFORM flow.audit(p_actor, 'STAFF_TASK_UPDATED', 'staff_tasks', p_id,
    jsonb_build_object('from', t.status, 'to', p_status, 'report', btrim(p_report), 'version', t.version + 1));
  RETURN t.version + 1;
END $$;

-- Manager confirmation is the gate that releases the Seller lease and makes
-- the existing Manager-only soft-archive endpoint available.
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
  v_kiosk_seller uuid;
  rec kiosk_ops.kiosk_terminations%ROWTYPE;
  v_task kiosk_ops.staff_tasks%ROWTYPE;
BEGIN
  SELECT role, status INTO v_role, v_status
  FROM identity.users WHERE id = p_manager FOR SHARE;
  IF NOT FOUND OR v_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'Chỉ Operations Manager đang hoạt động mới được xác nhận hoàn tất tháo dỡ' USING ERRCODE = '42501';
  END IF;
  SELECT kiosk_id, seller_id INTO v_kiosk_id, v_seller_id
  FROM kiosk_ops.kiosk_terminations WHERE id = p_id;
  IF NOT FOUND THEN RAISE EXCEPTION 'Yêu cầu chấm dứt không tồn tại' USING ERRCODE = 'P0002'; END IF;

  SELECT seller_id INTO v_kiosk_seller FROM kiosk_ops.kiosks WHERE id = v_kiosk_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk không tồn tại' USING ERRCODE = 'P0002'; END IF;
  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_id ORDER BY id FOR UPDATE;
  PERFORM 1 FROM identity.sellers WHERE id = v_seller_id FOR UPDATE;
  SELECT * INTO rec FROM kiosk_ops.kiosk_terminations WHERE id = p_id FOR UPDATE;

  IF rec.status = 'COMPLETED' THEN RETURN 'COMPLETED'; END IF;
  IF rec.status <> 'ACCEPTED' THEN
    RAISE EXCEPTION 'Chỉ hoàn tất hồ sơ đã được chấp thuận (ACCEPTED), trạng thái hiện tại: %', rec.status USING ERRCODE = '40001';
  END IF;
  IF v_kiosk_seller IS DISTINCT FROM v_seller_id THEN
    RAISE EXCEPTION 'Kiosk không còn thuộc về người bán ban đầu của hồ sơ chấm dứt' USING ERRCODE = '40001';
  END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_manager AND kiosk_id = v_kiosk_id
  ) THEN
    RAISE EXCEPTION 'Manager chưa được phân công phụ trách kiosk này' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO v_task FROM kiosk_ops.staff_tasks
  WHERE id = rec.correlated_task_id AND kind = 'KIOSK_REMOVAL' AND kiosk_id = v_kiosk_id FOR UPDATE;
  IF NOT FOUND OR v_task.status <> 'SUBMITTED' OR NOT (
    (v_task.report IS NOT NULL AND length(btrim(v_task.report)) BETWEEN 10 AND 2000)
    OR EXISTS (
      SELECT 1 FROM notify.attachments a
      WHERE a.owner_service = 'kiosk_ops' AND a.owner_type = 'staff_task'
        AND a.owner_id = v_task.id AND a.phase = 'EVIDENCE'
        AND a.sha256 IS NOT NULL AND length(a.sha256) > 0 AND a.size_bytes > 0
        AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
    )
  ) THEN
    RAISE EXCEPTION 'Kỹ thuật viên phải gửi báo cáo hoặc ảnh tháo dỡ để Manager kiểm tra trước khi xác nhận' USING ERRCODE = '40001';
  END IF;

  -- Optional Manager evidence remains private and is validated if supplied.
  IF p_evidence_id IS NOT NULL AND NOT EXISTS (
    SELECT 1 FROM notify.attachments a
    JOIN identity.users u ON u.id = a.uploaded_by
    WHERE a.id = p_evidence_id AND a.owner_service = 'kiosk_ops' AND a.owner_type = 'kiosk_termination'
      AND a.owner_id = p_id AND a.phase = 'EVIDENCE'
      AND a.sha256 IS NOT NULL AND length(a.sha256) > 0 AND a.size_bytes > 0
      AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
      AND u.role = 'OPERATIONS_MANAGER' AND u.status = 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'Ảnh xác nhận Manager không hợp lệ hoặc chưa được đính kèm vào hồ sơ' USING ERRCODE = '22023';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = v_kiosk_id
      AND (bouquet_id IS NOT NULL OR status NOT IN ('FREE', 'RENTED_EMPTY')
        OR hold_until IS NOT NULL OR hold_order_id IS NOT NULL)
  ) THEN
    RAISE EXCEPTION 'Vẫn còn hoa tồn hoặc ô đang bị giữ tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (SELECT 1 FROM kiosk_ops.accessories WHERE kiosk_id = v_kiosk_id AND stock_quantity > 0) THEN
    RAISE EXCEPTION 'Vẫn còn phụ kiện tồn kho tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (SELECT 1 FROM ordering.disputes WHERE kiosk_id = v_kiosk_id AND status = 'OPEN') THEN
    RAISE EXCEPTION 'Vẫn còn sự cố hoặc khiếu nại chưa giải quyết tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.staff_tasks
    WHERE kiosk_id = v_kiosk_id AND status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED') AND id <> v_task.id
  ) THEN
    RAISE EXCEPTION 'Vẫn còn công việc nhân viên khác chưa kết thúc tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (SELECT 1 FROM ordering.orders WHERE kiosk_id = v_kiosk_id AND status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING')) THEN
    RAISE EXCEPTION 'Vẫn còn đơn hàng chưa kết thúc hoặc đang nhận tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (SELECT 1 FROM ordering.web_requests WHERE kiosk_id = v_kiosk_id AND state IN ('REQUESTED', 'QUOTED', 'ORDERED')) THEN
    RAISE EXCEPTION 'Vẫn còn đơn đặt trước chưa hoàn tất tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM payment.payments p JOIN ordering.orders o ON o.checkout_id = p.checkout_id
    WHERE o.kiosk_id = v_kiosk_id AND p.status = 'PENDING'
  ) THEN
    RAISE EXCEPTION 'Vẫn còn thanh toán đang chờ xử lý tại kiosk' USING ERRCODE = '40001';
  END IF;
  IF EXISTS (
    SELECT 1 FROM kiosk_ops.unlock_tokens
    WHERE kiosk_id = v_kiosk_id AND status IN ('ISSUED', 'SENT', 'ACKED', 'OPENED')
  ) THEN
    RAISE EXCEPTION 'Vẫn còn lệnh điều khiển thiết bị chưa đóng tại kiosk' USING ERRCODE = '40001';
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET status = 'COMPLETED', version = version + 1, updated_at = v_now
  WHERE id = v_task.id AND status = 'SUBMITTED';
  PERFORM flow.audit(p_manager, 'STAFF_TASK_UPDATED', 'staff_tasks', v_task.id,
    jsonb_build_object('from', 'SUBMITTED', 'to', 'COMPLETED', 'report', v_task.report, 'confirmed_by_termination', p_id));

  UPDATE kiosk_ops.kiosk_terminations
  SET status = 'COMPLETED', completed_by = p_manager, completed_at = v_now,
      completion_notes = nullif(btrim(p_notes), ''), version = version + 1, updated_at = v_now
  WHERE id = p_id;
  UPDATE identity.subscriptions
  SET status = 'EXPIRED', updated_at = v_now
  WHERE kiosk_id = v_kiosk_id AND seller_id = v_seller_id AND status = 'ACTIVE';
  UPDATE kiosk_ops.slot_assignments
  SET status = 'RELEASED', released_at = v_now, release_reason = 'TERMINATION'
  WHERE slot_id IN (SELECT id FROM kiosk_ops.slots WHERE kiosk_id = v_kiosk_id)
    AND seller_id = v_seller_id AND status = 'ACTIVE';
  UPDATE kiosk_ops.slots
  SET current_seller_id = NULL, status = 'FREE', row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = v_kiosk_id AND current_seller_id = v_seller_id;
  UPDATE kiosk_ops.kiosks SET seller_id = NULL, updated_at = v_now
  WHERE id = v_kiosk_id AND seller_id = v_seller_id;

  PERFORM flow.audit(p_manager, 'KIOSK_TERMINATION_COMPLETED', 'kiosk_terminations', p_id,
    jsonb_build_object('kiosk_id', v_kiosk_id, 'seller_id', v_seller_id, 'removal_task_id', v_task.id,
      'staff_report', v_task.report, 'manager_notes', nullif(btrim(p_notes), '')));
  RETURN 'COMPLETED';
END $$;

COMMIT;
