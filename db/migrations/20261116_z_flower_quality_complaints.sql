BEGIN;

INSERT INTO kiosk_ops.system_settings(key,value,description) VALUES
  ('quality_replacement_hours','24','Thời hạn giữ hoa thay kể từ khi nạp, giới hạn thêm bởi hạn bán của bó hoa (giờ)')
  ON CONFLICT(key) DO NOTHING;

-- After-sale handling supplements COMPLAINT; original orders, prices and payments stay intact.
CREATE TABLE ordering.complaint_handling (
  complaint_id uuid PRIMARY KEY REFERENCES ordering.disputes(id),
  state text NOT NULL DEFAULT 'REPORTED' CHECK (state IN
    ('REPORTED','ACCEPTED','IN_PROGRESS','REVIEW_REQUIRED','REPLACEMENT_READY','ATTENTION_REQUIRED','REFUND_REVIEW','RESOLVED')),
  version integer NOT NULL DEFAULT 1,
  accepted_by uuid,
  accepted_at timestamptz,
  current_task_id uuid REFERENCES kiosk_ops.staff_tasks(id),
  outcome text CHECK (outcome IN ('EXPLANATION','REPLACEMENT','REFUND_REVIEW')),
  resolution text,
  reviewed_by uuid,
  updated_at timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE TABLE kiosk_ops.staff_quality_tasks (
  task_id uuid PRIMARY KEY REFERENCES kiosk_ops.staff_tasks(id),
  complaint_id uuid NOT NULL REFERENCES ordering.complaint_handling(complaint_id),
  order_item_id uuid NOT NULL REFERENCES ordering.order_items(id),
  staff_report text
);
CREATE INDEX staff_quality_tasks_complaint_idx ON kiosk_ops.staff_quality_tasks(complaint_id,task_id);
CREATE TABLE kiosk_ops.quality_replacements (
  task_id uuid PRIMARY KEY REFERENCES kiosk_ops.staff_quality_tasks(task_id),
  complaint_id uuid NOT NULL REFERENCES ordering.complaint_handling(complaint_id),
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  product_id uuid NOT NULL REFERENCES catalog.flower_products(id),
  state text NOT NULL DEFAULT 'PLANNED' CHECK (state IN ('PLANNED','LOADED','READY','COLLECTED','CANCELLED','EXPIRED')),
  bouquet_id uuid UNIQUE REFERENCES kiosk_ops.bouquets(id),
  expires_at timestamptz,
  ready_at timestamptz,
  collected_at timestamptz
);
CREATE UNIQUE INDEX quality_replacement_live_slot ON kiosk_ops.quality_replacements(slot_id)
  WHERE state IN ('PLANNED','LOADED','READY');
CREATE UNIQUE INDEX quality_replacement_live_complaint ON kiosk_ops.quality_replacements(complaint_id)
  WHERE state IN ('PLANNED','LOADED','READY');
CREATE TABLE kiosk_ops.quality_pickup_commands (
  cmd_id uuid PRIMARY KEY REFERENCES kiosk_ops.unlock_tokens(cmd_id),
  task_id uuid NOT NULL REFERENCES kiosk_ops.quality_replacements(task_id),
  attempt_id uuid NOT NULL UNIQUE
);

CREATE FUNCTION flow.initialize_quality_complaint() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.kind='COMPLAINT' THEN
    INSERT INTO ordering.complaint_handling(complaint_id,state)
      VALUES(NEW.id,CASE WHEN NEW.status='OPEN' THEN 'REPORTED' ELSE 'RESOLVED' END);
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER initialize_quality_complaint AFTER INSERT ON ordering.disputes
  FOR EACH ROW EXECUTE FUNCTION flow.initialize_quality_complaint();
INSERT INTO ordering.complaint_handling(complaint_id,state)
SELECT id,CASE WHEN status='OPEN' THEN 'REPORTED' ELSE 'RESOLVED' END FROM ordering.disputes WHERE kind='COMPLAINT';

CREATE FUNCTION flow.quality_task_live(p_task uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT NOT EXISTS (SELECT 1 FROM kiosk_ops.staff_quality_tasks q WHERE q.task_id=p_task)
    OR EXISTS (
      SELECT 1 FROM kiosk_ops.staff_quality_tasks q
      JOIN ordering.disputes d ON d.id=q.complaint_id AND d.kind='COMPLAINT' AND d.status='OPEN'
      JOIN ordering.complaint_handling h ON h.complaint_id=d.id AND h.current_task_id=q.task_id
      JOIN ordering.orders o ON o.id=d.order_id AND o.status='DISPUTED' AND o.completed_at IS NOT NULL
      WHERE q.task_id=p_task AND h.state IN ('ACCEPTED','IN_PROGRESS','REVIEW_REQUIRED','ATTENTION_REQUIRED')
        AND NOT EXISTS(SELECT 1 FROM payment.payments p WHERE p.order_id=o.id AND p.kind='REFUND' AND p.status IN ('PENDING','SUCCEEDED'))
    );
$$;

CREATE FUNCTION flow.quality_seller_lock(p_complaint uuid,p_actor uuid)
RETURNS ordering.complaint_handling LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling;
BEGIN
  PERFORM 1 FROM ordering.disputes d JOIN ordering.orders o ON o.id=d.order_id
    JOIN identity.users u ON u.id=p_actor AND u.seller_id=o.seller_id AND u.role='SELLER' AND u.status='ACTIVE'
    JOIN identity.sellers sel ON sel.id=u.seller_id AND sel.status='ACTIVE'
    WHERE d.id=p_complaint AND d.kind='COMPLAINT' FOR UPDATE OF d FOR SHARE OF u,sel;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy phản ánh của shop.' USING ERRCODE='P0002'; END IF;
  SELECT * INTO h FROM ordering.complaint_handling WHERE complaint_id=p_complaint FOR UPDATE;
  IF (SELECT status FROM ordering.disputes WHERE id=p_complaint)<>'OPEN' THEN
    RAISE EXCEPTION 'Phản ánh đã được giải quyết.' USING ERRCODE='40001';
  END IF;
  PERFORM 1 FROM ordering.orders o JOIN ordering.disputes d ON d.order_id=o.id
    WHERE d.id=p_complaint AND o.status='DISPUTED' AND o.completed_at IS NOT NULL FOR UPDATE OF o;
  IF NOT FOUND THEN RAISE EXCEPTION 'Chỉ xử lý chất lượng sau khi khách đã nhận hoa.' USING ERRCODE='40001'; END IF;
  IF EXISTS(SELECT 1 FROM payment.payments p JOIN ordering.disputes d ON d.order_id=p.order_id
    WHERE d.id=p_complaint AND p.kind='REFUND' AND p.status IN ('PENDING','SUCCEEDED')) THEN
    RAISE EXCEPTION 'Đơn đã được chuyển sang quy trình hoàn tiền.' USING ERRCODE='40001';
  END IF;
  RETURN h;
END $$;

CREATE FUNCTION flow.accept_quality_complaint(p_complaint uuid,p_actor uuid,p_version integer)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling;
BEGIN
  h:=flow.quality_seller_lock(p_complaint,p_actor);
  IF h.accepted_by=p_actor AND h.state='ACCEPTED' THEN RETURN h.version; END IF;
  IF h.version<>p_version OR h.state<>'REPORTED' THEN
    RAISE EXCEPTION 'Tải lại phản ánh trước khi tiếp nhận.' USING ERRCODE='40001'; END IF;
  UPDATE ordering.complaint_handling SET state='ACCEPTED',accepted_by=p_actor,accepted_at=public.app_now(),
    version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.audit(p_actor,'QUALITY_COMPLAINT_ACCEPTED','disputes',p_complaint,NULL);
  RETURN h.version+1;
END $$;

CREATE FUNCTION flow.assign_quality_task(p_complaint uuid,p_actor uuid,p_version integer,
  p_task uuid,p_staff uuid,p_item uuid,p_instructions text) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling; o ordering.orders;
BEGIN
  h:=flow.quality_seller_lock(p_complaint,p_actor);
  SELECT od.* INTO o FROM ordering.orders od JOIN ordering.disputes d ON d.order_id=od.id WHERE d.id=p_complaint;
  IF EXISTS(SELECT 1 FROM kiosk_ops.staff_quality_tasks q JOIN kiosk_ops.staff_tasks t ON t.id=q.task_id
    WHERE q.task_id=p_task AND q.complaint_id=p_complaint AND q.order_item_id=p_item
      AND t.assignee_id=p_staff AND t.instructions=btrim(p_instructions)) THEN RETURN h.version; END IF;
  IF h.version<>p_version OR h.accepted_at IS NULL OR h.state IN ('REPLACEMENT_READY','REFUND_REVIEW')
    OR EXISTS(SELECT 1 FROM kiosk_ops.staff_tasks WHERE id=h.current_task_id AND status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED'))
    OR EXISTS(SELECT 1 FROM kiosk_ops.staff_tasks WHERE id=p_task) THEN
    RAISE EXCEPTION 'Đã có công việc đang mở hoặc hồ sơ đã thay đổi.' USING ERRCODE='40001'; END IF;
  IF NOT EXISTS(SELECT 1 FROM ordering.order_items WHERE id=p_item AND order_id=o.id AND item_type='BOUQUET' AND line_status='ACTIVE') THEN
    RAISE EXCEPTION 'Chọn đúng bó hoa trong đơn bị phản ánh.' USING ERRCODE='22023'; END IF;
  PERFORM flow.assign_staff_task(p_task,p_actor,p_staff,'DELIVERY',o.kiosk_id,o.seller_id,NULL,p_instructions);
  INSERT INTO kiosk_ops.staff_quality_tasks(task_id,complaint_id,order_item_id) VALUES(p_task,p_complaint,p_item);
  UPDATE ordering.complaint_handling SET state='IN_PROGRESS',current_task_id=p_task,outcome=NULL,resolution=NULL,
    reviewed_by=NULL,version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.audit(p_actor,'QUALITY_TASK_ASSIGNED','staff_tasks',p_task,jsonb_build_object('complaintId',p_complaint,'orderItemId',p_item));
  RETURN h.version+1;
END $$;

CREATE FUNCTION flow.plan_quality_replacement(p_complaint uuid,p_actor uuid,p_version integer,
  p_task_version integer,p_product uuid,p_slot uuid) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling; t kiosk_ops.staff_tasks;
BEGIN
  h:=flow.quality_seller_lock(p_complaint,p_actor);
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=h.current_task_id FOR UPDATE;
  IF EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=t.id AND product_id=p_product AND slot_id=p_slot AND state='PLANNED') THEN RETURN h.version; END IF;
  IF h.version<>p_version OR t.version<>p_task_version OR t.status NOT IN ('ASSIGNED','IN_PROGRESS')
    OR NOT flow.quality_task_live(t.id) THEN
    RAISE EXCEPTION 'Trả công việc về đang xử lý và tải lại trước khi chọn hoa thay.' USING ERRCODE='40001'; END IF;
  PERFORM 1 FROM kiosk_ops.slots s JOIN kiosk_ops.kiosks k ON k.id=s.kiosk_id
    JOIN kiosk_ops.slot_sensor_capabilities cap ON cap.slot_id=s.id AND cap.kiosk_id=k.id AND cap.relay_channel=s.relay_channel
    JOIN catalog.flower_products p ON p.id=p_product AND p.seller_id=t.seller_id AND p.status='ACTIVE' AND p.quantity>0
    WHERE s.id=p_slot AND k.id=t.kiosk_id AND k.seller_id=t.seller_id AND s.current_seller_id=t.seller_id
      AND s.zone='WALK_IN' AND s.status='RENTED_EMPTY' AND s.bouquet_id IS NULL
      AND s.hold_order_id IS NULL AND s.hold_until IS NULL AND flow.slot_sellable(s.id,t.seller_id)
      AND cap.enabled AND cap.door_verified AND cap.presence_verified
    FOR UPDATE OF s FOR SHARE OF k,p,cap;
  IF NOT FOUND THEN RAISE EXCEPTION 'Cần ô Walk-in trống có cảm biến và mẫu hoa còn tồn của shop tại kiosk này.' USING ERRCODE='40001'; END IF;
  INSERT INTO kiosk_ops.quality_replacements(task_id,complaint_id,slot_id,product_id) VALUES(t.id,p_complaint,p_slot,p_product);
  INSERT INTO kiosk_ops.staff_inventory_tasks(task_id,slot_id,action,product_id) VALUES(t.id,p_slot,'LOAD',p_product);
  UPDATE ordering.complaint_handling SET version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.audit(p_actor,'QUALITY_REPLACEMENT_PLANNED','staff_tasks',t.id,jsonb_build_object('slotId',p_slot,'productId',p_product));
  RETURN h.version+1;
END $$;

-- The monitored inventory flow validates OTP, current assignee, door cycle and fresh presence.
-- Reserve its output in the SAME transaction, before it can appear in any sale catalog.
CREATE FUNCTION flow.quality_loading_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r kiosk_ops.quality_replacements; o ordering.orders;
BEGIN
  SELECT * INTO r FROM kiosk_ops.quality_replacements WHERE task_id=NEW.id FOR UPDATE;
  IF NOT FOUND THEN
    IF EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE slot_id=NEW.slot_id AND state IN ('PLANNED','LOADED','READY')) THEN
      RAISE EXCEPTION 'Ô đã được giao để nạp hoa thay cho phản ánh khác.' USING ERRCODE='40001'; END IF;
    RETURN NEW;
  END IF;
  IF NEW.stage='NeedsInspection' THEN RETURN NEW; END IF;
  IF NOT flow.quality_task_live(NEW.id) OR r.state<>'PLANNED'
    OR NOT EXISTS(SELECT 1 FROM kiosk_ops.staff_tasks t JOIN identity.users u ON u.id=NEW.actor_id
      WHERE t.id=NEW.id AND t.assignee_id=u.id AND t.seller_id=u.seller_id AND u.status='ACTIVE'
        AND u.role='SELLER_STAFF' AND t.status='IN_PROGRESS' AND t.kiosk_id=NEW.kiosk_id
        AND NEW.slot_id=r.slot_id AND NEW.new_product_id=r.product_id AND NEW.action='LOAD') THEN
    RAISE EXCEPTION 'Công việc chất lượng không còn cho phép nạp hoa thay.' USING ERRCODE='40001'; END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quality_loading_guard BEFORE INSERT OR UPDATE ON kiosk_ops.slot_maintenance_sessions
  FOR EACH ROW WHEN (NEW.stage NOT IN ('Completed','Recovered')) EXECUTE FUNCTION flow.quality_loading_guard();

CREATE FUNCTION flow.reserve_loaded_quality_bouquet() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r kiosk_ops.quality_replacements; o ordering.orders; v_deadline timestamptz;
BEGIN
  SELECT * INTO r FROM kiosk_ops.quality_replacements WHERE task_id=NEW.id FOR UPDATE;
  IF NOT FOUND THEN RETURN NEW; END IF;
  IF NOT flow.quality_task_live(NEW.id) OR r.state<>'PLANNED' OR NEW.new_bouquet_id IS NULL
    OR NEW.slot_id<>r.slot_id OR NEW.new_product_id<>r.product_id THEN
    RAISE EXCEPTION 'Không xác nhận được hoa thay cho phản ánh đang mở.' USING ERRCODE='40001'; END IF;
  SELECT od.* INTO o FROM ordering.orders od JOIN ordering.disputes d ON d.order_id=od.id WHERE d.id=r.complaint_id FOR UPDATE OF od;
  SELECT least(b.sellable_until,public.app_now()+flow.cfg_hour('quality_replacement_hours')) INTO v_deadline FROM kiosk_ops.bouquets b
    WHERE b.id=NEW.new_bouquet_id AND b.product_id=r.product_id AND b.seller_id=o.seller_id AND b.status='STOCKED' FOR UPDATE;
  IF v_deadline IS NULL OR v_deadline<=public.app_now() THEN RAISE EXCEPTION 'Hoa thay đã hết hạn.' USING ERRCODE='40001'; END IF;
  UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=o.id,hold_until=v_deadline
    WHERE id=r.slot_id AND bouquet_id=NEW.new_bouquet_id AND status='STOCKED';
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô nạp hoa đã thay đổi.' USING ERRCODE='40001'; END IF;
  UPDATE kiosk_ops.bouquets SET status='SOLD' WHERE id=NEW.new_bouquet_id;
  UPDATE kiosk_ops.quality_replacements SET state='LOADED',bouquet_id=NEW.new_bouquet_id,expires_at=v_deadline WHERE task_id=NEW.id;
  PERFORM flow.audit(NEW.actor_id,'QUALITY_REPLACEMENT_LOADED','disputes',r.complaint_id,
    jsonb_build_object('taskId',NEW.id,'bouquetId',NEW.new_bouquet_id,'slotId',r.slot_id));
  RETURN NEW;
END $$;
CREATE TRIGGER reserve_loaded_quality_bouquet AFTER UPDATE OF stage ON kiosk_ops.slot_maintenance_sessions
  FOR EACH ROW WHEN (NEW.stage='Completed' AND OLD.stage IS DISTINCT FROM NEW.stage)
  EXECUTE FUNCTION flow.reserve_loaded_quality_bouquet();

CREATE FUNCTION flow.quality_task_transition_guard() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE q kiosk_ops.staff_quality_tasks; h ordering.complaint_handling;
BEGIN
  SELECT * INTO q FROM kiosk_ops.staff_quality_tasks WHERE task_id=NEW.id;
  IF NOT FOUND THEN RETURN NEW; END IF;
  SELECT * INTO h FROM ordering.complaint_handling WHERE complaint_id=q.complaint_id FOR UPDATE;
  IF NEW.assignee_id<>OLD.assignee_id OR NEW.status='CANCELLED' THEN
    IF EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions WHERE id=NEW.id AND stage NOT IN ('Completed','Recovered'))
      OR EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=NEW.id AND state IN ('LOADED','READY')) THEN
      RAISE EXCEPTION 'Phải xử lý phiên mở ô hoặc thu hồi hoa thay trước khi hủy/đổi người.' USING ERRCODE='40001'; END IF;
    IF NEW.status='CANCELLED' THEN
      UPDATE kiosk_ops.quality_replacements SET state='CANCELLED' WHERE task_id=NEW.id AND state='PLANNED';
    END IF;
  END IF;
  IF NEW.status='COMPLETED' AND (h.outcome IS NULL OR h.current_task_id<>NEW.id OR h.reviewed_by IS NULL) THEN
    RAISE EXCEPTION 'Seller phải duyệt kết quả qua hồ sơ phản ánh.' USING ERRCODE='40001'; END IF;
  IF NEW.status IN ('IN_PROGRESS','SUBMITTED') AND NOT flow.quality_task_live(NEW.id) THEN
    RAISE EXCEPTION 'Phản ánh không còn mở để xử lý.' USING ERRCODE='40001'; END IF;
  IF NEW.status='SUBMITTED' THEN
    IF length(btrim(coalesce(NEW.report,''))) NOT BETWEEN 10 AND 2000
      OR EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=NEW.id AND state='PLANNED') THEN
      RAISE EXCEPTION 'Cần báo cáo và hoàn tất nạp hoa thay đã được giao.' USING ERRCODE='40001'; END IF;
    UPDATE kiosk_ops.staff_quality_tasks SET staff_report=NEW.report WHERE task_id=NEW.id;
  END IF;
  IF NEW.status IS DISTINCT FROM OLD.status AND NEW.status IN ('IN_PROGRESS','SUBMITTED','CANCELLED') THEN
    UPDATE ordering.complaint_handling SET state=CASE WHEN NEW.status='SUBMITTED' THEN 'REVIEW_REQUIRED'
      WHEN NEW.status='CANCELLED' THEN 'ACCEPTED' ELSE 'IN_PROGRESS' END,version=version+1,updated_at=public.app_now()
      WHERE complaint_id=q.complaint_id AND state<>'RESOLVED';
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quality_task_transition_guard BEFORE UPDATE OF status,assignee_id ON kiosk_ops.staff_tasks
  FOR EACH ROW EXECUTE FUNCTION flow.quality_task_transition_guard();

