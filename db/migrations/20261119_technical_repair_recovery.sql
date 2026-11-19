BEGIN;

-- A report returned for rework needs a new operation ID while old retries remain replayable.
ALTER TABLE kiosk_ops.staff_incident_actions DROP CONSTRAINT staff_incident_actions_task_id_key;
CREATE INDEX staff_incident_actions_task_idx ON kiosk_ops.staff_incident_actions(task_id);

-- Keep the established door-repair implementation as the authority for that path.
ALTER FUNCTION flow.submit_technical_repair(uuid,uuid,uuid,integer,text) RENAME TO submit_technical_repair_door;
ALTER FUNCTION flow.accept_technical_repair(uuid,uuid,integer,text) RENAME TO accept_technical_repair_door;

CREATE FUNCTION flow.technical_repair_blocker(p_task uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE v record; v_recovered boolean; v_cmd record; v_presence record; v_closed timestamptz;
BEGIN
  SELECT t.*,d.slot_id,k.status AS kiosk_status,k.last_heartbeat_at,k.seller_id AS owner,
         s.status AS slot_status,s.bouquet_id,s.hold_order_id,s.hold_until,s.relay_channel,s.current_seller_id
  INTO v FROM kiosk_ops.staff_tasks t JOIN ordering.disputes d ON d.id=t.incident_id
  JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id JOIN kiosk_ops.slots s ON s.id=d.slot_id AND s.kiosk_id=k.id
  WHERE t.id=p_task;
  IF NOT FOUND THEN RETURN 'REPAIR_SLOT_STATE_CONFLICT'; END IF;
  IF v.slot_status='LOCKED' THEN RETURN 'REPAIR_SESSION_RECOVERY_REQUIRED'; END IF;
  IF v.slot_status NOT IN ('FAULT','STOCKED','RENTED_EMPTY','PENDING_REMOVAL')
     OR v.owner IS DISTINCT FROM v.current_seller_id OR v.owner IS NULL
     OR v.hold_order_id IS NOT NULL OR v.hold_until IS NOT NULL
     OR (v.bouquet_id IS NOT NULL AND flow.check_bouquet_active_order(v.bouquet_id) IS NOT NULL)
  THEN RETURN 'REPAIR_SLOT_STATE_CONFLICT'; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions WHERE slot_id=v.slot_id AND stage NOT IN ('Completed','Recovered'))
     OR EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens u WHERE u.slot_id=v.slot_id AND
       (u.status IN ('ISSUED','SENT','ACKED','OPENED') OR (u.door_opened_at IS NOT NULL AND u.door_closed_at IS NULL)
        OR EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=u.cmd_id AND i.disposition='PENDING')))
  THEN RETURN 'REPAIR_DOOR_CYCLE_REQUIRED'; END IF;
  IF v.kiosk_status<>'ONLINE' OR v.last_heartbeat_at IS NULL
     OR v.last_heartbeat_at<=clock_timestamp()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
  THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slot_sensor_capabilities c WHERE c.slot_id=v.slot_id
    AND c.kiosk_id=v.kiosk_id AND c.relay_channel=v.relay_channel AND c.enabled AND c.door_verified AND c.presence_verified)
  THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
  v_recovered := v.slot_status<>'FAULT';
  IF v_recovered THEN
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms
      WHERE ms.slot_id=v.slot_id AND ms.kiosk_id=v.kiosk_id AND ms.seller_id=v.owner
        AND ms.stage='Recovered' AND ms.updated_at>=v.created_at)
    THEN RETURN 'REPAIR_SLOT_STATE_CONFLICT'; END IF;
    IF NOT EXISTS(SELECT 1 FROM notify.attachments a WHERE a.owner_service='kiosk_ops'
      AND a.owner_type='staff_task' AND a.owner_id=p_task AND a.phase='EVIDENCE'
      AND a.uploaded_by=v.assignee_id AND NOT a.is_hidden AND a.created_at>=v.created_at
      AND a.created_at>=(SELECT max(updated_at) FROM kiosk_ops.slot_maintenance_sessions
        WHERE slot_id=v.slot_id AND stage='Recovered'))
    THEN RETURN 'REPAIR_EVIDENCE_REQUIRED'; END IF;
  ELSE
    SELECT u.* INTO v_cmd FROM kiosk_ops.technical_repair_access g
      JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=g.latest_cmd_id
      WHERE g.task_id=p_task AND g.technician_id=v.assignee_id AND g.slot_id=v.slot_id AND g.seller_id=v.owner;
    IF NOT FOUND THEN
      IF NOT EXISTS(SELECT 1 FROM kiosk_ops.technical_repair_access g WHERE g.task_id=p_task
        AND g.technician_id=v.assignee_id AND g.revoked_at IS NULL AND g.expires_at>clock_timestamp())
      THEN RETURN 'REPAIR_APPROVAL_REQUIRED'; END IF;
      RETURN 'REPAIR_DOOR_CYCLE_REQUIRED';
    END IF;
    v_closed:=v_cmd.door_closed_at;
    IF v_cmd.status<>'CLOSED' OR v_cmd.door_opened_at IS NULL OR v_cmd.door_closed_at IS NULL
      OR v_cmd.door_closed_at<v_cmd.door_opened_at
      OR NOT EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=v_cmd.cmd_id AND i.kiosk_id=v.kiosk_id
        AND i.event='CLOSED' AND i.disposition='APPLIED') THEN RETURN 'REPAIR_DOOR_CYCLE_REQUIRED'; END IF;
  END IF;
  SELECT * INTO v_presence FROM kiosk_ops.flower_presence WHERE kiosk_id=v.kiosk_id;
  IF NOT FOUND OR v_presence.received_at<=clock_timestamp()-interval '45 seconds'
    OR v_presence.observed_at<extract(epoch FROM clock_timestamp())*1000-45000
    OR v_presence.observed_at>extract(epoch FROM clock_timestamp())*1000+5000
    OR (NOT v_recovered AND (v_presence.received_at<v_closed
        OR v_presence.observed_at<extract(epoch FROM v_closed)*1000))
    OR (v_recovered AND EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms
        WHERE ms.slot_id=v.slot_id AND ms.stage='Recovered'
          AND (v_presence.received_at<ms.updated_at
            OR v_presence.observed_at<extract(epoch FROM ms.updated_at)*1000)))
  THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
  IF v_presence.states[v.relay_channel+1] IS DISTINCT FROM (CASE WHEN v.bouquet_id IS NULL THEN 0 ELSE 1 END)
  THEN RETURN 'REPAIR_SENSOR_MISMATCH'; END IF;
  RETURN NULL;
