BEGIN;

-- 1. Explicit immutable payment funding provenance enum
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_type t
        JOIN pg_namespace n ON n.oid = t.typnamespace
        WHERE t.typname = 'payment_funding_provenance' AND n.nspname = 'payment'
    ) THEN
        CREATE TYPE payment.payment_funding_provenance AS ENUM (
            'PLATFORM_LEGACY',
            'PLATFORM',
            'DIRECT_REQUIRED',
            'SELLER_DIRECT'
        );
    END IF;
END $$;

-- 2. Extend payment.payments for seller direct payment provenance, receiver snapshot, and channel pinning
ALTER TABLE payment.payments
    ADD COLUMN IF NOT EXISTS funding_provenance payment.payment_funding_provenance,
    ADD COLUMN IF NOT EXISTS seller_id UUID REFERENCES identity.sellers(id),
    ADD COLUMN IF NOT EXISTS channel_id UUID REFERENCES payment.payment_channel_versions(id),
    ADD COLUMN IF NOT EXISTS channel_version INT,
    ADD COLUMN IF NOT EXISTS receiver_snapshot JSONB,
    ADD COLUMN IF NOT EXISTS is_direct_receipt BOOLEAN NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS idx_payments_seller_id ON payment.payments(seller_id);
CREATE INDEX IF NOT EXISTS idx_payments_channel_id ON payment.payments(channel_id);
CREATE INDEX IF NOT EXISTS idx_payments_funding_provenance ON payment.payments(funding_provenance);
CREATE INDEX IF NOT EXISTS idx_payments_is_direct_receipt ON payment.payments(is_direct_receipt);

-- 3. Explicit immutable provenance cutover: existing rows at migration time ONLY become PLATFORM_LEGACY
-- Reapplication does not reclassify existing new rows or repin legacy
UPDATE payment.payments
SET funding_provenance = 'PLATFORM_LEGACY',
    is_direct_receipt = false
WHERE funding_provenance IS NULL;

-- 4. Trigger to assign default funding provenance and is_direct_receipt on INSERT compatible with all existing SQL callers
CREATE OR REPLACE FUNCTION payment.assign_payment_provenance()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
    v_parent_prov payment.payment_funding_provenance;
    v_parent_direct boolean;
BEGIN
    -- Refunds inherit parent payment's provenance
    IF NEW.kind = 'REFUND' AND NEW.parent_payment_id IS NOT NULL THEN
        SELECT funding_provenance, is_direct_receipt
        INTO v_parent_prov, v_parent_direct
        FROM payment.payments WHERE id = NEW.parent_payment_id;

        IF FOUND THEN
            NEW.funding_provenance := coalesce(v_parent_prov, 'PLATFORM_LEGACY');
            NEW.is_direct_receipt := coalesce(v_parent_direct, false);
            RETURN NEW;
        END IF;
    END IF;

    -- Platform purposes: SUBSCRIPTION and KIOSK_APPLICATION are always PLATFORM
    IF NEW.purpose IN ('SUBSCRIPTION', 'KIOSK_APPLICATION') THEN
        NEW.funding_provenance := 'PLATFORM';
        NEW.is_direct_receipt := false;
        RETURN NEW;
    END IF;

    -- If explicit provenance was provided, respect it
    IF NEW.funding_provenance IS NOT NULL THEN
        IF NEW.funding_provenance = 'SELLER_DIRECT' THEN
            NEW.is_direct_receipt := true;
        ELSE
            NEW.is_direct_receipt := false;
        END IF;
        RETURN NEW;
    END IF;

    -- Default for any new flower sale charge is DIRECT_REQUIRED (unresolved direct receipt before channel pin)
    IF NEW.purpose = 'ORDER_CHECKOUT' THEN
        NEW.funding_provenance := 'DIRECT_REQUIRED';
        NEW.is_direct_receipt := false;
        RETURN NEW;
    END IF;

    -- Fallback for any other purpose
    NEW.funding_provenance := 'PLATFORM';
    NEW.is_direct_receipt := false;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_assign_payment_provenance ON payment.payments;
CREATE TRIGGER trg_assign_payment_provenance
BEFORE INSERT ON payment.payments
FOR EACH ROW EXECUTE FUNCTION payment.assign_payment_provenance();

