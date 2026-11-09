BEGIN;

CREATE TABLE kiosk_ops.technical_repair_access (
    task_id uuid PRIMARY KEY REFERENCES kiosk_ops.staff_tasks(id) ON DELETE CASCADE,
    seller_id uuid NOT NULL REFERENCES identity.sellers(id) ON DELETE CASCADE,
    slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id) ON DELETE CASCADE,
    technician_id uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    approved_by uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
    expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    latest_cmd_id uuid,
    reviewed_at timestamptz
);

CREATE TABLE kiosk_ops.technical_repair_commands (
    cmd_id uuid PRIMARY KEY REFERENCES kiosk_ops.unlock_tokens(cmd_id) ON DELETE CASCADE,
    task_id uuid NOT NULL REFERENCES kiosk_ops.staff_tasks(id) ON DELETE CASCADE
);

CREATE OR REPLACE FUNCTION flow.technical_repair_scope(p_task uuid, p_actor uuid)
RETURNS TABLE (kiosk_id uuid, slot_id uuid, seller_id uuid, slot_code text)
LANGUAGE sql AS $$
    SELECT t.kiosk_id, s.id, g.seller_id, s.slot_code
    FROM kiosk_ops.technical_repair_access g
    JOIN kiosk_ops.staff_tasks t ON t.id=g.task_id AND t.assignee_id=g.technician_id
    JOIN ordering.disputes d ON d.id=t.incident_id AND d.kiosk_id=t.kiosk_id AND d.slot_id=g.slot_id
    JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id AND k.seller_id=g.seller_id
    JOIN kiosk_ops.slots s ON s.id=g.slot_id AND s.kiosk_id=k.id AND s.current_seller_id=g.seller_id
    JOIN kiosk_ops.slot_sensor_capabilities c ON c.slot_id=s.id AND c.kiosk_id=k.id
        AND c.relay_channel=s.relay_channel AND c.enabled AND c.door_verified AND c.presence_verified
    JOIN identity.users tech ON tech.id=g.technician_id AND tech.role='TECHNICIAN' AND tech.status='ACTIVE'
    JOIN identity.users owner ON owner.id=g.approved_by AND owner.role='SELLER' AND owner.seller_id=g.seller_id AND owner.status='ACTIVE'
    JOIN identity.users manager ON manager.id=t.assigned_by AND manager.role='OPERATIONS_MANAGER' AND manager.status='ACTIVE'
    JOIN kiosk_ops.manager_kiosks scope ON scope.kiosk_id=k.id AND scope.manager_id=manager.id
    WHERE g.task_id=p_task AND g.technician_id=p_actor AND g.revoked_at IS NULL AND g.expires_at>clock_timestamp()
      AND t.kind='INCIDENT' AND t.status='IN_PROGRESS' AND d.status='OPEN'
      AND k.status='ONLINE' AND k.last_heartbeat_at>clock_timestamp()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
      AND s.status='FAULT' AND s.hold_order_id IS NULL AND s.hold_until IS NULL
      AND (s.bouquet_id IS NULL OR flow.check_bouquet_active_order(s.bouquet_id) IS NULL)
    FOR SHARE OF g,t,d,k,s,c,tech,owner,manager,scope;
$$;

CREATE OR REPLACE FUNCTION flow.approve_technical_repair(p_task uuid, p_actor uuid)
RETURNS timestamptz LANGUAGE plpgsql AS $$
DECLARE
    v record;
    v_expiry timestamptz;