CREATE FUNCTION flow.review_quality_complaint(p_complaint uuid,p_actor uuid,p_version integer,
  p_task_version integer,p_outcome text,p_resolution text) RETURNS integer LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling; t kiosk_ops.staff_tasks; d ordering.disputes;
BEGIN
  h:=flow.quality_seller_lock(p_complaint,p_actor);
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=h.current_task_id FOR UPDATE;
  SELECT * INTO d FROM ordering.disputes WHERE id=p_complaint;
  IF h.version<>p_version OR t.id IS NULL OR t.version<>p_task_version OR t.status<>'SUBMITTED' THEN
    RAISE EXCEPTION 'Cần báo cáo SUBMITTED và phiên bản mới nhất để duyệt.' USING ERRCODE='40001'; END IF;
  IF length(btrim(coalesce(p_resolution,''))) NOT BETWEEN 10 AND 2000
    OR p_outcome NOT IN ('RETURN','EXPLANATION','REPLACEMENT','REFUND_REVIEW') THEN
    RAISE EXCEPTION 'Chọn kết quả và ghi rõ nội dung phản hồi cho khách.' USING ERRCODE='22023'; END IF;
  IF p_outcome='RETURN' THEN
    PERFORM flow.transition_staff_task(t.id,p_actor,t.version,'IN_PROGRESS',p_resolution);
    SELECT version INTO h.version FROM ordering.complaint_handling WHERE complaint_id=p_complaint;
    RETURN h.version;
  END IF;
  IF p_outcome='REPLACEMENT' THEN
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements r JOIN kiosk_ops.slots s ON s.id=r.slot_id
      WHERE r.task_id=t.id AND r.state='LOADED' AND r.expires_at>public.app_now()
        AND s.bouquet_id=r.bouquet_id AND s.status='HELD' AND s.hold_order_id=d.order_id) THEN
      RAISE EXCEPTION 'Chưa có bó hoa thay đã nạp và giữ riêng cho khách.' USING ERRCODE='40001'; END IF;
  ELSIF EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=t.id AND state IN ('PLANNED','LOADED','READY')) THEN
    RAISE EXCEPTION 'Hủy và thu hồi hoa thay trước khi chuyển phương án xử lý.' USING ERRCODE='40001';
  END IF;
  UPDATE ordering.complaint_handling SET outcome=p_outcome,resolution=btrim(p_resolution),reviewed_by=p_actor,
    state=CASE p_outcome WHEN 'REPLACEMENT' THEN 'REPLACEMENT_READY' WHEN 'REFUND_REVIEW' THEN 'REFUND_REVIEW' ELSE 'RESOLVED' END,
    version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.transition_staff_task(t.id,p_actor,t.version,'COMPLETED',p_resolution);
  IF p_outcome='REPLACEMENT' THEN
    UPDATE kiosk_ops.quality_replacements SET state='READY',ready_at=public.app_now() WHERE task_id=t.id;
  ELSIF p_outcome='EXPLANATION' THEN
    UPDATE ordering.disputes SET status='RESOLVED_FIXED',decision=btrim(p_resolution),decided_by=p_actor,resolved_at=public.app_now() WHERE id=p_complaint;
    PERFORM flow.set_order_status(d.order_id,'COMPLETED','SELLER',p_actor,p_resolution);
    PERFORM flow.move_pending_to_available(d.order_id,'Đối soát sau phản ánh chất lượng');
  END IF;
  PERFORM flow.audit(p_actor,'QUALITY_COMPLAINT_REVIEWED','disputes',p_complaint,
    jsonb_build_object('taskId',t.id,'outcome',p_outcome,'resolution',btrim(p_resolution)));
  SELECT version INTO h.version FROM ordering.complaint_handling WHERE complaint_id=p_complaint;
  RETURN h.version;
