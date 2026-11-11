BEGIN;

CREATE TABLE kiosk_ops.staff_preorder_tasks (
  task_id uuid PRIMARY KEY REFERENCES kiosk_ops.staff_tasks(id) ON DELETE CASCADE,
  request_id uuid NOT NULL REFERENCES ordering.web_requests(id),
  slot_id uuid NOT NULL REFERENCES kiosk_ops.slots(id)
);

CREATE INDEX staff_preorder_tasks_request_idx ON kiosk_ops.staff_preorder_tasks(request_id);
CREATE INDEX staff_preorder_tasks_slot_idx ON kiosk_ops.staff_preorder_tasks(slot_id);

CREATE FUNCTION flow.assign_preorder_staff_task(
  p_id uuid, p_actor uuid, p_staff uuid, p_request uuid, p_slot uuid, p_instructions text
)
RETURNS uuid LANGUAGE plpgsql AS $$
DECLARE
  v_seller uuid;
  request_row ordering.web_requests;
  order_row ordering.orders;
  slot_row kiosk_ops.slots;
  existing kiosk_ops.staff_tasks;
BEGIN
  SELECT seller_id INTO v_seller
  FROM identity.users
  WHERE id = p_actor AND role = 'SELLER' AND status = 'ACTIVE'
  FOR SHARE;
  IF v_seller IS NULL THEN
    RAISE EXCEPTION 'Active seller required' USING ERRCODE = '42501';
  END IF;

  IF p_id IS NULL OR p_request IS NULL OR p_slot IS NULL OR p_staff IS NULL THEN
    RAISE EXCEPTION 'Task, request, slot and staff required' USING ERRCODE = '22023';
  END IF;

  PERFORM pg_advisory_xact_lock(hashtextextended('staff-task:' || p_id::text, 0));
  SELECT * INTO existing FROM kiosk_ops.staff_tasks WHERE id = p_id FOR SHARE;
  IF FOUND THEN
    IF existing.assigned_by = p_actor AND existing.assignee_id = p_staff
       AND existing.seller_id = v_seller AND existing.instructions = btrim(p_instructions)
       AND EXISTS (
         SELECT 1 FROM kiosk_ops.staff_preorder_tasks
         WHERE task_id = p_id AND request_id = p_request AND slot_id = p_slot
       ) THEN
      RETURN p_id;
    END IF;
    RAISE EXCEPTION 'Task ID already used' USING ERRCODE = '40001';
  END IF;

  SELECT * INTO request_row FROM ordering.web_requests
  WHERE id = p_request AND seller_id = v_seller FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Preorder not found' USING ERRCODE = 'P0002';
  END IF;

  SELECT * INTO order_row FROM ordering.orders WHERE id = request_row.order_id FOR UPDATE;
  IF request_row.kind IS DISTINCT FROM 'CUSTOM' OR request_row.state IS DISTINCT FROM 'ORDERED'
     OR order_row.status IS DISTINCT FROM 'PAID' OR request_row.pickup_before <= public.app_now()
     OR EXISTS (
       SELECT 1 FROM ordering.order_items WHERE order_id = order_row.id AND item_type = 'BOUQUET'
     ) THEN
    RAISE EXCEPTION 'Paid, unfulfilled preorder required' USING ERRCODE = '22023';
  END IF;

  SELECT * INTO slot_row FROM kiosk_ops.slots
  WHERE id = p_slot AND kiosk_id = request_row.kiosk_id AND current_seller_id = v_seller FOR UPDATE;
  IF NOT FOUND THEN
    RAISE EXCEPTION 'Preorder slot not found' USING ERRCODE = 'P0002';
  END IF;

  IF slot_row.zone IS DISTINCT FROM 'PREORDER' OR slot_row.status IS DISTINCT FROM 'RENTED_EMPTY'
     OR slot_row.bouquet_id IS NOT NULL OR slot_row.hold_order_id IS NOT NULL
     OR slot_row.hold_until IS NOT NULL
     OR NOT flow.slot_sellable(p_slot, v_seller) THEN
    RAISE EXCEPTION 'Empty PREORDER slot in an active lease required' USING ERRCODE = '22023';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.preorder_loading_sessions loading
    WHERE (loading.request_id = p_request OR loading.slot_id = p_slot)
      AND loading.completed_bouquet_id IS NULL AND loading.expires_at > clock_timestamp()
  ) THEN
    RAISE EXCEPTION 'Preorder or slot is being loaded' USING ERRCODE = '40001';
  END IF;

  IF EXISTS (
    SELECT 1 FROM kiosk_ops.staff_preorder_tasks scope
    JOIN kiosk_ops.staff_tasks task ON task.id = scope.task_id
    WHERE (scope.request_id = p_request OR scope.slot_id = p_slot)
      AND task.status IN ('ASSIGNED', 'IN_PROGRESS', 'SUBMITTED')
  ) THEN
    RAISE EXCEPTION 'Preorder or slot already assigned' USING ERRCODE = '40001';
  END IF;

  PERFORM flow.assign_staff_task(
    p_id, p_actor, p_staff, 'DELIVERY', request_row.kiosk_id, v_seller, NULL, p_instructions
  );
  INSERT INTO kiosk_ops.staff_preorder_tasks (task_id, request_id, slot_id)
  VALUES (p_id, p_request, p_slot);
  RETURN p_id;
END;
$$;

COMMIT;
