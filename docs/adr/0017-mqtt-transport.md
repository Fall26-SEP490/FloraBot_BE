# 0017 - Local MQTT TLS and device isolation

Status: accepted for local transport; command bridge and firmware pending.

The source brief requires Mosquitto 2.1, TLS and separate device identities.
Use the published 2.1.2-alpine image (the bare 2.1 tag is unavailable), bound to loopback port 58883.
Generate a local CA, hostname-valid certificate and random passwords using the setup script.
Retain existing credentials across restarts; reject incomplete credential directories.
Use explicit seed-device ACLs: each device reads its own cmd topic and writes its own evt topic.
The backend account has inverse topic permissions. No anonymous or plaintext listener exists.
Copy broker secrets into private tmpfs at startup to enforce POSIX permissions on Windows bind mounts.
Transport checks use actual MQTT v5 clients with certificate validation and assert negative PUBACK reasons,
because mosquitto_pub can exit zero when the broker rejects a publication.
CA rejection is checked with valid account credentials so authentication rejection cannot mask a TLS defect.
Local CA rotation, additional device provisioning and production certificates require operator procedures.
HMAC secrets now sign the shared .NET/Python protocol and simulator; the application bridge remains pending.
No transport test is evidence that a physical door opens or that end-to-end purchase delivery is complete.

The v1 canonical command authenticates hardware, existing command/slot IDs, relay, purpose and server
timestamps with domain-separated HMAC-SHA256. Events use a separate canonical domain and unique event IDs.
Simulator claims are committed before actuation; a crash in the uncertainty window suppresses retries.
Pending events survive process restart and are removed from the pending list only after broker PUBACK.
MQTT v5 Retain As Published allows explicit retained-command rejection. The integration test verifies
tampering, a signed cycle, duplicate delivery, retained publication and process restart through TLS.
See firmware/README.md for the wire format and remaining backend inbox/state-machine requirements.