END $$;

-- Release reserved flowers for physical recovery, never put them back on sale or refund stock automatically.
CREATE FUNCTION flow.release_quality_replacement(p_task uuid,p_state text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r kiosk_ops.quality_replacements;
BEGIN
  SELECT * INTO r FROM kiosk_ops.quality_replacements WHERE task_id=p_task FOR UPDATE;
  IF NOT FOUND OR r.state NOT IN ('PLANNED','LOADED','READY') THEN RETURN; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=c.cmd_id
    WHERE c.task_id=p_task AND (u.status='OPENED' OR (u.door_opened_at IS NOT NULL AND u.door_closed_at IS NULL))) THEN
    RAISE EXCEPTION 'Cửa nhận hoa thay đang mở, cần xử lý đóng cửa trước.' USING ERRCODE='40001'; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions WHERE id=p_task AND stage NOT IN ('Completed','Recovered')) THEN
    RAISE EXCEPTION 'Cần hoàn tất/khôi phục phiên nạp hoa đang mở trước.' USING ERRCODE='40001'; END IF;
  UPDATE kiosk_ops.unlock_tokens u SET status='REVOKED' FROM kiosk_ops.quality_pickup_commands c
    WHERE c.task_id=p_task AND c.cmd_id=u.cmd_id AND u.status IN ('ISSUED','SENT','ACKED');
  UPDATE kiosk_ops.slots SET status='PENDING_REMOVAL',hold_order_id=NULL,hold_until=NULL
    WHERE id=r.slot_id AND bouquet_id=r.bouquet_id AND r.bouquet_id IS NOT NULL;
  UPDATE kiosk_ops.bouquets SET status='EXPIRED' WHERE id=r.bouquet_id AND status='SOLD';
  UPDATE kiosk_ops.quality_replacements SET state=p_state WHERE task_id=p_task;
