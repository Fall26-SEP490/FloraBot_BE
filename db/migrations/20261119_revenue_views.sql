BEGIN;

-- Legacy GMV counts an order once, on its first successful allocated charge.
-- Actual cash and refunds are reported separately by /api/admin/revenue/flowers.
CREATE OR REPLACE VIEW screen.v_revenue_daily AS
WITH paid_orders AS (
  SELECT order_id,kiosk_id,min(coalesce(paid_at,created_at)) AS first_paid_at
  FROM payment.v_order_payment_allocations WHERE kind='CHARGE' AND payment_status='SUCCEEDED'
  GROUP BY order_id,kiosk_id
)
SELECT (p.first_paid_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS ngay,k.code AS kiosk,
       count(DISTINCT o.id) AS so_don,sum(o.total_amount) AS gmv,sum(o.discount_amount) AS giam_do_diem
FROM paid_orders p JOIN ordering.orders o ON o.id=p.order_id JOIN kiosk_ops.kiosks k ON k.id=p.kiosk_id
GROUP BY 1,2;

CREATE OR REPLACE VIEW screen.v_platform_revenue AS
SELECT date_trunc('month',created_at AT TIME ZONE 'Asia/Ho_Chi_Minh')::date AS thang,
       -sum(amount) AS doanh_thu_phi_goi
FROM payment.ledger_entries WHERE account='PLATFORM_SUBSCRIPTION' GROUP BY 1;

COMMIT;
