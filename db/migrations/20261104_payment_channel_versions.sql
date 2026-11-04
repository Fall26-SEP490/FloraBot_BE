BEGIN;

CREATE TABLE IF NOT EXISTS payment.payment_channel_versions (
    id UUID PRIMARY KEY DEFAULT public.uuid_v7(),
    seller_id UUID NOT NULL REFERENCES identity.sellers(id),
    version INT NOT NULL CHECK (version > 0),
    secret_ref TEXT NOT NULL CHECK (secret_ref ~ '^[A-Za-z0-9_-]{1,80}$'),
    expected_bank_bin TEXT NOT NULL CHECK (expected_bank_bin ~ '^[0-9]{4,10}$'),
    expected_bank_account TEXT NOT NULL CHECK (expected_bank_account ~ '^[0-9A-Za-z]{4,30}$'),
    expected_bank_name TEXT NOT NULL CHECK (length(expected_bank_name) >= 2 AND length(expected_bank_name) <= 120),
    state TEXT NOT NULL DEFAULT 'PENDING_VERIFICATION' CHECK (state IN ('PENDING_VERIFICATION', 'ACTIVE', 'PAUSED', 'ERROR')),
    verified_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now(),
    updated_at TIMESTAMPTZ NOT NULL DEFAULT public.app_now(),
    CONSTRAINT uq_seller_channel_version UNIQUE (seller_id, version),
    CONSTRAINT chk_channel_active_requires_verified CHECK (state <> 'ACTIVE' OR verified_at IS NOT NULL)
);

CREATE INDEX IF NOT EXISTS idx_payment_channel_versions_seller ON payment.payment_channel_versions(seller_id);
CREATE INDEX IF NOT EXISTS idx_payment_channel_versions_state ON payment.payment_channel_versions(seller_id, state);

-- Enforce immutability of identity, version, secret_ref, and expected recipient metadata
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
           NEW.created_at <> OLD.created_at THEN
            RAISE EXCEPTION 'Thông tin định danh và tài khoản nhận tiền của phiên bản kênh thanh toán là bất biến (seller %, version %)', OLD.seller_id, OLD.version;
        END IF;
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_channel_version_immutability ON payment.payment_channel_versions;
CREATE TRIGGER trg_channel_version_immutability
BEFORE UPDATE OR DELETE ON payment.payment_channel_versions
FOR EACH ROW EXECUTE FUNCTION payment.enforce_channel_version_immutability();

COMMENT ON TABLE payment.payment_channel_versions IS 'Immutable payment channel version registry for seller direct payments.';
COMMENT ON COLUMN payment.payment_channel_versions.secret_ref IS 'Server configuration key reference under PAYOS_CHANNELS:<ref>. Raw secrets are never stored in DB.';

COMMIT;
