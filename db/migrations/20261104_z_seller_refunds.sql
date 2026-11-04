-- 20261104_z_seller_refunds.sql
-- Migration: Seller Direct Manual Refunds (Worker B)
-- Flowers seller receives direct payments, seller funds refund manually off-system,
-- submits private proof (PENDING_REVIEW), and Admin confirms after review.
-- Direct refunds write zero platform cash / SELLER_AVAILABLE / SELLER_PENDING ledger entries.

BEGIN;

-- 1. Ensure funding provenance enum exists
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

-- 2. Extend payment.payments if columns are not present
ALTER TABLE payment.payments
    ADD COLUMN IF NOT EXISTS funding_provenance payment.payment_funding_provenance,
    ADD COLUMN IF NOT EXISTS seller_id UUID REFERENCES identity.sellers(id),
    ADD COLUMN IF NOT EXISTS channel_id UUID,
    ADD COLUMN IF NOT EXISTS channel_version INT,
    ADD COLUMN IF NOT EXISTS receiver_snapshot JSONB,
    ADD COLUMN IF NOT EXISTS is_direct_receipt BOOLEAN NOT NULL DEFAULT false;

CREATE INDEX IF NOT EXISTS idx_payments_seller_id ON payment.payments(seller_id);
CREATE INDEX IF NOT EXISTS idx_payments_funding_provenance ON payment.payments(funding_provenance);
CREATE INDEX IF NOT EXISTS idx_payments_is_direct_receipt ON payment.payments(is_direct_receipt);

-- 3. Table for tracking seller direct transfer submissions
CREATE TABLE IF NOT EXISTS payment.seller_refund_transfers (
    id UUID PRIMARY KEY DEFAULT public.uuid_v7(),
    refund_id UUID NOT NULL UNIQUE REFERENCES payment.payments(id) ON DELETE RESTRICT,
    seller_id UUID NOT NULL REFERENCES identity.sellers(id) ON DELETE RESTRICT,
    order_id UUID NOT NULL REFERENCES ordering.orders(id) ON DELETE RESTRICT,
    submitted_by UUID NOT NULL REFERENCES identity.users(id) ON DELETE RESTRICT,
    amount BIGINT NOT NULL CHECK (amount > 0),
    currency TEXT NOT NULL DEFAULT 'VND' CHECK (currency = 'VND'),
    receiver_bank_name TEXT NOT NULL,
    receiver_bank_holder TEXT NOT NULL,
    receiver_account_no_enc TEXT NOT NULL,
    provider_reference TEXT NOT NULL,
    transferred_at TIMESTAMPTZ NOT NULL,
    evidence_attachment_id UUID REFERENCES notify.attachments(id),
    evidence_url TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'PENDING_REVIEW' CHECK (status IN ('PENDING_REVIEW', 'CONFIRMED', 'REJECTED')),
    note TEXT,
    reviewed_by UUID REFERENCES identity.users(id),
    reviewed_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now()
);

-- Unique provider reference across all refunds prevents duplicate pay claim on different refunds
CREATE UNIQUE INDEX IF NOT EXISTS idx_seller_refund_transfers_provider_ref
    ON payment.seller_refund_transfers (lower(provider_reference));

-- Unique evidence URL prevents reusing the same receipt proof image for multiple refunds
CREATE UNIQUE INDEX IF NOT EXISTS idx_seller_refund_transfers_evidence_url
    ON payment.seller_refund_transfers (evidence_url);

CREATE INDEX IF NOT EXISTS idx_seller_refund_transfers_seller
    ON payment.seller_refund_transfers (seller_id, created_at DESC);
CREATE INDEX IF NOT EXISTS idx_seller_refund_transfers_status
    ON payment.seller_refund_transfers (status);

-- 4. Supersede flow.confirm_refund: accepts seller direct refunds upon validated transfer submission
CREATE OR REPLACE FUNCTION flow.confirm_refund(
    p_refund uuid,
    p_admin uuid,
    p_proof_url text,
    p_now timestamptz DEFAULT NULL
)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    v_now timestamptz := flow.tick(p_now);
    r payment.payments;
    o ordering.orders;
    j uuid := public.uuid_v7();
    v_refunded bigint;
    v_sub payment.seller_refund_transfers;