END $$;

CREATE FUNCTION flow.cancel_quality_replacement(p_complaint uuid,p_actor uuid,p_version integer,p_reason text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling;
BEGIN
  h:=flow.quality_seller_lock(p_complaint,p_actor);
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=h.current_task_id AND state IN ('PLANNED','LOADED','READY')) THEN
    IF h.state='ATTENTION_REQUIRED' AND EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements WHERE task_id=h.current_task_id AND state='CANCELLED') THEN RETURN h.version; END IF;
    RAISE EXCEPTION 'Không có phương án hoa thay đang mở để hủy.' USING ERRCODE='40001';
  END IF;
  IF h.version<>p_version THEN RAISE EXCEPTION 'Hồ sơ đã thay đổi.' USING ERRCODE='40001'; END IF;
  IF length(btrim(coalesce(p_reason,''))) NOT BETWEEN 10 AND 2000 THEN RAISE EXCEPTION 'Cần lý do hủy.' USING ERRCODE='22023'; END IF;
  PERFORM flow.release_quality_replacement(h.current_task_id,'CANCELLED');
  UPDATE ordering.complaint_handling SET state='ATTENTION_REQUIRED',outcome=NULL,reviewed_by=NULL,
    resolution=btrim(p_reason),version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.audit(p_actor,'QUALITY_REPLACEMENT_CANCELLED','disputes',p_complaint,jsonb_build_object('reason',p_reason));
  RETURN h.version+1;
END $$;

CREATE FUNCTION flow.quality_complaint_closed() RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE r record;
BEGIN
  IF NEW.kind='COMPLAINT' AND NEW.status<>'OPEN' AND OLD.status='OPEN' THEN
    FOR r IN SELECT task_id FROM kiosk_ops.quality_replacements WHERE complaint_id=NEW.id AND state IN ('PLANNED','LOADED','READY') LOOP
      PERFORM flow.release_quality_replacement(r.task_id,'CANCELLED');
    END LOOP;
    -- Reject closure while staff still owns an unfinished physical loading session.
    IF EXISTS(SELECT 1 FROM kiosk_ops.staff_quality_tasks q JOIN kiosk_ops.slot_maintenance_sessions s ON s.id=q.task_id
      WHERE q.complaint_id=NEW.id AND s.stage NOT IN ('Completed','Recovered')) THEN
      RAISE EXCEPTION 'Cần xử lý phiên nạp hoa thay trước khi đóng phản ánh.' USING ERRCODE='40001'; END IF;
    UPDATE ordering.complaint_handling SET state='RESOLVED',version=version+1,updated_at=public.app_now() WHERE complaint_id=NEW.id;
    UPDATE kiosk_ops.staff_tasks t SET status='CANCELLED',version=version+1,updated_at=public.app_now()
      FROM kiosk_ops.staff_quality_tasks q WHERE q.task_id=t.id AND q.complaint_id=NEW.id AND t.status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED');
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quality_complaint_closed AFTER UPDATE OF status ON ordering.disputes
  FOR EACH ROW EXECUTE FUNCTION flow.quality_complaint_closed();

CREATE FUNCTION flow.quality_pickup_allowed(p_cmd uuid) RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS(
    SELECT 1 FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.quality_replacements r ON r.task_id=c.task_id
    JOIN ordering.disputes d ON d.id=r.complaint_id AND d.status='OPEN'
    JOIN ordering.complaint_handling h ON h.complaint_id=d.id AND h.state='REPLACEMENT_READY' AND h.current_task_id=r.task_id
    JOIN ordering.orders o ON o.id=d.order_id AND o.status='DISPUTED'
    JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=c.cmd_id AND u.order_id=o.id AND u.purpose='CUSTOMER_PICKUP'
    JOIN kiosk_ops.slots s ON s.id=r.slot_id AND s.kiosk_id=u.kiosk_id AND s.id=u.slot_id
      AND s.bouquet_id=r.bouquet_id AND s.current_seller_id=o.seller_id
    JOIN kiosk_ops.kiosks k ON k.id=s.kiosk_id AND k.seller_id=o.seller_id
    WHERE c.cmd_id=p_cmd AND r.state='READY' AND r.expires_at>public.app_now()
      AND s.status='HELD' AND s.hold_order_id=o.id AND s.hold_until>public.app_now()
      AND flow.slot_sellable(s.id,o.seller_id)
      AND NOT EXISTS(SELECT 1 FROM payment.payments p WHERE p.order_id=o.id AND p.kind='REFUND' AND p.status IN ('PENDING','SUCCEEDED'))
  );
$$;

