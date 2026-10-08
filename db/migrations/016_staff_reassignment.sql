BEGIN;

CREATE OR REPLACE FUNCTION flow.reassign_staff_task(p_id uuid,p_admin uuid,p_staff uuid,p_version integer,p_reason text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks;
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_admin AND role='ADMIN' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Administrator required' USING ERRCODE='42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=p_id FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  IF p_version IS DISTINCT FROM t.version THEN RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE='40001'; END IF;
  IF t.status NOT IN ('ASSIGNED','IN_PROGRESS') THEN RAISE EXCEPTION 'Review or closed task cannot be reassigned' USING ERRCODE='22023'; END IF;
  IF p_staff IS NULL OR p_staff=t.assignee_id OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Different staff and handoff reason required' USING ERRCODE='22023';
  END IF;
  PERFORM 1 FROM identity.users WHERE id=p_staff AND role='STAFF' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Active staff required' USING ERRCODE='22023'; END IF;
  UPDATE kiosk_ops.staff_tasks SET assignee_id=p_staff,status='ASSIGNED',report=btrim(p_reason),
    version=version+1,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_admin,'STAFF_TASK_REASSIGNED','staff_tasks',p_id,
    jsonb_build_object('previousAssignee',t.assignee_id,'assignee',p_staff,'from',t.status,'to','ASSIGNED','report',btrim(p_reason),'version',t.version+1));
  RETURN t.version+1;
END $$;

COMMIT;
