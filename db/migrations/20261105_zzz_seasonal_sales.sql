-- 20261105_zzz_seasonal_sales.sql
-- Module boundary typed contracts for Seasonal AI Sales and Forecasting (Ai module)
-- Encapsulates foreign schema reads (identity, catalog, kiosk_ops, ordering, payment, notify)
-- behind typed shared flow functions with SECURITY INVOKER.
-- Fixed canonical MIN paid_at per sale, order-scoped refund isolation, per-function p_actor verification,
-- exact 64-hex SHA-256 photo check, and coalesced non-null booleans.

BEGIN;

-- 1. Check actor & seller tenant scope and active status (coalesced non-null booleans)
CREATE OR REPLACE FUNCTION flow.seasonal_sales_check_scope(
    p_actor uuid,
    p_seller uuid
)
RETURNS TABLE (
    authorized boolean,
    seller_exists boolean,
    is_admin boolean,
    seller_status text
)
LANGUAGE plpgsql
SECURITY INVOKER
STABLE
AS $$
DECLARE
    v_actor_role text;
    v_actor_status text;
    v_actor_seller uuid;
    v_seller_status text;
    v_seller_found boolean := false;
BEGIN
    SELECT u.role, u.status, u.seller_id
    INTO v_actor_role, v_actor_status, v_actor_seller
    FROM identity.users u
    WHERE u.id = p_actor;

    SELECT s.status
    INTO v_seller_status
    FROM identity.sellers s
    WHERE s.id = p_seller;

    v_seller_found := FOUND;

    IF v_actor_status = 'ACTIVE' AND v_seller_found AND v_seller_status = 'ACTIVE' AND
       (v_actor_role = 'ADMIN' OR (v_actor_role = 'SELLER' AND v_actor_seller = p_seller)) THEN
        RETURN QUERY SELECT true, true, (v_actor_role = 'ADMIN'), v_seller_status;
    ELSE
        RETURN QUERY SELECT false, coalesce(v_seller_found, false), coalesce(v_actor_role = 'ADMIN', false), coalesce(v_seller_status, 'NOT_FOUND');
    END IF;
END;
$$;

