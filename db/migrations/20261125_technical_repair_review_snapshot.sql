BEGIN;

-- A technician proves readiness when submitting. Manager review may happen later,
-- so preserve the exact verified sensor/slot snapshot for that submission.
CREATE TABLE kiosk_ops.technical_repair_readiness_snapshots (
  task_id uuid PRIMARY KEY REFERENCES kiosk_ops.staff_tasks(id) ON DELETE CASCADE,
  action_id uuid NOT NULL UNIQUE REFERENCES kiosk_ops.staff_incident_actions(id) ON DELETE CASCADE,
  incident_id uuid NOT NULL REFERENCES ordering.disputes(id),
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  submitted_version integer NOT NULL CHECK (submitted_version > 0),
  captured_at timestamptz NOT NULL,
  kiosk_status text NOT NULL,
  heartbeat_at timestamptz NOT NULL,
  slot_status text NOT NULL,
  bouquet_id uuid,
  seller_id uuid,
  current_seller_id uuid,
  hold_order_id uuid,
  hold_until timestamptz,
  relay_channel integer NOT NULL,
  sensor_capability_verified boolean NOT NULL,
  presence_received_at timestamptz NOT NULL,
  presence_observed_at bigint NOT NULL,
  presence_state integer NOT NULL CHECK (presence_state IN (0,1)),
  door_closed_at timestamptz,
  recovered_at timestamptz,
  CHECK (seller_id IS NOT DISTINCT FROM current_seller_id),
  CHECK (hold_order_id IS NULL AND hold_until IS NULL)
);

