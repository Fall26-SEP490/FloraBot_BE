BEGIN;

CREATE OR REPLACE FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE
  v_current_status text;
BEGIN
  -- Keep the existing seller/owner/subscription authorization and lock the
  -- product row. Preorder creation takes a compatible share lock, so it cannot
  -- slip between this check and deactivation.
  PERFORM flow.check_catalog_editor(p_product, p_user);

  SELECT status INTO v_current_status
  FROM catalog.flower_products
  WHERE id = p_product;

  IF p_status = 'INACTIVE' AND v_current_status IS DISTINCT FROM 'INACTIVE' THEN
    IF EXISTS (
      SELECT 1
      FROM ordering.web_requests r
      LEFT JOIN ordering.orders o ON o.id = r.order_id
      WHERE r.product_id = p_product
        AND r.kind = 'CUSTOM'
        AND (
          r.state IN ('REQUESTED', 'QUOTED')
          OR (
            r.state = 'ORDERED'
            AND (o.id IS NULL OR o.status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING'))
          )
        )
    ) THEN
      RAISE EXCEPTION 'Không thể ngừng bán mẫu hoa khi còn đơn đặt trước chưa xử lý.'
        USING ERRCODE = 'P0001';
    END IF;

    IF EXISTS (
      SELECT 1
      FROM kiosk_ops.slot_maintenance_sessions s
      WHERE s.new_product_id = p_product
        AND s.stage NOT IN ('Completed', 'Recovered')
    ) THEN
      RAISE EXCEPTION 'Không thể ngừng bán mẫu hoa khi còn phiên bảo trì ô đang sử dụng mẫu này.'
        USING ERRCODE = 'P0001';
    END IF;
  END IF;

  UPDATE catalog.flower_products p SET status = p_status
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa sản phẩm %', p_product; END IF;
  PERFORM flow.audit(p_user, 'PRODUCT_STATUS_CHANGED', 'flower_products', p_product,
                     jsonb_build_object('status', p_status));
END $$;

COMMIT;