BEGIN
    SELECT t.assignee_id, s.id AS slot_id, k.seller_id INTO v
    FROM kiosk_ops.staff_tasks t
    JOIN ordering.disputes d ON d.id=t.incident_id AND d.kiosk_id=t.kiosk_id
    JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id
    JOIN kiosk_ops.slots s ON s.id=d.slot_id AND s.kiosk_id=k.id AND s.current_seller_id=k.seller_id
    JOIN kiosk_ops.slot_sensor_capabilities c ON c.slot_id=s.id AND c.kiosk_id=k.id
        AND c.relay_channel=s.relay_channel AND c.enabled AND c.door_verified AND c.presence_verified
    JOIN identity.users u ON u.id=p_actor AND u.role='SELLER' AND u.status='ACTIVE' AND u.seller_id=k.seller_id
    JOIN identity.users tech ON tech.id=t.assignee_id AND tech.role='TECHNICIAN' AND tech.status='ACTIVE'
    JOIN identity.users manager ON manager.id=t.assigned_by AND manager.role='OPERATIONS_MANAGER' AND manager.status='ACTIVE'
    JOIN kiosk_ops.manager_kiosks scope ON scope.kiosk_id=k.id AND scope.manager_id=manager.id
    WHERE t.id=p_task AND t.kind='INCIDENT' AND t.status IN ('ASSIGNED','IN_PROGRESS') AND d.status='OPEN'
      AND s.status='FAULT' AND s.hold_order_id IS NULL AND s.hold_until IS NULL
      AND (s.bouquet_id IS NULL OR flow.check_bouquet_active_order(s.bouquet_id) IS NULL)
    FOR UPDATE OF s
    FOR SHARE OF t,d,k,c,u,tech,manager,scope;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF EXISTS (
        SELECT 1 FROM kiosk_ops.technical_repair_access other
        WHERE other.slot_id=v.slot_id AND other.task_id<>p_task
          AND other.revoked_at IS NULL AND other.expires_at>clock_timestamp()
    ) OR EXISTS (
        SELECT 1 FROM kiosk_ops.unlock_tokens token
        WHERE token.slot_id=v.slot_id AND token.status IN ('ISSUED','SENT','ACKED','OPENED')
    ) THEN
        RAISE EXCEPTION 'Slot is still in use' USING ERRCODE='40001';
    END IF;

    INSERT INTO kiosk_ops.technical_repair_access AS g (task_id,seller_id,slot_id,technician_id,approved_by,expires_at)
    VALUES(p_task,v.seller_id,v.slot_id,v.assignee_id,p_actor,clock_timestamp()+interval '30 minutes')
    ON CONFLICT(task_id) DO UPDATE SET seller_id=excluded.seller_id,slot_id=excluded.slot_id,
        technician_id=excluded.technician_id,approved_by=excluded.approved_by,expires_at=excluded.expires_at,
        revoked_at=NULL,latest_cmd_id=NULL,reviewed_at=NULL
    WHERE g.revoked_at IS NOT NULL OR g.expires_at<=clock_timestamp()
      OR g.technician_id IS DISTINCT FROM excluded.technician_id
    RETURNING expires_at INTO v_expiry;

    IF v_expiry IS NULL THEN
        SELECT expires_at INTO v_expiry FROM kiosk_ops.technical_repair_access WHERE task_id=p_task;
        RETURN v_expiry;
    END IF;
    PERFORM flow.audit(p_actor,'TECHNICAL_REPAIR_APPROVED','staff_tasks',p_task,
        jsonb_build_object('slotId',v.slot_id,'technicianId',v.assignee_id,'expiresAt',v_expiry));
    RETURN v_expiry;
END $$;

CREATE OR REPLACE FUNCTION flow.open_technical_repair(p_task uuid,p_actor uuid,p_kiosk uuid,p_slot uuid)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
    v record;
    v_command uuid;
    v_expiry timestamptz;
BEGIN
    SELECT * INTO v FROM flow.technical_repair_scope(p_task,p_actor);
    IF NOT FOUND OR v.kiosk_id<>p_kiosk OR v.slot_id<>p_slot THEN RETURN NULL; END IF;

    SELECT expires_at INTO v_expiry FROM kiosk_ops.technical_repair_access WHERE task_id=p_task FOR UPDATE;
    PERFORM 1 FROM kiosk_ops.slots WHERE id=p_slot AND status='FAULT' FOR UPDATE;
    IF NOT FOUND OR v_expiry<=clock_timestamp() THEN RETURN NULL; END IF;
    IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id=p_slot AND status IN ('ISSUED','SENT','ACKED','OPENED')) THEN
        RAISE EXCEPTION 'Slot is still in use' USING ERRCODE='40001';
    END IF;
    INSERT INTO kiosk_ops.unlock_tokens (kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,issued_at,expires_at)
    VALUES(p_kiosk,p_slot,'SELLER_ACCESS',p_actor,encode(sha256(convert_to(public.uuid_v7()::text,'UTF8')),'hex'),
        clock_timestamp(),least(v_expiry,clock_timestamp()+interval '60 seconds'))
    RETURNING cmd_id INTO v_command;
    INSERT INTO kiosk_ops.technical_repair_commands(cmd_id,task_id) VALUES(v_command,p_task);
    UPDATE kiosk_ops.technical_repair_access SET latest_cmd_id=v_command WHERE task_id=p_task;
    PERFORM flow.audit(p_actor,'TECHNICAL_REPAIR_UNLOCK_ISSUED','staff_tasks',p_task,
        jsonb_build_object('slotId',p_slot,'cmdId',v_command));
    RETURN v_command;
END $$;

