# 0011: One-time portal credential provisioning

- Context: seed bcrypt values are intentionally unusable, and registrations create !unprovisioned contacts.
- Decision: a database-operator CLI provisions an existing active ADMIN/SELLER; no public setup endpoint is added.
- Input: user UUID and matching email in arguments, password only through stdin or the masked PowerShell prompt.
- Validation: minimum 12 characters, maximum 72 UTF-8 bytes, bcrypt cost 12; no silent bcrypt truncation.
- SQL: flow.provision_portal_user locks the account and normalized email, rejects an existing password and records an audit atomically.
- Scope: no role changes, membership changes, password reset or automatic shop activation.
- Development: only the exact original seed placeholders are additionally accepted in Development.
- Production: only !unprovisioned accounts are eligible; the operator must already hold database credentials.
- Audit: system-attributed PORTAL_USER_PROVISIONED records the subject and channel, never email/password/hash.
- Verification: isolated PostgreSQL provisioning and actual HTTP login, re-provision denial, collision and role/status checks.
