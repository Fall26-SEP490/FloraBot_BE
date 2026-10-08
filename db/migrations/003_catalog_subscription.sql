-- D2 requires an active, unexpired subscription for seller catalog mutations.
CREATE OR REPLACE FUNCTION flow.check_catalog_editor(p_product uuid, p_user uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE owner_id uuid; actor_role text; seller_status text;
BEGIN
  SELECT seller_id INTO owner_id FROM catalog.flower_products WHERE id=p_product FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa sản phẩm %', p_product; END IF;
  PERFORM flow.check_seller_user(p_user, owner_id);
  SELECT role INTO actor_role FROM identity.users WHERE id=p_user;
  IF actor_role='ADMIN' THEN RETURN; END IF;
  IF actor_role <> 'SELLER' THEN RAISE EXCEPTION 'Không có quyền sửa sản phẩm %', p_product; END IF;
  SELECT status INTO seller_status FROM identity.sellers WHERE id=owner_id FOR SHARE;
  IF seller_status IS DISTINCT FROM 'ACTIVE' OR NOT flow.package_valid(owner_id) THEN
    RAISE EXCEPTION 'Shop cần gói đang hoạt động để chỉnh sửa sản phẩm';
  END IF;
END $$;

-- Align catalog commands with the ADMIN/SELLER matrix without changing source SQL.
CREATE OR REPLACE FUNCTION flow.set_product_status(p_product uuid, p_user uuid, p_status text)
RETURNS void LANGUAGE plpgsql AS $$
BEGIN
  PERFORM flow.check_catalog_editor(p_product, p_user);
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
  PERFORM flow.check_catalog_editor(p_product, p_user);
  PERFORM set_config('app.actor', p_user::text, true);
  UPDATE catalog.flower_products p SET price = p_price
  WHERE p.id = p_product AND EXISTS (
    SELECT 1 FROM identity.users u WHERE u.id = p_user AND u.status = 'ACTIVE'
    AND (u.role = 'ADMIN' OR (u.role = 'SELLER' AND u.seller_id = p.seller_id))
  );
  IF NOT FOUND THEN RAISE EXCEPTION 'Không có quyền sửa giá sản phẩm %', p_product; END IF;
END $$;

CREATE OR REPLACE FUNCTION flow.add_product_photo(p_product uuid, p_user uuid, p_url text)
RETURNS uuid LANGUAGE plpgsql AS $$
BEGIN
  PERFORM flow.check_catalog_editor(p_product, p_user);
  RETURN flow.attach('catalog', 'flower_product', p_product, p_url, 'PRODUCT', p_user);
END $$;