CREATE OR REPLACE FUNCTION flow.capture_technical_repair_readiness(p_task uuid,p_action uuid,p_version integer)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := clock_timestamp(); v_rows integer;
BEGIN
  INSERT INTO kiosk_ops.technical_repair_readiness_snapshots(
    task_id,action_id,incident_id,kiosk_id,slot_id,submitted_version,captured_at,
    kiosk_status,heartbeat_at,slot_status,bouquet_id,seller_id,current_seller_id,
    hold_order_id,hold_until,relay_channel,sensor_capability_verified,
    presence_received_at,presence_observed_at,presence_state,door_closed_at,recovered_at)
  SELECT t.id,a.id,d.id,k.id,s.id,t.version,v_now,
    k.status,k.last_heartbeat_at,s.status,s.bouquet_id,k.seller_id,s.current_seller_id,
    s.hold_order_id,s.hold_until,s.relay_channel,
    EXISTS(SELECT 1 FROM kiosk_ops.slot_sensor_capabilities c
      WHERE c.slot_id=s.id AND c.kiosk_id=k.id AND c.relay_channel=s.relay_channel
        AND c.enabled AND c.door_verified AND c.presence_verified),
    p.received_at,p.observed_at,p.states[s.relay_channel+1],token.door_closed_at,
    (SELECT max(ms.updated_at) FROM kiosk_ops.slot_maintenance_sessions ms
      WHERE ms.slot_id=s.id AND ms.kiosk_id=k.id AND ms.stage='Recovered')
  FROM kiosk_ops.staff_tasks t
  JOIN ordering.disputes d ON d.id=t.incident_id AND d.kiosk_id=t.kiosk_id AND d.slot_id IS NOT NULL
  JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
  JOIN kiosk_ops.slots s ON s.id=d.slot_id AND s.kiosk_id=k.id
  JOIN kiosk_ops.staff_incident_actions a ON a.id=p_action AND a.task_id=t.id
    AND a.incident_id=d.id AND a.result_version=t.version
  JOIN kiosk_ops.flower_presence p ON p.kiosk_id=k.id
  LEFT JOIN LATERAL (
    SELECT u.door_closed_at FROM kiosk_ops.technical_repair_access g
    JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=g.latest_cmd_id
    WHERE g.task_id=t.id LIMIT 1
  ) token ON true
  WHERE t.id=p_task AND t.kind='INCIDENT' AND t.status='SUBMITTED'
    AND t.version=p_version AND k.status='ONLINE' AND k.last_heartbeat_at IS NOT NULL
    AND k.last_heartbeat_at>v_now-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
    AND EXISTS(SELECT 1 FROM kiosk_ops.slot_sensor_capabilities c
      WHERE c.slot_id=s.id AND c.kiosk_id=k.id AND c.relay_channel=s.relay_channel
        AND c.enabled AND c.door_verified AND c.presence_verified)
    AND p.received_at>v_now-interval '45 seconds'
    AND p.observed_at>=extract(epoch FROM v_now-interval '45 seconds')*1000
    AND p.observed_at<=extract(epoch FROM v_now+interval '5 seconds')*1000
    AND p.states[s.relay_channel+1]=CASE WHEN s.bouquet_id IS NULL THEN 0 ELSE 1 END
    AND (s.status<>'FAULT' OR (token.door_closed_at IS NOT NULL AND p.received_at>=token.door_closed_at
      AND p.observed_at>=extract(epoch FROM token.door_closed_at)*1000))
    AND (s.status='FAULT' OR EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms
      WHERE ms.slot_id=s.id AND ms.kiosk_id=k.id AND ms.seller_id=k.seller_id
        AND ms.stage='Recovered' AND p.received_at>=ms.updated_at
        AND p.observed_at>=extract(epoch FROM ms.updated_at)*1000))
  ON CONFLICT (task_id) DO UPDATE SET
    action_id=EXCLUDED.action_id,incident_id=EXCLUDED.incident_id,kiosk_id=EXCLUDED.kiosk_id,
    slot_id=EXCLUDED.slot_id,submitted_version=EXCLUDED.submitted_version,
    captured_at=EXCLUDED.captured_at,kiosk_status=EXCLUDED.kiosk_status,
    heartbeat_at=EXCLUDED.heartbeat_at,slot_status=EXCLUDED.slot_status,
    bouquet_id=EXCLUDED.bouquet_id,seller_id=EXCLUDED.seller_id,
    current_seller_id=EXCLUDED.current_seller_id,hold_order_id=EXCLUDED.hold_order_id,
    hold_until=EXCLUDED.hold_until,relay_channel=EXCLUDED.relay_channel,
    sensor_capability_verified=EXCLUDED.sensor_capability_verified,
    presence_received_at=EXCLUDED.presence_received_at,
    presence_observed_at=EXCLUDED.presence_observed_at,presence_state=EXCLUDED.presence_state,
    door_closed_at=EXCLUDED.door_closed_at,recovered_at=EXCLUDED.recovered_at;
  GET DIAGNOSTICS v_rows = ROW_COUNT;
  IF v_rows=0 THEN RAISE EXCEPTION 'REPAIR_SENSOR_STALE' USING ERRCODE='P4090'; END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.technical_repair_blocker(p_task uuid) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v record; sn kiosk_ops.technical_repair_readiness_snapshots; v_recovered boolean;
  v_cmd record; v_closed timestamptz;
  v_presence_received timestamptz; v_presence_observed bigint; v_presence_state integer;
  v_freshness_reference timestamptz; v_has_snapshot boolean := false;
