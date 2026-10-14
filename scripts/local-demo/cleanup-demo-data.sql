-- =============================================================================
-- FloraBot Local Demo Data Cleanup Script (Bounded Namespace: de000000-*)
-- Scope: Remove ONLY demo records created for the local UI demonstration.
-- Safety & Invariants:
--   - Guards: Aborts immediately if genuine payments, ledger entries, or non-demo orders exist.
--   - Strictly deletes rows matching namespace 'de000000-*'.
--   - Does NOT delete or alter pre-existing user accounts (24 users preserved).
--   - Does NOT delete or alter pre-existing kiosks (K-Q1-01, K-TD-01).
--   - Does NOT hardcode reset identity.sellers (never corrupts unknown/pre-existing baseline).
-- =============================================================================

\set ON_ERROR_STOP on

BEGIN;

-- =============================================================================
-- 1. SAFETY GUARDS: Refuse cleanup if genuine customer activity, orders, payments,
--    or ledger entries reference our demo entities.
-- =============================================================================
DO $$
BEGIN
  -- Guard A: Abort if any payment records reference demo orders or checkouts
  IF EXISTS (
    SELECT 1 FROM payment.payments p
    WHERE p.order_id IN (SELECT id FROM ordering.orders WHERE id::text LIKE 'de000000-%')
       OR p.checkout_id IN (SELECT checkout_id FROM ordering.orders WHERE id::text LIKE 'de000000-%')
  ) THEN
    RAISE EXCEPTION 'Cleanup aborted: payment records exist referencing demo orders or checkouts.' USING ERRCODE = '55000';
  END IF;

  -- Guard B: Abort if any ledger entries reference demo orders
  IF EXISTS (
    SELECT 1 FROM payment.ledger_entries l
    WHERE l.ref_id IN (SELECT id FROM ordering.orders WHERE id::text LIKE 'de000000-%')
  ) THEN
    RAISE EXCEPTION 'Cleanup aborted: ledger entries exist referencing demo orders.' USING ERRCODE = '55000';
  END IF;

  -- Guard C: Abort if any non-demo orders reference our demo bouquets or slots
  IF EXISTS (
    SELECT 1 FROM ordering.orders
    WHERE kiosk_id = 'de000000-0000-0000-0001-000000000001'::uuid
      AND id::text NOT LIKE 'de000000-%'
  ) THEN
    RAISE EXCEPTION 'Cleanup aborted: non-demo customer orders reference the demo kiosk.' USING ERRCODE = '55000';
  END IF;

  IF EXISTS (
    SELECT 1 FROM ordering.order_items oi
    JOIN ordering.orders o ON o.id = oi.order_id
    WHERE o.id::text NOT LIKE 'de000000-%'
      AND (
        oi.bouquet_id IN (SELECT id FROM kiosk_ops.bouquets WHERE id::text LIKE 'de000000-%')
        OR oi.slot_id IN (SELECT id FROM kiosk_ops.slots WHERE id::text LIKE 'de000000-%')
      )
  ) THEN
    RAISE EXCEPTION 'Cleanup aborted: non-demo customer orders reference demo bouquets or slots.' USING ERRCODE = '55000';
  END IF;

  -- Guard D: Abort if any non-demo web requests reference our demo products or kiosk
  IF EXISTS (
    SELECT 1 FROM ordering.web_requests wr
    WHERE wr.id::text NOT LIKE 'de000000-%'
      AND (
        wr.product_id IN (SELECT id FROM catalog.flower_products WHERE id::text LIKE 'de000000-%')
        OR wr.kiosk_id = 'de000000-0000-0000-0001-000000000001'::uuid
      )
  ) THEN
    RAISE EXCEPTION 'Cleanup aborted: non-demo web requests reference demo products or kiosk.' USING ERRCODE = '55000';
  END IF;
END $$;

-- =============================================================================
-- 2. DELETE DEMO RECORDS: Strictly bounded to namespace de000000-*
-- =============================================================================

-- A. Preorders / Web requests in bounded namespace
DELETE FROM ordering.web_requests WHERE id::text LIKE 'de000000-%';

-- B. Order items & Orders in bounded namespace
DELETE FROM ordering.order_items WHERE id::text LIKE 'de000000-%';
DELETE FROM ordering.orders WHERE id::text LIKE 'de000000-%';

-- C. Staff tasks in bounded namespace
DELETE FROM kiosk_ops.staff_tasks WHERE id::text LIKE 'de000000-%';

-- D. Disputes / Incidents in bounded namespace
DELETE FROM ordering.disputes WHERE id::text LIKE 'de000000-%';

-- E. Slot assignments in bounded namespace
DELETE FROM kiosk_ops.slot_assignments WHERE id::text LIKE 'de000000-%';

-- F. Slots in bounded namespace (deleted before bouquets to satisfy FK constraint)
DELETE FROM kiosk_ops.slots WHERE id::text LIKE 'de000000-%';

-- G. Bouquets in bounded namespace
DELETE FROM kiosk_ops.bouquets WHERE id::text LIKE 'de000000-%';

-- H. Product photos / Attachments in bounded namespace
DELETE FROM notify.attachments WHERE id::text LIKE 'de000000-%';

-- I. Flower products in bounded namespace
DELETE FROM catalog.flower_products WHERE id::text LIKE 'de000000-%';

-- J. Manager-Kiosk mappings for our demo kiosk only
DELETE FROM kiosk_ops.manager_kiosks WHERE kiosk_id = 'de000000-0000-0000-0001-000000000001'::uuid;

-- K. Simulated Kiosk in bounded namespace
DELETE FROM kiosk_ops.kiosks WHERE id = 'de000000-0000-0000-0001-000000000001'::uuid;

-- L. Subscriptions in bounded namespace
DELETE FROM identity.subscriptions WHERE id::text LIKE 'de000000-%';

-- NOTE: As mandated by coordinator review, existing seller entitlement in identity.sellers
-- is NEVER automatically modified or reset by cleanup default to prevent corrupting pre-existing state.

COMMIT;
