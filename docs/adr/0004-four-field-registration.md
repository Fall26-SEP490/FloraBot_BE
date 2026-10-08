# 0004: Four-field registration without invented identity

Context: the landing requires shop, phone, area and package interest only.
The supplied register_seller additionally accepts owner name/email and writes a fake demo bcrypt hash.
Decision: retain its exact signature in a supplemental migration, accepting NULL email.
The shop name is the provisional contact display name, not a fabricated person's identity.
A non-login !unprovisioned hash satisfies the schema without creating usable credentials.
The requested package is audited as interest; it does not approve the shop or create a paid subscription.
An administrator must verify ownership before provisioning portal credentials.
No new business table or invented email address is introduced.
The original archive remains untouched; integration tests check this registration behavior.
Consequence: account provisioning is a separate administrative step, not public password assignment.