-- 5. Trigger enforcing immutable payment snapshot and provenance invariants
CREATE OR REPLACE FUNCTION payment.enforce_payment_snapshot_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    -- Provenance cannot be changed backwards once set to PLATFORM_LEGACY, PLATFORM, or SELLER_DIRECT
    IF OLD.funding_provenance IN ('PLATFORM_LEGACY', 'PLATFORM', 'SELLER_DIRECT') THEN
        IF NEW.funding_provenance IS DISTINCT FROM OLD.funding_provenance THEN
            RAISE EXCEPTION 'Nguồn gốc thanh toán funding_provenance là bất biến sau khi xác định (payment %, % -> %)',
                OLD.id, OLD.funding_provenance, NEW.funding_provenance;
        END IF;
    END IF;

    -- Once channel_id is pinned, it can NEVER be modified, repinned, or cleared
    IF OLD.channel_id IS NOT NULL THEN
        IF NEW.channel_id IS DISTINCT FROM OLD.channel_id OR
           NEW.channel_version IS DISTINCT FROM OLD.channel_version OR
           NEW.seller_id IS DISTINCT FROM OLD.seller_id OR
           NEW.is_direct_receipt IS DISTINCT FROM OLD.is_direct_receipt OR
           NEW.receiver_snapshot IS DISTINCT FROM OLD.receiver_snapshot THEN
            RAISE EXCEPTION 'Thông tin kênh thanh toán và ảnh chụp người nhận là bất biến sau khi gắn vào giao dịch (payment %)', OLD.id;
        END IF;
    END IF;

    -- When pinning channel_id for the first time:
    IF OLD.channel_id IS NULL AND NEW.channel_id IS NOT NULL THEN
        IF OLD.funding_provenance = 'PLATFORM_LEGACY' THEN
            RAISE EXCEPTION 'Không thể gắn kênh người bán vào giao dịch cũ thuộc nền tảng (payment %)', OLD.id;
        END IF;
        IF NEW.seller_id IS NULL THEN
            RAISE EXCEPTION 'Giao dịch thanh toán trực tiếp phải có seller_id hợp lệ (payment %)', NEW.id;
        END IF;
        IF NEW.channel_version IS NULL OR NEW.channel_version <= 0 THEN
            RAISE EXCEPTION 'Phiên bản kênh thanh toán không hợp lệ (payment %)', NEW.id;
        END IF;
        IF NEW.funding_provenance <> 'SELLER_DIRECT' OR NEW.is_direct_receipt IS NOT TRUE THEN
            RAISE EXCEPTION 'Giao dịch gắn kênh người bán phải có funding_provenance = SELLER_DIRECT và is_direct_receipt = true (payment %)', NEW.id;
        END IF;
        IF NEW.receiver_snapshot IS NULL THEN
            RAISE EXCEPTION 'Giao dịch gắn kênh người bán phải có ảnh chụp người nhận receiver_snapshot (payment %)', NEW.id;
        END IF;
        -- Ensure channel belongs to the seller and version matches
        IF NOT EXISTS (
            SELECT 1 FROM payment.payment_channel_versions cv
            WHERE cv.id = NEW.channel_id
              AND cv.seller_id = NEW.seller_id
              AND cv.version = NEW.channel_version
        ) THEN
            RAISE EXCEPTION 'Kênh thanh toán % không khớp với seller % hoặc version %', NEW.channel_id, NEW.seller_id, NEW.channel_version;
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_payment_snapshot_immutability ON payment.payments;
CREATE TRIGGER trg_payment_snapshot_immutability
BEFORE UPDATE ON payment.payments
FOR EACH ROW EXECUTE FUNCTION payment.enforce_payment_snapshot_immutability();

-- 6. Table for tracking channel verification attempts and confirm-webhook challenges with attempt nonce and expiry
CREATE TABLE IF NOT EXISTS payment.channel_verification_attempts (
    id UUID PRIMARY KEY DEFAULT public.uuid_v7(),
    channel_version_id UUID NOT NULL REFERENCES payment.payment_channel_versions(id),
    seller_id UUID NOT NULL REFERENCES identity.sellers(id),
    attempt_token TEXT NOT NULL UNIQUE,
    sample_received BOOLEAN NOT NULL DEFAULT false,
    confirm_reply_received BOOLEAN NOT NULL DEFAULT false,
    confirmed_account TEXT,
    confirmed_name TEXT,
    status TEXT NOT NULL DEFAULT 'PENDING' CHECK (status IN ('PENDING', 'SUCCEEDED', 'FAILED', 'EXPIRED')),
    expires_at TIMESTAMPTZ NOT NULL DEFAULT (public.app_now() + interval '10 minutes'),
    created_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now()
);

CREATE INDEX IF NOT EXISTS idx_channel_verify_attempts_version ON payment.channel_verification_attempts(channel_version_id);
CREATE INDEX IF NOT EXISTS idx_channel_verify_attempts_token ON payment.channel_verification_attempts(attempt_token);
CREATE UNIQUE INDEX IF NOT EXISTS idx_channel_one_pending_attempt ON payment.channel_verification_attempts(channel_version_id) WHERE status = 'PENDING';

