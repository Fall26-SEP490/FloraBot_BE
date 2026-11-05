-- 20261104_zz_refund_contracts.sql
-- Module boundary typed contracts for Seller Direct Refunds (Payment module)
-- Replaces cross-module direct table references with typed contract functions.

BEGIN;

-- 1. Active seller owner verification with share lock
CREATE OR REPLACE FUNCTION payment.v2_rf_verify_active_seller_owner(
    p_actor_id uuid,
    p_seller_id uuid
)
RETURNS TABLE (
    id uuid
)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    RETURN QUERY
    SELECT u.id
    FROM identity.users u
    JOIN identity.sellers s ON s.id = u.seller_id
    WHERE u.id = p_actor_id
      AND u.role = 'SELLER'
      AND u.seller_id = p_seller_id
      AND u.status = 'ACTIVE'
      AND s.status = 'ACTIVE'
    FOR SHARE;
END;
$$;

-- 2. Paginated list of seller direct refunds with status filter
CREATE OR REPLACE FUNCTION payment.v2_rf_list_seller_refunds(
    p_seller_id uuid,
    p_status_filter text,
    p_offset integer,
    p_limit integer
)
RETURNS TABLE (
    id uuid,
    order_id uuid,
    order_code text,
    amount bigint,
    status text,
    submission_status text,
    reason text,
    created_at timestamptz,
    is_approved boolean,
    transferred_at timestamptz
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT p.id,
           p.order_id,
           o.order_code,
           p.amount,
           p.status,
           st.status AS submission_status,
           p.reason,
           p.created_at,
           (p.approved_by IS NOT NULL AND EXISTS(
               SELECT 1 FROM identity.users u WHERE u.id = p.approved_by AND u.role = 'ADMIN' AND u.status = 'ACTIVE'
           )) AS is_approved,
           st.transferred_at
    FROM payment.payments p
    JOIN ordering.orders o ON o.id = p.order_id
    LEFT JOIN payment.seller_refund_transfers st ON st.refund_id = p.id
    WHERE p.kind = 'REFUND'
      AND o.seller_id = p_seller_id
      AND (p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) = true)
      AND (p_status_filter IS NULL OR p.status = p_status_filter)
    ORDER BY p.created_at DESC, p.id DESC
    OFFSET p_offset LIMIT p_limit;
END;
$$;

-- 3. Detail view of seller direct refund
CREATE OR REPLACE FUNCTION payment.v2_rf_get_seller_refund_detail(
    p_seller_id uuid,
    p_refund_id uuid
)
RETURNS TABLE (
    id uuid,
    order_id uuid,
    order_code text,
    amount bigint,
    status text,
    reason text,
    created_at timestamptz,
    is_approved boolean,
    has_bank boolean,
    sub_id uuid,
    provider_reference text,
    transferred_at timestamptz,
    sub_status text,
    sub_note text,
    sub_created timestamptz,
    reviewed_by uuid,
    reviewed_at timestamptz
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT p.id,
           p.order_id,
           o.order_code,
           p.amount,
           p.status,
           p.reason,
           p.created_at,
           (p.approved_by IS NOT NULL AND EXISTS(
               SELECT 1 FROM identity.users u WHERE u.id = p.approved_by AND u.role = 'ADMIN' AND u.status = 'ACTIVE'
           )) AS is_approved,
           o.refund_bank_name IS NOT NULL AS has_bank,
           st.id AS sub_id,
           st.provider_reference,
           st.transferred_at,
           st.status AS sub_status,
           st.note AS sub_note,
           st.created_at AS sub_created,
           st.reviewed_by,
           st.reviewed_at
    FROM payment.payments p
    JOIN ordering.orders o ON o.id = p.order_id
    LEFT JOIN payment.seller_refund_transfers st ON st.refund_id = p.id
    WHERE p.id = p_refund_id
      AND p.kind = 'REFUND'
      AND o.seller_id = p_seller_id
      AND (p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) = true);
END;
$$;

