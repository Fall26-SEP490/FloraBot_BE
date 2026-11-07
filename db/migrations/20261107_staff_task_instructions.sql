BEGIN;

CREATE OR REPLACE FUNCTION flow.edit_staff_task_instructions(
  p_task uuid, p_actor uuid, p_version integer, p_instructions text
) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  v_role text;
  v_seller uuid;
  v_task kiosk_ops.staff_tasks%ROWTYPE;
BEGIN
  SELECT role,seller_id INTO v_role,v_seller
  FROM identity.users WHERE id=p_actor AND status='ACTIVE' FOR SHARE;
  IF v_role NOT IN ('ADMIN','OPERATIONS_MANAGER','SELLER') OR v_role IS NULL THEN
    RAISE EXCEPTION 'Access denied' USING ERRCODE='42501';
  END IF;
  IF p_instructions IS NULL OR length(btrim(p_instructions)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid instructions' USING ERRCODE='22023';
  END IF;
  SELECT * INTO v_task FROM kiosk_ops.staff_tasks WHERE id=p_task FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  IF v_role='SELLER' AND (v_task.kind<>'DELIVERY' OR v_task.seller_id IS DISTINCT FROM v_seller) THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002';
  END IF;
  IF v_role='OPERATIONS_MANAGER' AND (
    v_task.kind<>'INCIDENT' OR NOT EXISTS (
      SELECT 1 FROM kiosk_ops.manager_kiosks
      WHERE manager_id=p_actor AND kiosk_id=v_task.kiosk_id FOR SHARE
    )
  ) THEN
    RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002';
  END IF;
  IF v_task.version<>p_version OR v_task.status NOT IN ('ASSIGNED','IN_PROGRESS') THEN
    RAISE EXCEPTION 'Task changed or can no longer be edited' USING ERRCODE='40001';
  END IF;
  UPDATE kiosk_ops.staff_tasks
  SET instructions=btrim(p_instructions),version=version+1,updated_at=public.app_now()
  WHERE id=p_task;
  PERFORM flow.audit(p_actor,'STAFF_TASK_INSTRUCTIONS_EDITED','staff_tasks',p_task,
    jsonb_build_object('previous',v_task.instructions,'current',btrim(p_instructions),'version',v_task.version+1));
  RETURN v_task.version+1;
END $$;

COMMIT;