-- 7. Replace flow.post_order_ledger: direct receipts do NOT create GATEWAY_CLEARING, PLATFORM_LOYALTY, or SELLER_PENDING/AVAILABLE
CREATE OR REPLACE FUNCTION flow.post_order_ledger(p_order uuid)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE o ordering.orders; p payment.payments; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO o FROM ordering.orders WHERE id = p_order;
  IF NOT FOUND THEN RAISE EXCEPTION 'Đơn % không tồn tại', p_order; END IF;
  SELECT * INTO p FROM payment.payments WHERE checkout_id = o.checkout_id AND kind = 'CHARGE' AND status = 'SUCCEEDED';
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Chỉ ghi sổ đơn đã thu tiền thành công (đơn % đang %)', o.order_code, o.status; END IF;
  -- Direct receipt guard: no PLATFORM_LOYALTY subsidy, no GATEWAY_CLEARING, no SELLER_PENDING / AVAILABLE
  IF p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) THEN
    RETURN;
  END IF;
  -- Legacy platform flow remains completely unchanged:
  BEGIN
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
    SELECT j, a, s, amt, 'ORDER', o.id, m FROM (VALUES
      ('GATEWAY_CLEARING', NULL::uuid, o.total_amount, 'Tiền khách trả'),
      ('PLATFORM_LOYALTY', NULL::uuid, o.discount_amount, 'Nền tảng chịu phần đổi điểm'),
      ('SELLER_PENDING',   o.seller_id, -o.subtotal, 'Phần seller, chờ đối soát')
    ) v(a, s, amt, m) WHERE amt <> 0;
  EXCEPTION WHEN unique_violation THEN
    RETURN;   -- đã ghi sổ trước đó (sự kiện lặp): coi là đã xử lý
  END;
END $function$;

-- 8. Replace flow.checkout_paid: handles direct receipt late payment and refunds without platform cash
CREATE OR REPLACE FUNCTION flow.checkout_paid(p_checkout uuid, p_txn text, p_success boolean, p_amount bigint, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE v_now timestamptz := flow.tick(p_now); p payment.payments; o record; it record; v_ref uuid; v_order uuid; j uuid := public.uuid_v7(); v_online boolean;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE checkout_id = p_checkout AND kind = 'CHARGE' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không tìm thấy khoản thu cho checkout %', p_checkout; END IF;
  IF p_amount IS DISTINCT FROM p.amount THEN RAISE EXCEPTION 'Số tiền webhook % khác số tiền cần thu %', p_amount, p.amount; END IF;

  -- Cutover guard: new flower orders in DIRECT_REQUIRED cannot pay before direct binding
  IF p.purpose = 'ORDER_CHECKOUT' AND p.funding_provenance = 'DIRECT_REQUIRED' THEN
    RAISE EXCEPTION 'Giao dịch mua hoa mới (%) chưa được gắn kênh người bán trực tiếp, không thể thanh toán', p.id;
  END IF;

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
    INSERT INTO payment.payments (kind, purpose, checkout_id, order_id, parent_payment_id, gateway, idempotency_key, amount, status, approved_by, reason, seller_id, channel_id, channel_version, receiver_snapshot, funding_provenance, is_direct_receipt)
    VALUES ('REFUND','ORDER_CHECKOUT', p_checkout, v_order, p.id, 'PAYOS', 'late-' || p.id, p.amount, 'PENDING', flow.sys_user(), 'LATE_PAYMENT', p.seller_id, p.channel_id, p.channel_version, p.receiver_snapshot, p.funding_provenance, p.is_direct_receipt)
    RETURNING id INTO v_ref;
    -- Direct receipt guard: late payment creates direct seller refund liability, not platform cash
    IF p.funding_provenance <> 'SELLER_DIRECT' AND NOT coalesce(p.is_direct_receipt, false) THEN
      INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
       (j, 'GATEWAY_CLEARING', NULL,  p.amount, 'REFUND', v_ref, 'Tiền về muộn sau khi đơn hết hạn'),
       (j, 'REFUND_CLEARING',  NULL, -p.amount, 'REFUND', v_ref, 'Phải hoàn lại khách (LATE_PAYMENT)');
    END IF;
    PERFORM flow.audit(NULL, 'LATE_PAYMENT_REFUND_CREATED', 'payments', v_ref, jsonb_build_object('checkout', p_checkout, 'amount', p.amount, 'provenance', p.funding_provenance));
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
      UPDATE kiosk_ops.slots SET hold_until = v_now + flow.cfg_min('kiosk_pickup_minutes') WHERE id = it.slot_id;
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
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
      PERFORM flow.complete_order(o.id, 'KIOSK', 'Đơn chỉ có phụ kiện: giao ngay');
    ELSIF EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE hold_order_id = o.id AND status <> 'HELD') THEN
      PERFORM flow.dispense_failed(o.id, 'Ô gặp sự cố trong lúc thanh toán');
    ELSIF v_online THEN
      PERFORM flow.issue_pickup_tokens(o.id);
      PERFORM flow.set_order_status(o.id, 'DISPENSING', 'SYSTEM');
    END IF;
  END LOOP;
