BEGIN;

ALTER TABLE kiosk_ops.maintenance_access_challenges
  DROP CONSTRAINT maintenance_access_challenges_operation_check,
  ADD CONSTRAINT maintenance_access_challenges_operation_check
    CHECK (operation IN ('START', 'OPEN_NEXT', 'OPEN_FOR_CHECK', 'RETRY', 'REPAIR', 'PREORDER_LOAD'));

CREATE TABLE kiosk_ops.preorder_loading_sessions (
    challenge_id uuid PRIMARY KEY REFERENCES kiosk_ops.maintenance_access_challenges(id),
    request_id uuid NOT NULL REFERENCES ordering.web_requests(id),
    slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id),
    command_id uuid NOT NULL UNIQUE REFERENCES kiosk_ops.unlock_tokens(cmd_id),
    expires_at timestamptz NOT NULL,
    completed_bouquet_id uuid REFERENCES kiosk_ops.bouquets(id),
    completed_qr text,
    CHECK ((completed_bouquet_id IS NULL) = (completed_qr IS NULL))
);

CREATE INDEX preorder_loading_sessions_request_idx ON kiosk_ops.preorder_loading_sessions(request_id);
CREATE INDEX preorder_loading_sessions_slot_idx ON kiosk_ops.preorder_loading_sessions(slot_id);

CREATE FUNCTION flow.open_preorder_loading(p_challenge uuid, p_request uuid)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
    binding kiosk_ops.maintenance_access_challenges;
    target record;
    command uuid;
BEGIN
    SELECT * INTO binding FROM kiosk_ops.maintenance_access_challenges
    WHERE id = p_challenge AND operation = 'PREORDER_LOAD' AND consumed_at IS NOT NULL
      AND expires_at > clock_timestamp() AND payload->>'RequestId' = p_request::text FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;

    IF binding.task_id IS NOT NULL THEN
        PERFORM 1 FROM kiosk_ops.staff_tasks WHERE id = binding.task_id FOR UPDATE;
    END IF;
    PERFORM 1 FROM ordering.web_requests WHERE id = p_request FOR UPDATE;
    PERFORM 1 FROM ordering.orders WHERE id =
        (SELECT order_id FROM ordering.web_requests WHERE id = p_request) FOR UPDATE;
    PERFORM 1 FROM kiosk_ops.slots WHERE id = binding.slot_id FOR UPDATE;
    SELECT * INTO target FROM flow.preorder_load_scope(p_request, binding.slot_id, binding.actor_id, binding.task_id);
    IF NOT FOUND OR target.kiosk_id <> binding.kiosk_id OR target.seller_id <> binding.seller_id
        OR target.actor_role <> binding.actor_role THEN RETURN NULL; END IF;

    IF EXISTS (
        SELECT 1 FROM kiosk_ops.preorder_loading_sessions s
        WHERE (s.request_id = p_request OR s.slot_id = binding.slot_id)
          AND s.completed_bouquet_id IS NULL AND s.expires_at > clock_timestamp()
    ) OR EXISTS (
        SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.slot_id = binding.slot_id
          AND (t.status = 'OPENED' OR (t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at > clock_timestamp()))
    ) THEN
        RAISE EXCEPTION 'Preorder or slot is already being handled' USING ERRCODE = '40001';
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM kiosk_ops.flower_presence p
        WHERE p.kiosk_id = binding.kiosk_id AND p.states[target.relay_channel + 1] = 0
          AND p.received_at >= clock_timestamp() - interval '45 seconds'
          AND p.observed_at >= extract(epoch FROM clock_timestamp() - interval '45 seconds') * 1000
    ) THEN
        RAISE EXCEPTION 'Fresh empty slot reading required' USING ERRCODE = '22023';
    END IF;

    INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,issued_at,expires_at)
    VALUES(binding.kiosk_id,binding.slot_id,'SELLER_ACCESS',binding.actor_id,
           encode(sha256(convert_to(public.uuid_v7()::text,'UTF8')),'hex'),
           clock_timestamp(),clock_timestamp()+interval '60 seconds')
    RETURNING cmd_id INTO command;

    INSERT INTO kiosk_ops.preorder_loading_sessions(challenge_id,request_id,slot_id,command_id,expires_at)
    VALUES(p_challenge,p_request,binding.slot_id,command,clock_timestamp()+interval '5 minutes');
    RETURN command;
END;
$$;

CREATE FUNCTION flow.preorder_loading_dispatch_allowed(p_command uuid)
RETURNS boolean LANGUAGE sql STABLE AS $$
    SELECT EXISTS (
        SELECT 1 FROM kiosk_ops.preorder_loading_sessions s
        JOIN kiosk_ops.maintenance_access_challenges c ON c.id=s.challenge_id
        CROSS JOIN LATERAL flow.preorder_load_scope(s.request_id,s.slot_id,c.actor_id,c.task_id) scope
        WHERE s.command_id=p_command AND s.completed_bouquet_id IS NULL AND s.expires_at > CURRENT_TIMESTAMP
          AND scope.kiosk_id=c.kiosk_id AND scope.seller_id=c.seller_id AND scope.actor_role=c.actor_role
    );
