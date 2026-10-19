-- Migration 028: make every slot inherit the owner of its kiosk.
-- Repairs legacy kiosks that were assigned one slot at a time. Existing bouquets
-- and occupied slot states are preserved; only ownership references are synced.
BEGIN;

DO $$
DECLARE
  owned_kiosk record;
  v_assigned_by uuid;
  v_max_slots integer;
  v_active_slots integer;
  v_new_assignments integer;
  v_fallback_admin uuid;
BEGIN
  SELECT id INTO v_fallback_admin
  FROM identity.users
  WHERE role = 'ADMIN' AND status = 'ACTIVE'
  ORDER BY id
  LIMIT 1;

  FOR owned_kiosk IN
    SELECT id, seller_id
    FROM kiosk_ops.kiosks
    WHERE seller_id IS NOT NULL
    ORDER BY id
  LOOP
    -- Refuse ambiguous legacy ownership instead of silently transferring stock.
    IF EXISTS (
      SELECT 1
      FROM kiosk_ops.slots s
      WHERE s.kiosk_id = owned_kiosk.id
        AND s.current_seller_id IS NOT NULL
        AND s.current_seller_id IS DISTINCT FROM owned_kiosk.seller_id
    ) OR EXISTS (
      SELECT 1
      FROM kiosk_ops.slots s
      JOIN kiosk_ops.slot_assignments a ON a.slot_id = s.id
      WHERE s.kiosk_id = owned_kiosk.id
        AND a.status = 'ACTIVE'
        AND a.seller_id IS DISTINCT FROM owned_kiosk.seller_id
    ) OR EXISTS (
      SELECT 1
      FROM kiosk_ops.slots s
      JOIN kiosk_ops.bouquets b ON b.id = s.bouquet_id
      WHERE s.kiosk_id = owned_kiosk.id
        AND b.seller_id IS DISTINCT FROM owned_kiosk.seller_id
    ) THEN
      RAISE EXCEPTION 'Kiosk % có ô hoặc bó hoa đang thuộc seller khác; cần xử lý dữ liệu đó trước khi đồng bộ', owned_kiosk.id;
    END IF;

    -- Keep the original admin attribution where assignment history exists.
    -- For a legacy kiosk with no slot history, use an active admin as the migration actor.
    SELECT a.assigned_by INTO v_assigned_by
    FROM kiosk_ops.slots s
    JOIN kiosk_ops.slot_assignments a ON a.slot_id = s.id
    WHERE s.kiosk_id = owned_kiosk.id
      AND a.seller_id = owned_kiosk.seller_id
    ORDER BY (a.status = 'ACTIVE') DESC, a.assigned_at, a.id
    LIMIT 1;
    v_assigned_by := coalesce(v_assigned_by, v_fallback_admin);
    IF v_assigned_by IS NULL THEN
      RAISE EXCEPTION 'Không thể đồng bộ ô của kiosk %: không có tài khoản admin để ghi nhận', owned_kiosk.id;
    END IF;

    SELECT p.max_slots INTO v_max_slots
    FROM identity.sellers seller
    JOIN identity.subscription_packages p ON p.id = seller.package_id
    WHERE seller.id = owned_kiosk.seller_id;
    IF v_max_slots IS NULL THEN
      RAISE EXCEPTION 'Seller % sở hữu kiosk % nhưng chưa có gói quy định số ô', owned_kiosk.seller_id, owned_kiosk.id;
    END IF;

    SELECT count(*) INTO v_active_slots
    FROM kiosk_ops.slot_assignments
    WHERE seller_id = owned_kiosk.seller_id AND status = 'ACTIVE';

    SELECT count(*) INTO v_new_assignments
    FROM kiosk_ops.slots s
    WHERE s.kiosk_id = owned_kiosk.id
      AND NOT EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        WHERE a.slot_id = s.id AND a.status = 'ACTIVE'
      );

    -- Keep the subscription's slot quota intact while backfilling missing history.
    IF v_new_assignments > 0 AND v_active_slots + v_new_assignments > v_max_slots THEN
      RAISE EXCEPTION 'Kiosk % cần đồng bộ thêm % ô nhưng seller đã dùng %/% ô theo gói',
        owned_kiosk.id, v_new_assignments, v_active_slots, v_max_slots;
    END IF;

    INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at, status)
    SELECT s.id, owned_kiosk.seller_id, v_assigned_by, public.app_now(), 'ACTIVE'
    FROM kiosk_ops.slots s
    WHERE s.kiosk_id = owned_kiosk.id
      AND NOT EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        WHERE a.slot_id = s.id AND a.status = 'ACTIVE'
      );

    -- A01 (or any stocked slot) keeps its bouquet/status. FREE slots become
    -- RENTED_EMPTY once they are managed by the kiosk's seller.
    UPDATE kiosk_ops.slots s
    SET current_seller_id = owned_kiosk.seller_id,
        status = CASE WHEN s.status = 'FREE' THEN 'RENTED_EMPTY' ELSE s.status END,
        row_version = s.row_version + 1,
        updated_at = public.app_now()
    WHERE s.kiosk_id = owned_kiosk.id
      AND (s.current_seller_id IS DISTINCT FROM owned_kiosk.seller_id OR s.status = 'FREE')
      AND EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        WHERE a.slot_id = s.id
          AND a.seller_id = owned_kiosk.seller_id
          AND a.status = 'ACTIVE'
      );
  END LOOP;
END $$;

COMMIT;