END $function$;

-- 9. Replace flow.confirm_refund: refuses legacy admin confirmation on direct receipts
CREATE OR REPLACE FUNCTION flow.confirm_refund(p_refund uuid, p_admin uuid, p_proof_url text, p_now timestamp with time zone DEFAULT NULL::timestamp with time zone)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE v_now timestamptz := flow.tick(p_now); r payment.payments; o ordering.orders; j uuid := public.uuid_v7(); v_refunded bigint;
BEGIN
  PERFORM flow.require_active_admin(p_admin);
  SELECT * INTO r FROM payment.payments WHERE id = p_refund AND kind = 'REFUND' FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Lệnh hoàn % không tồn tại', p_refund; END IF;
  IF r.status <> 'PENDING' THEN RAISE EXCEPTION 'Lệnh hoàn không ở trạng thái chờ (%)', r.status; END IF;
  -- Direct receipt guard: refuse legacy admin confirmation on seller-direct receipts until increment B
  IF r.funding_provenance = 'SELLER_DIRECT' OR coalesce(r.is_direct_receipt, false) THEN
    RAISE EXCEPTION 'Lệnh hoàn tiền trực tiếp của người bán (%) chưa được hỗ trợ quyết toán qua luồng admin nền tảng trong giai đoạn A2', r.id;
  END IF;
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
    INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
     (j, 'REFUND_CLEARING',  NULL,  r.amount, 'REFUND_PAYOUT', r.id, 'Đã chi hoàn cho khách'),
     (j, 'GATEWAY_CLEARING', NULL, -r.amount, 'REFUND_PAYOUT', r.id, 'Chi hoàn qua ' || r.gateway);
  ELSE
    PERFORM flow.refund_allocation(r.id, 'GATEWAY_CLEARING');
  END IF;
  SELECT coalesce(sum(amount), 0) INTO v_refunded FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';
  IF o.status = 'DISPUTED' AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, 'Đã chi hoàn ' || r.amount || 'đ');
    PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
  ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% sau đối soát');
  ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
    PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% do tủ không nhả hàng');
  END IF;
  PERFORM flow.audit(p_admin, 'REFUND_CONFIRMED', 'payments', r.id, jsonb_build_object('amount', r.amount, 'order', o.order_code));
  IF o.id IS NOT NULL AND NOT EXISTS (SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING') THEN
    UPDATE ordering.orders SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL WHERE id = o.id AND refund_bank_name IS NOT NULL;
    IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
  END IF;
END $function$;

-- 10. Replace flow.refund_allocation: guards against writing platform ledger entries for direct receipts
CREATE OR REPLACE FUNCTION flow.refund_allocation(p_refund uuid, p_credit_account text, p_seller_share bigint DEFAULT NULL)
 RETURNS void
 LANGUAGE plpgsql
AS $function$
DECLARE r payment.payments; o ordering.orders; v_sp bigint; v_loy bigint; v_pend bigint; v_from_pend bigint; j uuid := public.uuid_v7();
BEGIN
  SELECT * INTO r FROM payment.payments WHERE id = p_refund;
  SELECT * INTO o FROM ordering.orders WHERE id = r.order_id;
  IF o.total_amount = 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không hoàn được'; END IF;
  -- Direct receipt guard: no platform ledger allocations
  IF r.funding_provenance = 'SELLER_DIRECT' OR coalesce(r.is_direct_receipt, false) THEN
    RETURN;
  END IF;
  v_sp  := coalesce(p_seller_share, round(r.amount::numeric * o.subtotal / o.total_amount));
  IF v_sp < r.amount OR v_sp > o.subtotal THEN RAISE EXCEPTION 'Phần seller chịu % không hợp lệ (hoàn %, hàng %)', v_sp, r.amount, o.subtotal; END IF;
  v_loy := v_sp - r.amount;
  v_pend := flow.order_seller_pending(o.id);
  v_from_pend := least(greatest(v_pend, 0), v_sp);
  INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo)
  SELECT j, a, s, amt, 'REFUND', r.id, m FROM (VALUES
    ('SELLER_PENDING',   o.seller_id, v_from_pend,        'Seller chịu hoàn (trừ tiền chờ đối soát)'),
    ('SELLER_AVAILABLE', o.seller_id, v_sp - v_from_pend, 'Seller chịu hoàn (trừ ví khả dụng, có thể thành công nợ)'),
    ('PLATFORM_LOYALTY', NULL::uuid,  -v_loy,             'Hoàn lại phần đổi điểm nền tảng đã chịu'),
    (p_credit_account,   NULL::uuid,  -r.amount,          'Chi hoàn ' || coalesce(r.reason, ''))
  ) v(a, s, amt, m) WHERE amt <> 0;
END $function$;

COMMIT;
