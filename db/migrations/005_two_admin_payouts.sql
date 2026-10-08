-- Supplemental runtime controls; the supplied SQL verification suite is unchanged.
ALTER TABLE payment.withdrawal_requests ADD COLUMN IF NOT EXISTS paid_by uuid;
ALTER TABLE payment.withdrawal_requests ADD COLUMN IF NOT EXISTS approved_at timestamptz;
ALTER TABLE payment.payments ADD COLUMN IF NOT EXISTS paid_by uuid;

CREATE OR REPLACE FUNCTION flow.require_active_admin(p_admin uuid) RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM identity.users WHERE id=p_admin AND role='ADMIN' AND status='ACTIVE' FOR SHARE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Cần tài khoản quản trị đang hoạt động'; END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.approve_withdrawal(p_w uuid, p_admin uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE w payment.withdrawal_requests;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO w FROM payment.withdrawal_requests WHERE id=p_w FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy yêu cầu rút tiền'; END IF;
  IF w.status='APPROVED' AND w.approved_by=p_admin THEN RETURN; END IF;
  IF w.status<>'PENDING' THEN RAISE EXCEPTION 'Yêu cầu rút không ở trạng thái chờ duyệt'; END IF;
  UPDATE payment.withdrawal_requests SET status='APPROVED',approved_by=p_admin,approved_at=public.app_now() WHERE id=p_w;
  PERFORM flow.audit(p_admin,'WITHDRAWAL_APPROVED','withdrawal_requests',p_w,jsonb_build_object('amount',w.amount));
END $$;

CREATE OR REPLACE FUNCTION flow.approve_refund(p_refund uuid, p_admin uuid) RETURNS void LANGUAGE plpgsql AS $$
DECLARE r payment.payments;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO r FROM payment.payments WHERE id=p_refund AND kind='REFUND' FOR UPDATE;
  IF NOT FOUND OR r.status<>'PENDING' THEN RAISE EXCEPTION 'Lệnh hoàn không ở trạng thái chờ'; END IF;
  IF r.approved_by=p_admin THEN RETURN; END IF;
  IF EXISTS(SELECT 1 FROM identity.users WHERE id=r.approved_by AND role='ADMIN') THEN
    RAISE EXCEPTION 'Lệnh hoàn đã có người duyệt, không được thay người duyệt';
  END IF;
  UPDATE payment.payments SET approved_by=p_admin WHERE id=p_refund;
  PERFORM flow.audit(p_admin,'REFUND_APPROVED','payments',p_refund,jsonb_build_object('amount',r.amount));
END $$;

