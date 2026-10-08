BEGIN;

CREATE TABLE IF NOT EXISTS ordering.web_requests (
  id uuid PRIMARY KEY,
  customer_id uuid NOT NULL,
  seller_id uuid NOT NULL,
  product_id uuid NOT NULL,
  kiosk_id uuid NOT NULL,
  kind text NOT NULL CHECK (kind IN ('STOCK','CUSTOM')),
  instructions text NOT NULL CHECK (length(instructions) <= 2000),
  pickup_at timestamptz NOT NULL,
  pickup_before timestamptz NOT NULL CHECK (pickup_before > pickup_at),
  quoted_price bigint CHECK (quoted_price BETWEEN 1000 AND 100000000),
  quote_note text,
  quote_expires_at timestamptz,
  state text NOT NULL CHECK (state IN ('REQUESTED','QUOTED','ORDERED','REJECTED','CANCELLED')),
  order_id uuid UNIQUE REFERENCES ordering.orders(id),
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at timestamptz NOT NULL DEFAULT public.app_now()
);
CREATE INDEX IF NOT EXISTS web_requests_customer ON ordering.web_requests(customer_id,created_at DESC);
ALTER TABLE ordering.web_requests ADD COLUMN IF NOT EXISTS request_payload jsonb;
ALTER TABLE ordering.web_requests ADD COLUMN IF NOT EXISTS display_snapshot jsonb NOT NULL DEFAULT '{}';
CREATE INDEX IF NOT EXISTS web_requests_seller ON ordering.web_requests(seller_id,created_at DESC);
INSERT INTO kiosk_ops.system_settings(key,value,description) VALUES
 ('web_pickup_hours','2','Thoi gian nhan hoa dat web sau moc hen (gio)'),
 ('web_quote_hours','24','Hieu luc bao gia hoa dat rieng (gio)') ON CONFLICT(key) DO NOTHING;

