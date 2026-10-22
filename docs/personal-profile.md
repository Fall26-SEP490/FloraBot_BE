# Personal account profile

All six authenticated roles manage their own profile through `/api/account` (Account policy). Anonymous kiosk users and retired STAFF have no profile access. Login identity/role, shop payout details and historical withdrawal evidence cannot be changed here.

## Deployment

Apply additive `db/migrations/024_user_personal_profiles.sql` and `db/migrations/029_profile_demographics.sql` after prior migrations on an existing database, with a backup. Do not recreate the database or replay db-init to install this feature. Retain the API Data Protection key ring across deploys; it encrypts personal bank numbers. Migration 029 adds optional `gender` (with check constraint for `MALE`, `FEMALE`, `OTHER`, `UNDISCLOSED`) and `date_of_birth` (SQL `date`) to `identity.user_profiles` while preserving all existing rows without fabricated defaults.

## API

- `GET /api/account/profile`: returns name, login email, optional unique phone/address, optional `gender`, optional `dateOfBirth` (YYYY-MM-DD), contact email status, and full personal `bank` object (containing `bankName`, `accountHolder`, `accountLast4`, `hasAccount`, decrypted `accountNumber`, and `status: "READY"`). Invalid legacy bank ciphertext returns 200 with `accountNumber: null` and `status: "NEEDS_UPDATE"` without failing with 500 or blocking profile access. Empty bank returns `null`. Every disclosure of full personal bank is audited (`PERSONAL_BANK_REVEALED`) prior to response and served with `Cache-Control: no-store`.
- `PUT /api/account/profile`: updates name, optional phone, address, optional `gender`, optional `dateOfBirth`, and personal bank details. Omitted `gender` and `dateOfBirth` preserve their existing values (legacy frontend requests will not erase demographics). Existing name/phone/address PUT semantics remain unchanged. Explicit clear is supported via `clearBank: true`, `clearGender: true`, and `clearDateOfBirth: true`; conflicting clear flag and value returns 400. Validates `dateOfBirth` is not in the future according to current Vietnam date (UTC+7) with no arbitrary minimum age restriction. Validates `gender` against allowed values. Personal address and phone mutations are strictly private and never modify kiosk locations, coordinates, or shop payout accounts.
- `POST /api/account/profile/bank/reveal`: maintained for deprecated backward compatibility; frontend clients can now obtain full bank details directly from `GET /api/account/profile` without making a separate reveal call.
- `POST /api/account/contact-email`: send a user-bound challenge to the requested email.
- `POST /api/account/contact-email/verify`: activate the verified address using challengeId/code.
- `POST /api/account/contact-email/reset`: restore login-email fallback and cancel pending challenges.

Contact OTP expires after ten minutes; sends have a sixty-second cooldown. Five wrong attempts lock verification for fifteen minutes. Resetting contact cannot clear the cooldown/attempt lock. Codes are cryptographically generated and stored only as HMACs in Valkey, never in the profile table. Current email remains active until verification succeeds.

Contact changes affect existing non-auth seller notification recipients. Authentication/Google/password-reset messages continue to use the login email. Default contact is a fallback, not a claim that an assigned employee email was verified. Phone-only accounts start with `null` contact email without generated placeholder emails and can establish verified contact email via OTP challenge without changing login identity.

## Manual Verification

1. Authenticate as any of the 6 roles and retrieve JWT token.
2. `GET /api/account/profile` with `Authorization: Bearer <token>`: observe full personal bank number in `bank.accountNumber`, optional `gender` and `dateOfBirth`.
3. `PUT /api/account/profile` with new `gender` ("FEMALE") and `dateOfBirth` ("1998-05-20"); verify updated values in response.
4. `PUT /api/account/profile` omitting `gender` and `dateOfBirth`; verify existing demographics are preserved.
5. Attempt `PUT /api/account/profile` with future date or invalid gender; verify 400 Validation Problem.
6. Check `notify.audit_logs` for `PERSONAL_BANK_REVEALED` and `PERSONAL_PROFILE_UPDATED` entries and confirm no plaintext bank account numbers are stored in payloads.

Tests: `PersonalProfileTests` and `ProfileConsolidationTests` cover all 6 roles, demographic validation, bank encryption, graceful invalid ciphertext handling, OTP verification for phone-only fixtures, and security boundaries. Note: Automated suites execute against isolated local PostgreSQL; no claim of frontend UI or VPS deployment is made.
