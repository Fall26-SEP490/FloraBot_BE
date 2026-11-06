-- Reuse only links validated against the receiver pinned to the charge.
-- Older links keep their gateway mapping so callbacks can still be processed.
ALTER TABLE payment.gateway_orders
    ADD COLUMN IF NOT EXISTS verified_seller_channel_id uuid REFERENCES payment.payment_channel_versions(id);
