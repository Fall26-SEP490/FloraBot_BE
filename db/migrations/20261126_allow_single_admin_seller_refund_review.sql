BEGIN;

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

    -- Platform-funded payouts still require two administrators.
    IF r.approved_by = p_admin
        AND (r.funding_provenance = 'SELLER_DIRECT' OR coalesce(r.is_direct_receipt, false)) IS NOT TRUE THEN
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

COMMIT;
