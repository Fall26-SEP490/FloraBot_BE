-- =============================================================================
-- FloraBot Local Demo Data Read-Only Verification Queries
-- Usage: docker exec -i florabot_demo_20261012-postgres-1 psql -U florabot -d florabot -f scripts/local-demo/verify-demo-data.sql
-- All queries are non-destructive and read-only.
-- =============================================================================

\echo '=== 1. PRESERVED USER ACCOUNTS INTEGRITY ==='
SELECT role, status, count(*) AS count
FROM identity.users
GROUP BY role, status
ORDER BY role;

\echo '=== 1b. OPTIONAL TEAM DEMO ACCOUNTS (df000000-*) ==='
SELECT id, email, role, status, full_name, (seller_id IS NOT NULL) AS has_seller, (password_hash IS NOT NULL) AS has_password
FROM identity.users
WHERE id::text LIKE 'df000000-%'
ORDER BY id;

\echo '=== 2. DEMO KIOSK (SIMULATED - NO HARDWARE) ==='
SELECT id, code, name, status, hardware_id, (last_heartbeat_at IS NULL) AS truthful_no_heartbeat, (api_key_hash IS NOT NULL) AS has_api_key_hash
FROM kiosk_ops.kiosks
WHERE id = 'de000000-0000-0000-0001-000000000001';

\echo '=== 3. OPERATIONS MANAGER MAPPING ==='
SELECT m.manager_id, u.email AS manager_email, u.role, m.kiosk_id, k.code AS kiosk_code
FROM kiosk_ops.manager_kiosks m
JOIN identity.users u ON u.id = m.manager_id
JOIN kiosk_ops.kiosks k ON k.id = m.kiosk_id
WHERE m.kiosk_id = 'de000000-0000-0000-0001-000000000001';

\echo '=== 4. DEMO ARTIFICIAL PRODUCTS & DIMENSIONS ==='
SELECT id, name, price, status, inventory_kind, length_cm, width_cm, height_cm
FROM catalog.flower_products
WHERE id::text LIKE 'de000000-%'
ORDER BY status, name;

\echo '=== 5. DEMO PRODUCT PHOTOS (VERIFIED MEDIA MANIFEST) ==='
SELECT a.id, p.name AS product_name, a.file_url, a.size_bytes, a.mime_type, substring(a.sha256 from 1 for 16) || '...' AS sha256_prefix
FROM notify.attachments a
JOIN catalog.flower_products p ON p.id = a.owner_id
WHERE a.id::text LIKE 'de000000-%';

\echo '=== 6. DEMO BOUQUETS & NON-EXPIRY TRIGGER ==='
SELECT b.id, p.name, b.price_snapshot, b.status, (b.sellable_until = 'infinity'::timestamptz) AS is_infinite_shelf_life
FROM kiosk_ops.bouquets b
JOIN catalog.flower_products p ON p.id = b.product_id
WHERE b.id::text LIKE 'de000000-%';

\echo '=== 7. ONE SELLER WHOLE KIOSK SLOTS (WALK_IN & PREORDER ZONES) ==='
SELECT s.slot_code,
       CASE WHEN s.slot_code LIKE 'W%' THEN 'WALK_IN' ELSE 'PREORDER' END AS zone,
       s.status,
       sel.shop_name AS leased_by,
       s.relay_channel,
       b.id AS bouquet_id
FROM kiosk_ops.slots s
LEFT JOIN identity.sellers sel ON sel.id = s.current_seller_id
LEFT JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id
WHERE s.kiosk_id = 'de000000-0000-0000-0001-000000000001'
ORDER BY s.slot_code;

\echo '=== 8. SLOT ASSIGNMENTS (ALL 8 SLOTS ASSIGNED TO SAME SELLER) ==='
SELECT a.id, s.slot_code, sel.shop_name, a.status, a.assigned_at
FROM kiosk_ops.slot_assignments a
JOIN kiosk_ops.slots s ON s.id = a.slot_id
JOIN identity.sellers sel ON sel.id = a.seller_id
WHERE a.id::text LIKE 'de000000-%'
ORDER BY s.slot_code;

\echo '=== 9. HARDWARE INCIDENTS & DISPUTES ==='
SELECT d.id, d.kind, d.status, d.reason, d.decision, s.slot_code
FROM ordering.disputes d
LEFT JOIN kiosk_ops.slots s ON s.id = d.slot_id
WHERE d.id::text LIKE 'de000000-%'
ORDER BY d.created_at;

\echo '=== 10. STAFF TASKS (TECHNICIAN INCIDENTS & SELLER STAFF DELIVERIES) ==='
SELECT t.id, t.kind, t.status, u.role AS assignee_role, u.full_name AS assignee_name, t.instructions, t.report
FROM kiosk_ops.staff_tasks t
JOIN identity.users u ON u.id = t.assignee_id
WHERE t.id::text LIKE 'de000000-%'
ORDER BY t.created_at;

\echo '=== 11. PREORDERS (WEB REQUESTS) ==='
SELECT w.id, w.kind, w.state, p.name AS product_name, w.quoted_price, w.instructions
FROM ordering.web_requests w
JOIN catalog.flower_products p ON p.id = w.product_id
WHERE w.id::text LIKE 'de000000-%'
ORDER BY w.created_at;

\echo '=== 12. REPRESENTATIVE ORDER (TRUTHFUL AWAITING PAYMENT) ==='
SELECT o.id, o.order_code, o.status, o.total_amount, o.tracking_token, (o.completed_at IS NULL) AS uncompleted_truthful
FROM ordering.orders o
WHERE o.id::text LIKE 'de000000-%';

\echo '=== 13. SCREEN READ VIEWS READINESS ==='
SELECT 'Admin Queue' AS view_name, count(*) AS matching_rows FROM screen.v_admin_queue
UNION ALL SELECT 'Kiosk Catalog', count(*) FROM screen.v_kiosk_catalog WHERE kiosk_id = 'de000000-0000-0000-0001-000000000001'
UNION ALL SELECT 'Web Catalog', count(*) FROM screen.v_web_catalog WHERE kiosk_id = 'de000000-0000-0000-0001-000000000001';