CREATE OR REPLACE FUNCTION flow.pay_withdrawal(p_w uuid, p_admin uuid, p_proof_url text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); w payment.withdrawal_requests; v_avail bigint; j uuid := public.uuid_v7();
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO w FROM payment.withdrawal_requests WHERE id = p_w FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Yêu cầu rút % không tồn tại', p_w; END IF;
  IF w.status <> 'APPROVED' THEN RAISE EXCEPTION 'Yêu cầu rút không ở trạng thái chờ'; END IF;
  IF w.approved_by=p_admin THEN RAISE EXCEPTION 'Người chi phải khác người duyệt'; END IF;
  IF p_proof_url IS NULL OR btrim(p_proof_url)='' THEN RAISE EXCEPTION 'Chi tiền phải kèm chứng từ chuyển khoản'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || w.seller_id::text));
  SELECT available_balance INTO v_avail FROM payment.v_seller_balance WHERE seller_id = w.seller_id;
  v_avail := coalesce(v_avail, 0);
  IF v_avail <= 0 OR v_avail < w.amount THEN RAISE EXCEPTION 'Số dư khả dụng không đủ tại thời điểm chi (% < %)', v_avail, w.amount; END IF;
  PERFORM flow.attach('payment', 'withdrawal', w.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.withdrawal_requests SET status = 'PAID', paid_by = p_admin, paid_at = v_now WHERE id = w.id;
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
   (j,'SELLER_AVAILABLE',w.seller_id, w.amount,'WITHDRAWAL',w.id,'Chi trả seller'),
   (j,'GATEWAY_CLEARING',NULL,-w.amount,'WITHDRAWAL',w.id,'Chuyển khoản ra ngân hàng');
  PERFORM flow.audit(p_admin, 'WITHDRAWAL_PAID', 'withdrawal_requests', w.id, jsonb_build_object('amount', w.amount));
END $$;

CREATE OR REPLACE FUNCTION flow.confirm_refund(p_refund uuid, p_admin uuid, p_proof_url text, p_now timestamptz DEFAULT NULL) RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_now timestamptz := flow.tick(p_now); r payment.payments; o ordering.orders; j uuid := public.uuid_v7(); v_refunded bigint;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO r FROM payment.payments WHERE id = p_refund AND kind = 'REFUND' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh hoàn % không tồn tại', p_refund; END IF;
  IF r.status <> 'PENDING' THEN RAISE EXCEPTION 'Lệnh hoàn không ở trạng thái chờ (%)', r.status; END IF;
  IF NOT EXISTS(SELECT 1 FROM identity.users WHERE id=r.approved_by AND role='ADMIN') THEN
    RAISE EXCEPTION 'Lệnh hoàn tự động cần quản trị viên duyệt trước khi chi';
  END IF;
  IF r.approved_by=p_admin THEN RAISE EXCEPTION 'Người chi phải khác người duyệt'; END IF;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id FOR UPDATE;
  IF r.purpose = 'ORDER_CHECKOUT' AND o.refund_bank_name IS NULL THEN
    RAISE EXCEPTION 'Đơn % chưa có tài khoản nhận hoàn: khách khai trên e-receipt (submit_refund_info) rồi admin mới chuyển khoản được', o.order_code; END IF;
  IF p_proof_url IS NULL OR btrim(p_proof_url)='' THEN RAISE EXCEPTION 'Chi hoàn phải kèm chứng từ chuyển khoản'; END IF;
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || coalesce(o.seller_id::text, 'none')));
  PERFORM flow.attach('payment', 'refund', r.id, p_proof_url, 'PROOF', p_admin);
  UPDATE payment.payments SET status = 'SUCCEEDED', paid_at = v_now, paid_by = p_admin,
         gateway_txn_id = coalesce(gateway_txn_id, r.gateway || '-RF-' || replace(r.id::text, '-', '')) WHERE id = r.id;
  IF EXISTS (SELECT 1 FROM payment.ledger_entries WHERE ref_type = 'REFUND' AND ref_id = r.id AND account = 'REFUND_CLEARING') THEN
    -- đã phân bổ lúc lập lệnh (tiền về muộn / tủ không nhả hàng): chỉ còn bút toán chi tiền ra
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'REFUND_CLEARING',  NULL,  r.amount, 'REFUND_PAYOUT', r.id, 'Đã chi hoàn cho khách'),
     (j, 'GATEWAY_CLEARING', NULL, -r.amount, 'REFUND_PAYOUT', r.id, 'Chi hoàn qua ' || r.gateway);
  ELSE
    PERFORM flow.refund_allocation(r.id, 'GATEWAY_CLEARING');
  END IF;
  SELECT coalesce(sum(amount), 0) INTO v_refunded FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';
  IF o.status = 'DISPUTED' AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    -- chỉ đóng khiếu nại và đối soát khi KHÔNG còn lệnh hoàn nào chờ chi (tiền seller bị giữ tới lúc đó)
    PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, 'Đã chi hoàn ' || r.amount || 'đ');
    PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
  ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% sau đối soát');
  ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% do tủ không nhả hàng');
    -- ticket DISPENSE_FAILED vẫn OPEN cho tới khi kỹ thuật xử lý ô (resolve_device_fault)
  END IF;
  PERFORM flow.audit(p_admin, 'REFUND_CONFIRMED', 'payments', r.id, jsonb_build_object('amount', r.amount, 'order', o.order_code));
  -- đã chi hết lệnh hoàn của đơn -> xóa STK khách (chỉ giữ đúng thời gian cần, NĐ 13/2023)
  IF o.id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    UPDATE ordering.orders SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL WHERE id = o.id AND refund_bank_name IS NOT NULL;
    IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
  END IF;
END $$;