CREATE FUNCTION flow.open_quality_pickup(p_complaint uuid,p_order uuid,p_tracking text,p_kiosk uuid,p_attempt uuid)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE h ordering.complaint_handling; r kiosk_ops.quality_replacements; v_cmd uuid;
BEGIN
  PERFORM 1 FROM ordering.orders WHERE id=p_order AND tracking_token=p_tracking AND kiosk_id=p_kiosk;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy biên nhận.' USING ERRCODE='P0002'; END IF;
  PERFORM 1 FROM ordering.disputes WHERE id=p_complaint AND order_id=p_order AND kind='COMPLAINT' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy phản ánh.' USING ERRCODE='P0002'; END IF;
  SELECT * INTO h FROM ordering.complaint_handling WHERE complaint_id=p_complaint FOR UPDATE;
  SELECT * INTO r FROM kiosk_ops.quality_replacements WHERE task_id=h.current_task_id FOR UPDATE;
  SELECT c.cmd_id INTO v_cmd FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.quality_replacements qr ON qr.task_id=c.task_id
    WHERE c.attempt_id=p_attempt AND qr.complaint_id=p_complaint;
  IF FOUND THEN RETURN v_cmd; END IF;
  PERFORM 1 FROM kiosk_ops.slots WHERE id=r.slot_id FOR UPDATE;
  IF h.state<>'REPLACEMENT_READY' OR r.state<>'READY' OR r.expires_at<=public.app_now()
    OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks k WHERE k.id=p_kiosk AND k.status='ONLINE'
      AND k.last_heartbeat_at>public.app_now()-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))) THEN
    RAISE EXCEPTION 'Hoa thay chưa sẵn sàng, đã hết hạn hoặc kiosk chưa trực tuyến.' USING ERRCODE='40001'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.flower_presence p JOIN kiosk_ops.slots s ON s.kiosk_id=p.kiosk_id
    WHERE s.id=r.slot_id AND p.states[s.relay_channel+1]=1 AND p.received_at>clock_timestamp()-interval '45 seconds'
      AND p.observed_at BETWEEN extract(epoch FROM clock_timestamp()-interval '45 seconds')*1000
        AND extract(epoch FROM clock_timestamp()+interval '5 seconds')*1000
      AND NOT EXISTS(SELECT 1 FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=c.cmd_id
        WHERE c.task_id=r.task_id AND u.door_closed_at IS NOT NULL AND p.received_at<u.door_closed_at)) THEN
    RAISE EXCEPTION 'Chưa có xác nhận cảm biến mới rằng hoa thay còn trong ô. Kiểm tra kết quả nhận trước khi thử lại.' USING ERRCODE='40001'; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id=r.slot_id AND
    (status='OPENED' OR (door_opened_at IS NOT NULL AND door_closed_at IS NULL)
      OR (status IN ('ISSUED','SENT','ACKED') AND expires_at>public.app_now()))) THEN
    RAISE EXCEPTION 'Đang có lệnh mở ô, cần kiểm tra trạng thái trước.' USING ERRCODE='40001'; END IF;
  UPDATE kiosk_ops.unlock_tokens SET status='EXPIRED' WHERE slot_id=r.slot_id AND status IN ('ISSUED','SENT','ACKED') AND expires_at<=public.app_now();
  INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,order_id,token_hash,expires_at)
    VALUES(p_kiosk,r.slot_id,'CUSTOMER_PICKUP',p_order,encode(sha256(convert_to(public.uuid_v7()::text,'UTF8')),'hex'),
      least(r.expires_at,public.app_now()+flow.cfg_min('token_minutes'))) RETURNING cmd_id INTO v_cmd;
  INSERT INTO kiosk_ops.quality_pickup_commands(cmd_id,task_id,attempt_id) VALUES(v_cmd,r.task_id,p_attempt);
  IF NOT flow.quality_pickup_allowed(v_cmd) THEN RAISE EXCEPTION 'Không còn quyền nhận bó hoa thay này.' USING ERRCODE='40001'; END IF;
  PERFORM flow.audit(p_kiosk,'QUALITY_REPLACEMENT_PICKUP_REQUESTED','disputes',p_complaint,jsonb_build_object('cmdId',v_cmd),'KIOSK');
  RETURN v_cmd;
END $$;

CREATE FUNCTION flow.confirm_quality_pickup(p_cmd uuid,p_now timestamptz) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.unlock_tokens; r kiosk_ops.quality_replacements; d ordering.disputes; h ordering.complaint_handling;
BEGIN
  SELECT qr.* INTO r FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.quality_replacements qr ON qr.task_id=c.task_id
    WHERE c.cmd_id=p_cmd AND qr.state='READY' FOR UPDATE OF qr;
  IF NOT FOUND THEN RETURN false; END IF;
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id=p_cmd AND status='CLOSED' FOR UPDATE;
  IF NOT FOUND THEN RETURN false; END IF;
  IF EXISTS(SELECT 1 FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.unlock_tokens newer ON newer.cmd_id=c.cmd_id
    WHERE c.task_id=r.task_id AND newer.issued_at>t.issued_at) THEN RETURN false; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.flower_presence p JOIN kiosk_ops.slots s ON s.kiosk_id=p.kiosk_id
    WHERE s.id=r.slot_id AND s.bouquet_id=r.bouquet_id AND s.status='HELD' AND p.states[s.relay_channel+1]=0
      AND p.received_at BETWEEN t.door_opened_at AND t.door_closed_at+interval '45 seconds'
      AND p.received_at>clock_timestamp()-interval '45 seconds'
      AND p.observed_at>=extract(epoch FROM t.door_opened_at)*1000
      AND p.observed_at BETWEEN extract(epoch FROM clock_timestamp()-interval '45 seconds')*1000
        AND extract(epoch FROM clock_timestamp()+interval '5 seconds')*1000) THEN RETURN false; END IF;
    SELECT * INTO d FROM ordering.disputes WHERE id=r.complaint_id FOR UPDATE;
    SELECT * INTO h FROM ordering.complaint_handling WHERE complaint_id=d.id FOR UPDATE;
    UPDATE kiosk_ops.bouquets SET status='PICKED_UP' WHERE id=r.bouquet_id;
    PERFORM flow.release_slot_empty(r.slot_id);
    UPDATE kiosk_ops.quality_replacements SET state='COLLECTED',collected_at=p_now WHERE task_id=r.task_id;
    UPDATE ordering.disputes SET status='RESOLVED_FIXED',decision=h.resolution,decided_by=h.reviewed_by,resolved_at=p_now WHERE id=d.id;
    PERFORM flow.set_order_status(d.order_id,'COMPLETED','KIOSK',NULL,'Khách đã nhận bó hoa thay.');
    PERFORM flow.move_pending_to_available(d.order_id,'Đối soát sau khi nhận hoa thay');
  PERFORM flow.audit(t.kiosk_id,'QUALITY_REPLACEMENT_COLLECTED','disputes',r.complaint_id,jsonb_build_object('cmdId',p_cmd),'KIOSK');
  -- No complete_order: the original charge and loyalty award are unchanged.
  RETURN true;
END $$;

CREATE FUNCTION flow.apply_quality_device_event(p_cmd uuid,p_event text,p_now timestamptz) RETURNS boolean LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.unlock_tokens; r kiosk_ops.quality_replacements; d ordering.disputes; h ordering.complaint_handling;
BEGIN
  SELECT qr.* INTO r FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.quality_replacements qr ON qr.task_id=c.task_id WHERE c.cmd_id=p_cmd FOR UPDATE OF qr;
  IF NOT FOUND THEN RETURN false; END IF;
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id=p_cmd FOR UPDATE;
  IF t.status IN ('CLOSED','FAILED','EXPIRED','REVOKED') THEN RETURN true; END IF;
  IF p_event IN ('SENT','ACK','OPENED') AND (t.expires_at<=p_now OR NOT flow.quality_pickup_allowed(p_cmd)) THEN
    UPDATE kiosk_ops.unlock_tokens SET status='REVOKED' WHERE cmd_id=p_cmd AND status IN ('ISSUED','SENT','ACKED'); RETURN true;
  END IF;
  IF p_event='SENT' THEN
    UPDATE kiosk_ops.unlock_tokens SET status='SENT',attempts=attempts+1 WHERE cmd_id=p_cmd AND status IN ('ISSUED','SENT');
  ELSIF p_event='ACK' THEN
    UPDATE kiosk_ops.unlock_tokens SET status='ACKED' WHERE cmd_id=p_cmd AND status='SENT';
  ELSIF p_event='OPENED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status='OPENED',door_opened_at=p_now WHERE cmd_id=p_cmd AND status='ACKED';
  ELSIF p_event='CLOSED' AND t.status='OPENED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status='CLOSED',door_closed_at=p_now WHERE cmd_id=p_cmd;
    PERFORM flow.confirm_quality_pickup(p_cmd,p_now);
  ELSIF p_event='FAILED' AND t.status IN ('ISSUED','SENT','ACKED') AND t.attempts>=flow.cfg('door_max_attempts') THEN
    UPDATE kiosk_ops.unlock_tokens SET status='FAILED' WHERE cmd_id=p_cmd;
    PERFORM flow.report_device_fault(t.kiosk_id,t.slot_id,NULL,'Không mở được ô nhận hoa thay.',NULL);
  END IF;
  PERFORM flow.audit(t.kiosk_id,'QUALITY_DEVICE_'||p_event,'unlock_tokens',t.id,jsonb_build_object('cmdId',p_cmd),'KIOSK');
  RETURN true;