$$;

CREATE FUNCTION flow.complete_preorder_loading(p_challenge uuid, p_request uuid, p_slot uuid,
                                                p_actor uuid, p_task uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
    session_row kiosk_ops.preorder_loading_sessions;
    binding kiosk_ops.maintenance_access_challenges;
    target record;
    bouquet uuid;
BEGIN
    IF p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN
        RAISE EXCEPTION 'Invalid bouquet QR' USING ERRCODE = '22023';
    END IF;
    SELECT * INTO session_row FROM kiosk_ops.preorder_loading_sessions
    WHERE challenge_id=p_challenge AND request_id=p_request AND slot_id=p_slot FOR UPDATE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    SELECT * INTO binding FROM kiosk_ops.maintenance_access_challenges
    WHERE id=p_challenge AND actor_id=p_actor AND task_id IS NOT DISTINCT FROM p_task;
    IF NOT FOUND THEN RETURN NULL; END IF;

    IF p_task IS NOT NULL THEN
        PERFORM 1 FROM kiosk_ops.staff_tasks
        WHERE id=p_task AND assignee_id=p_actor AND status='IN_PROGRESS' FOR UPDATE;
        IF NOT FOUND THEN RETURN NULL; END IF;
    END IF;
    PERFORM 1 FROM identity.users
    WHERE id=p_actor AND role=binding.actor_role AND seller_id=binding.seller_id AND status='ACTIVE' FOR SHARE;
    IF NOT FOUND THEN RETURN NULL; END IF;
    IF session_row.completed_bouquet_id IS NOT NULL THEN
        IF session_row.completed_qr=btrim(p_qr) THEN RETURN session_row.completed_bouquet_id; END IF;
        RAISE EXCEPTION 'Preorder already loaded' USING ERRCODE = '40001';
    END IF;
    IF session_row.expires_at <= clock_timestamp() THEN
        RAISE EXCEPTION 'Preorder loading session expired' USING ERRCODE = '22023';
    END IF;

    PERFORM 1 FROM ordering.web_requests WHERE id=p_request FOR UPDATE;
    PERFORM 1 FROM ordering.orders WHERE id=(SELECT order_id FROM ordering.web_requests WHERE id=p_request) FOR UPDATE;
    PERFORM 1 FROM kiosk_ops.slots WHERE id=p_slot FOR UPDATE;
    SELECT * INTO target FROM flow.preorder_load_scope(p_request,p_slot,p_actor,p_task);
    IF NOT FOUND OR target.kiosk_id <> binding.kiosk_id OR target.seller_id <> binding.seller_id
        OR target.actor_role <> binding.actor_role THEN RETURN NULL; END IF;

    IF NOT EXISTS (
        SELECT 1 FROM kiosk_ops.unlock_tokens token
        JOIN kiosk_ops.mqtt_inbox ack ON ack.cmd_id=token.cmd_id AND ack.kiosk_id=token.kiosk_id
        JOIN kiosk_ops.flower_presence p ON p.kiosk_id=token.kiosk_id
        WHERE token.cmd_id=session_row.command_id AND token.kiosk_id=binding.kiosk_id AND token.slot_id=p_slot
          AND token.status IN ('ACKED','OPENED','CLOSED')
          AND ack.event='ACK' AND ack.disposition='APPLIED' AND ack.occurred_at>=date_trunc('second',token.issued_at)
          AND p.states[target.relay_channel + 1] = 1
          AND p.observed_at > extract(epoch FROM greatest(ack.occurred_at,token.issued_at)) * 1000
          AND p.received_at >= ack.received_at
          AND p.received_at >= clock_timestamp() - interval '45 seconds'
          AND p.observed_at >= extract(epoch FROM clock_timestamp() - interval '45 seconds') * 1000
    ) THEN
        RAISE EXCEPTION 'Fresh flower reading after unlock required' USING ERRCODE = '22023';
    END IF;

    IF p_task IS NULL THEN
        bouquet := flow.web_fulfill(p_request,binding.seller_id,p_actor,p_slot,p_qr);
    ELSE
        bouquet := flow.staff_fulfill_preorder(p_task,p_request,p_actor,p_slot,p_qr);
    END IF;
    UPDATE kiosk_ops.preorder_loading_sessions SET completed_bouquet_id=bouquet, completed_qr=btrim(p_qr)
    WHERE challenge_id=p_challenge;
    RETURN bouquet;
END;
$$;

COMMIT;
