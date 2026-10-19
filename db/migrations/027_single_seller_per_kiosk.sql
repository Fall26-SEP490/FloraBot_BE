-- Migration 027: exclusive kiosk ownership and whole-kiosk admin assignment.
-- Existing mixed-owner kiosks are intentionally left unowned until they can be safely reassigned.
BEGIN;

ALTER TABLE kiosk_ops.kiosks
  ADD COLUMN IF NOT EXISTS seller_id uuid;

CREATE INDEX IF NOT EXISTS kiosks_seller_id_idx ON kiosk_ops.kiosks (seller_id) WHERE seller_id IS NOT NULL;

-- Backfill only when all current slot ownership points to the same seller.
-- A kiosk with mixed legacy ownership stays NULL; no seller is selected arbitrarily.
WITH owner_refs AS (
  SELECT s.kiosk_id, s.current_seller_id AS seller_id
  FROM kiosk_ops.slots s
  WHERE s.current_seller_id IS NOT NULL
  UNION
  SELECT s.kiosk_id, a.seller_id
  FROM kiosk_ops.slots s
  JOIN kiosk_ops.slot_assignments a ON a.slot_id = s.id AND a.status = 'ACTIVE'
), one_owner AS (
  SELECT kiosk_id, min(seller_id::text)::uuid AS seller_id
  FROM owner_refs
  GROUP BY kiosk_id
  HAVING count(DISTINCT seller_id) = 1
)
UPDATE kiosk_ops.kiosks k
SET seller_id = o.seller_id
FROM one_owner o
WHERE k.id = o.kiosk_id AND k.seller_id IS NULL;

CREATE OR REPLACE FUNCTION flow.guard_slot_assignment_kiosk_owner()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE v_owner uuid; v_kiosk uuid;
BEGIN
  IF NEW.status <> 'ACTIVE' THEN RETURN NEW; END IF;

  SELECT k.seller_id, k.id INTO v_owner, v_kiosk
  FROM kiosk_ops.slots s
  JOIN kiosk_ops.kiosks k ON k.id = s.kiosk_id
  WHERE s.id = NEW.slot_id;

  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không tìm thấy kiosk của ô được gán';
  END IF;
  -- Older 026 handover functions assign slots before promoting the application.
  -- Permit that short-lived state only inside a verified handover transaction;
  -- the completion trigger below records the seller as kiosk owner before commit.
  IF v_owner IS NULL AND EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications a
    WHERE a.assigned_kiosk_id = v_kiosk AND a.status = 'AWAITING_HANDOVER'
  ) THEN
    RETURN NEW;
  END IF;
  IF v_owner IS DISTINCT FROM NEW.seller_id THEN
    RAISE EXCEPTION 'Seller của slot assignment phải là seller sở hữu kiosk';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS slot_assignment_kiosk_owner_insert ON kiosk_ops.slot_assignments;
CREATE TRIGGER slot_assignment_kiosk_owner_insert
BEFORE INSERT ON kiosk_ops.slot_assignments
FOR EACH ROW EXECUTE FUNCTION flow.guard_slot_assignment_kiosk_owner();

DROP TRIGGER IF EXISTS slot_assignment_kiosk_owner_update ON kiosk_ops.slot_assignments;
CREATE TRIGGER slot_assignment_kiosk_owner_update
BEFORE UPDATE OF slot_id, seller_id, status ON kiosk_ops.slot_assignments
FOR EACH ROW EXECUTE FUNCTION flow.guard_slot_assignment_kiosk_owner();

CREATE OR REPLACE FUNCTION flow.guard_kiosk_owner_update()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.seller_id IS DISTINCT FROM OLD.seller_id AND EXISTS (
    SELECT 1
    FROM kiosk_ops.slot_assignments a
    JOIN kiosk_ops.slots s ON s.id = a.slot_id
    WHERE s.kiosk_id = NEW.id AND a.status = 'ACTIVE'
      AND a.seller_id IS DISTINCT FROM NEW.seller_id
  ) THEN
    RAISE EXCEPTION 'Không thể đổi chủ kiosk khi còn ô đang gán cho seller khác';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS kiosks_owner_update ON kiosk_ops.kiosks;
CREATE TRIGGER kiosks_owner_update
BEFORE UPDATE OF seller_id ON kiosk_ops.kiosks
FOR EACH ROW EXECUTE FUNCTION flow.guard_kiosk_owner_update();

