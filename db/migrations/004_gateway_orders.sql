-- Gateway identity must survive flow functions replacing raw_payload.
CREATE SEQUENCE IF NOT EXISTS payment.gateway_order_code_seq
  MINVALUE 1 MAXVALUE 9007199254740991 NO CYCLE;
CREATE TABLE IF NOT EXISTS payment.gateway_orders (
  payment_id uuid PRIMARY KEY REFERENCES payment.payments(id),
  order_code bigint NOT NULL UNIQUE DEFAULT nextval('payment.gateway_order_code_seq'),
  checkout_url text,
  created_at timestamptz NOT NULL DEFAULT public.app_now(),
  CHECK (order_code BETWEEN 1 AND 9007199254740991)
);

-- Preserve only actual numeric gateway identities from the old integration.
-- Ambiguous/duplicate existing codes intentionally fail migration for review.
INSERT INTO payment.gateway_orders(payment_id, order_code)
SELECT id, (raw_payload->>'orderCode')::bigint
FROM payment.payments
WHERE kind='CHARGE' AND gateway='PAYOS'
  AND jsonb_typeof(raw_payload->'orderCode') = 'number'
  AND (raw_payload->>'orderCode') ~ '^[0-9]{1,16}$'
  AND (raw_payload->>'orderCode')::numeric BETWEEN 1 AND 9007199254740991
ON CONFLICT (payment_id) DO NOTHING;
SELECT setval('payment.gateway_order_code_seq',
  greatest((SELECT last_value FROM payment.gateway_order_code_seq),
           coalesce((SELECT max(order_code) FROM payment.gateway_orders), 1)), true);

CREATE OR REPLACE FUNCTION flow.reserve_gateway_order(p_payment uuid)
RETURNS bigint LANGUAGE plpgsql AS $$
DECLARE p payment.payments; result bigint;
BEGIN
  SELECT * INTO p FROM payment.payments WHERE id=p_payment FOR UPDATE;
  IF NOT FOUND OR p.kind <> 'CHARGE' OR p.gateway <> 'PAYOS' OR p.status <> 'PENDING' THEN
    RAISE EXCEPTION 'Payment is not an eligible pending payOS charge';
  END IF;
  INSERT INTO payment.gateway_orders(payment_id) VALUES(p.id)
    ON CONFLICT(payment_id) DO NOTHING;
  SELECT order_code INTO result FROM payment.gateway_orders WHERE payment_id=p.id;
  RETURN result;
END $$;
