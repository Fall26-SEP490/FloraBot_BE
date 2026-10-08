BEGIN;

CREATE TABLE IF NOT EXISTS kiosk_ops.staff_incident_actions (
  id uuid PRIMARY KEY,
  task_id uuid NOT NULL UNIQUE REFERENCES kiosk_ops.staff_tasks(id),
  actor_id uuid NOT NULL REFERENCES identity.users(id),
  incident_id uuid NOT NULL REFERENCES ordering.disputes(id),
  request_version integer NOT NULL,
  result_version integer NOT NULL,
  report text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE OR REPLACE FUNCTION flow.staff_resolve_incident(p_id uuid,p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks; previous kiosk_ops.staff_incident_actions; d ordering.disputes;
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_actor AND role='STAFF' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Active staff required' USING ERRCODE='42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=p_task AND assignee_id=p_actor AND kind='INCIDENT' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  IF p_id IS NULL OR p_report IS NULL OR length(btrim(p_report)) NOT BETWEEN 10 AND 2000 THEN
    RAISE EXCEPTION 'Invalid resolution report' USING ERRCODE='22023';
  END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-incident:'||p_id::text,0));
  SELECT * INTO previous FROM kiosk_ops.staff_incident_actions WHERE id=p_id;
  IF FOUND THEN
    IF previous.task_id<>p_task OR previous.actor_id<>p_actor OR previous.request_version IS DISTINCT FROM p_version OR previous.report<>btrim(p_report) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE='40001';
    END IF;
    RETURN previous.result_version;
  END IF;
  IF t.version IS DISTINCT FROM p_version OR t.status<>'IN_PROGRESS' THEN
    RAISE EXCEPTION 'Task changed; reload first' USING ERRCODE='40001';
  END IF;
  SELECT * INTO d FROM ordering.disputes WHERE id=t.incident_id AND kiosk_id=t.kiosk_id AND kind IN ('DEVICE_FAULT','DISPENSE_FAILED') FOR UPDATE;
  IF NOT FOUND OR d.status<>'OPEN' THEN RAISE EXCEPTION 'Incident is no longer open' USING ERRCODE='40001'; END IF;
  -- Reuse inventory restoration and pending-refund guards; no door or payout command.
  PERFORM flow.resolve_device_fault(d.id,p_actor,btrim(p_report));
  UPDATE kiosk_ops.staff_tasks SET status='SUBMITTED',report=btrim(p_report),version=version+1,updated_at=public.app_now() WHERE id=t.id;
  INSERT INTO kiosk_ops.staff_incident_actions(id,task_id,actor_id,incident_id,request_version,result_version,report)
  VALUES(p_id,t.id,p_actor,d.id,p_version,t.version+1,btrim(p_report));
  PERFORM flow.audit(p_actor,'STAFF_INCIDENT_RESOLVED','staff_tasks',t.id,jsonb_build_object('from',t.status,'to','SUBMITTED','report',btrim(p_report),'operation',p_id));
  RETURN t.version+1;
END $$;

COMMIT;
