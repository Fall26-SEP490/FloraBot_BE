-- Migration 020: Retirement of legacy STAFF and split task flows into TECHNICIAN, OPERATIONS_MANAGER, SELLER_STAFF, and SELLER
-- Preserves legacy STAFF rows and task history for audit/FK compatibility while retiring STAFF from active operations.
BEGIN;

-- 1. Minimal explicit manager-kiosk mapping table (fails closed when empty)
CREATE TABLE IF NOT EXISTS kiosk_ops.manager_kiosks (
  manager_id uuid NOT NULL REFERENCES identity.users(id),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  PRIMARY KEY (manager_id, kiosk_id)
);
CREATE INDEX IF NOT EXISTS manager_kiosks_kiosk_idx ON kiosk_ops.manager_kiosks(kiosk_id);

CREATE OR REPLACE FUNCTION kiosk_ops.check_manager_kiosk() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = NEW.manager_id AND role = 'OPERATIONS_MANAGER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'User must be an active operations manager' USING ERRCODE = '22023';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS trg_check_manager_kiosk ON kiosk_ops.manager_kiosks;
CREATE TRIGGER trg_check_manager_kiosk
  BEFORE INSERT OR UPDATE ON kiosk_ops.manager_kiosks
  FOR EACH ROW EXECUTE FUNCTION kiosk_ops.check_manager_kiosk();

-- 2. Update assign_staff_task to enforce split roles, tenant, and kiosk scopes; reject legacy STAFF
DROP FUNCTION IF EXISTS flow.assign_staff_task(uuid,uuid,uuid,text,uuid,uuid,uuid,text);

CREATE OR REPLACE FUNCTION flow.assign_staff_task(
  p_id uuid,
  p_actor uuid,
  p_staff uuid,
  p_kind text,
  p_kiosk uuid,
  p_seller uuid,
  p_incident uuid,
  p_instructions text
)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_actor_role text;
  v_actor_seller uuid;
  existing kiosk_ops.staff_tasks;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  -- 1. Validate caller actor
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  -- 2. Validate task details
  IF p_kind IS NULL OR p_kind NOT IN ('INCIDENT', 'DELIVERY') OR p_instructions IS NULL OR length(btrim(p_instructions)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid task details' USING ERRCODE = '22023';
  END IF;

  -- 3. Validate caller scope and kind boundaries
  IF p_kind = 'INCIDENT' THEN
    IF v_actor_role = 'SELLER' THEN
      RAISE EXCEPTION 'Seller cannot assign incident tasks' USING ERRCODE = '42501';
    END IF;
    IF v_actor_role = 'OPERATIONS_MANAGER' THEN
      IF NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = p_kiosk) THEN
        RAISE EXCEPTION 'Operations manager is not assigned to this kiosk' USING ERRCODE = '42501';
      END IF;
    END IF;
    IF p_seller IS NOT NULL THEN
      RAISE EXCEPTION 'Incident scope mismatch' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    IF v_actor_role = 'OPERATIONS_MANAGER' THEN
      RAISE EXCEPTION 'Operations manager cannot assign delivery tasks' USING ERRCODE = '42501';
    END IF;
    IF v_actor_role = 'SELLER' THEN
      IF p_seller IS DISTINCT FROM v_actor_seller THEN
        RAISE EXCEPTION 'Seller can only assign delivery tasks for own shop' USING ERRCODE = '42501';
      END IF;
    END IF;
    IF p_incident IS NOT NULL THEN
      RAISE EXCEPTION 'Delivery scope mismatch' USING ERRCODE = '22023';
    END IF;
  END IF;

  -- 4. Validate assignee (strictly active TECHNICIAN or SELLER_STAFF of shop; legacy STAFF rejected)
  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users
  WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;

  IF v_staff_role IS NULL OR v_staff_role NOT IN ('TECHNICIAN', 'SELLER_STAFF') THEN
    RAISE EXCEPTION 'Active qualified staff required' USING ERRCODE = '22023';
  END IF;

  IF p_kind = 'INCIDENT' AND v_staff_role <> 'TECHNICIAN' THEN
    RAISE EXCEPTION 'Incident task must be assigned to an active technician' USING ERRCODE = '22023';
  END IF;

  IF p_kind = 'DELIVERY' AND (v_staff_role <> 'SELLER_STAFF' OR v_staff_seller IS DISTINCT FROM p_seller) THEN
    RAISE EXCEPTION 'Delivery task must be assigned to an active seller staff of the shop' USING ERRCODE = '22023';
  END IF;

  -- 5. Kiosk availability
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk AND status <> 'DISABLED' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE = '22023'; END IF;

  -- 6. Idempotent replay check (caller scope, assignee qualification, and kiosk verified)
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-task:' || p_id::text, 0));
  SELECT * INTO existing FROM kiosk_ops.staff_tasks WHERE id = p_id;
  IF FOUND THEN
    IF existing.assigned_by <> p_actor OR existing.assignee_id <> p_staff OR existing.kind <> p_kind
       OR existing.kiosk_id <> p_kiosk OR existing.seller_id IS DISTINCT FROM p_seller
       OR existing.incident_id IS DISTINCT FROM p_incident OR existing.instructions IS DISTINCT FROM btrim(p_instructions) THEN
      RAISE EXCEPTION 'Task ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN existing.id;
  END IF;

  -- 7. Domain preconditions
  IF p_kind = 'INCIDENT' THEN
    PERFORM 1 FROM ordering.disputes WHERE id = p_incident AND kiosk_id = p_kiosk AND status = 'OPEN' AND kind IN ('DEVICE_FAULT', 'DISPENSE_FAILED') FOR SHARE;
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Open incident required' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
    IF NOT FOUND OR NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = p_seller AND status NOT IN ('FREE', 'PENDING_RELEASE')) THEN
      RAISE EXCEPTION 'Seller does not serve kiosk' USING ERRCODE = '22023';
    END IF;
  END IF;

  INSERT INTO kiosk_ops.staff_tasks(id, kind, assignee_id, assigned_by, kiosk_id, seller_id, incident_id, instructions)
  VALUES (p_id, p_kind, p_staff, p_actor, p_kiosk, p_seller, p_incident, btrim(p_instructions));
  PERFORM flow.audit(p_actor, 'STAFF_TASK_ASSIGNED', 'staff_tasks', p_id, jsonb_build_object('assignee', p_staff, 'kind', p_kind));
  RETURN p_id;
