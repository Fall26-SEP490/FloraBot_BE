BEGIN;

-- Operational confirmation of physical removal. Preserve orders, refunds,
-- bouquets and inventory history; never synthesize device/door events.
CREATE OR REPLACE FUNCTION flow.seller_remove_slot_bouquet(
  p_kiosk uuid, p_slot uuid, p_actor uuid, p_bouquet uuid,
  p_reason text, p_confirmed_removed boolean
) RETURNS text LANGUAGE plpgsql AS $$
DECLARE
  v_seller uuid;
  s kiosk_ops.slots;
  b kiosk_ops.bouquets;
  v_status text;
BEGIN
  IF p_confirmed_removed IS DISTINCT FROM true OR p_bouquet IS NULL
     OR p_reason IS NULL OR length(btrim(p_reason)) NOT BETWEEN 5 AND 500 THEN
    RAISE EXCEPTION 'Xác nhận đã lấy hoa khỏi ô và nhập lý do từ 5 đến 500 ký tự.' USING ERRCODE = '22023';
  END IF;

  SELECT u.seller_id INTO v_seller
  FROM identity.users u JOIN identity.sellers seller ON seller.id = u.seller_id
  WHERE u.id = p_actor AND u.role = 'SELLER' AND u.status = 'ACTIVE'
  FOR SHARE OF u, seller;
  IF v_seller IS NULL THEN
    RAISE EXCEPTION 'Tài khoản Seller không còn hợp lệ.' USING ERRCODE = '42501';
  END IF;

  PERFORM 1 FROM kiosk_ops.kiosks WHERE id = p_kiosk AND seller_id = v_seller FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không tìm thấy kiosk đang quản lý.' USING ERRCODE = 'P0002';
  END IF;
  SELECT * INTO s FROM kiosk_ops.slots WHERE id = p_slot AND kiosk_id = p_kiosk FOR UPDATE;
  IF NOT FOUND OR s.current_seller_id IS DISTINCT FROM v_seller THEN
    RAISE EXCEPTION 'Không tìm thấy ô đang quản lý.' USING ERRCODE = 'P0002';
  END IF;
  PERFORM 1 FROM kiosk_ops.slot_assignments
  WHERE slot_id = s.id AND seller_id = v_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Ô không còn được gán cho shop.' USING ERRCODE = '22023';
  END IF;

  -- A retry may succeed only for the same already-removed bouquet and actor.
  -- A newly stocked bouquet always causes a conflict.
  IF s.status = 'RENTED_EMPTY' AND s.bouquet_id IS NULL
     AND s.hold_order_id IS NULL AND s.hold_until IS NULL
     AND EXISTS (SELECT 1 FROM notify.audit_logs
       WHERE actor_id = p_actor AND action = 'SELLER_SLOT_BOUQUET_REMOVED'
         AND entity_type = 'slots' AND entity_id = s.id
         AND payload->>'bouquetId' = p_bouquet::text) THEN
    RETURN s.status;
  END IF;

  IF s.status NOT IN ('STOCKED','PENDING_REMOVAL') OR s.bouquet_id IS DISTINCT FROM p_bouquet
     OR s.hold_order_id IS NOT NULL OR s.hold_until IS NOT NULL THEN
    RAISE EXCEPTION 'Chỉ thu hồi hoa trong ô STOCKED hoặc PENDING_REMOVAL chưa giữ cho đơn; hãy tải lại ô.' USING ERRCODE = '22023';
  END IF;
  SELECT * INTO b FROM kiosk_ops.bouquets WHERE id = p_bouquet AND seller_id = v_seller FOR UPDATE;
  IF NOT FOUND OR b.status IN ('HELD','PICKED_UP','RETURNED')
     OR (s.status = 'STOCKED' AND b.status <> 'STOCKED') THEN
    RAISE EXCEPTION 'Trạng thái bó hoa không cho phép thu hồi.' USING ERRCODE = '22023';
  END IF;
  IF EXISTS (SELECT 1 FROM ordering.order_items i JOIN ordering.orders o ON o.id = i.order_id
      WHERE i.bouquet_id = p_bouquet
        AND o.status NOT IN ('EXPIRED','CANCELLED','DISPENSE_FAILED','REFUNDED')) THEN
    RAISE EXCEPTION 'Hoa đang gắn với đơn cần được xử lý; không thể làm trống ô.' USING ERRCODE = '22023';
  END IF;

  PERFORM 1 FROM kiosk_ops.unlock_tokens WHERE slot_id = s.id ORDER BY id FOR UPDATE;
  IF EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens WHERE slot_id = s.id
      AND (status = 'OPENED' OR (door_opened_at IS NOT NULL AND door_closed_at IS NULL)
        OR (status IN ('ISSUED','SENT','ACKED') AND expires_at > public.app_now()))) THEN
    RAISE EXCEPTION 'Cửa ô đang mở hoặc có lệnh mở còn hiệu lực; hãy kiểm tra tủ.' USING ERRCODE = '22023';
  END IF;
  UPDATE kiosk_ops.unlock_tokens SET status = 'REVOKED'
  WHERE slot_id = s.id AND status IN ('ISSUED','SENT','ACKED');

  UPDATE kiosk_ops.bouquets
  SET status = CASE WHEN b.status = 'DAMAGED' THEN 'DAMAGED' ELSE 'RETURNED' END
  WHERE id = b.id;
  INSERT INTO kiosk_ops.inventory_logs(bouquet_id, slot_id, movement_type, performed_by, reason)
  VALUES (b.id, s.id, 'RETURN_SELLER', p_actor, btrim(p_reason));
  v_status := flow.release_slot_empty(s.id);
  IF v_status <> 'RENTED_EMPTY' THEN
    RAISE EXCEPTION 'Quyền thuê ô đã thay đổi; hãy tải lại.' USING ERRCODE = '22023';
  END IF;
  PERFORM flow.audit(p_actor, 'SELLER_SLOT_BOUQUET_REMOVED', 'slots', s.id,
    jsonb_build_object('kioskId', p_kiosk, 'bouquetId', b.id, 'from', s.status,
      'to', v_status, 'zone', s.zone, 'reason', btrim(p_reason), 'confirmedRemoved', true));
  RETURN v_status;
END $$;

COMMIT;
