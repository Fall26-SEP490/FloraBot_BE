BEGIN;

-- 1. Channel version credential pinning & explicit default-strict VA policy
ALTER TABLE payment.payment_channel_versions
    ADD COLUMN IF NOT EXISTS allow_virtual_accounts BOOLEAN NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS bound_credential_fingerprint TEXT,
    ADD COLUMN IF NOT EXISTS verified_credential_fingerprint TEXT;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_channel_fingerprint_hex_format'
          AND conrelid = 'payment.payment_channel_versions'::regclass
    ) THEN
        ALTER TABLE payment.payment_channel_versions
            ADD CONSTRAINT chk_channel_fingerprint_hex_format CHECK (
                (bound_credential_fingerprint IS NULL OR bound_credential_fingerprint ~ '^v1:[0-9a-fA-F]{64}$')
                AND (verified_credential_fingerprint IS NULL OR verified_credential_fingerprint ~ '^v1:[0-9a-fA-F]{64}$')
            );
    END IF;

    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_channel_va_requires_pinned_credentials'
          AND conrelid = 'payment.payment_channel_versions'::regclass
    ) THEN
        ALTER TABLE payment.payment_channel_versions
            ADD CONSTRAINT chk_channel_va_requires_pinned_credentials CHECK (
                NOT allow_virtual_accounts OR (
                    bound_credential_fingerprint IS NOT NULL
                    AND (
                        state <> 'ACTIVE' OR (
                            verified_credential_fingerprint IS NOT NULL
                            AND verified_credential_fingerprint = bound_credential_fingerprint
                        )
                    )
                )
            );
    END IF;
END $$;

CREATE OR REPLACE FUNCTION payment.enforce_channel_version_immutability()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Phiên bản kênh thanh toán là bất biến, không thể xóa (seller %, version %)', OLD.seller_id, OLD.version;
    END IF;

    IF TG_OP = 'UPDATE' THEN
        IF NEW.id <> OLD.id OR
           NEW.seller_id <> OLD.seller_id OR
           NEW.version <> OLD.version OR
           NEW.secret_ref <> OLD.secret_ref OR
           NEW.expected_bank_bin <> OLD.expected_bank_bin OR
           NEW.expected_bank_account <> OLD.expected_bank_account OR
           NEW.expected_bank_name IS DISTINCT FROM OLD.expected_bank_name OR
           NEW.allow_virtual_accounts IS DISTINCT FROM OLD.allow_virtual_accounts OR
           NEW.bound_credential_fingerprint IS DISTINCT FROM OLD.bound_credential_fingerprint OR
           NEW.created_at <> OLD.created_at THEN
            RAISE EXCEPTION 'Thông tin định danh và tài khoản nhận tiền của phiên bản kênh thanh toán là bất biến (seller %, version %)', OLD.seller_id, OLD.version;
        END IF;

        IF OLD.verified_credential_fingerprint IS NOT NULL AND
           NEW.verified_credential_fingerprint IS DISTINCT FROM OLD.verified_credential_fingerprint THEN
            RAISE EXCEPTION 'Dấu vân tay thông tin xác thực của phiên bản kênh thanh toán đã xác minh là bất biến (seller %, version %)', OLD.seller_id, OLD.version;
        END IF;

        IF OLD.bound_credential_fingerprint IS NOT NULL AND
           NEW.verified_credential_fingerprint IS NOT NULL AND
           NEW.verified_credential_fingerprint IS DISTINCT FROM OLD.bound_credential_fingerprint THEN
            RAISE EXCEPTION 'Dấu vân tay xác thực không khớp với dấu vân tay lúc gắn kết kênh (seller %, version %)', OLD.seller_id, OLD.version;
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

-- 2. Verification attempts bind the live server credential fingerprint used during verification
ALTER TABLE payment.channel_verification_attempts
    ADD COLUMN IF NOT EXISTS credential_fingerprint TEXT;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_attempt_fingerprint_hex_format'
          AND conrelid = 'payment.channel_verification_attempts'::regclass
    ) THEN
        ALTER TABLE payment.channel_verification_attempts
            ADD CONSTRAINT chk_attempt_fingerprint_hex_format CHECK (
                credential_fingerprint IS NULL OR credential_fingerprint ~ '^v1:[0-9a-fA-F]{64}$'
            );
    END IF;
END $$;

-- 3. Gateway order per-order signed receiver evidence bound atomically with payment link
ALTER TABLE payment.gateway_orders
    ADD COLUMN IF NOT EXISTS receiver_bin TEXT,
    ADD COLUMN IF NOT EXISTS receiver_account TEXT,
    ADD COLUMN IF NOT EXISTS receiver_account_name TEXT,
    ADD COLUMN IF NOT EXISTS receiver_mode TEXT,
    ADD COLUMN IF NOT EXISTS receiver_verified_at TIMESTAMPTZ;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'chk_gateway_order_receiver_evidence'
          AND conrelid = 'payment.gateway_orders'::regclass
    ) THEN
        ALTER TABLE payment.gateway_orders
            ADD CONSTRAINT chk_gateway_order_receiver_evidence CHECK (
                (
                    receiver_bin IS NULL
                    AND receiver_account IS NULL
                    AND receiver_account_name IS NULL
                    AND receiver_mode IS NULL
                    AND receiver_verified_at IS NULL
                )
                OR
                (
                    receiver_bin IS NOT NULL
                    AND receiver_bin ~ '^[0-9]{4,10}$'
                    AND receiver_account IS NOT NULL
                    AND receiver_account ~ '^[0-9A-Za-z]{4,30}$'
                    AND (receiver_account_name IS NULL OR (length(receiver_account_name) BETWEEN 1 AND 120))
                    AND receiver_mode IS NOT NULL
                    AND receiver_mode IN ('ROOT_ACCOUNT', 'VIRTUAL_ACCOUNT')
                    AND receiver_verified_at IS NOT NULL
                    AND checkout_url IS NOT NULL
                    AND verified_seller_channel_id IS NOT NULL
                )
            );
    END IF;
END $$;

COMMIT;
