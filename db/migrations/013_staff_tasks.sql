BEGIN;

CREATE TABLE IF NOT EXISTS kiosk_ops.staff_tasks (
  id uuid PRIMARY KEY,
  kind text NOT NULL CHECK (kind IN ('INCIDENT','DELIVERY')),
  assignee_id uuid NOT NULL REFERENCES identity.users(id),
  assigned_by uuid NOT NULL REFERENCES identity.users(id),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  seller_id uuid REFERENCES identity.sellers(id),
  incident_id uuid REFERENCES ordering.disputes(id),
  instructions text NOT NULL CHECK (length(btrim(instructions)) BETWEEN 10 AND 2000),
  status text NOT NULL DEFAULT 'ASSIGNED' CHECK (status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED','COMPLETED','CANCELLED')),
  report text,
  version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK ((kind='INCIDENT' AND incident_id IS NOT NULL AND seller_id IS NULL)
      OR (kind='DELIVERY' AND seller_id IS NOT NULL AND incident_id IS NULL))
);
CREATE INDEX IF NOT EXISTS staff_tasks_assignee ON kiosk_ops.staff_tasks(assignee_id,status,created_at);
CREATE UNIQUE INDEX IF NOT EXISTS staff_tasks_open_incident ON kiosk_ops.staff_tasks(incident_id)
  WHERE incident_id IS NOT NULL AND status NOT IN ('COMPLETED','CANCELLED');

CREATE OR REPLACE FUNCTION flow.assign_staff_task(p_id uuid,p_admin uuid,p_staff uuid,p_kind text,p_kiosk uuid,p_seller uuid,p_incident uuid,p_instructions text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE existing kiosk_ops.staff_tasks;
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_admin AND role='ADMIN' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Administrator required' USING ERRCODE='42501'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-task:'||p_id::text,0));
  SELECT * INTO existing FROM kiosk_ops.staff_tasks WHERE id=p_id;
  IF FOUND THEN
    IF existing.assigned_by<>p_admin OR existing.assignee_id<>p_staff OR existing.kind<>p_kind
       OR existing.kiosk_id<>p_kiosk OR existing.seller_id IS DISTINCT FROM p_seller
       OR existing.incident_id IS DISTINCT FROM p_incident OR existing.instructions IS DISTINCT FROM btrim(p_instructions) THEN
      RAISE EXCEPTION 'Task ID already used' USING ERRCODE='40001';
    END IF;
    RETURN existing.id;
  END IF;
  PERFORM 1 FROM identity.users WHERE id=p_staff AND role='STAFF' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Active staff required' USING ERRCODE='22023'; END IF;
  IF p_kind IS NULL OR p_kind NOT IN ('INCIDENT','DELIVERY') OR p_instructions IS NULL OR length(btrim(p_instructions)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid task details' USING ERRCODE='22023';
  END IF;
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id=p_kiosk AND status<>'DISABLED' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE='22023'; END IF;
  IF p_kind='INCIDENT' THEN
    IF p_seller IS NOT NULL THEN RAISE EXCEPTION 'Incident scope mismatch' USING ERRCODE='22023'; END IF;
    PERFORM 1 FROM ordering.disputes WHERE id=p_incident AND kiosk_id=p_kiosk AND status='OPEN' AND kind IN ('DEVICE_FAULT','DISPENSE_FAILED') FOR SHARE;
    IF NOT FOUND THEN RAISE EXCEPTION 'Open incident required' USING ERRCODE='22023'; END IF;
  ELSE
    IF p_incident IS NOT NULL THEN RAISE EXCEPTION 'Delivery scope mismatch' USING ERRCODE='22023'; END IF;
    PERFORM 1 FROM identity.sellers WHERE id=p_seller AND status='ACTIVE' FOR SHARE;
    IF NOT FOUND OR NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=p_seller AND status NOT IN ('FREE','PENDING_RELEASE')) THEN
      RAISE EXCEPTION 'Seller does not serve kiosk' USING ERRCODE='22023';
    END IF;
  END IF;
  INSERT INTO kiosk_ops.staff_tasks(id,kind,assignee_id,assigned_by,kiosk_id,seller_id,incident_id,instructions)
  VALUES(p_id,p_kind,p_staff,p_admin,p_kiosk,p_seller,p_incident,btrim(p_instructions));
  PERFORM flow.audit(p_admin,'STAFF_TASK_ASSIGNED','staff_tasks',p_id,jsonb_build_object('assignee',p_staff,'kind',p_kind));
  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION flow.transition_staff_task(p_id uuid,p_actor uuid,p_version integer,p_status text,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks; actor_role text;
BEGIN
  SELECT role INTO actor_role FROM identity.users WHERE id=p_actor AND status='ACTIVE' FOR SHARE;
  IF actor_role IS NULL OR actor_role NOT IN ('ADMIN','STAFF') THEN RAISE EXCEPTION 'Access denied' USING ERRCODE='42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=p_id FOR UPDATE;
  IF NOT FOUND OR (actor_role='STAFF' AND t.assignee_id<>p_actor) THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  IF p_version IS DISTINCT FROM t.version THEN RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE='40001'; END IF;
  IF p_status IS NULL OR NOT (
    (actor_role='STAFF' AND t.status='ASSIGNED' AND p_status='IN_PROGRESS') OR
    (actor_role='STAFF' AND t.status='IN_PROGRESS' AND p_status='SUBMITTED') OR
    (actor_role='ADMIN' AND t.status='SUBMITTED' AND p_status IN ('COMPLETED','IN_PROGRESS')) OR
    (actor_role='ADMIN' AND t.status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED') AND p_status='CANCELLED')
  ) THEN RAISE EXCEPTION 'Invalid task transition' USING ERRCODE='22023'; END IF;
  IF p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Provide a report of 10 to 2000 characters' USING ERRCODE='22023';
  END IF;
  UPDATE kiosk_ops.staff_tasks SET status=p_status,report=btrim(p_report),version=version+1,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_actor,'STAFF_TASK_UPDATED','staff_tasks',p_id,jsonb_build_object('from',t.status,'to',p_status,'report',btrim(p_report),'version',t.version+1));
  RETURN t.version+1;
END $$;

COMMIT;
