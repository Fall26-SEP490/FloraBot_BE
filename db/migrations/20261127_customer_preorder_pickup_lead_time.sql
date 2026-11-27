-- Match the seller quotation lead time. Keep the existing idempotency, tenant,
-- stock, accessory snapshot and capacity checks unchanged.
BEGIN;

CREATE OR REPLACE FUNCTION flow.web_request(
  p_id uuid, p_customer uuid, p_kind text, p_product uuid, p_kiosk uuid,
  p_bouquet uuid, p_pickup timestamptz, p_instructions text, p_accessories jsonb DEFAULT '[]'::jsonb
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  pr catalog.flower_products;
  w ordering.web_requests;
  v_selection jsonb;
  v_requested jsonb;
BEGIN
  PERFORM pg_advisory_xact_lock(hashtextextended('web-request:' || p_id::text, 0));
  v_selection := flow.normalize_preorder_accessory_selection(p_accessories);
  IF NOT EXISTS (SELECT 1 FROM identity.users WHERE id = p_customer AND role = 'CUSTOMER' AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Tai khoan khong hop le' USING ERRCODE = 'P0001';
  END IF;

  SELECT * INTO w FROM ordering.web_requests WHERE id = p_id;
  IF FOUND THEN
    IF w.request_payload IS NOT NULL AND (
      w.request_payload->'bouquet' IS DISTINCT FROM coalesce(to_jsonb(p_bouquet), 'null'::jsonb)
      OR (w.request_payload->>'pickup')::timestamptz IS DISTINCT FROM p_pickup
      OR coalesce(w.request_payload->'accessories', '[]'::jsonb) IS DISTINCT FROM v_selection
    ) THEN
      RAISE EXCEPTION 'Mã yêu cầu đã được dùng với thông tin khác' USING ERRCODE = 'P0001';
    END IF;
    IF w.customer_id <> p_customer OR w.product_id <> p_product OR w.kiosk_id <> p_kiosk
       OR w.kind <> p_kind OR w.instructions <> p_instructions THEN
      RAISE EXCEPTION 'Ma yeu cau da duoc su dung' USING ERRCODE = 'P0001';
    END IF;
    RETURN w.id;
  END IF;

  IF p_kind IS DISTINCT FROM 'CUSTOM' THEN RAISE EXCEPTION 'Web chỉ nhận đơn đặt hoa theo mẫu' USING ERRCODE = 'P0001'; END IF;
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND OR NOT flow.package_valid(pr.seller_id)
     OR NOT EXISTS (SELECT 1 FROM identity.sellers WHERE id = pr.seller_id AND status = 'ACTIVE') THEN
    RAISE EXCEPTION 'Shop hoac mau hoa chua nhan don' USING ERRCODE = 'P0001';
  END IF;
  IF pr.quantity <= 0 THEN RAISE EXCEPTION 'PREORDER_PRODUCT_OUT_OF_STOCK' USING ERRCODE = 'P0001'; END IF;
  IF NOT flow.kiosk_package_valid(p_kiosk) OR flow.is_kiosk_terminating(p_kiosk)
     OR length(p_instructions) > 2000 OR NOT EXISTS (
    SELECT 1 FROM kiosk_ops.kiosks
    WHERE id = p_kiosk AND seller_id = pr.seller_id AND status NOT IN ('DISABLED', 'MAINTENANCE')
      AND length(btrim(coalesce(address, ''))) > 0 AND latitude IS NOT NULL AND longitude IS NOT NULL
  ) THEN RAISE EXCEPTION 'Thông tin đặt hoa hoặc gói kiosk không hợp lệ' USING ERRCODE = 'P0001'; END IF;
  IF NOT EXISTS (
    SELECT 1 FROM kiosk_ops.slots
    WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND status NOT IN ('FREE', 'PENDING_RELEASE')
  ) THEN RAISE EXCEPTION 'Shop không phục vụ từ này' USING ERRCODE = 'P0001'; END IF;
  IF length(btrim(p_instructions)) < 10 OR p_pickup < public.app_now() + interval '1 hour'
     OR p_pickup > public.app_now() + interval '30 days' THEN
    RAISE EXCEPTION 'Mô tả tối thiểu 10 ký tự; giờ nhận phải sau ít nhất 1 giờ và trong 30 ngày kể từ lúc đặt' USING ERRCODE = 'P0001';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id = p_kiosk AND current_seller_id = pr.seller_id AND zone = 'PREORDER') THEN
    RAISE EXCEPTION 'Kiosk chưa có ô PREORDER do seller phân loại' USING ERRCODE = 'P0001';
  END IF;
  IF NOT EXISTS (SELECT 1 FROM flow.get_kiosk_preorder_availability(p_kiosk) WHERE is_available) THEN
    RAISE EXCEPTION 'Kiosk đã hết sức chứa đặt trước (PREORDER)' USING ERRCODE = 'P0001';
  END IF;

  -- New requests use shop stock. Existing request/quote snapshots above remain retryable.
  IF EXISTS (
    SELECT 1 FROM jsonb_array_elements(v_selection) selected(value)
    LEFT JOIN kiosk_ops.accessories a ON a.id = (selected.value->>'accessoryId')::uuid
    WHERE a.id IS NULL OR a.seller_id <> pr.seller_id OR a.kiosk_id IS NOT NULL
  ) THEN
    RAISE EXCEPTION 'PREORDER_ACCESSORY_NOT_IN_SHOP' USING ERRCODE = 'P0001';
  END IF;
  v_requested := flow.snapshot_preorder_accessories(v_selection, p_kiosk, pr.seller_id, NULL, false);
  INSERT INTO ordering.web_requests(
    id, customer_id, seller_id, product_id, kiosk_id, kind, instructions, pickup_at, pickup_before, state,
    requested_accessories
  ) VALUES (
    p_id, p_customer, pr.seller_id, pr.id, p_kiosk, 'CUSTOM', p_instructions, p_pickup,
    p_pickup + make_interval(hours => flow.cfg('web_pickup_hours')::integer), 'REQUESTED', v_requested
  );
  UPDATE ordering.web_requests
  SET request_payload = jsonb_build_object('bouquet', p_bouquet, 'pickup', p_pickup, 'accessories', v_selection),
      display_snapshot = (
        SELECT jsonb_build_object('product', pr.name, 'shop', shop.shop_name, 'kiosk', k.name, 'address', k.address)
        FROM identity.sellers shop CROSS JOIN kiosk_ops.kiosks k WHERE shop.id = pr.seller_id AND k.id = p_kiosk
      )
  WHERE id = p_id;
  PERFORM flow.audit(p_customer, 'WEB_REQUEST_CREATED', 'web_requests', p_id,
    jsonb_build_object('kind', 'CUSTOM', 'accessory_count', jsonb_array_length(v_requested)));
  RETURN p_id;
END;
$$;

COMMIT;
