BEGIN;

-- Physical bouquets still held at the shop. Existing products deliberately start
-- at zero: neither kiosk contents nor historical sales establish shop stock.
ALTER TABLE catalog.flower_products
  ADD COLUMN quantity integer NOT NULL DEFAULT 0,
  ADD CONSTRAINT flower_products_quantity_nonnegative CHECK (quantity >= 0);

COMMENT ON COLUMN catalog.flower_products.quantity IS
  'Physical bouquets at the shop; deduct once on kiosk STOCK_IN. Returns are reconciled manually by the seller.';

-- One durable deduction per physical bouquet, linked to its original inventory
-- event and actor. Do not backfill this ledger or change historical quantities.
CREATE TABLE kiosk_ops.product_quantity_deductions (
  bouquet_id uuid PRIMARY KEY REFERENCES kiosk_ops.bouquets(id),
  product_id uuid NOT NULL,
  seller_id uuid NOT NULL,
  inventory_log_id uuid NOT NULL UNIQUE REFERENCES kiosk_ops.inventory_logs(id),
  deducted_at timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE INDEX product_quantity_deductions_product_idx
  ON kiosk_ops.product_quantity_deductions(product_id);
CREATE TRIGGER product_quantity_deductions_append_only
  BEFORE UPDATE OR DELETE ON kiosk_ops.product_quantity_deductions
  FOR EACH ROW EXECUTE FUNCTION public.forbid_change();

CREATE OR REPLACE FUNCTION flow.deduct_shop_product_quantity()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_product uuid;
  v_seller uuid;
BEGIN
  SELECT product_id, seller_id INTO v_product, v_seller
  FROM kiosk_ops.bouquets WHERE id = NEW.bouquet_id;

  INSERT INTO kiosk_ops.product_quantity_deductions(
    bouquet_id, product_id, seller_id, inventory_log_id)
  VALUES (NEW.bouquet_id, v_product, v_seller, NEW.id)
  ON CONFLICT (bouquet_id) DO NOTHING;
  IF NOT FOUND THEN
    -- Repeated inventory events and successful request retries never deduct a
    -- second time, even when the remaining shop quantity has reached zero.
    IF NOT EXISTS (
      SELECT 1 FROM kiosk_ops.product_quantity_deductions
      WHERE bouquet_id = NEW.bouquet_id
        AND product_id = v_product AND seller_id = v_seller
    ) THEN
      RAISE EXCEPTION 'Bo hoa da duoc ghi nhan cho san pham khac' USING ERRCODE = '40001';
    END IF;
    RETURN NEW;
  END IF;

  -- This single row update locks and rechecks the latest value after concurrent
  -- transactions commit. A failed deduction rolls back the entire stock flow,
  -- including bouquet/slot/order writes, inventory event and ledger insertion.
  UPDATE catalog.flower_products
  SET quantity = quantity - 1
  WHERE id = v_product AND seller_id = v_seller
    AND status = 'ACTIVE' AND quantity > 0;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Khong du so luong bo hoa tai shop de nap vao kiosk'
      USING ERRCODE = '40001';
  END IF;
  RETURN NEW;
END $$;

-- All physical loading paths emit STOCK_IN: seller stock, staff delivery,
-- sensor-confirmed LOAD/REPLACE and CUSTOM preorder fulfillment. SALE_OUT,
-- return/removal, expiration, damage, disposal and refunds do not change stock
-- at the shop. The seller enters the actual returned count manually.
CREATE TRIGGER inventory_logs_deduct_shop_product_quantity
  AFTER INSERT ON kiosk_ops.inventory_logs
  FOR EACH ROW WHEN (NEW.movement_type = 'STOCK_IN')
  EXECUTE FUNCTION flow.deduct_shop_product_quantity();

-- KEY SHARE remains compatible with the quantity-only UPDATE's NO KEY UPDATE
-- lock. FOR SHARE here would deadlock when concurrent stock/fulfillment flows
-- both upgraded their product locks. The trigger rechecks active status and
-- ownership while it atomically deducts quantity. Existing function contracts,
-- task/lease/slot/actor checks and audit behavior are otherwise unchanged.

-- Bouquet creation also reads the product through this pre-existing expiry
-- trigger. Its old FOR SHARE lock must not reintroduce the same upgrade cycle.
-- Inventory kind is immutable through catalog APIs, whose metadata edits keep
-- their existing FOR UPDATE product lock. Artificial expiry behavior is kept.
CREATE OR REPLACE FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_kind text;
BEGIN
  SELECT inventory_kind INTO v_kind FROM catalog.flower_products WHERE id = NEW.product_id FOR KEY SHARE;
  IF v_kind = 'ARTIFICIAL' THEN
    NEW.sellable_until := 'infinity'::timestamptz;
  END IF;
  RETURN NEW;
END $$;

CREATE OR REPLACE FUNCTION flow.staff_stock_bouquet(p_id uuid,p_task uuid,p_staff uuid,p_product uuid,p_slot uuid,p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  t kiosk_ops.staff_tasks; previous kiosk_ops.staff_stock_actions; pr catalog.flower_products;
  s kiosk_ops.slots; v_b uuid; v_staff_role text; v_staff_seller uuid;
BEGIN
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

CREATE OR REPLACE FUNCTION flow.web_fulfill(p_id uuid, p_seller uuid, p_actor uuid, p_slot uuid, p_qr text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; o ordering.orders; s kiosk_ops.slots; pr catalog.flower_products; v_seller_status text; b uuid; v_now timestamptz := public.app_now();
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id AND seller_id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor, p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id = w.order_id FOR UPDATE;
  IF w.kind <> 'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF v_now >= w.pickup_before THEN
    RAISE EXCEPTION 'Can nap hoa truoc han nhan';
  END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = w.product_id FOR KEY SHARE;
  IF NOT FOUND OR pr.status <> 'ACTIVE' THEN RAISE EXCEPTION 'Mau hoa khong con duoc bat ban'; END IF;
  SELECT status INTO v_seller_status FROM identity.sellers WHERE id = p_seller;
  IF v_seller_status <> 'ACTIVE' THEN RAISE EXCEPTION 'Seller dang % (goi qua han hoac bi khoa), khong nap duoc', v_seller_status; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot AND current_seller_id = p_seller AND kiosk_id = w.kiosk_id FOR UPDATE;
  IF NOT FOUND OR s.status <> 'RENTED_EMPTY' OR s.zone IS DISTINCT FROM 'PREORDER' THEN
    RAISE EXCEPTION 'Can o PREORDER trong cua shop tai dung tu nhan';
  END IF;
  IF NOT flow.slot_sellable(p_slot, p_seller) THEN RAISE EXCEPTION 'Goi cua seller da het han hoac o da bi thu hoi'; END IF;
  b := public.uuid_v7();
  INSERT INTO kiosk_ops.bouquets(id, product_id, seller_id, qr_code, price_snapshot, status, sellable_until, stocked_at)
  VALUES (b, pr.id, p_seller, btrim(p_qr), pr.price, 'STOCKED', v_now + make_interval(hours => pr.shelf_life_hours), v_now);
  UPDATE kiosk_ops.slots SET status = 'STOCKED', bouquet_id = b, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, batch_id, performed_by)
  VALUES (b, p_slot, 'STOCK_IN', flow.new_batch(), p_actor);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id = b) < w.pickup_before THEN
    RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan';
  END IF;
  UPDATE kiosk_ops.bouquets SET status = 'SOLD', price_snapshot = o.total_amount WHERE id = b;
  UPDATE kiosk_ops.slots SET status = 'HELD', hold_order_id = o.id, hold_until = w.pickup_before, row_version = row_version + 1 WHERE id = p_slot;
  INSERT INTO ordering.order_items(order_id, item_type, bouquet_id, slot_id, name_snapshot, quantity, unit_price, line_total)
  VALUES(o.id, 'BOUQUET', b, p_slot, 'Hoa dat rieng: ' || pr.name, 1, o.total_amount, o.total_amount);
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, order_id, performed_by)
  VALUES(b, p_slot, 'SALE_OUT', o.id, p_actor);
  PERFORM flow.audit(p_actor, 'WEB_ORDER_READY', 'orders', o.id, jsonb_build_object('request', p_id, 'slot', p_slot));
  RETURN b;
END $$;

COMMIT;