END $$;

-- 3. Update transition_staff_task to enforce split roles and scope; reject legacy STAFF
DROP FUNCTION IF EXISTS flow.transition_staff_task(uuid,uuid,integer,text,text);

CREATE OR REPLACE FUNCTION flow.transition_staff_task(
  p_id uuid,
  p_actor uuid,
  p_version integer,
  p_status text,
  p_report text
)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  v_actor_role text;
  v_actor_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER', 'TECHNICIAN', 'SELLER_STAFF') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  -- Verify scope by role
  IF v_actor_role = 'TECHNICIAN' THEN
    IF t.assignee_id <> p_actor OR t.kind <> 'INCIDENT' THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'SELLER_STAFF' THEN
    IF t.assignee_id <> p_actor OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_actor_seller THEN
      RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
    END IF;
  ELSIF v_actor_role = 'OPERATIONS_MANAGER' THEN
    IF t.kind <> 'INCIDENT' OR NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = t.kiosk_id) THEN
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

  -- Validate state transitions
  IF p_status IS NULL OR NOT (
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'ASSIGNED' AND p_status = 'IN_PROGRESS') OR
    (v_actor_role IN ('TECHNICIAN', 'SELLER_STAFF') AND t.status = 'IN_PROGRESS' AND p_status = 'SUBMITTED') OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status = 'SUBMITTED' AND p_status IN ('COMPLETED', 'IN_PROGRESS')) OR
    (v_actor_role IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') AND t.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED') AND p_status = 'CANCELLED')
  ) THEN
    RAISE EXCEPTION 'Invalid task transition' USING ERRCODE = '22023';
  END IF;

  IF p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Provide a report of 10 to 2000 characters' USING ERRCODE = '22023';
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET status = p_status, report = btrim(p_report), version = version + 1, updated_at = public.app_now()
  WHERE id = p_id;

  PERFORM flow.audit(p_actor, 'STAFF_TASK_UPDATED', 'staff_tasks', p_id,
    jsonb_build_object('from', t.status, 'to', p_status, 'report', btrim(p_report), 'version', t.version + 1));

  RETURN t.version + 1;