CREATE OR REPLACE FUNCTION flow.web_request(p_id uuid,p_customer uuid,p_kind text,p_product uuid,p_kiosk uuid,p_bouquet uuid,p_pickup timestamptz,p_instructions text)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE pr catalog.flower_products; w ordering.web_requests; v_checkout uuid; v_order uuid; v_end timestamptz;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:'||p_id::text,0));
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND w.request_payload IS DISTINCT FROM jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) THEN RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác'; END IF;
    IF w.customer_id<>p_customer OR w.product_id<>p_product OR w.kiosk_id<>p_kiosk OR w.kind<>p_kind OR w.instructions<>p_instructions THEN RAISE EXCEPTION 'Ma yeu cau da duoc su dung'; END IF;
    RETURN w.id;
  END IF;
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=p_customer AND role='CUSTOMER' AND status='ACTIVE') THEN RAISE EXCEPTION 'Tai khoan khong hop le'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id=p_product AND status='ACTIVE';
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id=pr.seller_id AND status='ACTIVE') THEN RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don'; END IF;
  IF p_kind NOT IN ('STOCK','CUSTOM') OR length(p_instructions)>2000 OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE id=p_kiosk AND status NOT IN ('DISABLED','MAINTENANCE')) THEN RAISE EXCEPTION 'Thong tin dat hoa khong hop le'; END IF;
  IF NOT EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id=p_kiosk AND current_seller_id=pr.seller_id AND status NOT IN ('FREE','PENDING_RELEASE')) THEN RAISE EXCEPTION 'Shop khong phuc vu tu nay'; END IF;
  IF p_kind='CUSTOM' THEN
    IF length(btrim(p_instructions))<10 OR p_pickup<public.app_now()+interval '2 hours' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Mo ta it nhat 10 ky tu, hen nhan tu 2 gio den 30 ngay'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,state)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,p_pickup,p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),'REQUESTED');
  ELSE
    IF NOT EXISTS(SELECT 1 FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots s ON s.bouquet_id=b.id WHERE b.id=p_bouquet AND b.product_id=pr.id AND s.kiosk_id=p_kiosk) THEN RAISE EXCEPTION 'Bo hoa khong thuoc tu nay'; END IF;
    v_checkout:=flow.kiosk_checkout(p_kiosk,ARRAY[p_bouquet],p_customer);
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id=v_checkout;
    SELECT least(sellable_until,public.app_now()+make_interval(hours=>flow.cfg('web_pickup_hours')::int)) INTO v_end FROM kiosk_ops.bouquets WHERE id=p_bouquet;
    IF v_end<=public.app_now()+flow.cfg_min('hold_minutes') THEN RAISE EXCEPTION 'Hoa khong con du thoi gian de dat truoc'; END IF;
    INSERT INTO ordering.web_requests(id,customer_id,seller_id,product_id,kiosk_id,kind,instructions,pickup_at,pickup_before,quoted_price,state,order_id)
    VALUES(p_id,p_customer,pr.seller_id,pr.id,p_kiosk,p_kind,p_instructions,public.app_now(),v_end,(SELECT total_amount FROM ordering.orders WHERE id=v_order),'ORDERED',v_order);
  END IF;
  UPDATE ordering.web_requests SET request_payload=jsonb_build_object('bouquet',p_bouquet,'pickup',p_pickup) WHERE id=p_id;
  UPDATE ordering.web_requests SET display_snapshot=(SELECT jsonb_build_object('product',pr.name,'shop',s.shop_name,'kiosk',k.name,'address',k.address) FROM identity.sellers s CROSS JOIN kiosk_ops.kiosks k WHERE s.id=pr.seller_id AND k.id=p_kiosk) WHERE id=p_id;
  PERFORM flow.audit(p_customer,'WEB_REQUEST_CREATED','web_requests',p_id,jsonb_build_object('kind',p_kind));
  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION flow.web_quote(p_id uuid,p_seller uuid,p_actor uuid,p_price bigint,p_pickup timestamptz,p_note text,p_reject boolean DEFAULT false)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  IF w.state NOT IN ('REQUESTED','QUOTED') OR w.kind<>'CUSTOM' THEN RAISE EXCEPTION 'Yeu cau khong con cho bao gia'; END IF;
  IF length(btrim(p_note))<5 OR length(p_note)>1000 THEN RAISE EXCEPTION 'Ghi chu can tu 5 den 1000 ky tu'; END IF;
  IF p_reject THEN UPDATE ordering.web_requests SET state='REJECTED',quote_note=p_note,updated_at=public.app_now() WHERE id=p_id;
  ELSE
    IF p_price NOT BETWEEN 1000 AND 100000000 OR p_pickup<public.app_now()+interval '1 hour' OR p_pickup>public.app_now()+interval '30 days' THEN RAISE EXCEPTION 'Gia hoac lich nhan khong hop le'; END IF;
    UPDATE ordering.web_requests SET state='QUOTED',quoted_price=p_price,quote_note=p_note,pickup_at=p_pickup,
      pickup_before=p_pickup+make_interval(hours=>flow.cfg('web_pickup_hours')::int),
      quote_expires_at=least(p_pickup-interval '30 minutes',public.app_now()+make_interval(hours=>flow.cfg('web_quote_hours')::int)),updated_at=public.app_now() WHERE id=p_id;
  END IF;
  PERFORM flow.audit(p_actor,'WEB_REQUEST_QUOTED','web_requests',p_id);
END $$;

DROP FUNCTION IF EXISTS flow.web_accept(uuid,uuid);
CREATE OR REPLACE FUNCTION flow.web_accept(p_id uuid,p_customer uuid,p_price bigint,p_pickup timestamptz,p_note text) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; v_order uuid:=public.uuid_v7(); v_checkout uuid:=public.uuid_v7();
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND customer_id=p_customer FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong tim thay yeu cau'; END IF;
  IF w.state='ORDERED' THEN RETURN w.order_id; END IF;
  IF w.quoted_price IS DISTINCT FROM p_price OR w.pickup_at IS DISTINCT FROM p_pickup OR w.quote_note IS DISTINCT FROM p_note THEN RAISE EXCEPTION 'Báo giá đã thay đổi. Hãy cập nhật và kiểm tra lại trước khi xác nhận'; END IF;
  IF w.state<>'QUOTED' OR w.quote_expires_at<=public.app_now() THEN RAISE EXCEPTION 'Bao gia da het han hoac khong con hieu luc'; END IF;
  IF NOT flow.package_valid(w.seller_id) OR NOT EXISTS(SELECT 1 FROM identity.sellers WHERE id=w.seller_id AND status='ACTIVE') OR NOT EXISTS(SELECT 1 FROM catalog.flower_products WHERE id=w.product_id AND status='ACTIVE') OR NOT EXISTS(SELECT 1 FROM kiosk_ops.kiosks WHERE id=w.kiosk_id AND status NOT IN ('DISABLED','MAINTENANCE')) THEN RAISE EXCEPTION 'Shop tam ngung nhan don'; END IF;
  INSERT INTO ordering.orders(id,order_code,checkout_id,customer_id,seller_id,kiosk_id,subtotal,total_amount,tracking_token)
    VALUES(v_order,flow.new_order_code(),v_checkout,p_customer,w.seller_id,w.kiosk_id,w.quoted_price,w.quoted_price,flow.new_tracking_token());
  INSERT INTO payment.payments(kind,purpose,checkout_id,gateway,idempotency_key,amount,status)
    VALUES('CHARGE','ORDER_CHECKOUT',v_checkout,'PAYOS','web-'||p_id::text,w.quoted_price,'PENDING');
  UPDATE ordering.web_requests SET state='ORDERED',order_id=v_order,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_customer,'WEB_QUOTE_ACCEPTED','web_requests',p_id);
  RETURN v_order;
