# Enforce subscription eligibility for catalog editing

The D2 matrix requires seller catalog writes to be limited to ACTIVE shops with a valid package.
The original SQL price/status/photo functions do not enforce this condition.
Migration 003 adds a shared SQL guard and invokes it from all three catalog commands.
It checks the authenticated user's membership, locks the product and seller, and uses server-date package validity.
Admins retain the matrix's administrative access; inactive or expired sellers are rejected before mutation.
Function signatures and the supplied source archive remain unchanged.
The portal shows read-only products for non-active shops, while SQL is the authoritative security boundary.
Integration tests create isolated tenants and cover ACTIVE, same-day expiry, PAST_DUE, APPROVED, SUSPENDED and CLOSED.
API success tests retain price audit attribution; rejected writes leave the price and audit unchanged.
Media metadata verification remains a separate incomplete item; the photo guard does not certify uploaded bytes.
