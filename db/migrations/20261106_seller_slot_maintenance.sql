BEGIN;

-- Explicit per-slot sensor capability registration
-- Only explicitly verified slots with installed LM393 presence and door sensors are enrolled.
-- Enabled is false by default; manual retry is unsupported false by default in this increment.
CREATE TABLE IF NOT EXISTS kiosk_ops.slot_sensor_capabilities (
  slot_id uuid PRIMARY KEY REFERENCES kiosk_ops.slots(id) ON DELETE CASCADE,
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id) ON DELETE CASCADE,
  relay_channel integer NOT NULL CHECK (relay_channel >= 0 AND relay_channel <= 3),
  enabled boolean NOT NULL DEFAULT false,
  door_verified boolean NOT NULL DEFAULT false,
  presence_verified boolean NOT NULL DEFAULT false,
  supports_manual_retry boolean NOT NULL DEFAULT false,
  manual_retry_hardware_id text,
  enrolled_by uuid REFERENCES identity.users(id),
  enrolled_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Maintenance sessions for WALK_IN slots
-- Binds client idempotency ID, immutable inputs, current phase command, and audit trail.
CREATE TABLE IF NOT EXISTS kiosk_ops.slot_maintenance_sessions (
  id uuid PRIMARY KEY,
  kiosk_id uuid NOT NULL REFERENCES kiosk_ops.kiosks(id),
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id),
  seller_id uuid NOT NULL REFERENCES identity.sellers(id),
  actor_id uuid NOT NULL REFERENCES identity.users(id),
  action text NOT NULL CHECK (action IN ('LOAD','REMOVE','REPLACE')),
  stage text NOT NULL CHECK (stage IN ('AwaitRemoval','AwaitLoading','NeedsInspection','Completed')),
  intended_stage text NOT NULL CHECK (intended_stage IN ('AwaitRemoval','AwaitLoading','Completed')),
  substate text CHECK (substate IN ('Initial','RemovalDoorOpened','RemovalClosedEmptyConfirmed','LoadingDoorOpened','Completed')),
  previous_slot_status text NOT NULL,
  old_bouquet_id uuid REFERENCES kiosk_ops.bouquets(id),
  new_product_id uuid REFERENCES catalog.flower_products(id),
  new_bouquet_id uuid REFERENCES kiosk_ops.bouquets(id),
  reason text NOT NULL,
  phase_started_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  expires_at timestamptz NOT NULL,
  current_cmd_id uuid REFERENCES kiosk_ops.unlock_tokens(cmd_id),
  latest_inspection_at timestamptz,
  latest_inspection_observed_at bigint,
  latest_inspection_reason text,
  inspection_open_count integer NOT NULL DEFAULT 0,
  last_inspection_open_at timestamptz,
  row_version integer NOT NULL DEFAULT 1,
  created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
  updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- Only one active non-terminal session permitted per slot at a time (including NeedsInspection)
CREATE UNIQUE INDEX IF NOT EXISTS slot_maintenance_active_slot_idx
  ON kiosk_ops.slot_maintenance_sessions(slot_id)
  WHERE stage <> 'Completed';

CREATE INDEX IF NOT EXISTS slot_maintenance_kiosk_slot_idx
  ON kiosk_ops.slot_maintenance_sessions(kiosk_id, slot_id);

CREATE INDEX IF NOT EXISTS slot_maintenance_cmd_idx
  ON kiosk_ops.slot_maintenance_sessions(current_cmd_id)
  WHERE current_cmd_id IS NOT NULL;

CREATE OR REPLACE FUNCTION flow.seller_slot_maintenance_auth(
  p_actor uuid,
  p_seller uuid,
  p_slot uuid
)
RETURNS TABLE (
  actor_id uuid,
  actor_status text,
  actor_role text,
  actor_seller_id uuid,
  seller_status text,
  package_active boolean,
  assignment_active boolean
)
LANGUAGE plpgsql AS $$
BEGIN
  RETURN QUERY
  SELECT u.id, u.status, u.role, u.seller_id, sel.status,
         EXISTS(SELECT 1 FROM identity.sellers s WHERE s.id = p_seller AND s.package_expires_at IS NOT NULL AND s.package_expires_at > CURRENT_DATE),
         EXISTS(SELECT 1 FROM kiosk_ops.slot_assignments a WHERE a.slot_id = p_slot AND a.seller_id = p_seller AND a.status = 'ACTIVE')
  FROM identity.users u
  JOIN identity.sellers sel ON sel.id = u.seller_id
  WHERE u.id = p_actor AND u.seller_id = p_seller
  FOR SHARE OF u, sel;
END;
$$;

CREATE OR REPLACE FUNCTION flow.check_bouquet_active_order(p_bouquet uuid)
RETURNS integer
LANGUAGE sql STABLE AS $$
  SELECT 1 FROM ordering.order_items i
  JOIN ordering.orders o ON o.id = i.order_id
  WHERE i.bouquet_id = p_bouquet AND o.status NOT IN ('EXPIRED','CANCELLED','DISPENSE_FAILED','REFUNDED')
  LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION flow.seller_flower_product_check(p_product uuid, p_seller uuid)
RETURNS TABLE (
  is_active boolean,
  is_owned boolean
)
LANGUAGE sql STABLE AS $$
  SELECT status = 'ACTIVE' AS is_active, seller_id = p_seller AS is_owned
  FROM catalog.flower_products
  WHERE id = p_product;
$$;

CREATE OR REPLACE FUNCTION flow.check_seller_active_product(p_product uuid, p_seller uuid)
RETURNS integer
LANGUAGE sql STABLE AS $$
  SELECT 1 FROM catalog.flower_products
  WHERE id = p_product AND seller_id = p_seller AND status = 'ACTIVE'
  LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION flow.check_seller_slot_sensor_monitored(p_kiosk uuid, p_slot uuid, p_actor uuid)
RETURNS integer
LANGUAGE sql STABLE AS $$
  SELECT 1 FROM kiosk_ops.slots s
  JOIN kiosk_ops.kiosks k ON k.id = s.kiosk_id
  JOIN identity.users u ON u.id = p_actor AND u.seller_id = k.seller_id AND u.seller_id = s.current_seller_id
  JOIN kiosk_ops.slot_sensor_capabilities c ON c.slot_id = s.id AND c.enabled = true
  WHERE s.id = p_slot AND k.id = p_kiosk
  LIMIT 1;
$$;

CREATE OR REPLACE FUNCTION flow.verify_staff_delivery_task(p_task uuid, p_actor uuid)
RETURNS integer
LANGUAGE plpgsql AS $$
BEGIN
  PERFORM 1 FROM kiosk_ops.staff_tasks t
  JOIN identity.users u ON u.id = p_actor AND u.role = 'SELLER_STAFF' AND u.status = 'ACTIVE' AND u.seller_id = t.seller_id
  WHERE t.id = p_task AND t.assignee_id = p_actor AND t.kind = 'DELIVERY' AND t.status = 'IN_PROGRESS'
  FOR SHARE OF t, u;
  IF FOUND THEN
    RETURN 1;
  ELSE
    RETURN NULL;
  END IF;
END;
$$;

COMMIT;
