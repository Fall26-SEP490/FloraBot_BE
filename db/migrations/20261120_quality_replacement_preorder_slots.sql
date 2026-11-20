-- Permit either classified slot zone for compensation; all reservation checks remain intact.
CREATE OR REPLACE FUNCTION flow.plan_quality_replacement(p_complaint uuid,p_actor uuid,p_version integer,
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
      AND s.zone IN ('WALK_IN','PREORDER') AND s.status='RENTED_EMPTY' AND s.bouquet_id IS NULL
      AND s.hold_order_id IS NULL AND s.hold_until IS NULL AND flow.slot_sellable(s.id,t.seller_id)
      AND cap.enabled AND cap.door_verified AND cap.presence_verified
    FOR UPDATE OF s FOR SHARE OF k,p,cap;
  IF NOT FOUND THEN RAISE EXCEPTION 'Cần ô Walk-in hoặc Preorder trống có cảm biến và mẫu hoa còn tồn của shop tại kiosk này.' USING ERRCODE='40001'; END IF;
  INSERT INTO kiosk_ops.quality_replacements(task_id,complaint_id,slot_id,product_id) VALUES(t.id,p_complaint,p_slot,p_product);
  INSERT INTO kiosk_ops.staff_inventory_tasks(task_id,slot_id,action,product_id) VALUES(t.id,p_slot,'LOAD',p_product);
  UPDATE ordering.complaint_handling SET version=version+1,updated_at=public.app_now() WHERE complaint_id=p_complaint;
  PERFORM flow.audit(p_actor,'QUALITY_REPLACEMENT_PLANNED','staff_tasks',t.id,jsonb_build_object('slotId',p_slot,'productId',p_product));
  RETURN h.version+1;
END $$;

-- Ordinary stock remains Walk-in only; Preorder is allowed solely for its assigned monitored replacement.
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
  IF s.zone IS DISTINCT FROM 'WALK_IN' AND NOT (s.zone IS NOT DISTINCT FROM 'PREORDER' AND EXISTS (
    SELECT 1 FROM kiosk_ops.quality_replacements r
    JOIN kiosk_ops.slot_maintenance_sessions ms ON ms.id=r.task_id
    JOIN kiosk_ops.staff_tasks t ON t.id=r.task_id
    WHERE r.slot_id=s.id AND r.product_id=pr.id AND r.state='PLANNED'
      AND ms.slot_id=s.id AND ms.new_product_id=pr.id AND ms.actor_id=p_staff AND ms.action='LOAD'
      AND ms.stage IN ('AwaitLoading','NeedsInspection') AND t.assignee_id=p_staff
      AND t.seller_id=pr.seller_id AND t.kiosk_id=s.kiosk_id AND t.status='IN_PROGRESS'
      AND flow.quality_task_live(t.id))) THEN
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