CREATE OR REPLACE FUNCTION flow.technical_repair_verified(p_task uuid)
RETURNS boolean LANGUAGE sql AS $$
    SELECT EXISTS (
        SELECT 1 FROM kiosk_ops.technical_repair_access g
        JOIN kiosk_ops.staff_tasks t ON t.id=g.task_id
        JOIN kiosk_ops.kiosks k ON k.id=t.kiosk_id AND k.seller_id=g.seller_id
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
    );
$$;

CREATE OR REPLACE FUNCTION flow.submit_technical_repair(p_id uuid,p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE v_version integer; v_incident uuid; previous kiosk_ops.staff_incident_actions;
BEGIN
    SELECT t.incident_id INTO v_incident FROM kiosk_ops.staff_tasks t
    JOIN identity.users tech ON tech.id=t.assignee_id AND tech.role='TECHNICIAN' AND tech.status='ACTIVE'
    WHERE t.id=p_task AND t.kind='INCIDENT' AND t.assignee_id=p_actor FOR UPDATE OF t;
    IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
    SELECT * INTO previous FROM kiosk_ops.staff_incident_actions WHERE id=p_id;
    IF FOUND THEN
        IF previous.task_id<>p_task OR previous.actor_id<>p_actor
          OR previous.request_version<>p_version OR previous.report<>btrim(p_report) THEN
            RAISE EXCEPTION 'Operation ID already used' USING ERRCODE='40001';
        END IF;
        RETURN previous.result_version;
    END IF;
    PERFORM 1 FROM kiosk_ops.technical_repair_access WHERE task_id=p_task AND technician_id=p_actor FOR UPDATE;
    IF NOT FOUND OR NOT flow.technical_repair_verified(p_task) THEN
        RAISE EXCEPTION 'Repair requires a verified door cycle and matching sensor state' USING ERRCODE='40001';
    END IF;
    v_version := flow.transition_staff_task(p_task,p_actor,p_version,'SUBMITTED',p_report);
    UPDATE kiosk_ops.technical_repair_access SET revoked_at=clock_timestamp() WHERE task_id=p_task;
    INSERT INTO kiosk_ops.staff_incident_actions(id,task_id,actor_id,incident_id,request_version,result_version,report)
    VALUES(p_id,p_task,p_actor,v_incident,p_version,v_version,btrim(p_report));
    RETURN v_version;
END $$;

CREATE OR REPLACE FUNCTION flow.accept_technical_repair(p_task uuid,p_actor uuid,p_version integer,p_report text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE v_incident uuid; v_version integer;
BEGIN
    SELECT t.incident_id INTO v_incident FROM kiosk_ops.staff_tasks t
    JOIN kiosk_ops.manager_kiosks m ON m.kiosk_id=t.kiosk_id AND m.manager_id=p_actor
    JOIN identity.users u ON u.id=p_actor AND u.role='OPERATIONS_MANAGER' AND u.status='ACTIVE'
    JOIN kiosk_ops.technical_repair_access g ON g.task_id=t.id AND g.technician_id=t.assignee_id AND g.revoked_at IS NOT NULL
    WHERE t.id=p_task AND t.kind='INCIDENT' AND t.status='SUBMITTED'
    FOR UPDATE OF t,g;
    IF NOT FOUND THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
    PERFORM 1 FROM kiosk_ops.slots s JOIN kiosk_ops.technical_repair_access g ON g.slot_id=s.id
    WHERE g.task_id=p_task FOR UPDATE OF s;
    IF NOT flow.technical_repair_verified(p_task) THEN
        RAISE EXCEPTION 'Repair evidence is no longer current' USING ERRCODE='40001';
    END IF;
    PERFORM flow.resolve_device_fault(v_incident,p_actor,btrim(p_report));
    v_version := flow.transition_staff_task(p_task,p_actor,p_version,'COMPLETED',p_report);
    UPDATE kiosk_ops.technical_repair_access SET reviewed_at=clock_timestamp() WHERE task_id=p_task;
    RETURN v_version;
END $$;

CREATE OR REPLACE FUNCTION flow.revoke_technical_repair_on_task_change()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.assignee_id IS DISTINCT FROM OLD.assignee_id OR NEW.status IN ('SUBMITTED','COMPLETED','CANCELLED') THEN
        UPDATE kiosk_ops.technical_repair_access SET revoked_at=clock_timestamp()
        WHERE task_id=NEW.id AND revoked_at IS NULL;
    END IF;
    RETURN NEW;
END $$;

CREATE TRIGGER revoke_technical_repair_on_task_change
AFTER UPDATE OF assignee_id,status ON kiosk_ops.staff_tasks
FOR EACH ROW EXECUTE FUNCTION flow.revoke_technical_repair_on_task_change();

COMMIT;