END $$;

CREATE FUNCTION flow.expire_quality_replacements() RETURNS integer LANGUAGE plpgsql AS $$
DECLARE r record; n integer:=0;
BEGIN
  FOR r IN SELECT c.cmd_id FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.quality_replacements qr ON qr.task_id=c.task_id JOIN kiosk_ops.unlock_tokens u ON u.cmd_id=c.cmd_id WHERE qr.state='READY' AND u.status='CLOSED' LOOP
    IF flow.confirm_quality_pickup(r.cmd_id,public.app_now()) THEN n:=n+1; END IF;
  END LOOP;
  FOR r IN SELECT * FROM kiosk_ops.quality_replacements WHERE state IN ('LOADED','READY') AND expires_at<=public.app_now() ORDER BY task_id FOR UPDATE SKIP LOCKED LOOP
    IF EXISTS(SELECT 1 FROM kiosk_ops.quality_pickup_commands c JOIN kiosk_ops.unlock_tokens t ON t.cmd_id=c.cmd_id
      WHERE c.task_id=r.task_id AND (t.status='OPENED' OR (t.door_opened_at IS NOT NULL AND t.door_closed_at IS NULL))) THEN CONTINUE; END IF;
    PERFORM flow.release_quality_replacement(r.task_id,'EXPIRED');
    UPDATE ordering.complaint_handling SET state='ATTENTION_REQUIRED',version=version+1,updated_at=public.app_now() WHERE complaint_id=r.complaint_id AND state<>'RESOLVED';
    PERFORM flow.audit(NULL,'QUALITY_REPLACEMENT_EXPIRED','disputes',r.complaint_id,jsonb_build_object('taskId',r.task_id),'SYSTEM');
    n:=n+1;
  END LOOP;
  RETURN n;
END $$;

-- Read contracts perform tenant/assignee/capability checks inside the owning flow boundary.
CREATE FUNCTION flow.quality_evidence_rows(p_complaint uuid)
RETURNS TABLE(id uuid,file_url text,mime_type text,size_bytes bigint,sha256 text,created_at timestamptz,source text,task_id uuid)
LANGUAGE sql STABLE AS $$
  SELECT a.id,a.file_url,a.mime_type,a.size_bytes,a.sha256,a.created_at,'COMPLAINT'::text,NULL::uuid
    FROM notify.attachments a WHERE a.owner_service='ordering' AND a.owner_type='dispute'
      AND a.owner_id=p_complaint AND a.phase='EVIDENCE'
  UNION ALL
  SELECT a.id,a.file_url,a.mime_type,a.size_bytes,a.sha256,a.created_at,'STAFF_TASK'::text,q.task_id
    FROM kiosk_ops.staff_quality_tasks q JOIN notify.attachments a ON a.owner_service='kiosk_ops'
      AND a.owner_type='staff_task' AND a.owner_id=q.task_id AND a.phase='EVIDENCE'
    WHERE q.complaint_id=p_complaint AND a.sha256 IS NOT NULL AND a.size_bytes>0
      AND a.mime_type IN ('image/jpeg','image/png','image/webp');
$$;
CREATE FUNCTION flow.quality_complaint_visible(p_complaint uuid,p_actor uuid,p_order uuid,p_tracking text)
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS(SELECT 1 FROM ordering.disputes d JOIN ordering.orders o ON o.id=d.order_id
    WHERE d.id=p_complaint AND d.kind='COMPLAINT' AND (
      (p_actor IS NULL AND o.id=p_order AND o.tracking_token=p_tracking)
      OR EXISTS(SELECT 1 FROM identity.users u WHERE u.id=p_actor AND u.status='ACTIVE' AND (
        u.role='ADMIN' OR (u.role='SELLER' AND u.seller_id=o.seller_id)
        OR (u.role='CUSTOMER' AND u.id=o.customer_id)
        OR (u.role='SELLER_STAFF' AND u.seller_id=o.seller_id AND EXISTS(
          SELECT 1 FROM kiosk_ops.staff_quality_tasks q JOIN kiosk_ops.staff_tasks t ON t.id=q.task_id
          WHERE q.complaint_id=d.id AND t.assignee_id=u.id AND t.seller_id=u.seller_id
            AND t.id=(SELECT current_task_id FROM ordering.complaint_handling WHERE complaint_id=d.id)))
      ))
    ));
$$;
CREATE FUNCTION flow.read_quality_complaint(p_complaint uuid,p_actor uuid,p_order uuid,p_tracking text,p_evidence_page integer)
RETURNS jsonb LANGUAGE sql STABLE AS $$
  SELECT jsonb_build_object('id',d.id,'orderId',o.id,'orderCode',o.order_code,'kioskId',d.kiosk_id,
    'status',d.status,'handlingState',h.state,'version',h.version,'reason',d.reason,'createdAt',d.created_at,
    'acceptedAt',h.accepted_at,'outcome',h.outcome,'resolution',coalesce(d.decision,h.resolution),'resolvedAt',d.resolved_at,
    'items',coalesce((SELECT jsonb_agg(jsonb_build_object('orderItemId',i.id,'bouquetId',i.bouquet_id,
      'productId',b.product_id,'slotId',i.slot_id,'slotCode',s.slot_code,'name',i.name_snapshot) ORDER BY i.id)
      FROM ordering.order_items i LEFT JOIN kiosk_ops.bouquets b ON b.id=i.bouquet_id
      LEFT JOIN kiosk_ops.slots s ON s.id=i.slot_id WHERE i.order_id=o.id AND i.item_type='BOUQUET'),'[]'::jsonb),
    'task',CASE WHEN p_actor IS NOT NULL AND EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role IN ('SELLER','SELLER_STAFF','ADMIN'))
      THEN (SELECT jsonb_build_object('id',t.id,'version',t.version,'status',t.status,'assigneeId',t.assignee_id,
        'orderItemId',q.order_item_id,'instructions',t.instructions,'report',q.staff_report)
        FROM kiosk_ops.staff_tasks t JOIN kiosk_ops.staff_quality_tasks q ON q.task_id=t.id WHERE t.id=h.current_task_id) END,
    'replacement',(SELECT jsonb_build_object('taskId',r.task_id,'state',r.state,'productId',r.product_id,
      'bouquetId',r.bouquet_id,'slotId',r.slot_id,'slotCode',s.slot_code,'expiresAt',r.expires_at,'collectedAt',r.collected_at,
      'commandId',u.cmd_id,'commandStatus',u.status)
      FROM kiosk_ops.quality_replacements r JOIN kiosk_ops.slots s ON s.id=r.slot_id
      LEFT JOIN LATERAL (SELECT tok.cmd_id,tok.status FROM kiosk_ops.quality_pickup_commands c
        JOIN kiosk_ops.unlock_tokens tok ON tok.cmd_id=c.cmd_id WHERE c.task_id=r.task_id ORDER BY tok.issued_at DESC,tok.cmd_id DESC LIMIT 1) u ON true
      WHERE r.task_id=h.current_task_id),
    'evidence',jsonb_build_object('page',p_evidence_page,'hasMore',
      (SELECT count(*) FROM flow.quality_evidence_rows(d.id))>p_evidence_page*20,
      'items',coalesce((SELECT jsonb_agg(jsonb_build_object('id',e.id,'createdAt',e.created_at,'source',e.source,'taskId',e.task_id,
        'url',CASE WHEN p_actor IS NULL OR EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='CUSTOMER')
          THEN '/api/receipts/complaints/'||d.id||'/evidence/'||e.id
          WHEN EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='ADMIN')
          THEN '/api/admin/quality-complaints/'||d.id||'/evidence/'||e.id
          WHEN EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='SELLER_STAFF')
          THEN '/api/staff/quality-complaints/'||d.id||'/evidence/'||e.id
          ELSE '/api/me/complaints/'||d.id||'/evidence/'||e.id END) ORDER BY e.created_at,e.id)
        FROM (SELECT * FROM flow.quality_evidence_rows(d.id) ORDER BY created_at,id LIMIT 20 OFFSET (p_evidence_page-1)*20) e),'[]'::jsonb)))
  FROM ordering.disputes d JOIN ordering.orders o ON o.id=d.order_id
  JOIN ordering.complaint_handling h ON h.complaint_id=d.id
  WHERE d.id=p_complaint AND flow.quality_complaint_visible(d.id,p_actor,p_order,p_tracking);