CREATE OR REPLACE FUNCTION flow.sync_kiosk_owner_after_handover()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status = 'ACTIVE' AND NEW.assigned_kiosk_id IS NOT NULL AND NEW.seller_id IS NOT NULL
     AND (OLD.status IS DISTINCT FROM NEW.status OR OLD.assigned_kiosk_id IS DISTINCT FROM NEW.assigned_kiosk_id
          OR OLD.seller_id IS DISTINCT FROM NEW.seller_id) THEN
    UPDATE kiosk_ops.kiosks
    SET seller_id = NEW.seller_id, updated_at = public.app_now()
    WHERE id = NEW.assigned_kiosk_id AND (seller_id IS NULL OR seller_id = NEW.seller_id);
    IF NOT FOUND THEN
      RAISE EXCEPTION 'Kiosk đã thuộc seller khác, không thể hoàn tất bàn giao';
    END IF;
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS kiosk_application_sync_owner ON kiosk_ops.kiosk_applications;
CREATE TRIGGER kiosk_application_sync_owner
AFTER UPDATE OF status, assigned_kiosk_id, seller_id ON kiosk_ops.kiosk_applications
FOR EACH ROW EXECUTE FUNCTION flow.sync_kiosk_owner_after_handover();

CREATE OR REPLACE FUNCTION flow.guard_kiosk_application_reservation()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
  IF NEW.status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER') AND NEW.assigned_kiosk_id IS NOT NULL
     AND EXISTS (
       SELECT 1 FROM kiosk_ops.kiosks k
       WHERE k.id = NEW.assigned_kiosk_id AND k.seller_id IS NOT NULL
     ) THEN
    RAISE EXCEPTION 'Kiosk đã được gán cho seller khác';
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS kiosk_application_guard_owner ON kiosk_ops.kiosk_applications;
CREATE TRIGGER kiosk_application_guard_owner
BEFORE INSERT OR UPDATE OF status, assigned_kiosk_id ON kiosk_ops.kiosk_applications
FOR EACH ROW EXECUTE FUNCTION flow.guard_kiosk_application_reservation();

