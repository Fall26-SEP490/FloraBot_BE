-- Keep file metadata and URLs for historical records; hide a removed product
-- photo from catalog surfaces rather than deleting the Cloudinary asset.
BEGIN;

ALTER TABLE notify.attachments
  ADD COLUMN IF NOT EXISTS is_hidden boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS is_cover boolean NOT NULL DEFAULT false,
  ADD COLUMN IF NOT EXISTS sort_order integer NOT NULL DEFAULT 0;

WITH ranked AS (
  SELECT id, row_number() OVER (PARTITION BY owner_id ORDER BY created_at,id) AS position
  FROM notify.attachments
  WHERE owner_service='catalog' AND owner_type='flower_product' AND phase='PRODUCT'
)
UPDATE notify.attachments a SET sort_order=ranked.position::integer,
  is_cover=(ranked.position=1)
FROM ranked WHERE a.id=ranked.id AND a.sort_order=0;

CREATE UNIQUE INDEX IF NOT EXISTS uq_product_photo_cover
  ON notify.attachments(owner_id)
  WHERE owner_service='catalog' AND owner_type='flower_product'
    AND phase='PRODUCT' AND is_cover AND NOT is_hidden;

CREATE OR REPLACE VIEW screen.v_web_catalog AS
SELECT p.id AS product_id, p.seller_id, s.shop_name, p.name, p.description, p.price,
       k.id AS kiosk_id, k.name AS kiosk_name, k.address,
       (SELECT a.file_url FROM notify.attachments a
        WHERE a.owner_service='catalog' AND a.owner_type='flower_product'
          AND a.owner_id=p.id AND a.phase='PRODUCT' AND NOT a.is_hidden
        ORDER BY a.is_cover DESC,a.sort_order,a.created_at,a.id LIMIT 1) AS photo,
       ARRAY(
         SELECT b.id FROM kiosk_ops.bouquets b
         JOIN kiosk_ops.slots sl ON sl.bouquet_id=b.id
         WHERE b.product_id=p.id AND sl.kiosk_id=k.id AND sl.status='STOCKED'
           AND b.status='STOCKED' AND b.sellable_until>public.app_now()+flow.cfg_min('hold_minutes')
           AND flow.slot_sellable(sl.id,s.id) AND sl.zone='PREORDER'
         ORDER BY b.sellable_until,b.id
       ) AS bouquets
FROM catalog.flower_products p
JOIN identity.sellers s ON s.id=p.seller_id
JOIN kiosk_ops.kiosks k ON k.status NOT IN ('DISABLED','MAINTENANCE')
  AND length(btrim(coalesce(k.address,'')))>0 AND k.latitude IS NOT NULL AND k.longitude IS NOT NULL
WHERE p.status='ACTIVE' AND s.status='ACTIVE' AND flow.package_valid(s.id)
  AND EXISTS (
    SELECT 1 FROM kiosk_ops.slots sl
    WHERE sl.kiosk_id=k.id AND sl.current_seller_id=s.id
      AND flow.slot_sellable(sl.id,s.id) AND sl.zone='PREORDER'
  );

COMMIT;