END $$;

-- 4. Update reassign_staff_task to enforce split roles and scope; reject legacy STAFF
DROP FUNCTION IF EXISTS flow.reassign_staff_task(uuid,uuid,uuid,integer,text);

CREATE OR REPLACE FUNCTION flow.reassign_staff_task(
  p_id uuid,
  p_actor uuid,
  p_staff uuid,
  p_version integer,
  p_reason text
)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  v_actor_role text;
  v_actor_seller uuid;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_actor_role, v_actor_seller
  FROM identity.users
  WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  
  IF v_actor_role IS NULL OR v_actor_role NOT IN ('ADMIN', 'OPERATIONS_MANAGER', 'SELLER') THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_id FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  -- Scope verification
  IF v_actor_role = 'OPERATIONS_MANAGER' THEN
    IF t.kind <> 'INCIDENT' OR NOT EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE manager_id = p_actor AND kiosk_id = t.kiosk_id) THEN
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

  IF t.status NOT IN ('ASSIGNED', 'IN_PROGRESS') THEN
    RAISE EXCEPTION 'Review or closed task cannot be reassigned' USING ERRCODE = '22023';
  END IF;

  IF p_staff IS NULL OR p_staff = t.assignee_id OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Different staff and handoff reason required' USING ERRCODE = '22023';
  END IF;

  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users
  WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;

  IF v_staff_role IS NULL THEN
    RAISE EXCEPTION 'Active staff required' USING ERRCODE = '22023';
  END IF;

  IF t.kind = 'INCIDENT' THEN
    IF v_staff_role <> 'TECHNICIAN' THEN
      RAISE EXCEPTION 'Incident task must be reassigned to an active technician' USING ERRCODE = '22023';
    END IF;
  ELSE -- DELIVERY
    IF v_staff_role <> 'SELLER_STAFF' OR v_staff_seller IS DISTINCT FROM t.seller_id THEN
      RAISE EXCEPTION 'Delivery task must be reassigned to an active seller staff of the same shop' USING ERRCODE = '22023';
    END IF;
  END IF;

  UPDATE kiosk_ops.staff_tasks
  SET assignee_id = p_staff, status = 'ASSIGNED', report = btrim(p_reason),
      version = version + 1, updated_at = public.app_now()
  WHERE id = p_id;

  PERFORM flow.audit(p_actor, 'STAFF_TASK_REASSIGNED', 'staff_tasks', p_id,
    jsonb_build_object('previousAssignee', t.assignee_id, 'assignee', p_staff, 'from', t.status, 'to', 'ASSIGNED', 'report', btrim(p_reason), 'version', t.version + 1));

  RETURN t.version + 1;
END $$;

