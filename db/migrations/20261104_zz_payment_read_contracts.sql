-- 20261104_zz_payment_read_contracts.sql
-- Migration: Payment V2 Read Contracts & Foreign Schema Boundary Encapsulation
-- Encapsulates foreign-schema SQL reads (identity, ordering, kiosk_ops) behind typed contract functions in payment schema.

BEGIN;

-- =============================================================================
-- NAMESPACE 1: Channels (payment.v2_cp_*)
-- =============================================================================

-- 1. Active actor lookup for channel operations
CREATE OR REPLACE FUNCTION payment.v2_cp_get_actor(p_user_id uuid)
RETURNS TABLE(
    id uuid,
    role text,
    status text,
    seller_id uuid
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT u.id, u.role, u.status, u.seller_id
    FROM identity.users u
    WHERE u.id = p_user_id;
END;
$$;

-- 2. Seller existence verification
CREATE OR REPLACE FUNCTION payment.v2_cp_get_seller(p_seller_id uuid)
RETURNS TABLE(id uuid)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id
    FROM identity.sellers s
    WHERE s.id = p_seller_id;
END;
$$;

-- 3. Seller row lock for channel version binding
CREATE OR REPLACE FUNCTION payment.v2_cp_lock_seller(p_seller_id uuid)
RETURNS TABLE(id uuid, status text)
LANGUAGE plpgsql
VOLATILE
AS $$
BEGIN
    RETURN QUERY
    SELECT s.id, s.status
    FROM identity.sellers s
    WHERE s.id = p_seller_id
    FOR UPDATE;
END;
$$;

-- 4. Admin channel list with seller shop name join
CREATE OR REPLACE FUNCTION payment.v2_cp_list_admin_channels()
RETURNS TABLE(
    seller_id uuid,
    shop_name text,
    version integer,
    state text,
    expected_bank_bin text,
    expected_bank_account text,
    expected_bank_name text,
    verified_at timestamptz,
    updated_at timestamptz
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT DISTINCT ON (v.seller_id)
        v.seller_id,
        s.shop_name,
        v.version,
        v.state,
        v.expected_bank_bin,
        v.expected_bank_account,
        v.expected_bank_name,
        v.verified_at,
        v.updated_at
    FROM payment.payment_channel_versions v
    JOIN identity.sellers s ON s.id = v.seller_id
    ORDER BY v.seller_id, v.version DESC;
END;
$$;

-- =============================================================================
-- NAMESPACE 2: Seller Payments (payment.v2_sp_*)
-- =============================================================================

-- 1. Active actor lookup for seller payment operations
CREATE OR REPLACE FUNCTION payment.v2_sp_get_actor(p_user_id uuid)
RETURNS TABLE(
    id uuid,
    role text,
    status text,
    seller_id uuid
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT u.id, u.role, u.status, u.seller_id
    FROM identity.users u
    WHERE u.id = p_user_id;
END;
$$;

-- 2. Checkout orders lookup for payment details
CREATE OR REPLACE FUNCTION payment.v2_sp_get_checkout_orders(p_checkout_id uuid)
RETURNS TABLE(
    id uuid,
    order_code text,
    status text,
    total_amount bigint,
    tracking_token text,
    points_redeemed bigint,
    discount_amount bigint
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT o.id, o.order_code, o.status, o.total_amount, o.tracking_token, o.points_redeemed, o.discount_amount
    FROM ordering.orders o
    WHERE o.checkout_id = p_checkout_id;
END;
$$;

-- 3. Sales report summary join with orders
CREATE OR REPLACE FUNCTION payment.v2_sp_get_sales_summary(
    p_seller_id uuid,
    p_from timestamptz DEFAULT NULL,
    p_to timestamptz DEFAULT NULL,
    p_kiosk_id uuid DEFAULT NULL
)
RETURNS TABLE(
    gross_paid bigint,
    total_refunded bigint,
    paid_orders bigint,
    unclaimed_orders bigint,
    pending_amount bigint
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT
        COALESCE(SUM(CASE WHEN p.status = 'SUCCEEDED' AND p.kind = 'CHARGE' THEN p.amount ELSE 0 END), 0)::bigint AS gross_paid,
        COALESCE(SUM(CASE WHEN p.status = 'SUCCEEDED' AND p.kind = 'REFUND' THEN p.amount ELSE 0 END), 0)::bigint AS total_refunded,
        COUNT(DISTINCT CASE WHEN p.status = 'SUCCEEDED' AND p.kind = 'CHARGE' THEN o.id END)::bigint AS paid_orders,
        COUNT(DISTINCT CASE WHEN o.status IN ('PAID', 'DISPENSING', 'AWAITING_PAYMENT') THEN o.id END)::bigint AS unclaimed_orders,
        COALESCE(SUM(CASE WHEN p.status = 'PENDING' AND p.kind = 'CHARGE' THEN p.amount ELSE 0 END), 0)::bigint AS pending_amount
    FROM payment.payments p
    JOIN ordering.orders o ON o.checkout_id = p.checkout_id
    WHERE p.seller_id = p_seller_id AND p.is_direct_receipt = true
      AND (p_from IS NULL OR p.created_at >= p_from)
      AND (p_to IS NULL OR p.created_at <= p_to)
      AND (p_kiosk_id IS NULL OR o.kiosk_id = p_kiosk_id);
END;
$$;

-- 4. Sales report kiosk breakdown join with kiosks and orders
CREATE OR REPLACE FUNCTION payment.v2_sp_get_sales_by_kiosk(
    p_seller_id uuid,
    p_from timestamptz DEFAULT NULL,
    p_to timestamptz DEFAULT NULL,
    p_kiosk_id uuid DEFAULT NULL
)
RETURNS TABLE(
    id uuid,
    code text,
    name text,
    gross_paid bigint,
    order_count bigint
)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT k.id, k.code, k.name,
           COALESCE(SUM(CASE WHEN p.status = 'SUCCEEDED' AND p.kind = 'CHARGE' THEN p.amount ELSE 0 END), 0)::bigint AS gross_paid,
           COUNT(DISTINCT o.id)::bigint AS order_count
    FROM kiosk_ops.kiosks k
    JOIN ordering.orders o ON o.kiosk_id = k.id
    JOIN payment.payments p ON p.checkout_id = o.checkout_id AND p.is_direct_receipt = true AND p.seller_id = p_seller_id
    WHERE (p_from IS NULL OR p.created_at >= p_from)
      AND (p_to IS NULL OR p.created_at <= p_to)
      AND (p_kiosk_id IS NULL OR k.id = p_kiosk_id)
    GROUP BY k.id, k.code, k.name
    ORDER BY gross_paid DESC, k.name;
END;
$$;

-- =============================================================================
-- NAMESPACE 3: Payment Links (payment.v2_link_*)
-- =============================================================================

-- 1. Distinct seller lookup for checkout orders
CREATE OR REPLACE FUNCTION payment.v2_link_get_checkout_sellers(p_checkout_id uuid)
RETURNS TABLE(seller_id uuid)
LANGUAGE plpgsql
STABLE
AS $$
BEGIN
    RETURN QUERY
    SELECT DISTINCT o.seller_id
    FROM ordering.orders o
    WHERE o.checkout_id = p_checkout_id;
END;
$$;

COMMIT;
