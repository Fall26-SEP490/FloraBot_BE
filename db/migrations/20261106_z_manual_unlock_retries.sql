-- Migration: manual unlock retries persistent requests and dispatch snapshot hardening
BEGIN;

-- 1. Snapshot hardening: add nullable snapshot columns to existing mqtt_dispatches (legacy rows NULL)
ALTER TABLE kiosk_ops.mqtt_dispatches
  ADD COLUMN IF NOT EXISTS hardware_id_snapshot text,
  ADD COLUMN IF NOT EXISTS relay_channel_snapshot integer,
  ADD COLUMN IF NOT EXISTS slot_id_snapshot uuid;

-- 2. Persistent manual unlock retry requests
CREATE TABLE IF NOT EXISTS kiosk_ops.manual_unlock_retries (
  request_id uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  parent_cmd_id uuid NOT NULL UNIQUE REFERENCES kiosk_ops.unlock_tokens(cmd_id) ON DELETE CASCADE,
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id) ON DELETE CASCADE,
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id) ON DELETE CASCADE,
  order_id uuid REFERENCES ordering.orders(id) ON DELETE SET NULL,
  session_id uuid,
  actor_kind text NOT NULL CHECK (actor_kind IN ('SELLER', 'CUSTOMER', 'GUEST')),
  actor_id uuid NOT NULL,
  hardware_id_snapshot text NOT NULL,
  relay_channel_snapshot integer NOT NULL,
  slot_id_snapshot uuid NOT NULL,
  requested_at bigint NOT NULL CHECK (requested_at > 0),
  expires_at bigint NOT NULL CHECK (expires_at > requested_at AND expires_at - requested_at <= 30),
  state text NOT NULL DEFAULT 'PENDING' CHECK (state IN ('PENDING', 'PUBLISHED', 'CANCELLED', 'EXPIRED')),
  delivery_id uuid,
  next_attempt_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  published_at timestamptz,
  created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);


CREATE INDEX IF NOT EXISTS idx_manual_unlock_retries_pending
  ON kiosk_ops.manual_unlock_retries(next_attempt_at)
  WHERE state = 'PENDING';

CREATE INDEX IF NOT EXISTS idx_manual_unlock_retries_kiosk_slot
  ON kiosk_ops.manual_unlock_retries(kiosk_id, slot_id);

CREATE INDEX IF NOT EXISTS idx_manual_unlock_retries_order
  ON kiosk_ops.manual_unlock_retries(order_id)
  WHERE order_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS idx_manual_unlock_retries_session
  ON kiosk_ops.manual_unlock_retries(session_id)
  WHERE session_id IS NOT NULL;

CREATE OR REPLACE FUNCTION flow.check_active_admin(p_admin uuid)
RETURNS integer
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM identity.users
  WHERE id = p_admin AND role = 'ADMIN' AND status = 'ACTIVE'
  FOR SHARE;
  IF FOUND THEN
    RETURN 1;
  ELSE
    RETURN NULL;
  END IF;
END;
$$;

