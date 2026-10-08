BEGIN;

CREATE TABLE IF NOT EXISTS kiosk_ops.staff_stock_actions (
  id uuid PRIMARY KEY,
  task_id uuid NOT NULL REFERENCES kiosk_ops.staff_tasks(id),
  actor_id uuid NOT NULL REFERENCES identity.users(id),
  product_id uuid NOT NULL REFERENCES catalog.flower_products(id),
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  qr_code text NOT NULL,
  bouquet_id uuid NOT NULL REFERENCES kiosk_ops.bouquets(id),
  created_at timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE OR REPLACE FUNCTION flow.staff_stock_bouquet(p_id uuid,p_task uuid,p_staff uuid,p_product uuid,p_slot uuid,p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE t kiosk_ops.staff_tasks; previous kiosk_ops.staff_stock_actions; pr catalog.flower_products; s kiosk_ops.slots; v_b uuid;
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_staff AND role='STAFF' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Active staff required' USING ERRCODE='42501'; END IF;
  SELECT * INTO t FROM kiosk_ops.staff_tasks WHERE id=p_task FOR UPDATE;
  IF NOT FOUND OR t.assignee_id<>p_staff THEN RAISE EXCEPTION 'Task not found' USING ERRCODE='P0002'; END IF;
  PERFORM pg_advisory_xact_lock(hashtextextended('staff-stock:'||p_id::text,0));
  SELECT * INTO previous FROM kiosk_ops.staff_stock_actions WHERE id=p_id;
  IF FOUND THEN
    IF previous.task_id<>p_task OR previous.actor_id<>p_staff OR previous.product_id<>p_product
       OR previous.slot_id<>p_slot OR previous.qr_code IS DISTINCT FROM btrim(p_qr) THEN
      RAISE EXCEPTION 'Operation ID already used' USING ERRCODE='40001';
    END IF;
    RETURN previous.bouquet_id;
  END IF;
  IF t.kind<>'DELIVERY' OR t.status<>'IN_PROGRESS' THEN RAISE EXCEPTION 'Active delivery task required' USING ERRCODE='22023'; END IF;
  IF p_id IS NULL OR p_qr IS NULL OR length(btrim(p_qr)) NOT BETWEEN 1 AND 120 THEN RAISE EXCEPTION 'Invalid stock reference' USING ERRCODE='22023'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id=p_product FOR SHARE;
  IF NOT FOUND OR pr.seller_id IS DISTINCT FROM t.seller_id OR pr.status<>'ACTIVE' THEN
    RAISE EXCEPTION 'Product outside task scope' USING ERRCODE='22023';
  END IF;
  PERFORM 1 FROM identity.sellers WHERE id=t.seller_id AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller inactive' USING ERRCODE='22023'; END IF;
  PERFORM 1 FROM kiosk_ops.kiosks WHERE id=t.kiosk_id AND status NOT IN ('DISABLED','MAINTENANCE') FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk unavailable' USING ERRCODE='22023'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id=p_slot FOR UPDATE;
  IF NOT FOUND OR s.kiosk_id<>t.kiosk_id OR s.current_seller_id IS DISTINCT FROM t.seller_id
     OR s.status<>'RENTED_EMPTY' OR NOT flow.slot_sellable(p_slot,t.seller_id) THEN
    RAISE EXCEPTION 'Slot is unavailable or outside task scope' USING ERRCODE='22023';
  END IF;
  -- Preserve the seller stocking flow's inventory rules while auditing the actual staff actor.
  INSERT INTO kiosk_ops.bouquets(product_id,seller_id,qr_code,price_snapshot,status,sellable_until,stocked_at)
  VALUES(pr.id,pr.seller_id,btrim(p_qr),pr.price,'STOCKED',public.app_now()+make_interval(hours=>pr.shelf_life_hours),public.app_now()) RETURNING id INTO v_b;
  UPDATE kiosk_ops.slots SET status='STOCKED',bouquet_id=v_b,row_version=row_version+1 WHERE id=s.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,batch_id,performed_by)
  VALUES(v_b,s.id,'STOCK_IN',p_task,p_staff);
  INSERT INTO kiosk_ops.staff_stock_actions(id,task_id,actor_id,product_id,slot_id,qr_code,bouquet_id)
  VALUES(p_id,p_task,p_staff,pr.id,s.id,btrim(p_qr),v_b);
  PERFORM flow.audit(p_staff,'STAFF_TASK_STOCKED','staff_tasks',p_task,jsonb_build_object('operation',p_id,'bouquet',v_b,'slot',s.id));
  RETURN v_b;
END $$;

COMMIT;