$$;

CREATE FUNCTION flow.list_quality_complaints(p_actor uuid,p_state text,p_page integer) RETURNS jsonb LANGUAGE sql STABLE AS $$
  WITH visible AS (SELECT d.id,d.created_at FROM ordering.disputes d
    JOIN ordering.complaint_handling h ON h.complaint_id=d.id
    WHERE (p_state IS NULL OR h.state=p_state) AND flow.quality_complaint_visible(d.id,p_actor,NULL,NULL)
    ORDER BY d.created_at DESC,d.id DESC LIMIT 26 OFFSET (p_page-1)*25)
  SELECT jsonb_build_object('page',p_page,'hasMore',(SELECT count(*) FROM visible)>25,
    'items',coalesce((SELECT jsonb_agg(flow.read_quality_complaint(v.id,p_actor,NULL,NULL,1) ORDER BY v.created_at DESC,v.id DESC)
      FROM (SELECT * FROM visible ORDER BY created_at DESC,id DESC LIMIT 25) v),'[]'::jsonb));
$$;

CREATE FUNCTION flow.quality_task_complaint(p_task uuid,p_actor uuid) RETURNS uuid LANGUAGE sql STABLE AS $$
  SELECT q.complaint_id FROM kiosk_ops.staff_quality_tasks q JOIN kiosk_ops.staff_tasks t ON t.id=q.task_id
    JOIN ordering.complaint_handling h ON h.current_task_id=t.id
    JOIN identity.users u ON u.id=p_actor AND u.id=t.assignee_id AND u.seller_id=t.seller_id AND u.status='ACTIVE' AND u.role='SELLER_STAFF'
    WHERE q.task_id=p_task;
$$;

CREATE FUNCTION flow.quality_task_binding_guard() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF EXISTS(SELECT 1 FROM kiosk_ops.staff_quality_tasks WHERE task_id=NEW.task_id) THEN
    IF TG_TABLE_NAME='staff_preorder_tasks' THEN
      RAISE EXCEPTION 'Công việc chất lượng không thể đồng thời giao một đơn preorder khác.' USING ERRCODE='40001';
    ELSIF NOT EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements r WHERE r.task_id=NEW.task_id
      AND r.slot_id=NEW.slot_id AND r.product_id=NEW.product_id AND NEW.action='LOAD' AND NEW.expected_bouquet_id IS NULL AND r.state='PLANNED') THEN
      RAISE EXCEPTION 'Chỉ nạp đúng hoa và ô đã giao trong phương án thay hoa.' USING ERRCODE='40001';
    END IF;
  END IF;
  RETURN NEW;
END $$;
CREATE TRIGGER quality_inventory_binding BEFORE INSERT OR UPDATE ON kiosk_ops.staff_inventory_tasks FOR EACH ROW EXECUTE FUNCTION flow.quality_task_binding_guard();
CREATE TRIGGER quality_preorder_binding BEFORE INSERT OR UPDATE ON kiosk_ops.staff_preorder_tasks FOR EACH ROW EXECUTE FUNCTION flow.quality_task_binding_guard();

CREATE FUNCTION flow.quality_command_status(p_cmd uuid) RETURNS text LANGUAGE sql STABLE AS $$
  SELECT u.status FROM kiosk_ops.unlock_tokens u JOIN kiosk_ops.quality_pickup_commands c ON c.cmd_id=u.cmd_id WHERE u.cmd_id=p_cmd;
$$;

-- Existing behavior stays intact for non-quality commands.
CREATE OR REPLACE FUNCTION flow.device_event(p_cmd uuid, p_event text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); t kiosk_ops.unlock_tokens; v_bq uuid;
BEGIN
  IF flow.apply_quality_device_event(p_cmd,p_event,v_now) THEN RETURN; END IF;
  SELECT * INTO t FROM kiosk_ops.unlock_tokens WHERE cmd_id = p_cmd FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh % không tồn tại', p_cmd; END IF;
  -- token quá hạn: ghi EXPIRED và dừng (không RAISE để trạng thái EXPIRED được lưu)
  IF p_event IN ('SENT','ACK','OPENED') AND t.status IN ('ISSUED','SENT','ACKED') AND t.expires_at < v_now THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'EXPIRED' WHERE id = t.id;
    PERFORM flow.audit(t.kiosk_id, 'DEVICE_TOKEN_EXPIRED', 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd, 'event', p_event), 'KIOSK');
    RETURN;
  END IF;
  IF p_event = 'SENT' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'SENT', attempts = attempts + 1 WHERE id = t.id AND status IN ('ISSUED','SENT');
  ELSIF p_event = 'ACK' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'ACKED' WHERE id = t.id AND status = 'SENT';
  ELSIF p_event = 'OPENED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'OPENED', door_opened_at = v_now WHERE id = t.id AND status = 'ACKED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Cửa mở khi token không ở trạng thái ACKED (status %)', t.status; END IF;
  ELSIF p_event = 'CLOSED' THEN
    UPDATE kiosk_ops.unlock_tokens SET status = 'CLOSED', door_closed_at = v_now WHERE id = t.id AND status = 'OPENED';
    IF NOT FOUND THEN RAISE EXCEPTION 'Đóng cửa khi chưa mở'; END IF;
    IF t.purpose = 'CUSTOMER_PICKUP' THEN
      SELECT bouquet_id INTO v_bq FROM kiosk_ops.slots WHERE id = t.slot_id;
      UPDATE kiosk_ops.bouquets SET status = 'PICKED_UP' WHERE id = v_bq;
      PERFORM flow.release_slot_empty(t.slot_id);
      -- đơn hoàn tất khi không còn ô nào giữ cho đơn (BRD: cảm biến cửa đóng mới hoàn tất)
      IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = t.order_id)
         AND (SELECT status FROM ordering.orders WHERE id = t.order_id) = 'DISPENSING' THEN
        PERFORM flow.complete_order(t.order_id, 'KIOSK', 'Cửa đã đóng, khách đã lấy hàng');
      END IF;
    END IF;
  ELSIF p_event = 'FAILED' THEN
    -- chỉ áp cho token còn sống; đủ số lần thất bại (BRD: 2) thì khóa token và DISPENSE_FAILED
    IF t.status IN ('ISSUED','SENT','ACKED') AND t.attempts >= flow.cfg('door_max_attempts') THEN
      UPDATE kiosk_ops.unlock_tokens SET status = 'FAILED' WHERE id = t.id;
      IF t.purpose = 'CUSTOMER_PICKUP' THEN
        PERFORM flow.dispense_failed(t.order_id, 'Khóa không phản hồi sau ' || t.attempts || ' lần gửi lệnh', t.slot_id);
      ELSE
        PERFORM flow.report_device_fault(t.kiosk_id, t.slot_id, NULL, 'Cửa kỹ thuật không mở sau ' || t.attempts || ' lần', NULL);
      END IF;
    END IF;
  ELSE RAISE EXCEPTION 'Sự kiện thiết bị lạ %', p_event; END IF;
  PERFORM flow.audit(t.kiosk_id, 'DEVICE_' || p_event, 'unlock_tokens', t.id, jsonb_build_object('cmd_id', p_cmd), 'KIOSK');
