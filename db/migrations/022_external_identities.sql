-- Migration 022: External identity linkage for OAuth providers (e.g. Google)
-- Forward-only migration preserving existing accounts and history. Repeat-safe.
BEGIN;

CREATE TABLE IF NOT EXISTS identity.external_identities (
  id          uuid PRIMARY KEY DEFAULT public.uuid_v7(),
  user_id     uuid NOT NULL REFERENCES identity.users(id) ON DELETE CASCADE,
  provider    text NOT NULL,
  subject     text NOT NULL,
  email       text,
  created_at  timestamptz NOT NULL DEFAULT public.app_now(),
  updated_at  timestamptz NOT NULL DEFAULT public.app_now(),
  CONSTRAINT uq_external_identities_provider_subject UNIQUE (provider, subject),
  CONSTRAINT uq_external_identities_user_provider UNIQUE (user_id, provider)
);

CREATE INDEX IF NOT EXISTS idx_external_identities_user_id ON identity.external_identities(user_id);

COMMIT;
