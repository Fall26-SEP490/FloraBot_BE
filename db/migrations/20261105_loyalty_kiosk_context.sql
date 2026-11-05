-- 20261105_loyalty_kiosk_context.sql
-- Typed private identity-owned contract for kiosk seller ownership lookup.
-- Preserves schema boundaries: identity module queries this contract instead of kiosk_ops directly.

BEGIN;

CREATE OR REPLACE FUNCTION identity.get_kiosk_seller_id(p_kiosk_id uuid)
RETURNS TABLE (
    seller_id uuid
)
LANGUAGE sql
STABLE
SECURITY INVOKER
AS $$
    SELECT k.seller_id
    FROM kiosk_ops.kiosks k
    WHERE k.id = p_kiosk_id;
$$;

COMMIT;
