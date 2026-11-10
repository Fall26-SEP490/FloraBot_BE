BEGIN;

-- Kiosk removal is archival: keep all orders, payments, device events and slot history.
CREATE OR REPLACE FUNCTION flow.archive_kiosk(
  p_kiosk_id uuid,
  p_actor uuid
) RETURNS boolean
LANGUAGE plpgsql
AS $$
DECLARE
  k kiosk_ops.kiosks%ROWTYPE;
  v_role text;
  v_actor_status text;
  v_now timestamptz := public.app_now();
  v_slot_count integer;
BEGIN
  IF p_kiosk_id IS NULL OR p_actor IS NULL THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_INVALID_ARGUMENT' USING ERRCODE = '22023';
  END IF;

  SELECT role, status INTO v_role, v_actor_status
  FROM identity.users
  WHERE id = p_actor
  FOR SHARE;

  IF NOT FOUND OR v_actor_status <> 'ACTIVE' OR v_role <> 'OPERATIONS_MANAGER' THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_MANAGER_REQUIRED' USING ERRCODE = '42501';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('kiosk-archive:' || p_kiosk_id::text, 0));

  SELECT * INTO k
  FROM kiosk_ops.kiosks
  WHERE id = p_kiosk_id
  FOR UPDATE;

  IF NOT FOUND THEN
    RETURN false;
  END IF;

  -- Idempotent DELETE: already archived kiosks remain archived and are never physically removed.
  IF k.status = 'DISABLED' THEN
    RETURN true;
  END IF;

  IF k.status <> 'OFFLINE' THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_MUST_BE_OFFLINE' USING ERRCODE = 'P0001';
  END IF;

  IF k.seller_id IS NOT NULL THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_SELLER_STILL_ASSIGNED' USING ERRCODE = 'P0001';
  END IF;

  -- Use the same lock order as lease termination: kiosk, slots, then related records.
  PERFORM 1
  FROM kiosk_ops.slots
  WHERE kiosk_id = p_kiosk_id
  ORDER BY id
  FOR UPDATE;

  IF EXISTS (
    SELECT 1
    FROM kiosk_ops.slots s
    WHERE s.kiosk_id = p_kiosk_id
      AND (s.status NOT IN ('FREE', 'RENTED_EMPTY')
        OR s.current_seller_id IS NOT NULL
        OR s.bouquet_id IS NOT NULL
        OR s.hold_until IS NOT NULL
        OR s.hold_order_id IS NOT NULL
        OR EXISTS (
          SELECT 1 FROM kiosk_ops.slot_assignments a
          WHERE a.slot_id = s.id AND a.status = 'ACTIVE'
        ))
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_SLOTS_NOT_EMPTY' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.slot_assignments a
    JOIN kiosk_ops.slots s ON s.id = a.slot_id
    WHERE s.kiosk_id = p_kiosk_id AND a.status = 'ACTIVE'
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_SELLER_STILL_ASSIGNED' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1
    FROM identity.subscriptions sub
    WHERE sub.kiosk_id = p_kiosk_id
      AND (sub.status = 'PENDING_PAYMENT'
        OR (sub.status = 'ACTIVE'
          AND coalesce(sub.period_to_ts, sub.period_to::timestamp AT TIME ZONE 'UTC') > v_now))
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_LEASE_STILL_ACTIVE' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM payment.payments p
    JOIN identity.subscriptions sub ON sub.id = p.subscription_id
    WHERE sub.kiosk_id = p_kiosk_id AND p.kind = 'CHARGE' AND p.status = 'PENDING'
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_PENDING_PAYMENT' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_applications app
    WHERE app.assigned_kiosk_id = p_kiosk_id
      AND app.status IN ('AWAITING_PAYMENT', 'PAID_REVIEW', 'CONFIRMED', 'SCHEDULED', 'INSTALLING', 'AWAITING_HANDOVER')
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_ONBOARDING_IN_PROGRESS' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.kiosk_terminations t
    WHERE t.kiosk_id = p_kiosk_id AND t.status IN ('REQUESTED', 'ACCEPTED')
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_TERMINATION_IN_PROGRESS' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.accessories a
    WHERE a.kiosk_id = p_kiosk_id AND a.stock_quantity > 0
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_ACCESSORY_STOCK_REMAINS' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM ordering.disputes d
    WHERE d.kiosk_id = p_kiosk_id AND d.status = 'OPEN'
  ) OR EXISTS (
    SELECT 1 FROM kiosk_ops.staff_tasks t
    WHERE t.kiosk_id = p_kiosk_id AND t.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED')
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_PENDING_WORK' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM ordering.orders o
    WHERE o.kiosk_id = p_kiosk_id AND o.status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING')
  ) OR EXISTS (
    SELECT 1
    FROM ordering.web_requests w
    LEFT JOIN ordering.orders o ON o.id = w.order_id
    WHERE w.kiosk_id = p_kiosk_id
      AND (w.state IN ('REQUESTED', 'QUOTED')
        OR (w.state = 'ORDERED' AND (o.id IS NULL OR o.status IN ('AWAITING_PAYMENT', 'PAID', 'DISPENSING'))))
  ) OR EXISTS (
    SELECT 1 FROM payment.payments p
    JOIN ordering.orders o ON o.checkout_id = p.checkout_id
    WHERE o.kiosk_id = p_kiosk_id AND p.kind = 'CHARGE' AND p.status = 'PENDING'
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_PENDING_ORDER' USING ERRCODE = 'P0001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.unlock_tokens token
    WHERE token.kiosk_id = p_kiosk_id AND token.status IN ('ISSUED', 'SENT', 'ACKED', 'OPENED')
  ) THEN
    RAISE EXCEPTION 'KIOSK_ARCHIVE_DEVICE_COMMAND_PENDING' USING ERRCODE = 'P0001';
  END IF;

  SELECT count(*) INTO v_slot_count
  FROM kiosk_ops.slots
  WHERE kiosk_id = p_kiosk_id;

  PERFORM flow.audit(
    p_actor,
    'KIOSK_ARCHIVED',
    'kiosks',
    p_kiosk_id,
    jsonb_build_object(
      'code', k.code,
      'name', k.name,
      'previous_status', k.status,
      'last_heartbeat_at', k.last_heartbeat_at,
      'slot_count', v_slot_count
    )
  );

  UPDATE kiosk_ops.kiosks
  SET status = 'DISABLED', updated_at = v_now
  WHERE id = p_kiosk_id;

  RETURN true;
END;
$$;

-- Keep old API binaries safe during a rolling deployment: the legacy DB function
-- now delegates to soft archival and cannot physically delete historical records.
CREATE OR REPLACE FUNCTION flow.delete_unassigned_kiosk(
  p_kiosk_id uuid,
  p_actor uuid
) RETURNS boolean
LANGUAGE sql
AS $$
  SELECT flow.archive_kiosk(p_kiosk_id, p_actor);
$$;

COMMIT;
