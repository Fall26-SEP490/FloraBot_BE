BEGIN;

CREATE FUNCTION flow.preorder_load_scope(p_request uuid,p_slot uuid,p_actor uuid,p_task uuid)
RETURNS TABLE(kiosk_id uuid,seller_id uuid,actor_role text,relay_channel integer,email text)
LANGUAGE sql STABLE AS $$
  SELECT k.id,r.seller_id,u.role::text,s.relay_channel::integer,u.email::text
  FROM ordering.web_requests r
  JOIN ordering.orders o ON o.id=r.order_id
  JOIN identity.sellers shop ON shop.id=r.seller_id AND shop.status='ACTIVE'
  JOIN identity.users u ON u.id=p_actor AND u.seller_id=r.seller_id AND u.status='ACTIVE'
  JOIN kiosk_ops.kiosks k ON k.id=r.kiosk_id AND k.seller_id=r.seller_id
  JOIN kiosk_ops.slots s ON s.id=p_slot AND s.kiosk_id=k.id AND s.current_seller_id=r.seller_id
  JOIN kiosk_ops.slot_sensor_capabilities cap ON cap.slot_id=s.id AND cap.kiosk_id=k.id
      AND cap.relay_channel=s.relay_channel
  WHERE r.id=p_request AND r.kind='CUSTOM' AND r.state='ORDERED' AND o.status='PAID'
    AND r.pickup_before>CURRENT_TIMESTAMP
    AND NOT EXISTS (SELECT 1 FROM ordering.order_items i WHERE i.order_id=o.id AND i.item_type='BOUQUET')
    AND k.status='ONLINE'
    AND k.last_heartbeat_at>CURRENT_TIMESTAMP-make_interval(secs=>flow.cfg('heartbeat_offline_seconds'))
    AND NOT flow.is_kiosk_terminating(k.id)
    AND s.zone='PREORDER' AND s.status='RENTED_EMPTY'
    AND s.bouquet_id IS NULL AND s.hold_order_id IS NULL AND s.hold_until IS NULL
    AND flow.slot_sellable(s.id,r.seller_id)
    AND cap.enabled AND cap.presence_verified
    AND (
      (u.role='SELLER' AND p_task IS NULL AND NOT EXISTS (
        SELECT 1 FROM kiosk_ops.staff_preorder_tasks a
        JOIN kiosk_ops.staff_tasks t ON t.id=a.task_id
        WHERE (a.request_id=r.id OR a.slot_id=s.id)
          AND t.status IN ('ASSIGNED','IN_PROGRESS','SUBMITTED')
      ))
      OR (u.role='SELLER_STAFF' AND p_task IS NOT NULL AND EXISTS (
        SELECT 1 FROM kiosk_ops.staff_preorder_tasks a
        JOIN kiosk_ops.staff_tasks t ON t.id=a.task_id
        WHERE a.task_id=p_task AND a.request_id=r.id AND a.slot_id=s.id
          AND t.assignee_id=u.id AND t.seller_id=r.seller_id AND t.kiosk_id=k.id
          AND t.kind='DELIVERY' AND t.status='IN_PROGRESS'
      ))
    );
$$;

COMMIT;
