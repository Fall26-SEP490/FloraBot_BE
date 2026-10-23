-- Migration 029: Add optional gender and date_of_birth to user_profiles
-- Forward-only migration preserving existing profiles. Idempotent.
BEGIN;

ALTER TABLE identity.user_profiles
  ADD COLUMN IF NOT EXISTS gender text,
  ADD COLUMN IF NOT EXISTS date_of_birth date;

DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'chk_user_profiles_gender'
                 AND conrelid = 'identity.user_profiles'::regclass) THEN
    ALTER TABLE identity.user_profiles
      ADD CONSTRAINT chk_user_profiles_gender
      CHECK (gender IS NULL OR gender IN ('MALE', 'FEMALE', 'OTHER', 'UNDISCLOSED'));
  END IF;
END $$;

COMMIT;