-- 4. Locked read of refund and order to reveal customer bank details
CREATE OR REPLACE FUNCTION payment.v2_rf_lock_refund_for_bank_details(
    p_seller_id uuid,
    p_refund_id uuid
)
RETURNS TABLE (
    id uuid,
    order_id uuid,
    status text,
    is_approved boolean,
    refund_bank_name text,
    refund_bank_holder text,
    refund_bank_account_enc text
)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    RETURN QUERY
    SELECT p.id,
           p.order_id,
           p.status,
           (p.approved_by IS NOT NULL AND EXISTS(
               SELECT 1 FROM identity.users u WHERE u.id = p.approved_by AND u.role = 'ADMIN' AND u.status = 'ACTIVE'
           )) AS is_approved,
           o.refund_bank_name,
           o.refund_bank_holder,
           o.refund_bank_account_enc
    FROM payment.payments p
    JOIN ordering.orders o ON o.id = p.order_id
    WHERE p.id = p_refund_id
      AND p.kind = 'REFUND'
      AND o.seller_id = p_seller_id
      AND (p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) = true)
    FOR UPDATE OF p, o;
END;
$$;

-- 5. Eligibility check for private evidence upload
CREATE OR REPLACE FUNCTION payment.v2_rf_check_evidence_upload_eligibility(
    p_seller_id uuid,
    p_refund_id uuid
)
RETURNS TABLE (
    status text,
    is_approved boolean
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT p.status,
           (p.approved_by IS NOT NULL AND EXISTS(
               SELECT 1 FROM identity.users u WHERE u.id = p.approved_by AND u.role = 'ADMIN' AND u.status = 'ACTIVE'
           )) AS is_approved
    FROM payment.payments p
    JOIN ordering.orders o ON o.id = p.order_id
    WHERE p.id = p_refund_id
      AND p.kind = 'REFUND'
      AND o.seller_id = p_seller_id
      AND (p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) = true);
END;
$$;

-- 6. Locked read of refund and order for transfer submission verification
CREATE OR REPLACE FUNCTION payment.v2_rf_lock_refund_for_submit_transfer(
    p_seller_id uuid,
    p_refund_id uuid
)
RETURNS TABLE (
    id uuid,
    order_id uuid,
    amount bigint,
    status text,
    is_approved boolean,
    refund_bank_name text,
    refund_bank_holder text,
    refund_bank_account_enc text
)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    RETURN QUERY
    SELECT p.id,
           p.order_id,
           p.amount,
           p.status,
           (p.approved_by IS NOT NULL AND EXISTS(
               SELECT 1 FROM identity.users u WHERE u.id = p.approved_by AND u.role = 'ADMIN' AND u.status = 'ACTIVE'
           )) AS is_approved,
           o.refund_bank_name,
           o.refund_bank_holder,
           o.refund_bank_account_enc
    FROM payment.payments p
    JOIN ordering.orders o ON o.id = p.order_id
    WHERE p.id = p_refund_id
      AND p.kind = 'REFUND'
      AND o.seller_id = p_seller_id
      AND (p.funding_provenance = 'SELLER_DIRECT' OR coalesce(p.is_direct_receipt, false) = true)
    FOR UPDATE OF p, o;
END;
$$;

-- 7. Lookup attachment ID by file URL
CREATE OR REPLACE FUNCTION payment.v2_rf_get_attachment_id(
    p_url text
)
RETURNS TABLE (
    id uuid
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT a.id
    FROM notify.attachments a
    WHERE a.file_url = p_url;
END;
$$;

-- 8. Admin inspection of seller transfer submission
CREATE OR REPLACE FUNCTION payment.v2_rf_get_admin_seller_submission(
    p_refund_id uuid
)
RETURNS TABLE (
    id uuid,
    refund_id uuid,
    seller_id uuid,
    shop_name text,
    submitted_by uuid,
    submitter_name text,
    amount bigint,
    currency text,
    receiver_bank_name text,
    receiver_bank_holder text,
    provider_reference text,
    transferred_at timestamptz,
    evidence_attachment_id uuid,
    evidence_url text,
    status text,
    note text,
    created_at timestamptz,
    reviewed_by uuid,
    reviewed_at timestamptz
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT st.id,
           st.refund_id,
           st.seller_id,
           s.shop_name,
           st.submitted_by,
           u.full_name AS submitter_name,
           st.amount,
           st.currency,
           st.receiver_bank_name,
           st.receiver_bank_holder,
           st.provider_reference,
           st.transferred_at,
           st.evidence_attachment_id,
           st.evidence_url,
           st.status,
           st.note,
           st.created_at,
           st.reviewed_by,
           st.reviewed_at
    FROM payment.seller_refund_transfers st
    JOIN identity.sellers s ON s.id = st.seller_id
    JOIN identity.users u ON u.id = st.submitted_by
    WHERE st.refund_id = p_refund_id;
END;
$$;

COMMIT;
