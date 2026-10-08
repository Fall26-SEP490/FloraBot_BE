# Private evidence: proposed implementation

Status: approved by the user on 2026-10-08 and implemented. Operational handoff:
[handoff.md](handoff.md). The following records the original plan and final scope.

## Current behavior and affected scope

The original SQL functions return_to_seller, report_device_fault and open_dispute
accept p_photo_url/p_photo and call flow.attach. That helper records demo size/hash
metadata. Product photographs now verify bytes; this proposal does not reuse their
public storage path for evidence.

## Proposed behavior

- Store evidence as Cloudinary authenticated assets. Never return a public upload
  URL for these files. Keep credentials on the API.
- Authorize upload against the same seller resource or receipt capability as the
  business command. Bind an expiring upload reference to that resource and actor.
- Preserve existing SQL business transitions; resolve the scoped reference at the
  API boundary and record verified MIME, size and SHA-256 in the flow transaction.
- Serve evidence through an authenticated API that checks the existing incident,
  seller or receipt access rule on every request. Avoid persisting expiring signed
  delivery URLs as the canonical attachment identity.
- Bound uploads/downloads to supported image formats and 5 MiB; do not follow
  arbitrary remote URLs. Missing provider configuration must fail explicitly.
- Keep historic demo records labelled as unverified; do not migrate/delete
  existing records or external objects automatically.

## Verification before acceptance

Use real PostgreSQL and a fake provider to prove hash/size persistence, rollback on
provider or flow failure, expiry/resource binding of upload references, and denial
for anonymous/foreign-tenant evidence reads. Exercise seller return/fault and
receipt complaint forms against the API. Run provider acceptance separately with
user-configured sandbox credentials and designated test assets.

## Decision and implemented scope

The user explicitly selected integrated Cloudinary private delivery. Upload references
are protected by Data Protection, expire in 30 minutes and bind to actor/resource/purpose.
Existing SQL receives the private canonical URL; the same transaction replaces demo metadata.
Advisory transaction locking plus an attachment lookup prevents concurrent reference reuse.
The new read route is admin-only, matching the existing incident/complaint review workflow.
No seller/customer evidence-download role was added. Signed provider downloads stay server-side.
Unreferenced uploads require the documented operator reconciliation; automatic deletion is not enabled.