BEGIN
    PERFORM flow.require_active_admin(p_admin);

    SELECT * INTO r FROM payment.payments WHERE id = p_refund AND kind = 'REFUND' FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'Lệnh hoàn % không tồn tại', p_refund;
    END IF;

    IF r.status <> 'PENDING' THEN
        RAISE EXCEPTION 'Lệnh hoàn không ở trạng thái chờ (%)', r.status;
    END IF;

    -- Human admin approval check: must be approved by an active ADMIN
    IF NOT EXISTS (
        SELECT 1 FROM identity.users
        WHERE id = r.approved_by AND role = 'ADMIN' AND status = 'ACTIVE'
    ) THEN
        RAISE EXCEPTION 'Lệnh hoàn cần quản trị viên duyệt trước khi chi';
    END IF;

    -- Reviewer independence: confirmer cannot be the approver
    IF r.approved_by = p_admin THEN
        RAISE EXCEPTION 'Người chi phải khác người duyệt';
    END IF;

    SELECT * INTO o FROM ordering.orders WHERE id = r.order_id FOR UPDATE;

    -- =========================================================================
    -- BRANCH A: SELLER DIRECT MANUAL REFUND
    -- =========================================================================
    IF r.funding_provenance = 'SELLER_DIRECT' OR coalesce(r.is_direct_receipt, false) THEN
        SELECT * INTO v_sub FROM payment.seller_refund_transfers WHERE refund_id = r.id FOR UPDATE;
        IF NOT FOUND OR v_sub.status <> 'PENDING_REVIEW' THEN
            RAISE EXCEPTION 'Lệnh hoàn tiền trực tiếp của người bán (%) chưa có chuyển khoản hợp lệ chờ duyệt', r.id;
        END IF;

        -- Reviewer independence: admin confirming transfer cannot be the submitter
        IF v_sub.submitted_by = p_admin THEN
            RAISE EXCEPTION 'Quản trị viên duyệt xác nhận phải khác người bán gửi chuyển khoản';
        END IF;

        -- Attach admin confirmation proof if provided
        IF p_proof_url IS NOT NULL AND btrim(p_proof_url) <> '' THEN
            PERFORM flow.attach('payment', 'refund', r.id, p_proof_url, 'PROOF', p_admin);
        END IF;

        -- Mark transfer confirmed
        UPDATE payment.seller_refund_transfers
        SET status = 'CONFIRMED',
            reviewed_by = p_admin,
            reviewed_at = v_now,
            updated_at = v_now
        WHERE id = v_sub.id;

        -- Mark refund payment succeeded with provider reference
        UPDATE payment.payments
        SET status = 'SUCCEEDED',
            paid_at = v_now,
            paid_by = p_admin,
            gateway_txn_id = coalesce(gateway_txn_id, v_sub.provider_reference, r.gateway || '-RF-' || replace(r.id::text, '-', ''))
        WHERE id = r.id;

        -- DIRECT REFUND: ZERO platform cash / SELLER_PENDING / SELLER_AVAILABLE ledger entries!

        -- Update order status
        SELECT coalesce(sum(amount), 0) INTO v_refunded
        FROM payment.payments
        WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';

        IF o.status = 'DISPUTED' AND NOT EXISTS (
            SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING'
        ) THEN
            PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, 'Người bán đã hoàn trực tiếp ' || r.amount || 'đ');
            PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
        ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
            PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Người bán đã hoàn trực tiếp 100%');
        ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
            PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Người bán đã hoàn trực tiếp 100% do tủ không nhả hàng');
        END IF;

        PERFORM flow.audit(p_admin, 'SELLER_DIRECT_REFUND_CONFIRMED', 'payments', r.id,
            jsonb_build_object('amount', r.amount, 'order', o.order_code, 'seller_id', o.seller_id, 'reference', v_sub.provider_reference));

        -- Clear customer bank account if all refunds finalized
        IF o.id IS NOT NULL AND NOT EXISTS (
            SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING'
        ) THEN
            UPDATE ordering.orders
            SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL
            WHERE id = o.id AND refund_bank_name IS NOT NULL;
            IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
        END IF;

        RETURN;
    END IF;

    -- =========================================================================
    -- BRANCH B: LEGACY PLATFORM REFUND (Preserved byte-for-byte behavior)
    -- =========================================================================
    IF r.purpose = 'ORDER_CHECKOUT' AND o.refund_bank_name IS NULL THEN
        RAISE EXCEPTION 'Đơn % chưa có tài khoản nhận hoàn: khách khai trên e-receipt (submit_refund_info) rồi admin mới chuyển khoản được', o.order_code;
    END IF;

    IF p_proof_url IS NULL OR btrim(p_proof_url) = '' THEN
        RAISE EXCEPTION 'Chi hoàn phải kèm chứng từ chuyển khoản';
    END IF;

    PERFORM pg_advisory_xact_lock(hashtext('seller:' || coalesce(o.seller_id::text, 'none')));
    PERFORM flow.attach('payment', 'refund', r.id, p_proof_url, 'PROOF', p_admin);

    UPDATE payment.payments
    SET status = 'SUCCEEDED',
        paid_at = v_now,
        paid_by = p_admin,
        gateway_txn_id = coalesce(gateway_txn_id, r.gateway || '-RF-' || replace(r.id::text, '-', ''))
    WHERE id = r.id;

    IF EXISTS (
        SELECT 1 FROM payment.ledger_entries
        WHERE ref_type = 'REFUND' AND ref_id = r.id AND account = 'REFUND_CLEARING'
    ) THEN
        INSERT INTO payment.ledger_entries (journal_id, account, seller_id, amount, ref_type, ref_id, memo) VALUES
            (j, 'REFUND_CLEARING',  NULL,  r.amount, 'REFUND_PAYOUT', r.id, 'Đã chi hoàn cho khách'),
            (j, 'GATEWAY_CLEARING', NULL, -r.amount, 'REFUND_PAYOUT', r.id, 'Chi hoàn qua ' || r.gateway);
    ELSE
        PERFORM flow.refund_allocation(r.id, 'GATEWAY_CLEARING');
    END IF;

    SELECT coalesce(sum(amount), 0) INTO v_refunded
    FROM payment.payments
    WHERE order_id = o.id AND kind = 'REFUND' AND status = 'SUCCEEDED';

    IF o.status = 'DISPUTED' AND NOT EXISTS (
        SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING'
    ) THEN
        PERFORM flow.set_order_status(o.id, CASE WHEN v_refunded >= o.total_amount THEN 'REFUNDED' ELSE 'COMPLETED' END, 'ADMIN', p_admin, 'Đã chi hoàn ' || r.amount || 'đ');
        PERFORM flow.move_pending_to_available(o.id, 'Đối soát sau khiếu nại');
    ELSIF o.status = 'COMPLETED' AND v_refunded >= o.total_amount THEN
        PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% sau đối soát');
    ELSIF o.status = 'DISPENSE_FAILED' AND v_refunded >= o.total_amount THEN
        PERFORM flow.set_order_status(o.id, 'REFUNDED', 'ADMIN', p_admin, 'Đã chi hoàn 100% do tủ không nhả hàng');
    END IF;

    PERFORM flow.audit(p_admin, 'REFUND_CONFIRMED', 'payments', r.id, jsonb_build_object('amount', r.amount, 'order', o.order_code));

    IF o.id IS NOT NULL AND NOT EXISTS (
        SELECT 1 FROM payment.payments WHERE order_id = o.id AND kind = 'REFUND' AND status = 'PENDING'
    ) THEN
        UPDATE ordering.orders
        SET refund_bank_name = NULL, refund_bank_account_enc = NULL, refund_bank_holder = NULL
        WHERE id = o.id AND refund_bank_name IS NOT NULL;
        IF FOUND THEN PERFORM flow.audit(NULL, 'REFUND_INFO_CLEARED', 'orders', o.id); END IF;
    END IF;
