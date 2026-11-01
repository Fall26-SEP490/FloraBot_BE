-- Read-only verification of one account and its latest application for one kiosk.
-- Usage: psql ... -v email='user@example.com' -v kiosk_code='K-EXAMPLE-01' -f this-file.sql
-- Optional overrides: -v expected_slots=2 -v expected_expiry=2027-11-01
-- This script does not rerun handover, payment callbacks, jobs or device commands.
\set ON_ERROR_STOP on
\pset pager off
\x on

\if :{?email}
\else
  \set email 'nguyenthanhloi150@gmail.com'
\endif
\if :{?kiosk_code}
\else
  \set kiosk_code 'K-TEST-LOI-01'
\endif
\if :{?expected_slots}
\else
  \set expected_slots 2
\endif
\if :{?expected_expiry}
\else
  \set expected_expiry '2027-11-01'
\endif

BEGIN READ ONLY;

WITH target AS (
    SELECT :'email'::text AS email,
           :'kiosk_code'::text AS kiosk_code,
           :'expected_slots'::integer AS expected_slots,
           :'expected_expiry'::date AS expected_expiry
)
SELECT
    CURRENT_TIMESTAMP AS checked_at,
    u.id IS NOT NULL AS account_found,
    app.id IS NOT NULL AS application_found,
    u.email AS account_email,
    u.role AS account_role,
    u.status AS account_status,
    s.id AS seller_id,
    s.status AS seller_status,
    app.id AS application_id,
    app.status AS application_status,
    p.status AS payment_status,
    p.amount AS payment_amount,
    app.annual_fee AS application_fee,
    tech.full_name AS assigned_technician_name,
    tech.role AS assigned_technician_role,
    tech.status AS assigned_technician_status,
    k.id AS kiosk_id,
    k.code AS kiosk_code,
    k.status AS kiosk_status,
    app.total_slots AS package_slot_count,
    slots.total_count AS total_slot_count,
    slots.seller_count AS seller_slot_count,
    assignments.assigned_count AS active_assignment_count,
    lease.id AS current_lease_id,
    lease.status AS current_lease_status,
    lease.period_from AS lease_period_from,
    lease.period_to AS lease_period_to,
    lease.period_from_ts AS lease_period_from_ts,
    lease.period_to_ts AS lease_period_to_ts,
    app.handover_at,
    COALESCE(app.status = 'ACTIVE', false) AS application_active_ok,
    COALESCE(p.status = 'SUCCEEDED' AND p.amount = app.annual_fee, false) AS payment_ok,
    COALESCE(u.role = 'SELLER' AND u.status = 'ACTIVE' AND s.status = 'ACTIVE', false) AS seller_active_ok,
    COALESCE(s.id IS NOT NULL AND u.seller_id = s.id AND k.seller_id = s.id, false) AS kiosk_seller_matches,
    COALESCE(tech.role = 'TECHNICIAN' AND tech.status = 'ACTIVE', false) AS technician_ok,
    COALESCE(slots.total_count = target.expected_slots
             AND slots.total_count = app.total_slots
             AND slots.seller_count = slots.total_count
             AND assignments.assigned_count = slots.total_count, false) AS slot_assignment_ok,
    COALESCE(lease.period_to = target.expected_expiry, false) AS expected_lease_expiry_ok,
    COALESCE(lease.period_to_ts = app.handover_at + interval '1 year', false) AS one_year_from_handover_ok
FROM target
LEFT JOIN identity.users u ON lower(u.email) = lower(target.email)
LEFT JOIN LATERAL (
    SELECT a.id, a.status, a.seller_id, a.payment_id, a.assigned_technician_id,
           a.assigned_kiosk_id, a.total_slots, a.annual_fee, a.handover_at
    FROM kiosk_ops.kiosk_applications a
    JOIN kiosk_ops.kiosks selected_kiosk ON selected_kiosk.id = a.assigned_kiosk_id
    WHERE a.customer_id = u.id AND selected_kiosk.code = target.kiosk_code
    ORDER BY a.created_at DESC, a.id DESC
    LIMIT 1
) app ON true
LEFT JOIN payment.payments p ON p.id = app.payment_id
LEFT JOIN identity.users tech ON tech.id = app.assigned_technician_id
LEFT JOIN identity.sellers s ON s.id = app.seller_id
LEFT JOIN kiosk_ops.kiosks k ON k.id = app.assigned_kiosk_id
LEFT JOIN LATERAL (
    SELECT count(*) AS total_count,
           count(*) FILTER (WHERE sl.current_seller_id = s.id) AS seller_count
    FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id = k.id
) slots ON true
LEFT JOIN LATERAL (
    SELECT count(DISTINCT sa.slot_id) AS assigned_count
    FROM kiosk_ops.slot_assignments sa
    JOIN kiosk_ops.slots sl ON sl.id = sa.slot_id
    WHERE sl.kiosk_id = k.id AND sa.seller_id = s.id AND sa.status = 'ACTIVE'
) assignments ON true
LEFT JOIN LATERAL (
    SELECT sub.id, sub.status, sub.period_from, sub.period_to,
           sub.period_from_ts, sub.period_to_ts
    FROM identity.subscriptions sub
    WHERE sub.seller_id = s.id AND sub.kiosk_id = k.id AND sub.status = 'ACTIVE'
      AND ((sub.period_from_ts IS NOT NULL AND sub.period_to_ts IS NOT NULL
            AND sub.period_from_ts <= CURRENT_TIMESTAMP AND CURRENT_TIMESTAMP < sub.period_to_ts)
           OR (sub.period_from_ts IS NULL AND sub.period_to_ts IS NULL
               AND sub.period_from <= CURRENT_DATE AND CURRENT_DATE < sub.period_to))
    ORDER BY sub.period_to DESC, sub.created_at DESC, sub.id DESC
    LIMIT 1
) lease ON true;

COMMIT;