END $$;

CREATE OR REPLACE FUNCTION flow.web_fulfill(p_id uuid,p_seller uuid,p_actor uuid,p_slot uuid,p_qr text) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; o ordering.orders; s kiosk_ops.slots; b uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id AND seller_id=p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Khong co quyen xu ly yeu cau'; END IF;
  PERFORM flow.check_seller_user(p_actor,p_seller);
  SELECT * INTO o FROM ordering.orders WHERE id=w.order_id FOR UPDATE;
  IF w.kind<>'CUSTOM' OR o.status IS DISTINCT FROM 'PAID' THEN RAISE EXCEPTION 'Chi nap don rieng da thanh toan'; END IF;
  SELECT bouquet_id INTO b FROM ordering.order_items WHERE order_id=o.id AND item_type='BOUQUET';
  IF FOUND THEN RETURN b; END IF;
  IF public.app_now()<w.pickup_at-interval '2 hours' OR public.app_now()>=w.pickup_before THEN RAISE EXCEPTION 'Nap hoa trong 2 gio truoc hen va truoc han nhan'; END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id=p_slot AND current_seller_id=p_seller AND kiosk_id=w.kiosk_id FOR UPDATE;
  IF NOT FOUND OR s.status<>'RENTED_EMPTY' THEN RAISE EXCEPTION 'Can o trong cua shop tai dung tu nhan'; END IF;
  b:=flow.stock_bouquet(flow.new_batch(),w.product_id,p_slot,p_actor,p_qr);
  IF (SELECT sellable_until FROM kiosk_ops.bouquets WHERE id=b)<w.pickup_before THEN RAISE EXCEPTION 'Han tuoi cua hoa khong du den het lich nhan'; END IF;
  UPDATE kiosk_ops.bouquets SET status='SOLD',price_snapshot=o.total_amount WHERE id=b;
  UPDATE kiosk_ops.slots SET status='HELD',hold_order_id=o.id,hold_until=w.pickup_before,row_version=row_version+1 WHERE id=p_slot;
  INSERT INTO ordering.order_items(order_id,item_type,bouquet_id,slot_id,name_snapshot,quantity,unit_price,line_total)
    VALUES(o.id,'BOUQUET',b,p_slot,'Hoa dat rieng: '||(SELECT name FROM catalog.flower_products WHERE id=w.product_id),1,o.total_amount,o.total_amount);
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id,slot_id,movement_type,order_id,performed_by) VALUES(b,p_slot,'SALE_OUT',o.id,p_actor);
  PERFORM flow.audit(p_actor,'WEB_ORDER_READY','orders',o.id,jsonb_build_object('request',p_id,'slot',p_slot));
  RETURN b;
END $$;

