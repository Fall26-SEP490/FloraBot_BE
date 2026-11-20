BEGIN;
-- Lock subscription then payment, matching flow.subscription_paid so a webhook and
-- cancellation cannot both activate/cancel the same renewal.
CREATE OR REPLACE FUNCTION flow.cancel_kiosk_renewal(p_actor uuid, p_seller uuid, p_sub uuid)
RETURNS void LANGUAGE plpgsql AS $$
DECLARE v_sub identity.subscriptions; v_payment payment.payments;
BEGIN
  SELECT * INTO v_sub FROM identity.subscriptions
    WHERE id=p_sub AND seller_id=p_seller AND kiosk_id IS NOT NULL FOR UPDATE;
  IF NOT FOUND THEN RAISE EXCEPTION 'Renewal not found' USING ERRCODE='42501'; END IF;
  SELECT * INTO v_payment FROM payment.payments WHERE id=v_sub.payment_id FOR UPDATE;
  -- Recheck the actor after acquiring payment locks. Do not lock the seller
  -- first: subscription_paid refreshes that seller after locking this payment.
  PERFORM 1 FROM identity.users u JOIN identity.sellers s ON s.id=u.seller_id
    WHERE u.id=p_actor AND u.role='SELLER' AND u.status='ACTIVE'
      AND u.seller_id=p_seller AND s.status IN ('ACTIVE','PAST_DUE') FOR SHARE OF u,s;
  IF NOT FOUND THEN RAISE EXCEPTION 'Not authorized' USING ERRCODE='42501'; END IF;
  IF v_sub.status='CANCELLED' THEN RETURN; END IF;
  IF v_sub.status<>'PENDING_PAYMENT' THEN
    RAISE EXCEPTION 'Renewal is no longer awaiting payment' USING ERRCODE='55000';
  END IF;
  IF v_payment.id IS NULL OR v_payment.subscription_id IS DISTINCT FROM p_sub OR v_payment.kind<>'CHARGE'
    OR v_payment.purpose<>'SUBSCRIPTION' OR v_payment.status='SUCCEEDED' THEN
    RAISE EXCEPTION 'Payment cannot be cancelled' USING ERRCODE='55000';
  END IF;
  UPDATE payment.payments SET status='CANCELLED' WHERE id=v_payment.id AND status='PENDING';
  UPDATE identity.subscriptions SET status='CANCELLED',updated_at=public.app_now() WHERE id=p_sub;
  PERFORM flow.audit(p_actor,'KIOSK_RENEWAL_CANCELLED','subscriptions',p_sub,
    jsonb_build_object('kiosk_id',v_sub.kiosk_id,'payment_id',v_payment.id));
  -- Existing flow.subscription_paid records a late charge and requests a refund
  -- for CANCELLED renewals; it must never activate the cancelled period.
END $$;
COMMIT;
