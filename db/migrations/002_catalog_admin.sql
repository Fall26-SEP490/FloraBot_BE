-- Align catalog commands with the ADMIN/SELLER matrix without changing source SQL.
CREATE OR REPLACE FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  UPDATE catalog.flower_products p SET status = p_status
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa sản phẩm %', p_product; END IF;
  PERFORM flow.audit(p_user, 'PRODUCT_STATUS_CHANGED', 'flower_products', p_product,
                     jsonb_build_object('status', p_status));
END $$;

CREATE OR REPLACE FUNCTION flow.update_product_price(p_product uuid, p_user uuid, p_price bigint)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM set_config('app.actor', p_user::text, true);
  UPDATE catalog.flower_products p SET price = p_price
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa giá sản phẩm %', p_product; END IF;
END $$;
