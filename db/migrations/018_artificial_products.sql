-- Migration 018: Artificial flower product catalog, dimensions, and non-expiring bouquet trigger
BEGIN;

-- 1. Extend catalog.flower_products with inventory_kind and dimensions
ALTER TABLE catalog.flower_products
  ADD COLUMN IF NOT EXISTS inventory_kind text NOT NULL DEFAULT 'LEGACY_FRESH'
    CHECK (inventory_kind IN ('LEGACY_FRESH', 'ARTIFICIAL')),
  ADD COLUMN IF NOT EXISTS length_cm numeric(6,2)
    CHECK (length_cm IS NULL OR (length_cm >= 0.01 AND length_cm <= 1000)),
  ADD COLUMN IF NOT EXISTS width_cm numeric(6,2)
    CHECK (width_cm IS NULL OR (width_cm >= 0.01 AND width_cm <= 1000)),
  ADD COLUMN IF NOT EXISTS height_cm numeric(6,2)
    CHECK (height_cm IS NULL OR (height_cm >= 0.01 AND height_cm <= 1000));

-- Require dimensions for ARTIFICIAL products (legacy fresh products do not require dimensions)
ALTER TABLE catalog.flower_products DROP CONSTRAINT IF EXISTS flower_products_artificial_dimensions_check;
ALTER TABLE catalog.flower_products ADD CONSTRAINT flower_products_artificial_dimensions_check
  CHECK (inventory_kind <> 'ARTIFICIAL' OR (length_cm IS NOT NULL AND width_cm IS NOT NULL AND height_cm IS NOT NULL));

-- 2. Tightly scoped BEFORE INSERT/UPDATE trigger on bouquets for ARTIFICIAL non-expiry
-- Uses FOR SHARE to ensure concurrent metadata updates wait and serialize properly
CREATE OR REPLACE FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry()
RETURNS trigger LANGUAGE plpgsql AS $$
DECLARE
  v_kind text;
BEGIN
  SELECT inventory_kind INTO v_kind FROM catalog.flower_products WHERE id = NEW.product_id FOR SHARE;
  IF v_kind = 'ARTIFICIAL' THEN
    NEW.sellable_until := 'infinity'::timestamptz;
  END IF;
  RETURN NEW;
END $$;

DROP TRIGGER IF EXISTS bouquets_artificial_no_expiry_trg ON kiosk_ops.bouquets;
CREATE TRIGGER bouquets_artificial_no_expiry_trg
  BEFORE INSERT OR UPDATE OF product_id ON kiosk_ops.bouquets
  FOR EACH ROW
  EXECUTE FUNCTION kiosk_ops.set_artificial_bouquet_no_expiry();