END $$;

CREATE OR REPLACE FUNCTION flow.staff_stock_bouquet(p_id uuid,p_task uuid,p_staff uuid,p_product uuid,p_slot uuid,p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks; previous kiosk_ops.staff_stock_actions; pr catalog.flower_products;
  s kiosk_ops.slots; v_b uuid; v_staff_role text; v_staff_seller uuid;
BEGIN
  IF EXISTS(SELECT 1 FROM kiosk_ops.staff_quality_tasks WHERE task_id=p_task) THEN RAISE EXCEPTION 'Use the assigned quality replacement loading flow' USING ERRCODE='42501'; END IF;
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
  IF t.status <> 'IN_PROGRESS' THEN RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023'; END IF;
  IF p_id IS NULL OR p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN
    RAISE EXCEPTION 'Invalid stock reference' USING ERRCODE = '22023';
  END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR KEY SHARE;
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
  IF s.zone IS DISTINCT FROM 'WALK_IN' THEN
    RAISE EXCEPTION 'Nạp bán sẵn chỉ được dùng ô WALK_IN đã được seller phân loại' USING ERRCODE = '22023';
  END IF;
  INSERT INTO kiosk_ops.bouquets(product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, btrim(p_qr), pr.price, 'STOCKED', public.app_now() + make_interval(hours => pr.shelf_life_hours), public.app_now())
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = s.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, s.id, 'STOCK_IN', p_task, p_staff);
  INSERT INTO kiosk_ops.staff_stock_actions(id, task_id, actor_id, product_id, slot_id, qr_code, bouquet_id)
  VALUES (p_id, p_task, p_staff, pr.id, s.id, btrim(p_qr), v_b);
  PERFORM flow.audit(p_staff, 'STAFF_TASK_STOCKED', 'staff_tasks', p_task,
    jsonb_build_object('operation', p_id, 'bouquet', v_b, 'slot', s.id));
  RETURN v_b;
END $$;

CREATE OR REPLACE FUNCTION flow.staff_fulfill_preorder(p_task uuid, p_request uuid, p_actor uuid, p_slot uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks; v_role text; v_seller uuid; w ordering.web_requests;
BEGIN
  IF EXISTS(SELECT 1 FROM kiosk_ops.staff_quality_tasks WHERE task_id=p_task) THEN RAISE EXCEPTION 'Quality task cannot fulfill another preorder' USING ERRCODE='42501'; END IF;
  SELECT role, seller_id INTO v_role, v_seller FROM identity.users WHERE id = p_actor AND status = 'ACTIVE' FOR SHARE;
  IF v_role IS DISTINCT FROM 'SELLER_STAFF' THEN RAISE EXCEPTION 'Active seller staff required' USING ERRCODE = '42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id = p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id <> p_actor OR t.kind <> 'DELIVERY' OR t.seller_id IS DISTINCT FROM v_seller THEN
    RAISE EXCEPTION 'Assigned delivery task not found' USING ERRCODE = 'P0002';
  END IF;
  IF t.status <> 'IN_PROGRESS' THEN RAISE EXCEPTION 'Active delivery task required' USING ERRCODE = '22023'; END IF;
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_request AND seller_id = t.seller_id AND kiosk_id = t.kiosk_id;
  IF NOT FOUND THEN RAISE EXCEPTION 'Preorder is outside this task' USING ERRCODE = 'P0002'; END IF;
  RETURN flow.web_fulfill(p_request, t.seller_id, p_actor, p_slot, p_qr);
END $$;
CREATE OR REPLACE FUNCTION flow.verify_staff_delivery_task(p_task uuid, p_actor uuid)
RETURNS integer
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM kiosk_ops.staff_tasks t
  JOIN identity.users u ON u.id = p_actor AND u.role = 'SELLER_STAFF' AND u.status = 'ACTIVE' AND u.seller_id = t.seller_id
  WHERE t.id = p_task AND t.assignee_id = p_actor AND t.kind = 'DELIVERY' AND t.status = 'IN_PROGRESS' AND flow.quality_task_live(t.id)
  FOR SHARE OF t, u;
  IF FOUND THEN
    RETURN 1;
  ELSE
    RETURN NULL;
  END IF;
END;
$$;
CREATE OR REPLACE FUNCTION flow.stock_bouquet(
  p_batch uuid, p_product uuid, p_slot uuid, p_staff uuid, p_qr text, p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); pr catalog.flower_products; s kiosk_ops.slots; v_b uuid; v_seller_status text;
BEGIN
  IF EXISTS(SELECT 1 FROM kiosk_ops.quality_replacements r WHERE r.slot_id=p_slot AND r.state IN ('PLANNED','LOADED','READY')
    AND NOT EXISTS(SELECT 1 FROM kiosk_ops.slot_maintenance_sessions ms JOIN kiosk_ops.staff_tasks t ON t.id=ms.id
      WHERE ms.id=r.task_id AND ms.slot_id=p_slot AND ms.new_product_id=p_product AND ms.actor_id=p_staff
        AND ms.stage IN ('AwaitLoading','NeedsInspection') AND t.assignee_id=p_staff AND t.status='IN_PROGRESS'
        AND flow.quality_task_live(t.id))) THEN
    RAISE EXCEPTION 'Use the assigned monitored quality loading session' USING ERRCODE='40001';
  END IF;
  IF p_batch IS NULL THEN RAISE EXCEPTION 'Thiếu mã lô nạp hàng (batch_id)'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product;
  IF NOT FOUND THEN RAISE EXCEPTION 'Sản phẩm % không tồn tại', p_product; END IF;
  IF pr.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Sản phẩm % chưa được bật bán', pr.name; END IF;
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = pr.seller_id;
  IF v_seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller đang % (gói quá hạn hoặc bị khóa), không nạp được', v_seller_status; END IF;
  PERFORM flow.check_seller_user(p_staff, pr.seller_id);
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Ô % không tồn tại', p_slot; END IF;
  IF s.status <> 'RENTED_EMPTY' OR s.current_seller_id IS DISTINCT FROM pr.seller_id THEN
    RAISE EXCEPTION 'Ô % không phải ô trống do seller này thuê (status %)', s.slot_code, s.status;
  END IF;
  IF s.zone IS DISTINCT FROM 'WALK_IN' THEN
    RAISE EXCEPTION 'Nạp bán sẵn chỉ được dùng ô WALK_IN đã được seller phân loại';
  END IF;
  IF NOT flow.slot_sellable(p_slot, pr.seller_id) THEN
    RAISE EXCEPTION 'Gói của seller đã hết hạn ngày % (hoặc ô đã bị thu hồi), không nạp được vào ô %', v_now::date, s.slot_code;
  END IF;
  INSERT INTO kiosk_ops.bouquets (product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (pr.id, pr.seller_id, p_qr, pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now)
  RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = v_b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (v_b, p_slot, 'STOCK_IN', p_batch, p_staff);
  RETURN v_b;
END $$;
COMMIT;