BEGIN
  SELECT t.id AS task_id,t.assignee_id,t.kiosk_id,t.incident_id,t.version,t.status AS task_status,t.created_at,
         d.slot_id,k.status AS kiosk_status,k.last_heartbeat_at,k.seller_id AS owner,
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

  v_recovered := v.slot_status<>'FAULT';
  IF v.task_status='SUBMITTED' THEN
    SELECT rs.* INTO sn FROM kiosk_ops.technical_repair_readiness_snapshots rs
    JOIN kiosk_ops.staff_incident_actions a ON a.id=rs.action_id AND a.task_id=rs.task_id
      AND a.result_version=rs.submitted_version
    WHERE rs.task_id=p_task AND rs.incident_id=v.incident_id AND rs.kiosk_id=v.kiosk_id
      AND rs.slot_id=v.slot_id AND rs.submitted_version=v.version;
    v_has_snapshot := FOUND;
    IF v_has_snapshot THEN
      IF sn.slot_status IS DISTINCT FROM v.slot_status OR sn.bouquet_id IS DISTINCT FROM v.bouquet_id
        OR sn.seller_id IS DISTINCT FROM v.owner OR sn.current_seller_id IS DISTINCT FROM v.current_seller_id
        OR sn.relay_channel IS DISTINCT FROM v.relay_channel
        OR sn.hold_order_id IS NOT NULL OR sn.hold_until IS NOT NULL
      THEN RETURN 'REPAIR_SLOT_STATE_CONFLICT'; END IF;
      IF sn.kiosk_status<>'ONLINE' OR sn.heartbeat_at IS NULL
        OR sn.heartbeat_at<=sn.captured_at-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
        OR NOT sn.sensor_capability_verified
        OR NOT EXISTS(SELECT 1 FROM kiosk_ops.slot_sensor_capabilities c
          WHERE c.slot_id=v.slot_id AND c.kiosk_id=v.kiosk_id AND c.relay_channel=v.relay_channel
            AND c.enabled AND c.door_verified AND c.presence_verified)
      THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
      v_presence_received:=sn.presence_received_at;
      v_presence_observed:=sn.presence_observed_at;
      v_presence_state:=sn.presence_state;
      v_freshness_reference:=sn.captured_at;
      v_closed:=sn.door_closed_at;
    END IF;
  END IF;

  IF v.slot_status='FAULT' THEN
    SELECT u.* INTO v_cmd FROM kiosk_ops.technical_repair_access g
      JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=g.latest_cmd_id
      WHERE g.task_id=p_task AND g.technician_id=v.assignee_id AND g.slot_id=v.slot_id AND g.seller_id=v.owner;
    IF NOT FOUND THEN
      IF NOT EXISTS(SELECT 1 FROM kiosk_ops.technical_repair_access g WHERE g.task_id=p_task
        AND g.technician_id=v.assignee_id AND g.revoked_at IS NULL AND g.expires_at>clock_timestamp())
      THEN RETURN 'REPAIR_APPROVAL_REQUIRED'; END IF;
      RETURN 'REPAIR_DOOR_CYCLE_REQUIRED';
    END IF;
    IF v_has_snapshot AND v_cmd.door_closed_at IS DISTINCT FROM sn.door_closed_at
    THEN RETURN 'REPAIR_DOOR_CYCLE_REQUIRED'; END IF;
    v_closed:=v_cmd.door_closed_at;
    IF v_cmd.status<>'CLOSED' OR v_cmd.door_opened_at IS NULL OR v_cmd.door_closed_at IS NULL
      OR v_cmd.door_closed_at<v_cmd.door_opened_at
      OR NOT EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=v_cmd.cmd_id AND i.kiosk_id=v.kiosk_id
        AND i.event='CLOSED' AND i.disposition='APPLIED') THEN RETURN 'REPAIR_DOOR_CYCLE_REQUIRED'; END IF;
  END IF;

  IF NOT v_has_snapshot THEN
    IF v.kiosk_status<>'ONLINE' OR v.last_heartbeat_at IS NULL
      OR v.last_heartbeat_at<=clock_timestamp()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
    THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slot_sensor_capabilities c WHERE c.slot_id=v.slot_id
      AND c.kiosk_id=v.kiosk_id AND c.relay_channel=v.relay_channel AND c.enabled AND c.door_verified AND c.presence_verified)
    THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
    SELECT p.received_at,p.observed_at,p.states[v.relay_channel+1]
      INTO v_presence_received,v_presence_observed,v_presence_state
    FROM kiosk_ops.flower_presence p WHERE p.kiosk_id=v.kiosk_id;
    v_freshness_reference:=clock_timestamp();
  END IF;

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
    IF v_has_snapshot AND (sn.recovered_at IS NULL OR v_presence_received<sn.recovered_at
      OR v_presence_observed<extract(epoch FROM sn.recovered_at)*1000)
    THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
  END IF;

  IF v_presence_received IS NULL OR v_presence_received<=v_freshness_reference-interval '45 seconds'
    OR v_presence_observed<extract(epoch FROM v_freshness_reference)*1000-45000
    OR v_presence_observed>extract(epoch FROM v_freshness_reference)*1000+5000
    OR (NOT v_recovered AND (v_presence_received<v_closed
        OR v_presence_observed<extract(epoch FROM v_closed)*1000))
    OR (v_recovered AND NOT v_has_snapshot AND EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms
        WHERE ms.slot_id=v.slot_id AND ms.stage='Recovered'
          AND (v_presence_received<ms.updated_at
            OR v_presence_observed<extract(epoch FROM ms.updated_at)*1000)))
  THEN RETURN 'REPAIR_SENSOR_STALE'; END IF;
  IF v_presence_state IS DISTINCT FROM (CASE WHEN v.bouquet_id IS NULL THEN 0 ELSE 1 END)
  THEN RETURN 'REPAIR_SENSOR_MISMATCH'; END IF;
  RETURN NULL;
