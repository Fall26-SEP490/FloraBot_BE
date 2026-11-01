BEGIN;

ALTER TABLE payment.gateway_orders
    ADD COLUMN IF NOT EXISTS qr_code text;

COMMENT ON COLUMN payment.gateway_orders.qr_code IS
    'VietQR payload from a verified payOS create response. Null for older links or responses lost before persistence.';

COMMIT;