CREATE OR REPLACE FUNCTION flow.web_cancel(p_id uuid,p_actor uuid,p_reason text) RETURNS void LANGUAGE plpgsql AS $$
DECLARE w ordering.web_requests; o ordering.orders; v_ref uuid;
BEGIN
  SELECT * INTO w FROM ordering.web_requests WHERE id=p_id FOR UPDATE;
  IF NOT FOUND OR NOT (p_actor=flow.sys_user() OR w.customer_id=p_actor OR EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role='SELLER' AND seller_id=w.seller_id AND status='ACTIVE') OR EXISTS(SELECT 1 FROM identity.users WHERE id=p_actor AND role IN ('ADMIN','SYSTEM') AND status='ACTIVE')) THEN RAISE EXCEPTION 'Khong co quyen huy yeu cau'; END IF;
  IF length(btrim(p_reason))<5 OR length(p_reason)>1000 THEN RAISE EXCEPTION 'Ly do can tu 5 den 1000 ky tu'; END IF;
  IF w.state IN ('CANCELLED','REJECTED') THEN RETURN; END IF;
  IF w.order_id IS NOT NULL THEN
    PERFORM 1 FROM payment.payments WHERE checkout_id=(SELECT checkout_id FROM ordering.orders WHERE id=w.order_id) AND kind='CHARGE' FOR UPDATE;
    SELECT * INTO o FROM ordering.orders WHERE id=w.order_id FOR UPDATE;
    IF o.status='AWAITING_PAYMENT' THEN
      UPDATE payment.payments SET status='CANCELLED' WHERE checkout_id=o.checkout_id AND kind='CHARGE' AND status='PENDING';
      PERFORM flow.release_checkout(o.checkout_id,'CANCELLED');
    ELSIF o.status='PAID' THEN
      IF w.customer_id=p_actor THEN RAISE EXCEPTION 'Don da tra tien: lien he shop qua ho so de doi chieu truoc khi huy'; END IF;
      IF EXISTS(SELECT 1 FROM ordering.order_items WHERE order_id=o.id) THEN PERFORM flow.dispense_failed(o.id,p_reason);
      ELSE
        v_ref:=flow.create_refund(o.id,o.total_amount,p_reason,flow.sys_user(),'MANUAL');
        PERFORM flow.refund_allocation(v_ref,'REFUND_CLEARING',o.subtotal);
        INSERT INTO ordering.disputes(kind,order_id,kiosk_id,reason) VALUES('DISPENSE_FAILED',o.id,o.kiosk_id,p_reason);
        PERFORM flow.set_order_status(o.id,'DISPENSE_FAILED','SYSTEM',NULL,p_reason);
        PERFORM flow.audit(NULL,'DISPENSE_FAILED','orders',o.id,jsonb_build_object('refund',v_ref,'reason',p_reason));
      END IF;
    ELSIF o.status NOT IN ('EXPIRED','CANCELLED','DISPENSE_FAILED','REFUNDED') THEN RAISE EXCEPTION 'Khong the huy don dang mo cua hoac da nhan'; END IF;
  END IF;
  UPDATE ordering.web_requests SET state='CANCELLED',quote_note=p_reason,updated_at=public.app_now() WHERE id=p_id;
  PERFORM flow.audit(p_actor,'WEB_REQUEST_CANCELLED','web_requests',p_id);
END $$;

CREATE OR REPLACE FUNCTION flow.expire_web_orders() RETURNS int LANGUAGE plpgsql AS $$
DECLARE w record; n int:=0;
BEGIN
  FOR w IN SELECT r.id FROM ordering.web_requests r JOIN ordering.orders o ON o.id=r.order_id WHERE r.kind='CUSTOM' AND r.state='ORDERED' AND o.status='PAID' AND r.pickup_before<public.app_now() AND NOT EXISTS(SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id) LOOP
    PERFORM flow.web_cancel(w.id,flow.sys_user(),'Shop chua nap hoa truoc han nhan'); n:=n+1;
  END LOOP;
  RETURN n;
END $$;