-- Admin assigns a whole, unoccupied kiosk to one active, subscribed seller.
-- Reassignment is allowed only while every slot is empty and unlocked.
CREATE OR REPLACE FUNCTION flow.assign_kiosk(
  p_kiosk uuid,
  p_seller uuid,
  p_admin uuid,
  p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_now timestamptz := flow.tick(p_now);
  k kiosk_ops.kiosks;
  v_seller identity.sellers;
  v_package identity.subscription_packages;
  v_slot_count integer;
  v_other_slots integer;
BEGIN
  IF (SELECT role FROM identity.users WHERE id = p_admin) IS DISTINCT FROM 'ADMIN' THEN
    RAISE EXCEPTION 'Chỉ admin được gán kiosk';
  END IF;

  -- Serialize quota checks for this seller, then serialize ownership changes for this kiosk.
  PERFORM pg_advisory_xact_lock(hashtext('seller:' || p_seller::text));
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-owner:' || p_kiosk::text, 0));
  SELECT * INTO k FROM kiosk_ops.kiosks WHERE id = p_kiosk FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Kiosk % không tồn tại', p_kiosk; END IF;
  IF k.status IN ('DISABLED', 'MAINTENANCE') THEN
    RAISE EXCEPTION 'Kiosk % đang bị vô hiệu hóa hoặc bảo trì', k.code;
  END IF;

  SELECT * INTO v_seller FROM identity.sellers WHERE id = p_seller FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Seller % không tồn tại', p_seller; END IF;
  IF v_seller.status <> 'ACTIVE' OR NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Seller % chưa có gói hiệu lực (status %), không gán kiosk được', v_seller.shop_name, v_seller.status;
  END IF;
  SELECT * INTO v_package FROM identity.subscription_packages WHERE id = v_seller.package_id;
  IF NOT FOUND OR v_package.status <> 'ACTIVE' THEN
    RAISE EXCEPTION 'Gói của seller không còn hoạt động';
  END IF;

  PERFORM 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk ORDER BY id FOR UPDATE;
  SELECT count(*) INTO v_slot_count FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk;
  IF v_slot_count = 0 THEN RAISE EXCEPTION 'Kiosk % chưa có ô nào', k.code; END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slots s
    WHERE s.kiosk_id = p_kiosk
      AND (s.status NOT IN ('FREE', 'RENTED_EMPTY')
        OR s.bouquet_id IS NOT NULL
        OR s.hold_until IS NOT NULL
        OR s.hold_order_id IS NOT NULL)
  ) THEN
    RAISE EXCEPTION 'Chỉ chuyển kiosk khi mọi ô đều trống, không giữ đơn và không khóa/bảo trì';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications a
    WHERE a.assigned_kiosk_id = p_kiosk
      AND a.status IN ('SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER')
  ) THEN
    RAISE EXCEPTION 'Kiosk đang được giữ cho quy trình bàn giao seller';
  END IF;

  SELECT count(*) INTO v_other_slots
  FROM kiosk_ops.slot_assignments a
  JOIN kiosk_ops.slots s ON s.id = a.slot_id
  WHERE a.seller_id = p_seller AND a.status = 'ACTIVE' AND s.kiosk_id <> p_kiosk;
  IF v_other_slots + v_slot_count > v_package.max_slots THEN
    RAISE EXCEPTION 'Gói % chỉ cho phép % ô; seller đã dùng % ô ở kiosk khác, kiosk này cần % ô',
      v_package.name, v_package.max_slots, v_other_slots, v_slot_count;
  END IF;

  -- Same owner with a complete whole-kiosk assignment is idempotent.
  IF k.seller_id = p_seller AND NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots s
    WHERE s.kiosk_id = p_kiosk
      AND (s.current_seller_id IS DISTINCT FROM p_seller OR NOT EXISTS (
        SELECT 1 FROM kiosk_ops.slot_assignments a
        WHERE a.slot_id = s.id AND a.seller_id = p_seller AND a.status = 'ACTIVE'
      ))
  ) THEN
    RETURN p_kiosk;
  END IF;

  UPDATE kiosk_ops.slot_assignments a
  SET status = 'RELEASED', released_at = v_now, release_reason = 'ADMIN'
  FROM kiosk_ops.slots s
  WHERE s.id = a.slot_id AND s.kiosk_id = p_kiosk AND a.status = 'ACTIVE';

  UPDATE kiosk_ops.kiosks SET seller_id = p_seller, updated_at = v_now WHERE id = p_kiosk;

  UPDATE kiosk_ops.slots
  SET current_seller_id = p_seller, status = 'RENTED_EMPTY',
      hold_until = NULL, hold_order_id = NULL,
      row_version = row_version + 1, updated_at = v_now
  WHERE kiosk_id = p_kiosk;

  INSERT INTO kiosk_ops.slot_assignments (slot_id, seller_id, assigned_by, assigned_at, status)
  SELECT s.id, p_seller, p_admin, v_now, 'ACTIVE'
  FROM kiosk_ops.slots s
  WHERE s.kiosk_id = p_kiosk
  ORDER BY s.slot_code;

  PERFORM flow.audit(p_admin, 'KIOSK_ASSIGNED', 'kiosks', p_kiosk,
    jsonb_build_object('seller', p_seller, 'previous_seller', k.seller_id, 'slot_count', v_slot_count));
  RETURN p_kiosk;
END $$;

-- Legacy per-slot assignment is disabled; use flow.assign_kiosk for a whole kiosk.
CREATE OR REPLACE FUNCTION flow.assign_slot(p_slot uuid, p_seller uuid, p_admin uuid, p_now timestamptz DEFAULT NULL)
RETURNS uuid LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Không còn gán riêng từng ô. Admin hãy dùng flow.assign_kiosk cho toàn bộ kiosk.';
END $$;

-- A slot is sellable only when both its assignment history and kiosk owner agree.
CREATE OR REPLACE FUNCTION flow.slot_sellable(p_slot uuid, p_seller uuid)
RETURNS boolean LANGUAGE sql STABLE AS $$
  SELECT EXISTS (
    SELECT 1
    FROM kiosk_ops.slots s
    JOIN kiosk_ops.kiosks k ON k.id = s.kiosk_id
    JOIN kiosk_ops.slot_assignments a ON a.slot_id = s.id
    WHERE s.id = p_slot AND k.seller_id = p_seller
      AND a.seller_id = p_seller AND a.status = 'ACTIVE'
  ) AND flow.package_valid(p_seller)
$$;

COMMIT;
