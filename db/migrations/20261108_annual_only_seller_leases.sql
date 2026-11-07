-- Retire the legacy seller-wide monthly subscription and its PayOS checkout.
-- Kiosk-scoped annual subscriptions and their PayOS renewals remain supported.
BEGIN;

-- New packages default to annual billing. The legacy monthly_fee column stays
-- in place for historical records and schema compatibility, but monthly
-- packages are no longer offered by the API.
ALTER TABLE identity.subscription_packages
  ALTER COLUMN billing_cycle SET DEFAULT 'ANNUAL';

-- Stop the legacy monthly self-subscribe SQL entry point as well as its HTTP route.
CREATE OR REPLACE FUNCTION flow.subscribe(
  p_seller uuid,
  p_user uuid,
  p_months integer,
  p_from date DEFAULT NULL,
  p_now timestamptz DEFAULT NULL
) RETURNS uuid LANGUAGE plpgsql AS $$
BEGIN
  RAISE EXCEPTION 'Monthly subscriptions are retired; request or renew an annual kiosk lease.'
    USING ERRCODE = '0A000';
END $$;

-- Cancel unpaid monthly leases and their PayOS charges. If an old PayOS link is
-- paid after this cutover, flow.subscription_paid handles the cancelled lease
-- as a late payment and records the existing manual refund workflow.
UPDATE payment.payments p
SET status = 'CANCELLED', updated_at = public.app_now(),
    reason = coalesce(p.reason, 'MONTHLY_SUBSCRIPTIONS_RETIRED')
FROM identity.subscriptions s
JOIN identity.subscription_packages pkg ON pkg.id = s.package_id
WHERE p.id = s.payment_id
  AND p.status = 'PENDING'
  AND s.status = 'PENDING_PAYMENT'
  AND s.kiosk_id IS NULL
  AND pkg.billing_cycle = 'MONTHLY';

UPDATE identity.subscriptions s
SET status = 'CANCELLED', updated_at = public.app_now()
FROM identity.subscription_packages pkg
WHERE pkg.id = s.package_id
  AND s.status = 'PENDING_PAYMENT'
  AND s.kiosk_id IS NULL
  AND pkg.billing_cycle = 'MONTHLY';

-- End already-active monthly periods now. Existing successful payment and
-- ledger rows remain intact for audit/reconciliation; this does not issue refunds.
UPDATE identity.subscriptions s
SET status = 'EXPIRED', updated_at = public.app_now()
FROM identity.subscription_packages pkg
WHERE pkg.id = s.package_id
  AND s.status = 'ACTIVE'
  AND s.kiosk_id IS NULL
  AND pkg.billing_cycle = 'MONTHLY';

UPDATE identity.subscription_packages
SET status = 'INACTIVE', updated_at = public.app_now()
WHERE billing_cycle = 'MONTHLY' AND status <> 'INACTIVE';

-- A seller whose only entitlement was a monthly package must no longer pass
-- the legacy timestamp fallback. Annual kiosk leases are recalculated below.
UPDATE identity.sellers s
SET package_expires_at = public.app_now()::date,
    package_expires_at_ts = public.app_now(),
    updated_at = public.app_now()
WHERE s.package_id IN (
    SELECT id FROM identity.subscription_packages WHERE billing_cycle = 'MONTHLY'
  )
  AND s.status IN ('ACTIVE', 'PAST_DUE')
  AND NOT EXISTS (
    SELECT 1 FROM identity.subscriptions sub
    WHERE sub.seller_id = s.id AND sub.kiosk_id IS NOT NULL
  );

-- Re-evaluate sellers from the remaining annual entitlements.
DO $$
DECLARE r record;
BEGIN
  FOR r IN
    SELECT id FROM identity.sellers
    WHERE status IN ('APPROVED', 'ACTIVE', 'PAST_DUE')
    ORDER BY id
  LOOP
    PERFORM flow.refresh_seller_package(r.id);
  END LOOP;
END $$;

-- Monthly periods no longer need seller-wide overlap protection; annual
-- kiosk-specific exclusion remains in force.
ALTER TABLE identity.subscriptions
  DROP CONSTRAINT IF EXISTS subscriptions_monthly_seller_period_excl;

COMMIT;