CREATE OR REPLACE FUNCTION flow.load_manual_unlock_retry_candidate(
  p_actor_kind text,
  p_actor_id uuid,
  p_kiosk_id uuid,
  p_slot_id uuid,
  p_cmd_id uuid DEFAULT NULL,
  p_order_id uuid DEFAULT NULL,
  p_session_id uuid DEFAULT NULL,
  p_guest_tracking_token text DEFAULT NULL,
  p_skip_locked boolean DEFAULT false
)
RETURNS TABLE (
  cmd_id uuid,
  hardware_id text,
  relay_channel smallint,
  order_id uuid,
  session_id uuid,
  parent_expires_epoch bigint,
  is_parent_acked boolean,
  is_door_untouched boolean,
  is_kiosk_online_fresh boolean,
  hardware_id_snapshot text,
  relay_channel_snapshot integer,
  slot_id_snapshot uuid,
  is_snapshots_matching boolean,
  is_shared_cap_valid boolean,
  is_seller_cap_full boolean,
  is_seller_state_valid boolean,
  is_customer_state_valid boolean,
  ack_occurred_at timestamptz,
  has_pending_or_door_event boolean
)
LANGUAGE plpgsql AS $$
BEGIN
  IF p_skip_locked THEN
    RETURN QUERY
    SELECT
      t.cmd_id,
      k.hardware_id,
      sl.relay_channel,
      t.order_id,
      ms.id AS session_id,
      floor(extract(epoch FROM LEAST(t.expires_at, ms.expires_at, sl.hold_until, wr.pickup_before)))::bigint AS parent_expires_epoch,
      (t.status = 'ACKED') AS is_parent_acked,
      (t.door_opened_at IS NULL AND t.door_closed_at IS NULL) AS is_door_untouched,
      COALESCE(k.status = 'ONLINE'
          AND k.last_heartbeat_at > CURRENT_TIMESTAMP - make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
          AND k.last_heartbeat_at <= CURRENT_TIMESTAMP + interval '5 seconds', false) AS is_kiosk_online_fresh,
      d.hardware_id_snapshot,
      d.relay_channel_snapshot,
      d.slot_id_snapshot,
      (d.hardware_id_snapshot = k.hardware_id AND d.relay_channel_snapshot = sl.relay_channel AND d.slot_id_snapshot = sl.id) AS is_snapshots_matching,
      (cap.door_verified IS TRUE AND cap.supports_manual_retry IS TRUE AND cap.manual_retry_hardware_id = k.hardware_id AND cap.relay_channel = sl.relay_channel) AS is_shared_cap_valid,
      (cap.enabled IS TRUE AND cap.presence_verified IS TRUE) AS is_seller_cap_full,
      (ms.id IS NOT NULL AND ms.stage IN ('AwaitRemoval', 'AwaitLoading') AND ms.expires_at > CURRENT_TIMESTAMP
          AND sl.zone = 'WALK_IN' AND sl.status = 'LOCKED'
          AND sl.hold_order_id IS NULL AND sl.hold_until IS NULL
          AND EXISTS(SELECT 1 FROM kiosk_ops.slot_assignments sa WHERE sa.slot_id = sl.id AND sa.seller_id = ms.seller_id AND sa.status = 'ACTIVE')
          AND NOT EXISTS(SELECT 1 FROM ordering.orders co JOIN ordering.order_items coi ON coi.order_id = co.id WHERE coi.slot_id = sl.id AND co.status IN ('AWAITING_PAYMENT','PAID','DISPENSING') AND coi.line_status = 'ACTIVE')
          AND (ms.stage <> 'AwaitLoading' OR (flow.slot_sellable(sl.id, ms.seller_id) AND EXISTS(SELECT 1 FROM catalog.flower_products fp WHERE fp.id = ms.new_product_id AND fp.seller_id = ms.seller_id AND fp.status = 'ACTIVE')))
      ) AS is_seller_state_valid,
      (o.id IS NOT NULL AND o.status IN ('PAID', 'DISPENSING')
          AND sl.status = 'HELD' AND sl.hold_order_id = o.id AND sl.hold_until > CURRENT_TIMESTAMP
          AND k.seller_id = o.seller_id AND sl.current_seller_id = o.seller_id
          AND EXISTS(SELECT 1 FROM ordering.order_items oi WHERE oi.order_id = o.id AND oi.slot_id = sl.id AND oi.item_type = 'BOUQUET' AND oi.line_status = 'ACTIVE')
          AND NOT EXISTS(SELECT 1 FROM payment.payments p WHERE p.order_id = o.id AND p.kind = 'REFUND' AND p.status IN ('PENDING', 'SUCCEEDED'))
          AND (wr.order_id IS NULL OR wr.pickup_before > CURRENT_TIMESTAMP)
      ) AS is_customer_state_valid,
      (SELECT i.occurred_at FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id = t.cmd_id AND i.kiosk_id = k.id AND i.event = 'ACK' AND i.disposition = 'APPLIED' AND i.occurred_at >= date_trunc('second', t.issued_at) ORDER BY i.occurred_at DESC LIMIT 1) AS ack_occurred_at,
      EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id = t.cmd_id AND (i.event IN ('OPENED', 'CLOSED') OR i.disposition = 'PENDING')) AS has_pending_or_door_event
    FROM kiosk_ops.unlock_tokens t
    JOIN kiosk_ops.kiosks k ON k.id = t.kiosk_id
    JOIN kiosk_ops.slots sl ON sl.id = t.slot_id AND sl.kiosk_id = k.id
    LEFT JOIN kiosk_ops.slot_sensor_capabilities cap ON cap.slot_id = sl.id AND cap.kiosk_id = k.id
    LEFT JOIN kiosk_ops.mqtt_dispatches d ON d.cmd_id = t.cmd_id
    LEFT JOIN ordering.orders o ON o.id = t.order_id
    LEFT JOIN ordering.web_requests wr ON wr.order_id = o.id
    LEFT JOIN kiosk_ops.slot_maintenance_sessions ms ON ms.id = p_session_id
    WHERE t.kiosk_id = p_kiosk_id AND t.slot_id = p_slot_id
      AND (p_cmd_id IS NULL OR t.cmd_id = p_cmd_id)
      AND (p_session_id IS NULL OR (ms.id = p_session_id AND t.cmd_id = ms.current_cmd_id))
      AND (
          (p_actor_kind = 'SELLER' AND t.purpose = 'SELLER_ACCESS' AND t.issued_to_user_id = p_actor_id
              AND ms.actor_id = p_actor_id
              AND ms.kiosk_id = p_kiosk_id AND ms.slot_id = p_slot_id
              AND ms.seller_id = k.seller_id AND ms.seller_id = sl.current_seller_id
              AND EXISTS(SELECT 1 FROM identity.users u WHERE u.id = p_actor_id AND u.role = 'SELLER' AND u.status = 'ACTIVE' AND u.seller_id = ms.seller_id)
              AND EXISTS(SELECT 1 FROM identity.sellers s WHERE s.id = ms.seller_id AND (s.status = 'ACTIVE' OR (s.status = 'PAST_DUE' AND ms.action = 'REMOVE' AND ms.stage = 'AwaitRemoval' AND ms.intended_stage = 'AwaitRemoval'))))
          OR
          (p_actor_kind = 'CUSTOMER' AND t.purpose = 'CUSTOMER_PICKUP' AND t.order_id = p_order_id
              AND o.id = p_order_id AND o.kiosk_id = p_kiosk_id AND o.customer_id = p_actor_id
              AND EXISTS(SELECT 1 FROM identity.users u WHERE u.id = p_actor_id AND u.role = 'CUSTOMER' AND u.status = 'ACTIVE'))
          OR
          (p_actor_kind = 'GUEST' AND t.purpose = 'CUSTOMER_PICKUP' AND t.order_id = p_order_id
              AND o.id = p_order_id AND o.kiosk_id = p_kiosk_id AND o.customer_id IS NULL
              AND wr.order_id IS NULL AND p_actor_id = p_kiosk_id
              AND (p_guest_tracking_token IS NULL OR o.tracking_token = p_guest_tracking_token))
      )
    FOR NO KEY UPDATE OF t SKIP LOCKED;
  ELSE
    RETURN QUERY
    SELECT
      t.cmd_id,
      k.hardware_id,
      sl.relay_channel,
      t.order_id,
      ms.id AS session_id,
      floor(extract(epoch FROM LEAST(t.expires_at, ms.expires_at, sl.hold_until, wr.pickup_before)))::bigint AS parent_expires_epoch,
      (t.status = 'ACKED') AS is_parent_acked,
      (t.door_opened_at IS NULL AND t.door_closed_at IS NULL) AS is_door_untouched,
      COALESCE(k.status = 'ONLINE'
          AND k.last_heartbeat_at > CURRENT_TIMESTAMP - make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
          AND k.last_heartbeat_at <= CURRENT_TIMESTAMP + interval '5 seconds', false) AS is_kiosk_online_fresh,
      d.hardware_id_snapshot,
      d.relay_channel_snapshot,
      d.slot_id_snapshot,
      (d.hardware_id_snapshot = k.hardware_id AND d.relay_channel_snapshot = sl.relay_channel AND d.slot_id_snapshot = sl.id) AS is_snapshots_matching,
      (cap.door_verified IS TRUE AND cap.supports_manual_retry IS TRUE AND cap.manual_retry_hardware_id = k.hardware_id AND cap.relay_channel = sl.relay_channel) AS is_shared_cap_valid,
      (cap.enabled IS TRUE AND cap.presence_verified IS TRUE) AS is_seller_cap_full,
      (ms.id IS NOT NULL AND ms.stage IN ('AwaitRemoval', 'AwaitLoading') AND ms.expires_at > CURRENT_TIMESTAMP
          AND sl.zone = 'WALK_IN' AND sl.status = 'LOCKED'
          AND sl.hold_order_id IS NULL AND sl.hold_until IS NULL
          AND EXISTS(SELECT 1 FROM kiosk_ops.slot_assignments sa WHERE sa.slot_id = sl.id AND sa.seller_id = ms.seller_id AND sa.status = 'ACTIVE')
          AND NOT EXISTS(SELECT 1 FROM ordering.orders co JOIN ordering.order_items coi ON coi.order_id = co.id WHERE coi.slot_id = sl.id AND co.status IN ('AWAITING_PAYMENT','PAID','DISPENSING') AND coi.line_status = 'ACTIVE')
          AND (ms.stage <> 'AwaitLoading' OR (flow.slot_sellable(sl.id, ms.seller_id) AND EXISTS(SELECT 1 FROM catalog.flower_products fp WHERE fp.id = ms.new_product_id AND fp.seller_id = ms.seller_id AND fp.status = 'ACTIVE')))
      ) AS is_seller_state_valid,
      (o.id IS NOT NULL AND o.status IN ('PAID', 'DISPENSING')
          AND sl.status = 'HELD' AND sl.hold_order_id = o.id AND sl.hold_until > CURRENT_TIMESTAMP
          AND k.seller_id = o.seller_id AND sl.current_seller_id = o.seller_id
          AND EXISTS(SELECT 1 FROM ordering.order_items oi WHERE oi.order_id = o.id AND oi.slot_id = sl.id AND oi.item_type = 'BOUQUET' AND oi.line_status = 'ACTIVE')
          AND NOT EXISTS(SELECT 1 FROM payment.payments p WHERE p.order_id = o.id AND p.kind = 'REFUND' AND p.status IN ('PENDING', 'SUCCEEDED'))
          AND (wr.order_id IS NULL OR wr.pickup_before > CURRENT_TIMESTAMP)
      ) AS is_customer_state_valid,
      (SELECT i.occurred_at FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id = t.cmd_id AND i.kiosk_id = k.id AND i.event = 'ACK' AND i.disposition = 'APPLIED' AND i.occurred_at >= date_trunc('second', t.issued_at) ORDER BY i.occurred_at DESC LIMIT 1) AS ack_occurred_at,
      EXISTS(SELECT 1 FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id = t.cmd_id AND (i.event IN ('OPENED', 'CLOSED') OR i.disposition = 'PENDING')) AS has_pending_or_door_event
    FROM kiosk_ops.unlock_tokens t
    JOIN kiosk_ops.kiosks k ON k.id = t.kiosk_id
    JOIN kiosk_ops.slots sl ON sl.id = t.slot_id AND sl.kiosk_id = k.id
    LEFT JOIN kiosk_ops.slot_sensor_capabilities cap ON cap.slot_id = sl.id AND cap.kiosk_id = k.id
    LEFT JOIN kiosk_ops.mqtt_dispatches d ON d.cmd_id = t.cmd_id
    LEFT JOIN ordering.orders o ON o.id = t.order_id
    LEFT JOIN ordering.web_requests wr ON wr.order_id = o.id
    LEFT JOIN kiosk_ops.slot_maintenance_sessions ms ON ms.id = p_session_id
    WHERE t.kiosk_id = p_kiosk_id AND t.slot_id = p_slot_id
      AND (p_cmd_id IS NULL OR t.cmd_id = p_cmd_id)
      AND (p_session_id IS NULL OR (ms.id = p_session_id AND t.cmd_id = ms.current_cmd_id))
      AND (
          (p_actor_kind = 'SELLER' AND t.purpose = 'SELLER_ACCESS' AND t.issued_to_user_id = p_actor_id
              AND ms.actor_id = p_actor_id
              AND ms.kiosk_id = p_kiosk_id AND ms.slot_id = p_slot_id
              AND ms.seller_id = k.seller_id AND ms.seller_id = sl.current_seller_id
              AND EXISTS(SELECT 1 FROM identity.users u WHERE u.id = p_actor_id AND u.role = 'SELLER' AND u.status = 'ACTIVE' AND u.seller_id = ms.seller_id)
              AND EXISTS(SELECT 1 FROM identity.sellers s WHERE s.id = ms.seller_id AND (s.status = 'ACTIVE' OR (s.status = 'PAST_DUE' AND ms.action = 'REMOVE' AND ms.stage = 'AwaitRemoval' AND ms.intended_stage = 'AwaitRemoval'))))
          OR
          (p_actor_kind = 'CUSTOMER' AND t.purpose = 'CUSTOMER_PICKUP' AND t.order_id = p_order_id
              AND o.id = p_order_id AND o.kiosk_id = p_kiosk_id AND o.customer_id = p_actor_id
              AND EXISTS(SELECT 1 FROM identity.users u WHERE u.id = p_actor_id AND u.role = 'CUSTOMER' AND u.status = 'ACTIVE'))
          OR
          (p_actor_kind = 'GUEST' AND t.purpose = 'CUSTOMER_PICKUP' AND t.order_id = p_order_id
              AND o.id = p_order_id AND o.kiosk_id = p_kiosk_id AND o.customer_id IS NULL
              AND wr.order_id IS NULL AND p_actor_id = p_kiosk_id
              AND (p_guest_tracking_token IS NULL OR o.tracking_token = p_guest_tracking_token))
      )
    FOR NO KEY UPDATE OF t;
  END IF;
END;
$$;

COMMIT;