END $$;

-- 5. Guard flow.refund_allocation: zero platform ledger for direct refunds
CREATE OR REPLACE FUNCTION flow.refund_allocation(
    p_refund uuid,
    p_credit_account text,
    p_seller_share bigint DEFAULT NULL
)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
    r payment.payments;
    o ordering.orders;
    v_sp bigint;
    v_loy bigint;
    v_pend bigint;
    v_from_pend bigint;
    j uuid := public.uuid_v7();
BEGIN
    SELECT * INTO r FROM payment.payments WHERE id = p_refund;
    SELECT * INTO o FROM ordering.orders WHERE id = r.order_id;
    IF o.total_amount = 0 THEN RAISE EXCEPTION 'Đơn 0 đồng không hoàn được'; END IF;

    -- Direct receipt guard: no platform ledger allocations
    IF r.funding_provenance = 'SELLER_DIRECT' OR coalesce(r.is_direct_receipt, false) THEN
        RETURN;
    END IF;

    v_sp  := coalesce(p_seller_share, round(r.amount::numeric * o.subtotal / o.total_amount));
    IF v_sp < r.amount OR v_sp > o.subtotal THEN
        RAISE EXCEPTION 'Phần seller chịu % không hợp lệ (hoàn %, hàng %)', v_sp, r.amount, o.subtotal;
    END IF;
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
END $$;

COMMIT;
