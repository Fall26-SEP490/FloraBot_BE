# 0047 — Communication service email extraction

The approved migration target has six .NET business services plus Python AI.
The first local increment extracts delivery transport into a standalone process
and a service-owned database. ADR 0045's provider validation and ambiguous-outcome
rules remain in force. ADR 0046's current-state business reads and ID-only CAP
events remain in the API at this milestone.

Shared transport types/templates move to `FloraBot.Email`. The API selects its
adapter through `EMAIL_PROVIDER`; Communication mode validates a service origin
and Bearer token at startup. Communication itself selects an external provider
and rejects recursively selecting Communication mode. Direct-provider and Disabled
API configurations remain compatible.

A database receipt prevents concurrent or acknowledged delivery replays across
service restarts. The receipt holds only ID/content hashes, state and timestamps.
The earlier restriction on adding business notification storage is preserved in
the API. This new store belongs to the extracted service and tracks technical
delivery outcomes, with no recipient or message payload columns.

After claiming a request, the service finishes its bounded provider attempt and
receipt write even when the HTTP caller disconnects. An interrupted or ambiguous
provider attempt is blocked pending reconciliation. Provider acceptance cannot be
made atomic with PostgreSQL; these receipts do not guarantee exactly-once delivery.

The public FE contracts, payment routing and device flows are outside this
increment. Public chat, notifications, broker ownership and subsequent business
service migrations are described in `docs/microservices/README.md`.

## Increment 2 — broker delivery and reconciliation

Opt-in queued seller email uses API-owned technical correlation metadata plus a
Communication-owned protected payload queue. This extends the initial receipt-only
increment; the earlier statements about no payload storage describe increment 1.
API owns business eligibility. Signed ID-only CAP requests and outcomes cross the
boundary, and an authenticated callback rechecks eligibility before delivery.
Queued payloads are cleared on ACCEPTED/SKIPPED. UNCERTAIN requires explicit audited
operator reconciliation with optimistic revision and an idempotent operation ID.
Public Admin control endpoints are added; existing FE behavior is unchanged.
See the Vietnamese operational notes in docs/microservices/communication.md.
