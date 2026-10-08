# FloraBot device simulator and wire protocol

The Python simulator models a complete door cycle over the actual TLS broker. It does not control GPIO.
The ESP32 Arduino core 3.x application, build instructions and native tests are in `esp32/`.
Real board/wiring validation remains required. The backend event receiver uses a durable inbox (ADR 0018),
and outbound dispatch uses only live tokens with persisted retries (ADR 0019).

From BE, provision and start the local broker, then run the explicit simulator profile:

```powershell
./scripts/initialize-mqtt.ps1
docker compose up -d mosquitto
docker compose --profile simulator up -d --build device-simulator
docker compose logs -f device-simulator
```

The simulator sends a signed heartbeat every 30 seconds. Its persistent `simulator-state` volume
retains command claims and undelivered events across restarts. Do not erase state to retry a command.
The runtime container receives only its own credentials and the public CA, not other devices' keys.
It simulates only explicitly signed commands. Payment flows create unlock tokens; the backend worker reads
and dispatches those live tokens after validation. Old payment events cannot create a fresh unlock.

To test without an interactive simulator running:

```powershell
docker compose stop device-simulator
./scripts/test-device-simulator.ps1
./scripts/test-mqtt-door-cycle.ps1
dotnet test tests/FloraBot.Api.Tests --filter FullyQualifiedName~DeviceProtocolTests
```

Tests exercise a shared .NET/Python HMAC vector, tampered fields, expiry, wrong identity/key,
persistent deduplication, crash after claiming a command, actual TLS transport, rejection of retained
commands, and process restart. Test state is disposable; real simulator state is not modified.

The separate door-cycle test starts/stops the compose simulator and retains its state. It creates/reuses
an empty unassigned local test slot and issues a technical test token, then observes one backend send and
three applied sensor events ending in CLOSED. It does not create a paid order or operate physical GPIO.

## Signed unlock v1

JSON fields: `version` (integer 1), `hardware_id`, `cmd_id`, `slot_id`, `relay_channel` (0..63),
`purpose` (`CUSTOMER_PICKUP` or `SELLER_ACCESS`), `issued_at`, `expires_at`, `signature`.
Times are integer Unix seconds sourced from the server's stored unlock token.
Canonical bytes are ASCII, one field per line, with no trailing newline:

```text
florabot.unlock.v1
{hardware_id}
{cmd_id:lowercase-D-UUID}
{slot_id:lowercase-D-UUID}
{relay_channel:decimal}
{purpose}
{issued_at:decimal}
{expires_at:decimal}
```

The signature is HMAC-SHA256 with the device's 32-byte key, encoded as 64 hexadecimal characters.
Topic is `kiosk/{hardware_id}/cmd`, QoS 1, retain false. Hardware IDs allow only ASCII letters,
digits, hyphen and underscore (1..64 characters). Verify the expected hardware identity, every signed
field and `issued_at <= device_clock < expires_at` before claiming or actuating. A device must have
a synchronized clock; an unknown clock must fail closed. Never create a token from an OrderPaid event.
The backend must re-read the live token/status/expiry before dispatch and reuse its existing cmd_id.

The simulator uses MQTT v5 Retain As Published so it can reject retained publications even while online.
It commits the command identity and canonical digest before simulating a relay pulse. Identical repeats
are ignored; reuse of an ID with changed signed content is rejected. If power is lost between claim and
actuation, retry does not actuate: resolve uncertain physical state through the existing recovery flow.
Real firmware must implement this claim in durable NVS and must never evict an unexpired claim to make
space. The SQLite implementation is a simulator mechanism, not ESP32 firmware.

## Signed event v1

JSON fields: `version` (1), `hardware_id`, `event_id`, `cmd_id`, `event`, `occurred_at`, `signature`.
Canonical bytes:

```text
florabot.event.v1
{hardware_id}
{event_id:lowercase-D-UUID}
{cmd_id:lowercase-D-UUID}
{event}
{occurred_at:decimal}
```

Event is ACK, OPENED, CLOSED, FAILED, or HEARTBEAT. HEARTBEAT alone has the all-zero cmd_id.
SENT is a server dispatch fact and cannot be supplied by a device. Backend signature validation allows
events at most 120 seconds old or 5 seconds ahead; device timestamps must never become SQL p_now.
The backend inbox deduplicates event IDs, matches the command's kiosk, and enforces source-flow
state transitions. A valid HMAC alone does not authorize an order transition.

The simulator stores ACK/OPENED/CLOSED events after a simulated full cycle, then publishes them in order.
It marks them delivered only after broker PUBACK. The backend uses a persistent MQTT session and durable
inbox for application-level delivery. Broker acknowledgment is not proof of completed database processing.
Events replayed after the allowed time window require recovery/reconciliation; no stale event may reopen
a door. Physical firmware must emit OPENED/CLOSED only from actual sensor observations.
