-- Staff identity is separate from administrative authority. Task permissions follow separately.
BEGIN;
ALTER TABLE identity.users DROP CONSTRAINT IF EXISTS users_role_check;
ALTER TABLE identity.users ADD CONSTRAINT users_role_check
  CHECK (role IN ('ADMIN', 'STAFF', 'SELLER', 'CUSTOMER', 'SYSTEM'));
COMMIT;
