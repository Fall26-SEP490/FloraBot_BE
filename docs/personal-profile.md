# Personal account profile

All six authenticated roles manage their own profile through `/api/account` (Account policy). Anonymous kiosk users and retired STAFF have no profile access. Login identity/role, shop payout details and historical withdrawal evidence cannot be changed here.

## Deployment

Apply additive `db/migrations/024_user_personal_profiles.sql` after prior migrations on an existing database, with a backup. Do not recreate the database or replay db-init to install this feature. Retain the API Data Protection key ring across deploys; it encrypts personal bank numbers. This adds `identity.user_profiles` with a one-to-one foreign key to users and coupled bank/contact integrity constraints.

## API

- GET/PUT `/api/account/profile`: name, optional unique phone/address, personal bank bundle. Omitted bank preserves existing values; `clearBank: true` removes them. The normal response contains bank labels and last four characters only.
- POST `/api/account/profile/bank/reveal`: explicit own-bank reveal, audited and no-store.
- POST `/api/account/contact-email`: send a user-bound challenge to the requested email.
- POST `/api/account/contact-email/verify`: activate the verified address using challengeId/code.
- POST `/api/account/contact-email/reset`: restore login-email fallback and cancel pending challenges.

Contact OTP expires after ten minutes; sends have a sixty-second cooldown. Five wrong attempts lock verification for fifteen minutes. Resetting contact cannot clear the cooldown/attempt lock. Codes are cryptographically generated and stored only as HMACs in Valkey, never in the profile table. Current email remains active until verification succeeds.

Contact changes affect existing non-auth seller notification recipients. Authentication/Google/password-reset messages continue to use the login email. Default contact is a fallback, not a claim that an assigned employee email was verified. No real mail is sent by automated tests; verify SMTP delivery manually with an inbox you control.

Tests: PersonalProfileTests covers all roles, privacy, validation, encryption, OTP lifetime/reuse/races and login-email security routing; CustomEndpointAccessTests covers endpoint authorization metadata. Do not store test passwords, bank details, provider keys or OTPs in source control.