END $$;

CREATE FUNCTION flow.submit_technical_repair(p_id uuid,p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; previous kiosk_ops.staff_incident_actions; blocker text; result integer;
BEGIN
  SELECT task.* INTO t FROM kiosk_ops.staff_tasks task JOIN identity.users u ON u.id=task.assignee_id
  WHERE task.id=p_task AND task.assignee_id=p_actor AND task.kind='INCIDENT'
    AND u.role='TECHNICIAN' AND u.status='ACTIVE' FOR UPDATE OF task;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  SELECT * INTO previous FROM kiosk_ops.staff_incident_actions WHERE id=p_id;
  IF FOUND THEN
    IF previous.task_id<>p_task OR previous.actor_id<>p_actor OR previous.request_version<>p_version
       OR previous.report<>btrim(p_report) THEN RAISE EXCEPTION 'TASK_VERSION_CONFLICT' USING ERRCODE='P4090'; END IF;
    RETURN previous.result_version;
  END IF;
  IF t.version<>p_version THEN RAISE EXCEPTION 'TASK_VERSION_CONFLICT' USING ERRCODE='P4090'; END IF;

  PERFORM 1 FROM kiosk_ops.slots s JOIN ordering.disputes d ON d.slot_id=s.id
    WHERE d.id=t.incident_id FOR UPDATE OF s;
  blocker:=flow.technical_repair_blocker(p_task);
  IF blocker IS NOT NULL THEN RAISE EXCEPTION '%',blocker USING ERRCODE='P4090'; END IF;
  IF EXISTS(SELECT 1 FROM ordering.disputes d JOIN kiosk_ops.slots s ON s.id=d.slot_id
    WHERE d.id=t.incident_id AND s.status='FAULT') THEN
    RETURN flow.submit_technical_repair_door(p_id,p_task,p_actor,p_version,p_report);
  END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.manager_kiosks m JOIN identity.users u ON u.id=m.manager_id
      WHERE m.kiosk_id=t.kiosk_id AND m.manager_id=t.assigned_by AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE')
  THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  -- A recovered slot requires an explicit technician report and task-bound photo evidence.
  IF NOT EXISTS(SELECT 1 FROM ordering.disputes WHERE id=t.incident_id AND status='OPEN')
  THEN RAISE EXCEPTION 'REPAIR_SLOT_STATE_CONFLICT' USING ERRCODE='P4090'; END IF;
  result:=flow.transition_staff_task(p_task,p_actor,p_version,'SUBMITTED',p_report);
  INSERT INTO kiosk_ops.staff_incident_actions(id,task_id,actor_id,incident_id,request_version,result_version,report)
    VALUES(p_id,p_task,p_actor,t.incident_id,p_version,result,btrim(p_report));
  PERFORM flow.audit(p_actor,'TECHNICAL_RECOVERY_VERIFIED','staff_tasks',p_task,jsonb_build_object('incidentId',t.incident_id));
  RETURN result;
END $$;

CREATE FUNCTION flow.accept_technical_repair(p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE t record; blocker text; result integer;
BEGIN
  SELECT task.* INTO t FROM kiosk_ops.staff_tasks task
  JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=task.kiosk_id AND m.manager_id=p_actor
  JOIN identity.users u ON u.id=p_actor AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE'
  WHERE task.id=p_task AND task.kind='INCIDENT' AND task.status='SUBMITTED' FOR UPDATE OF task;
  IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  IF t.version<>p_version THEN RAISE EXCEPTION 'TASK_VERSION_CONFLICT' USING ERRCODE='P4090'; END IF;
  PERFORM 1 FROM kiosk_ops.slots s JOIN ordering.disputes d ON d.slot_id=s.id
    WHERE d.id=t.incident_id FOR UPDATE OF s;
  blocker:=flow.technical_repair_blocker(p_task);
  IF blocker IS NOT NULL THEN RAISE EXCEPTION '%',blocker USING ERRCODE='P4090'; END IF;
  IF EXISTS(SELECT 1 FROM ordering.disputes d JOIN kiosk_ops.slots s ON s.id=d.slot_id
    WHERE d.id=t.incident_id AND s.status='FAULT') THEN
    RETURN flow.accept_technical_repair_door(p_task,p_actor,p_version,p_report);
  END IF;
  IF NOT EXISTS(SELECT 1 FROM notify.audit_logs WHERE entity_type='staff_tasks' AND entity_id=p_task
    AND action='TECHNICAL_RECOVERY_VERIFIED' AND actor_id=t.assignee_id AND created_at>=t.created_at)
  THEN RAISE EXCEPTION 'REPAIR_EVIDENCE_REQUIRED' USING ERRCODE='P4090'; END IF;
  PERFORM flow.resolve_device_fault(t.incident_id,p_actor,btrim(p_report));
  result:=flow.transition_staff_task(p_task,p_actor,p_version,'COMPLETED',p_report);
  RETURN result;
END $$;

COMMIT;