END $$;

CREATE OR REPLACE FUNCTION flow.technical_repair_verified(p_task uuid)
RETURNS boolean LANGUAGE sql AS $$
  SELECT CASE WHEN t.status='SUBMITTED' THEN flow.technical_repair_blocker(t.id) IS NULL
    ELSE EXISTS (
      SELECT 1 FROM kiosk_ops.technical_repair_access g
      JOIN kiosk_ops.staff_tasks task ON task.id=g.task_id
      JOIN kiosk_ops.kiosks k ON k.id=task.kiosk_id AND k.seller_id=g.seller_id
      JOIN kiosk_ops.slots s ON s.id=g.slot_id AND s.kiosk_id=k.id AND s.current_seller_id=g.seller_id
      JOIN kiosk_ops.slot_sensor_capabilities c ON c.slot_id=s.id AND c.kiosk_id=k.id AND c.relay_channel=s.relay_channel
      JOIN kiosk_ops.unlock_tokens token ON token.cmd_id=g.latest_cmd_id AND token.kiosk_id=k.id AND token.slot_id=s.id
      JOIN kiosk_ops.flower_presence presence ON presence.kiosk_id=k.id
      WHERE g.task_id=p_task AND s.status='FAULT' AND s.hold_order_id IS NULL AND s.hold_until IS NULL
        AND k.status='ONLINE' AND k.last_heartbeat_at>clock_timestamp()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
        AND c.enabled AND c.door_verified AND c.presence_verified
        AND token.status='CLOSED' AND token.door_opened_at IS NOT NULL AND token.door_closed_at>=token.door_opened_at
        AND EXISTS (SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.kiosk_id=k.id AND i.cmd_id=token.cmd_id AND i.event='CLOSED' AND i.disposition='APPLIED')
        AND presence.received_at>=token.door_closed_at AND presence.received_at>clock_timestamp()-interval '2 minutes'
        AND presence.states[s.relay_channel+1]=CASE WHEN s.bouquet_id IS NULL THEN 0 ELSE 1 END
    ) END
  FROM kiosk_ops.staff_tasks t WHERE t.id=p_task
$$;

CREATE OR REPLACE FUNCTION flow.submit_technical_repair(p_id uuid,p_task uuid,p_actor uuid,p_version integer,p_report text)
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
    result:=flow.submit_technical_repair_door(p_id,p_task,p_actor,p_version,p_report);
  ELSE
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.manager_kiosks m JOIN identity.users u ON u.id=m.manager_id
        WHERE m.kiosk_id=t.kiosk_id AND m.manager_id=t.assigned_by AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE')
    THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
    IF NOT EXISTS(SELECT 1 FROM ordering.disputes WHERE id=t.incident_id AND status='OPEN')
    THEN RAISE EXCEPTION 'REPAIR_SLOT_STATE_CONFLICT' USING ERRCODE='P4090'; END IF;
    result:=flow.transition_staff_task(p_task,p_actor,p_version,'SUBMITTED',p_report);
    INSERT INTO kiosk_ops.staff_incident_actions(id,task_id,actor_id,incident_id,request_version,result_version,report)
      VALUES(p_id,p_task,p_actor,t.incident_id,p_version,result,btrim(p_report));
    PERFORM flow.audit(p_actor,'TECHNICAL_RECOVERY_VERIFIED','staff_tasks',p_task,jsonb_build_object('incidentId',t.incident_id));
  END IF;

  blocker:=flow.technical_repair_blocker(p_task);
  IF blocker IS NOT NULL THEN RAISE EXCEPTION '%',blocker USING ERRCODE='P4090'; END IF;
  PERFORM flow.capture_technical_repair_readiness(p_task,p_id,result);
  RETURN result;
END $$;

COMMIT;