-- Preserve v3 kiosk behavior; defer actuation only for web orders.
CREATE OR REPLACE FUNCTION flow.checkout_paid(p_checkout uuid, p_txn text, p_success boolean, p_amount bigint, p_now timestamptz DEFAULT NULL)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); p payment.payments; o record; it record; v_ref uuid; v_order uuid; j uuid := public.uuid_v7(); v_online boolean;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE checkout_id = p_checkout AND kind = 'CHARGE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy khoản thu cho checkout %', p_checkout; END IF;
  IF p_amount IS DISTINCT FROM p.amount THEN RAISE EXCEPTION 'Số tiền webhook % khác số tiền cần thu %', p_amount, p.amount; END IF;
  IF p.status = 'SUCCEEDED' THEN
    IF p.gateway_txn_id = p_txn THEN RETURN; END IF;   -- webhook gọi lại lần 2: bỏ qua
    RAISE EXCEPTION 'Checkout % đã thanh toán bằng giao dịch khác %', p_checkout, p.gateway_txn_id;
  END IF;
  IF NOT p_success THEN
    IF p.status = 'PENDING' THEN
      UPDATE payment.payments SET status = 'FAILED', gateway_txn_id = p_txn WHERE id = p.id;
      PERFORM flow.release_checkout(p_checkout, 'CANCELLED');
    END IF;
    RETURN;
  END IF;
  IF p.status='PENDING' AND EXISTS(SELECT 1 FROM ordering.orders expired_order JOIN ordering.web_requests w ON w.order_id=expired_order.id WHERE expired_order.checkout_id=p_checkout AND expired_order.created_at+flow.cfg_min('hold_minutes')<=v_now) THEN
    UPDATE payment.payments SET status='EXPIRED' WHERE id=p.id;
    PERFORM flow.release_checkout(p_checkout,'EXPIRED');
    p.status:='EXPIRED';
  END IF;
  IF p.status IN ('EXPIRED','CANCELLED','FAILED') THEN
    -- tiền về sau khi đơn đã hết hạn: ghi nhận tiền, lập lệnh hoàn tự động, đơn giữ nguyên EXPIRED/CANCELLED
    UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
           raw_payload = jsonb_build_object('code','00','desc','late','orderCode',p_txn) WHERE id = p.id;
    SELECT id INTO v_order FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY total_amount DESC, id LIMIT 1;
    INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason)
    VALUES ('REFUND','ORDER_CHECKOUT', p_checkout, v_order, p.id, 'PAYOS', 'late-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_PAYMENT')
    RETURNING id INTO v_ref;
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', v_ref, 'Tiền về muộn sau khi đơn hết hạn'),
     (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', v_ref, 'Phải hoàn lại khách (LATE_PAYMENT)');
    PERFORM flow.audit(NULL, 'LATE_PAYMENT_REFUND_CREATED', 'payments', v_ref, jsonb_build_object('checkout', p_checkout, 'amount', p.amount));
    RETURN;
  END IF;
  UPDATE payment.payments SET status = 'SUCCEEDED', gateway_txn_id = p_txn, paid_at = v_now,
         raw_payload = jsonb_build_object('code','00','desc','success','orderCode',p_txn) WHERE id = p.id;
  FOR o IN SELECT * FROM ordering.orders WHERE checkout_id = p_checkout ORDER BY id FOR UPDATE LOOP
    CONTINUE WHEN o.status <> 'AWAITING_PAYMENT';
    PERFORM flow.set_order_status(o.id, 'PAID', 'SYSTEM');
    PERFORM flow.post_order_ledger(o.id);
    FOR it IN SELECT * FROM ordering.order_items WHERE order_id = o.id AND item_type = 'BOUQUET' AND line_status = 'ACTIVE' LOOP
      UPDATE kiosk_ops.bouquets SET status = 'SOLD' WHERE id = it.bouquet_id;
      INSERT INTO kiosk_ops.inventory_logs (bouquet_id, slot_id, movement_type, order_id) VALUES (it.bouquet_id, it.slot_id, 'SALE_OUT', o.id);
      UPDATE kiosk_ops.slots SET hold_until = v_now + flow.cfg_min('kiosk_pickup_minutes') WHERE id = it.slot_id; -- giữ tới khi khách lấy
    END LOOP;
    -- Web purchases wait for an authenticated pickup at the physical kiosk.
    IF EXISTS(SELECT 1 FROM ordering.web_requests WHERE order_id=o.id) THEN
      IF EXISTS(SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id=o.id AND status<>'HELD') THEN
        PERFORM flow.dispense_failed(o.id, 'Ô gặp sự cố trong lúc thanh toán');
        CONTINUE;
      END IF;
      UPDATE kiosk_ops.slots SET hold_until=(SELECT pickup_before FROM ordering.web_requests WHERE order_id=o.id) WHERE hold_order_id=o.id;
      CONTINUE;
    END IF;
    -- kiosk mất kết nối: vẫn ghi nhận tiền, không phát token; khách gọi flow.request_pickup khi kiosk online lại
    SELECT status = 'ONLINE' INTO v_online FROM kiosk_ops.kiosks WHERE id = o.kiosk_id;
    IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id) THEN
      -- đơn chỉ có phụ kiện (không giữ ô nào): giao ngay tại ngăn phụ kiện
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
      PERFORM flow.complete_order(o.id, 'KIOSK', 'Đơn chỉ có phụ kiện: giao ngay');
    ELSIF EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status <> 'HELD') THEN
      -- ô đã vào FAULT trong lúc khách thanh toán: không nhả được, hoàn 100% ngay
      PERFORM flow.dispense_failed(o.id, 'Ô gặp sự cố trong lúc thanh toán');
    ELSIF v_online THEN
      PERFORM flow.issue_pickup_tokens(o.id);
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
    END IF;   -- offline: đơn ở PAID chờ kiosk online (request_pickup) hoặc expire_pickups hoàn tiền
  END LOOP;