-- 2. Authoritative seller products with current tags, price, and verified bounded photos
CREATE OR REPLACE FUNCTION flow.seasonal_sales_get_products(
    p_actor uuid,
    p_seller uuid,
    p_active_only boolean DEFAULT false
)
RETURNS TABLE (
    id uuid,
    name text,
    status text,
    price bigint,
    tags text[],
    available_since date,
    photos text[]
)
LANGUAGE plpgsql
SECURITY INVOKER
STABLE
AS $$
BEGIN
    -- Actor scope check: reject unauthorized actors
    IF NOT EXISTS (
        SELECT 1
        FROM identity.users u
        JOIN identity.sellers s ON s.id = p_seller
        WHERE u.id = p_actor
          AND u.status = 'ACTIVE'
          AND s.status = 'ACTIVE'
          AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p_seller))
    ) THEN
        RETURN;
    END IF;

    RETURN QUERY
    SELECT
        pr.id,
        pr.name,
        pr.status,
        pr.price,
        pr.tags,
        (pr.created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS available_since,
        COALESCE(
            (
                SELECT array_agg(sub.file_url)
                FROM (
                    SELECT a.file_url
                    FROM notify.attachments a
                    WHERE a.owner_service = 'catalog'
                      AND a.owner_type = 'flower_product'
                      AND a.owner_id = pr.id
                      AND a.phase = 'PRODUCT'
                      AND a.size_bytes > 0
                      AND a.mime_type IN ('image/jpeg', 'image/png', 'image/webp')
                      AND a.file_url LIKE 'https://%'
                      AND a.sha256 ~ '^[0-9a-fA-F]{64}$'
                    ORDER BY a.created_at DESC, a.id DESC
                    LIMIT 10
                ) sub
            ),
            '{}'::text[]
        ) AS photos
    FROM catalog.flower_products pr
    WHERE pr.seller_id = p_seller
      AND (NOT p_active_only OR pr.status = 'ACTIVE')
    ORDER BY pr.name, pr.id;
END;
$$;

-- 3. Daily aggregated observations for seller products (canonical charge, order-isolated refund, tenant verified)
CREATE OR REPLACE FUNCTION flow.seasonal_sales_get_observations(
    p_actor uuid,
    p_seller uuid,
    p_from date DEFAULT NULL,
    p_to date DEFAULT NULL
)
RETURNS TABLE (
    day date,
    product_id uuid,
    invoice_count int,
    units int
)
LANGUAGE plpgsql
SECURITY INVOKER
STABLE
AS $$
BEGIN
    -- Actor scope check: reject unauthorized actors
    IF NOT EXISTS (
        SELECT 1
        FROM identity.users u
        JOIN identity.sellers s ON s.id = p_seller
        WHERE u.id = p_actor
          AND u.status = 'ACTIVE'
          AND s.status = 'ACTIVE'
          AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p_seller))
    ) THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH canonical_charges AS (
        -- Canonical MIN(paid_at) per checkout sale to prevent order item duplication on multiple charges
        SELECT
            p.checkout_id,
            MIN(p.paid_at) AS paid_at
        FROM payment.payments p
        WHERE p.kind = 'CHARGE'
          AND p.status = 'SUCCEEDED'
          AND p.paid_at IS NOT NULL
        GROUP BY p.checkout_id
    ),
    eligible_orders AS (
        SELECT
            o.id AS order_id,
            o.checkout_id,
            (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS paid_date
        FROM ordering.orders o
        JOIN canonical_charges c ON c.checkout_id = o.checkout_id
        WHERE o.seller_id = p_seller
          AND o.status = 'COMPLETED'
          AND (p_from IS NULL OR (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date >= p_from)
          AND (p_to IS NULL OR (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date <= p_to)
          -- Precise refund exclusion: match specific order, or checkout-wide refund with null order_id
          AND NOT EXISTS (
              SELECT 1
              FROM payment.payments r
              WHERE r.kind = 'REFUND'
                AND r.status = 'SUCCEEDED'
                AND (
                    r.order_id = o.id
                    OR (r.order_id IS NULL AND r.checkout_id = o.checkout_id)
                )
          )
    ),
    order_products AS (
        SELECT
            eo.order_id,
            eo.paid_date,
            b.product_id,
            oi.quantity
        FROM eligible_orders eo
        JOIN ordering.order_items oi ON oi.order_id = eo.order_id
                                    AND oi.item_type = 'BOUQUET'
                                    AND oi.line_status = 'ACTIVE'
        JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
        JOIN catalog.flower_products pr ON pr.id = b.product_id
        WHERE b.seller_id = p_seller
          AND pr.seller_id = p_seller
    )
    SELECT
        op.paid_date AS day,
        op.product_id,
        COUNT(DISTINCT op.order_id)::int AS invoice_count,
        SUM(op.quantity)::int AS units
    FROM order_products op
    GROUP BY op.paid_date, op.product_id
    ORDER BY op.paid_date ASC, op.product_id ASC;
END;
$$;

-- 4. Granular invoice records per order/product for CSV export and reporting (canonical charge, bounded)
CREATE OR REPLACE FUNCTION flow.seasonal_sales_get_invoices(
    p_actor uuid,
    p_seller uuid,
    p_from date DEFAULT NULL,
    p_to date DEFAULT NULL,
    p_limit int DEFAULT 50001
)
RETURNS TABLE (
    order_id uuid,
    product_id uuid,
    product_name text,
    paid_date date,
    quantity int
)
LANGUAGE plpgsql
SECURITY INVOKER
STABLE
AS $$
BEGIN
    -- Actor scope check: reject unauthorized actors
    IF NOT EXISTS (
        SELECT 1
        FROM identity.users u
        JOIN identity.sellers s ON s.id = p_seller
        WHERE u.id = p_actor
          AND u.status = 'ACTIVE'
          AND s.status = 'ACTIVE'
          AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p_seller))
    ) THEN
        RETURN;
    END IF;

    RETURN QUERY
    WITH canonical_charges AS (
        SELECT
            p.checkout_id,
            MIN(p.paid_at) AS paid_at
        FROM payment.payments p
        WHERE p.kind = 'CHARGE'
          AND p.status = 'SUCCEEDED'
          AND p.paid_at IS NOT NULL
        GROUP BY p.checkout_id
    ),
    eligible_orders AS (
        SELECT
            o.id AS order_id,
            o.checkout_id,
            (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS paid_date
        FROM ordering.orders o
        JOIN canonical_charges c ON c.checkout_id = o.checkout_id
        WHERE o.seller_id = p_seller
          AND o.status = 'COMPLETED'
          AND (p_from IS NULL OR (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date >= p_from)
          AND (p_to IS NULL OR (c.paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date <= p_to)
          AND NOT EXISTS (
              SELECT 1
              FROM payment.payments r
              WHERE r.kind = 'REFUND'
                AND r.status = 'SUCCEEDED'
                AND (
                    r.order_id = o.id
                    OR (r.order_id IS NULL AND r.checkout_id = o.checkout_id)
                )
          )
    ),
    order_items_expanded AS (
        SELECT
            eo.order_id,
            eo.paid_date,
            b.product_id,
            COALESCE(pr.name, oi.name_snapshot) AS product_name,
            oi.quantity
        FROM eligible_orders eo
        JOIN ordering.order_items oi ON oi.order_id = eo.order_id
                                    AND oi.item_type = 'BOUQUET'
                                    AND oi.line_status = 'ACTIVE'
        JOIN kiosk_ops.bouquets b ON b.id = oi.bouquet_id
        JOIN catalog.flower_products pr ON pr.id = b.product_id
        WHERE b.seller_id = p_seller
          AND pr.seller_id = p_seller
    )
    SELECT
        oie.order_id,
        oie.product_id,
        oie.product_name,
        oie.paid_date,
        SUM(oie.quantity)::int AS quantity
    FROM order_items_expanded oie
    GROUP BY oie.order_id, oie.product_id, oie.product_name, oie.paid_date
    ORDER BY oie.paid_date DESC, oie.order_id DESC, oie.product_name ASC
    LIMIT p_limit;
END;
$$;

COMMIT;
