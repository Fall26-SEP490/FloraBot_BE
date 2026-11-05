-- Avoid the PL/pgSQL row variable a colliding with the SQL table alias.
-- Preserve release eligibility, row locks, HELD protection, state transitions and audit.
BEGIN;

CREATE OR REPLACE FUNCTION flow.release_kiosk_slots(p_kiosk uuid, p_seller uuid, p_reason text)
RETURNS integer LANGUAGE plpgsql AS $$
DECLARE
  a kiosk_ops.slot_assignments;
  s kiosk_ops.slots;
  v_count integer := 0;
BEGIN
  FOR a IN
    SELECT assignment.* FROM kiosk_ops.slot_assignments AS assignment
    JOIN kiosk_ops.slots sl ON sl.id = assignment.slot_id
    WHERE sl.kiosk_id = p_kiosk AND assignment.seller_id = p_seller AND assignment.status = 'ACTIVE'
    ORDER BY assignment.slot_id FOR UPDATE OF assignment
  LOOP
    SELECT * INTO s FROM kiosk_ops.slots WHERE id = a.slot_id FOR UPDATE;
    CONTINUE WHEN s.status = 'HELD';

    UPDATE kiosk_ops.slot_assignments
    SET status = 'RELEASED', released_at = public.app_now(), release_reason = p_reason
    WHERE id = a.id;

    IF s.status = 'RENTED_EMPTY' THEN
      UPDATE kiosk_ops.slots SET status = 'FREE', current_seller_id = NULL,
        row_version = row_version + 1, updated_at = public.app_now() WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_RELEASED', 'slots', s.id,
        jsonb_build_object('seller', p_seller, 'kiosk', p_kiosk, 'reason', p_reason));
    ELSIF s.status = 'STOCKED' THEN
      UPDATE kiosk_ops.slots SET status = 'PENDING_RELEASE',
        row_version = row_version + 1, updated_at = public.app_now() WHERE id = s.id;
      PERFORM flow.audit(NULL, 'SLOT_PENDING_RELEASE', 'slots', s.id,
        jsonb_build_object('seller', p_seller, 'kiosk', p_kiosk, 'reason', p_reason));
    END IF;
    v_count := v_count + 1;
  END LOOP;
  RETURN v_count;
END $$;

COMMIT;