-- 3. Stored procedures for artificial product creation and update
CREATE OR REPLACE FUNCTION flow.create_artificial_product(
  p_id uuid,
  p_seller uuid,
  p_actor uuid,
  p_name text,
  p_description text,
  p_price bigint,
  p_tags text[],
  p_length_cm numeric,
  p_width_cm numeric,
  p_height_cm numeric
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_name text := btrim(coalesce(p_name, ''));
  v_tag text;
  v_tags text[] := '{}';
BEGIN
  -- Rejection of null or zero UUIDs
  IF p_id IS NULL OR p_id = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã sản phẩm không hợp lệ' USING ERRCODE = '22023';
  END IF;
  IF p_seller IS NULL OR p_seller = '00000000-0000-0000-0000-000000000000'::uuid
     OR p_actor IS NULL OR p_actor = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã shop hoặc người thao tác không hợp lệ' USING ERRCODE = '22023';
  END IF;

  -- Strict actor eligibility check: actor must be active SELLER belonging to p_seller
  PERFORM 1 FROM identity.users WHERE id = p_actor AND role = 'SELLER' AND seller_id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không có quyền thao tác' USING ERRCODE = '42501';
  END IF;

  -- Active seller shop check
  PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Shop không hoạt động' USING ERRCODE = '42501';
  END IF;

  -- Active subscription package check
  IF NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Shop cần gói thuê bao đang hoạt động để tạo sản phẩm' USING ERRCODE = '42501';
  END IF;

  -- Input validations
  IF length(v_name) < 1 OR length(v_name) > 120 THEN
    RAISE EXCEPTION 'Tên sản phẩm từ 1 đến 120 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_description IS NOT NULL AND length(p_description) > 2000 THEN
    RAISE EXCEPTION 'Mô tả tối đa 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_price IS NULL OR p_price <= 0 THEN
    RAISE EXCEPTION 'Giá bán phải là số nguyên dương' USING ERRCODE = '22023';
  END IF;

  IF p_length_cm IS NULL OR p_length_cm < 0.01 OR p_length_cm > 1000 OR round(p_length_cm, 2) <> p_length_cm
     OR p_width_cm IS NULL OR p_width_cm < 0.01 OR p_width_cm > 1000 OR round(p_width_cm, 2) <> p_width_cm
     OR p_height_cm IS NULL OR p_height_cm < 0.01 OR p_height_cm > 1000 OR round(p_height_cm, 2) <> p_height_cm THEN
    RAISE EXCEPTION 'Kích thước phải từ 0.01 đến 1000 cm và tối đa 2 chữ số thập phân' USING ERRCODE = '22023';
  END IF;

  IF p_tags IS NULL THEN
    RAISE EXCEPTION 'Danh sách tags không được null' USING ERRCODE = '22023';
  END IF;

  IF cardinality(p_tags) > 20 THEN
    RAISE EXCEPTION 'Tối đa 20 tags' USING ERRCODE = '22023';
  END IF;

  FOREACH v_tag IN ARRAY p_tags LOOP
    IF v_tag IS NULL THEN
      RAISE EXCEPTION 'Tag không được là null' USING ERRCODE = '22023';
    END IF;
    v_tag := btrim(v_tag);
    IF length(v_tag) < 1 OR length(v_tag) > 64 THEN
      RAISE EXCEPTION 'Mỗi tag từ 1 đến 64 ký tự' USING ERRCODE = '22023';
    END IF;
    IF v_tag = ANY(v_tags) THEN
      RAISE EXCEPTION 'Tags không được trùng lặp' USING ERRCODE = '22023';
    END IF;
    v_tags := array_append(v_tags, v_tag);
  END LOOP;

  -- Client-generated ID duplicate conflict check (atomic lock)
  PERFORM pg_advisory_xact_lock(hashtextextended('product:' || p_id::text, 0));
  IF EXISTS (SELECT 1 FROM catalog.flower_products WHERE id = p_id) THEN
    RAISE EXCEPTION 'Mã sản phẩm đã tồn tại' USING ERRCODE = '23505';
  END IF;

  -- Store 48 as positive placeholder for shelf_life_hours
  INSERT INTO catalog.flower_products (
    id, seller_id, name, description, price, tags, shelf_life_hours,
    status, inventory_kind, length_cm, width_cm, height_cm
  ) VALUES (
    p_id, p_seller, v_name, p_description, p_price, v_tags, 48,
    'DRAFT', 'ARTIFICIAL', p_length_cm, p_width_cm, p_height_cm
  );

  PERFORM flow.audit(p_actor, 'PRODUCT_CREATED', 'flower_products', p_id,
    jsonb_build_object('name', v_name, 'price', p_price, 'inventory_kind', 'ARTIFICIAL',
      'dimensions', jsonb_build_object('length', p_length_cm, 'width', p_width_cm, 'height', p_height_cm)));

  RETURN p_id;
END $$;

CREATE OR REPLACE FUNCTION flow.update_artificial_product(
  p_product uuid,
  p_seller uuid,
  p_actor uuid,
  p_name text,
  p_description text,
  p_price bigint,
  p_tags text[],
  p_length_cm numeric,
  p_width_cm numeric,
  p_height_cm numeric
) RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_name text := btrim(coalesce(p_name, ''));
  v_tag text;
  v_tags text[] := '{}';
  pr catalog.flower_products;
BEGIN
  -- Rejection of null or zero UUIDs
  IF p_product IS NULL OR p_product = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã sản phẩm không hợp lệ' USING ERRCODE = '22023';
  END IF;
  IF p_seller IS NULL OR p_seller = '00000000-0000-0000-0000-000000000000'::uuid
     OR p_actor IS NULL OR p_actor = '00000000-0000-0000-0000-000000000000'::uuid THEN
    RAISE EXCEPTION 'Mã shop hoặc người thao tác không hợp lệ' USING ERRCODE = '22023';
  END IF;

  -- Strict actor eligibility check: actor must be active SELLER belonging to p_seller
  PERFORM 1 FROM identity.users WHERE id = p_actor AND role = 'SELLER' AND seller_id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Không có quyền thao tác' USING ERRCODE = '42501';
  END IF;

  -- Active seller shop check
  PERFORM 1 FROM identity.sellers WHERE id = p_seller AND status = 'ACTIVE' FOR SHARE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Shop không hoạt động' USING ERRCODE = '42501';
  END IF;

  -- Active subscription package check
  IF NOT flow.package_valid(p_seller) THEN
    RAISE EXCEPTION 'Shop cần gói thuê bao đang hoạt động để cập nhật sản phẩm' USING ERRCODE = '42501';
  END IF;

  -- Lock product for update and check ownership
  PERFORM pg_advisory_xact_lock(hashtextextended('product:' || p_product::text, 0));
  SELECT * INTO pr FROM catalog.flower_products WHERE id = p_product FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Sản phẩm không tồn tại' USING ERRCODE = 'P0002';
  END IF;
  IF pr.seller_id <> p_seller THEN
    RAISE EXCEPTION 'Sản phẩm không thuộc shop' USING ERRCODE = 'P0002';
  END IF;

  -- Input validations
  IF length(v_name) < 1 OR length(v_name) > 120 THEN
    RAISE EXCEPTION 'Tên sản phẩm từ 1 đến 120 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_description IS NOT NULL AND length(p_description) > 2000 THEN
    RAISE EXCEPTION 'Mô tả tối đa 2000 ký tự' USING ERRCODE = '22023';
  END IF;

  IF p_price IS NULL OR p_price <= 0 THEN
    RAISE EXCEPTION 'Giá bán phải là số nguyên dương' USING ERRCODE = '22023';
  END IF;

  IF p_length_cm IS NULL OR p_length_cm < 0.01 OR p_length_cm > 1000 OR round(p_length_cm, 2) <> p_length_cm
     OR p_width_cm IS NULL OR p_width_cm < 0.01 OR p_width_cm > 1000 OR round(p_width_cm, 2) <> p_width_cm
     OR p_height_cm IS NULL OR p_height_cm < 0.01 OR p_height_cm > 1000 OR round(p_height_cm, 2) <> p_height_cm THEN
    RAISE EXCEPTION 'Kích thước phải từ 0.01 đến 1000 cm và tối đa 2 chữ số thập phân' USING ERRCODE = '22023';
  END IF;

  IF p_tags IS NULL THEN
    RAISE EXCEPTION 'Danh sách tags không được null' USING ERRCODE = '22023';
  END IF;

  IF cardinality(p_tags) > 20 THEN
    RAISE EXCEPTION 'Tối đa 20 tags' USING ERRCODE = '22023';
  END IF;

  FOREACH v_tag IN ARRAY p_tags LOOP
    IF v_tag IS NULL THEN
      RAISE EXCEPTION 'Tag không được là null' USING ERRCODE = '22023';
    END IF;
    v_tag := btrim(v_tag);
    IF length(v_tag) < 1 OR length(v_tag) > 64 THEN
      RAISE EXCEPTION 'Mỗi tag từ 1 đến 64 ký tự' USING ERRCODE = '22023';
    END IF;
    IF v_tag = ANY(v_tags) THEN
      RAISE EXCEPTION 'Tags không được trùng lặp' USING ERRCODE = '22023';
    END IF;
    v_tags := array_append(v_tags, v_tag);
  END LOOP;

  -- Conflict rule: dimension change conflicts (409) if live bouquets (STOCKED or HELD) exist
  IF (pr.length_cm IS DISTINCT FROM p_length_cm OR
      pr.width_cm IS DISTINCT FROM p_width_cm OR
      pr.height_cm IS DISTINCT FROM p_height_cm) THEN
    IF EXISTS (SELECT 1 FROM kiosk_ops.bouquets WHERE product_id = p_product AND status IN ('STOCKED', 'HELD')) THEN
      RAISE EXCEPTION 'Không thể thay đổi kích thước khi có bó hoa đang trong tủ' USING ERRCODE = '40001';
    END IF;
  END IF;

  -- Update metadata only (never change seller_id or status)
  UPDATE catalog.flower_products SET
    name = v_name,
    description = p_description,
    price = p_price,
    tags = v_tags,
    length_cm = p_length_cm,
    width_cm = p_width_cm,
    height_cm = p_height_cm,
    updated_at = public.app_now()
  WHERE id = p_product;

  PERFORM flow.audit(p_actor, 'PRODUCT_UPDATED', 'flower_products', p_product,
    jsonb_build_object('name', v_name, 'price', p_price,
      'dimensions', jsonb_build_object('length', p_length_cm, 'width', p_width_cm, 'height', p_height_cm)));

  RETURN p_product;
END $$;

COMMIT;