-- 5. Update staff_stock_bouquet to require SELLER_STAFF
CREATE OR REPLACE FUNCTION flow.staff_stock_bouquet(p_id uuid,p_task uuid,p_staff uuid,p_product uuid,p_slot uuid,p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  previous kiosk_ops.staff_stock_actions;
  pr catalog.flower_products;
  s kiosk_ops.slots;
  v_b uuid;
  v_staff_role text;
  v_staff_seller uuid;
BEGIN
  SELECT role, seller_id INTO v_staff_role, v_staff_seller
  FROM identity.users WHERE id = p_staff AND status = 'ACTIVE' FOR SHARE;
  
  IF v_staff_role IS NULL OR v_staff_role <> 'SELLER_STAFF' THEN
    RAISE EXCEPTION 'Active seller staff required' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id <> p_staff OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_staff_seller THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('staff-stock:' || p_id::text, 0));
  SELECT * INTO previous FROM kiosk_ops.staff_stock_actions WHERE id = p_id;
  IF FOUND THEN
    IF previous.task_id <> p_task OR previous.actor_id <> p_staff OR previous.product_id <> p_product
       OR previous.slot_id <> p_slot OR previous.qr_code IS DISTINCT FROM btrim(p_qr) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN previous.bouquet_id;
  END IF;

  IF t.status <> 'IN_PROGRESS' THEN
    RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023';
  END IF;

  IF p_id IS NULL OR p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN
    RAISE EXCEPTION 'Invalid stock reference' USING ERRCODE = '22023';
  END IF;

  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR SHARE;
  IF NOT FOUND OR pr.seller_id IS DISTINCT FROM t.seller_id OR pr.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Product outside task scope' USING ERRCODE = '22023';
  END IF;

  PERFORM 1 FROM identity.sellers WHERE id = t.seller_id AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller inactive' USING ERRCODE = '22023'; END IF;

  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = t.kiosk_id AND status NOT IN ('DISABLED', 'MAINTENANCE') FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE = '22023'; END IF;

  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND OR s.kiosk_id <> t.kiosk_id OR s.current_seller_id IS DISTINCT FROM t.seller_id
     OR s.status <> 'RENTED_EMPTY' OR NOT flow.slot_sellable(p_slot, t.seller_id) THEN
    RAISE EXCEPTION 'Slot is unavailable or outside task scope' USING ERRCODE = '22023';
  END IF;

  INSERT INTO kiosk_ops.bouquets(product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, btrim(p_qr), pr.price, 'STOCKED', public.app_now() + make_interval(hours => pr.shelf_life_hours), public.app_now())
  RETURNING id INTO v_b;

  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = s.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, s.id, 'STOCK_IN', p_task, p_staff);
  INSERT INTO kiosk_ops.staff_stock_actions(id, task_id, actor_id, product_id, slot_id, qr_code, bouquet_id)
  VALUES (p_id, p_task, p_staff, pr.id, s.id, btrim(p_qr), v_b);
  PERFORM flow.audit(p_staff, 'STAFF_TASK_STOCKED', 'staff_tasks', p_task, jsonb_build_object('operation', p_id, 'bouquet', v_b, 'slot', s.id));
  RETURN v_b;
END $$;

-- 6. Update staff_resolve_incident to require TECHNICIAN
CREATE OR REPLACE FUNCTION flow.staff_resolve_incident(p_id uuid,p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks;
  previous kiosk_ops.staff_incident_actions;
  d ordering.disputes;
  v_actor_role text;
BEGIN
  SELECT role INTO v_actor_role FROM identity.users WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  IF v_actor_role IS NULL OR v_actor_role <> 'TECHNICIAN' THEN
    RAISE EXCEPTION 'Active technician required' USING ERRCODE = '42501';
  END IF;

  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task AND assignee_id = p_actor AND kind = 'INCIDENT' FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE = 'P0002';
  END IF;

  IF p_id IS NULL OR p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid resolution report' USING ERRCODE = '22023';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('staff-incident:' || p_id::text, 0));
  SELECT * INTO previous FROM kiosk_ops.staff_incident_actions WHERE id = p_id;
  IF FOUND THEN
    IF previous.task_id <> p_task OR previous.actor_id <> p_actor OR previous.request_version IS DISTINCT FROM p_version OR previous.report <> btrim(p_report) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE = '40001';
    END IF;
    RETURN previous.result_version;
  END IF;

  IF t.version IS DISTINCT FROM p_version OR t.status <> 'IN_PROGRESS' THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE = '40001';
  END IF;

  SELECT * INTO d FROM ordering.disputes WHERE id = t.incident_id AND kiosk_id = t.kiosk_id AND kind IN ('DEVICE_FAULT', 'DISPENSE_FAILED') FOR UPDATE;
  IF NOT FOUND OR d.status <> 'OPEN' THEN
    RAISE EXCEPTION 'Incident is no longer open' USING ERRCODE = '40001';
  END IF;

  PERFORM flow.resolve_device_fault(d.id, p_actor, btrim(p_report));
  UPDATE kiosk_ops.staff_tasks SET status = 'SUBMITTED', report = btrim(p_report), version = version + 1, updated_at = public.app_now() WHERE id = t.id;
  INSERT INTO kiosk_ops.staff_incident_actions(id, task_id, actor_id, incident_id, request_version, result_version, report)
  VALUES (p_id, t.id, p_actor, d.id, p_version, t.version + 1, btrim(p_report));
  PERFORM flow.audit(p_actor, 'STAFF_INCIDENT_RESOLVED', 'staff_tasks', t.id, jsonb_build_object('from', t.status, 'to', 'SUBMITTED', 'report', btrim(p_report), 'operation', p_id));
  RETURN t.version + 1;
END $$;

COMMIT;
