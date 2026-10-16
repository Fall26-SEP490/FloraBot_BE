-- Migration 024: Additive user personal profiles table for address, verified contact email, and personal bank details
-- Forward-only migration preserving existing accounts and credentials. Idempotent.
BEGIN;

CREATE TABLE IF NOT EXISTS identity.user_profiles (
  user_id                     uuid PRIMARY KEY REFERENCES identity.users(id) ON DELETE CASCADE,
  address                     text CHECK (address IS NULL OR (length(address) <= 300 AND length(btrim(address)) > 0)),
  contact_email               text CHECK (contact_email IS NULL OR (length(contact_email) <= 255 AND length(btrim(contact_email)) > 0)),
  contact_email_verified_at   timestamptz,
  bank_name                   text CHECK (bank_name IS NULL OR (length(bank_name) <= 120 AND length(btrim(bank_name)) > 0)),
  account_holder              text CHECK (account_holder IS NULL OR (length(account_holder) <= 120 AND length(btrim(account_holder)) > 0)),
  account_encrypted           text,
  account_last4               text CHECK (account_last4 IS NULL OR (length(account_last4) <= 4 AND length(btrim(account_last4)) > 0)),
  created_at                  timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at                  timestamptz NOT NULL DEFAULT public.app_now()
);

CREATE INDEX IF NOT EXISTS idx_user_profiles_contact_email
  ON identity.user_profiles(contact_email);

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_profiles_contact_email_coupling') THEN
    ALTER TABLE identity.user_profiles
      ADD CONSTRAINT chk_user_profiles_contact_email_coupling
      CHECK (
        (contact_email IS NULL AND contact_email_verified_at IS NULL) OR
        (contact_email IS NOT NULL AND contact_email_verified_at IS NOT NULL)
      );
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_profiles_bank_coupling') THEN
    ALTER TABLE identity.user_profiles
      ADD CONSTRAINT chk_user_profiles_bank_coupling
      CHECK (
        (bank_name IS NULL AND account_holder IS NULL AND account_encrypted IS NULL AND account_last4 IS NULL) OR
        (bank_name IS NOT NULL AND account_holder IS NOT NULL AND account_encrypted IS NOT NULL AND account_last4 IS NOT NULL)
      );
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_profiles_account_last4_len') THEN
    ALTER TABLE identity.user_profiles
      ADD CONSTRAINT chk_user_profiles_account_last4_len
      CHECK (account_last4 IS NULL OR length(account_last4) = 4);
  END IF;

  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_profiles_account_encrypted_nonblank') THEN
    ALTER TABLE identity.user_profiles
      ADD CONSTRAINT chk_user_profiles_account_encrypted_nonblank
      CHECK (account_encrypted IS NULL OR length(btrim(account_encrypted)) > 0);
  END IF;
END $$;

COMMIT;
