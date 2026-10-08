# 0020 - Verify a paid purchase over the actual MQTT transport

Status: implemented and verified locally; payOS sandbox and physical hardware remain unverified.

The technical-token transport probe did not prove the full subscription-to-settlement path.
The new opt-in xUnit test uses the actual application host with MQTT enabled, original SQL flows,
TLS broker and persistent Python door simulator. Only the external payOS HTTP service is replaced
with signed deterministic responses. It never calls the dispatch or inbox processor directly.

The test starts with isolated seller/user/product fixtures, then uses authorized HTTP commands
for approval, subscription, slot assignment, stocking and checkout. Three concurrent copies of
each signed webhook exercise idempotency. Authenticated simulator heartbeats gate the purchase.
Cases cover one and two bouquets from the same seller in a checkout. Assertions require COMPLETED,
the exact set of slots, one CLOSED command/send, three applied sensor events and one pickup event
per slot. The real settlement job leaves funds pending before the dispute deadline. Aging only the
isolated completed order then permits one balanced transfer of 350000 or 700000 VND; repeated jobs
do not repeat that transfer. A successful run releases all empty test slots for reuse.

A dedicated database and Compose broker/simulator prevent this test from sending commands into
the running demo stack. Credentials are read from ignored provisioning files, never embedded in
test artifacts. Services stop after the wrapper; database and durable device state are retained.
Normal unit/integration runs skip this opt-in case; CI provisions and invokes it explicitly.
No API clock override, source SQL change or real payment has been introduced.
