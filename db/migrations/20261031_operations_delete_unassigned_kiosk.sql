BEGIN;

-- A physical kiosk may only be deleted before it is assigned or has operational history.
-- Historical rows are retained; an eligible kiosk's generated, unused slots are removed with it.
CREATE OR REPLACE FUNCTION flow.delete_unassigned_kiosk(
  p_kiosk_id uuid,
  p_actor uuid
) RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
  k kiosk_ops.kiosks%ROWTYPE;
  v_slot_count integer;
  v_has_subscription_reference boolean := false;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-delete:' || p_kiosk_id::text, 0));

  SELECT * INTO k
  FROM kiosk_ops.kiosks
  WHERE id = p_kiosk_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN false;
  END IF;

  IF k.seller_id IS NOT NULL THEN
    RAISE EXCEPTION 'Kiosk đã được gán cho Seller; không thể xóa. Hãy dùng luồng ngừng hoạt động.' USING ERRCODE = 'P0001';
  END IF;

  IF k.status <> 'OFFLINE' OR k.last_heartbeat_at IS NOT NULL THEN
    RAISE EXCEPTION 'Chỉ xóa được kiosk OFFLINE chưa từng kết nối.' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (SELECT 1 FROM kiosk_ops.slots
             WHERE kiosk_id = p_kiosk_id
               AND (status <> 'FREE' OR current_seller_id IS NOT NULL OR bouquet_id IS NOT NULL
                    OR hold_until IS NOT NULL OR hold_order_id IS NOT NULL)) THEN
    RAISE EXCEPTION 'Kiosk có ô đã được sử dụng hoặc gán Seller; không thể xóa.' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (SELECT 1 FROM kiosk_ops.kiosk_applications WHERE assigned_kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM ordering.orders WHERE kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM ordering.web_requests WHERE kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM kiosk_ops.staff_tasks WHERE kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM kiosk_ops.mqtt_inbox WHERE kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM kiosk_ops.accessories WHERE kiosk_id = p_kiosk_id)
     OR EXISTS (SELECT 1 FROM kiosk_ops.manager_kiosks WHERE kiosk_id = p_kiosk_id) THEN
    RAISE EXCEPTION 'Kiosk đã có hồ sơ hoặc dữ liệu liên quan; không thể xóa để bảo toàn lịch sử.' USING ERRCODE = 'P0001';
  END IF;

  -- Multi-kiosk deployments may add this optional ownership column in a later migration.
  IF EXISTS (
    SELECT 1 FROM information_schema.columns
    WHERE table_schema = 'identity' AND table_name = 'subscriptions' AND column_name = 'kiosk_id'
  ) THEN
    EXECUTE 'SELECT EXISTS (SELECT 1 FROM identity.subscriptions WHERE kiosk_id = $1)'
      INTO v_has_subscription_reference USING p_kiosk_id;
    IF v_has_subscription_reference THEN
      RAISE EXCEPTION 'Kiosk có gói thuê liên kết; không thể xóa để bảo toàn lịch sử.' USING ERRCODE = 'P0001';
    END IF;
  END IF;

  SELECT count(*) INTO v_slot_count
  FROM kiosk_ops.slots
  WHERE kiosk_id = p_kiosk_id;

  PERFORM flow.audit(
    p_actor,
    'KIOSK_DELETED',
    'kiosks',
    p_kiosk_id,
    jsonb_build_object('code', k.code, 'name', k.name, 'slot_count', v_slot_count)
  );

  BEGIN
    DELETE FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk_id;
    DELETE FROM kiosk_ops.kiosks WHERE id = p_kiosk_id;
  EXCEPTION WHEN foreign_key_violation THEN
    RAISE EXCEPTION 'Kiosk hoặc ô của kiosk có lịch sử được tham chiếu; không thể xóa.' USING ERRCODE = 'P0001';
  END;

  RETURN true;
END;
$$;

COMMIT;
