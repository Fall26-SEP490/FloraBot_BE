BEGIN;

-- Project each payment onto its associated order(s) once. A direct receipt
-- belongs only to its seller. Order-specific refunds use their explicit order.
CREATE OR REPLACE VIEW payment.v_order_payment_allocations AS
SELECT p.id AS payment_id, line.order_id, line.seller_id, line.kiosk_id, line.order_status,
       p.kind, p.status AS payment_status, p.is_direct_receipt, p.created_at, p.paid_at,
       (round(p.amount::numeric * line.running_weight / line.total_weight)
        - round(p.amount::numeric * (line.running_weight - line.weight) / line.total_weight))::bigint AS amount
FROM payment.payments p
CROSS JOIN LATERAL (
    SELECT matched.*,
           sum(weight) OVER (ORDER BY order_id) AS running_weight,
           sum(weight) OVER () AS total_weight
    FROM (
        SELECT o.id AS order_id, o.seller_id, o.kiosk_id, o.status AS order_status,
               CASE WHEN sum(o.total_amount) OVER () > 0 THEN o.total_amount ELSE 1 END AS weight
        FROM ordering.orders o
        WHERE ((p.order_id IS NOT NULL AND o.id=p.order_id)
            OR (p.order_id IS NULL AND o.checkout_id=p.checkout_id))
          AND (p.seller_id IS NULL OR o.seller_id=p.seller_id)
    ) matched
) line
WHERE p.purpose='ORDER_CHECKOUT' AND p.kind IN ('CHARGE','REFUND');

CREATE OR REPLACE FUNCTION payment.v2_sp_get_sales_summary(
    p_seller_id uuid, p_from timestamptz DEFAULT NULL, p_to timestamptz DEFAULT NULL,
    p_kiosk_id uuid DEFAULT NULL
)
RETURNS TABLE(gross_paid bigint, total_refunded bigint, paid_orders bigint, unclaimed_orders bigint, pending_amount bigint)
LANGUAGE sql STABLE AS $$
    SELECT coalesce(sum(amount) FILTER (WHERE payment_status='SUCCEEDED' AND kind='CHARGE'),0)::bigint,
           coalesce(sum(amount) FILTER (WHERE payment_status='SUCCEEDED' AND kind='REFUND'),0)::bigint,
           count(DISTINCT order_id) FILTER (WHERE payment_status='SUCCEEDED' AND kind='CHARGE'),
           count(DISTINCT order_id) FILTER (WHERE order_status IN ('PAID','DISPENSING','AWAITING_PAYMENT')),
           coalesce(sum(amount) FILTER (WHERE payment_status='PENDING' AND kind='CHARGE'),0)::bigint
    FROM payment.v_order_payment_allocations a
    WHERE a.seller_id=p_seller_id
      AND (p_from IS NULL OR coalesce(a.paid_at,a.created_at)>=p_from)
      AND (p_to IS NULL OR coalesce(a.paid_at,a.created_at)<=p_to)
      AND (p_kiosk_id IS NULL OR a.kiosk_id=p_kiosk_id)
$$;

CREATE OR REPLACE FUNCTION payment.v2_sp_get_sales_by_kiosk(
    p_seller_id uuid, p_from timestamptz DEFAULT NULL, p_to timestamptz DEFAULT NULL,
    p_kiosk_id uuid DEFAULT NULL
)
RETURNS TABLE(id uuid, code text, name text, gross_paid bigint, order_count bigint)
LANGUAGE sql STABLE AS $$
    SELECT k.id,k.code,k.name,
           sum(a.amount)::bigint,count(DISTINCT a.order_id)
    FROM payment.v_order_payment_allocations a
    JOIN kiosk_ops.kiosks k ON k.id=a.kiosk_id
    WHERE a.seller_id=p_seller_id AND a.payment_status='SUCCEEDED' AND a.kind='CHARGE'
      AND (p_from IS NULL OR coalesce(a.paid_at,a.created_at)>=p_from)
      AND (p_to IS NULL OR coalesce(a.paid_at,a.created_at)<=p_to)
      AND (p_kiosk_id IS NULL OR a.kiosk_id=p_kiosk_id)
    GROUP BY k.id,k.code,k.name
    ORDER BY sum(a.amount) DESC,k.name,k.id
$$;

COMMIT;