END $$;

CREATE OR REPLACE FUNCTION flow.request_pickup(p_order uuid, p_tracking text, p_kiosk uuid, p_now timestamptz DEFAULT NULL) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); o ordering.orders; v_k text; v_cmd uuid;
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  IF o.kiosk_id <> p_kiosk THEN RAISE EXCEPTION 'Đơn không nhận tại kiosk này'; END IF;
  IF o.status NOT IN ('PAID','DISPENSING') THEN RAISE EXCEPTION 'Đơn không ở trạng thái chờ nhận (%)', o.status; END IF;
  SELECT status INTO v_k FROM kiosk_ops.kiosks WHERE id = p_kiosk;
  IF v_k IS DISTINCT FROM 'ONLINE' THEN RAISE EXCEPTION 'Kiosk đang không trực tuyến, thử lại sau'; END IF;
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status = 'HELD' AND hold_until >= v_now) THEN
    RAISE EXCEPTION 'Hết thời gian giữ hàng tại tủ'; END IF;
  IF coalesce(p_tracking, '') = '' OR o.tracking_token <> p_tracking THEN
    PERFORM flow.audit(p_kiosk, 'PICKUP_CODE_REJECTED', 'orders', o.id, NULL, 'KIOSK');
    RAISE NOTICE 'Sai mã e-receipt';
    RETURN NULL;   -- không RAISE để audit được lưu
  END IF;
  IF EXISTS(SELECT 1 FROM ordering.web_requests WHERE order_id=o.id AND pickup_at>v_now) THEN RAISE EXCEPTION 'Chua den gio hen nhan hoa'; END IF;
  v_cmd := flow.issue_pickup_tokens(o.id);
  IF o.status = 'PAID' THEN PERFORM flow.set_order_status(o.id, 'DISPENSING', 'KIOSK', NULL, 'Kiosk online lại, phát token'); END IF;
  RETURN v_cmd;
END $$;

CREATE OR REPLACE VIEW screen.v_web_catalog AS
SELECT p.id AS product_id,p.seller_id,s.shop_name,p.name,p.description,p.price,k.id AS kiosk_id,k.name AS kiosk_name,k.address,
 (SELECT a.file_url FROM notify.attachments a WHERE a.owner_service='catalog' AND a.owner_type='flower_product' AND a.owner_id=p.id ORDER BY a.created_at LIMIT 1) AS photo,
 ARRAY(SELECT b.id FROM kiosk_ops.bouquets b JOIN kiosk_ops.slots sl ON sl.bouquet_id=b.id
       WHERE b.product_id=p.id AND sl.kiosk_id=k.id AND sl.status='STOCKED' AND b.status='STOCKED' AND b.sellable_until>public.app_now()+flow.cfg_min('hold_minutes') AND flow.slot_sellable(sl.id,s.id) ORDER BY b.sellable_until,b.id) AS bouquets
FROM catalog.flower_products p JOIN identity.sellers s ON s.id=p.seller_id
JOIN kiosk_ops.kiosks k ON k.status NOT IN ('DISABLED','MAINTENANCE')
WHERE p.status='ACTIVE' AND s.status='ACTIVE' AND flow.package_valid(s.id)
AND EXISTS(SELECT 1 FROM kiosk_ops.slots sl WHERE sl.kiosk_id=k.id AND sl.current_seller_id=s.id AND flow.slot_sellable(sl.id,s.id));

COMMIT;
