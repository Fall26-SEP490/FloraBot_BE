# ESP32 firmware

This directory contains the Arduino application, portable command/journal core and MQTT transport
extension. The pinned build targets the classic ESP32 Dev Module (4 MB flash), Arduino core 3.3.12
through pioarduino 55.03.312-1, and ArduinoJson 7.4.3. Hardware operation is not yet verified.

The default configuration is deliberately unprovisioned: startup reports configuration required
without connecting to Wi-Fi or configuring GPIO. Copy `include/config.example.h` to the ignored
`include/config.local.h` and provide the actual board wiring, Wi-Fi, hardware ID, per-device MQTT
password/HMAC key and broker CA. Use only that device's provisioning file, never backend credentials.
The broker hostname must resolve from the board and match the certificate SAN. The local-only default
certificate does not cover an arbitrary LAN IP; configure a matching DNS name/certificate for hardware.

Build the unprovisioned images without installing a host toolchain, from BE:

```sh
docker build --target firmware-artifacts --output type=local,dest=artifacts/firmware ./firmware/esp32
```

This also links a separate, explicitly non-operational `esp32dev-compilecheck` configuration to prevent
empty default pins from hiding linker problems in the hardware paths. Never flash the compilecheck
target. It has dummy credentials and is not exported. Private local config is excluded from Docker.
For a configured board, use PlatformIO 6.2.0 locally in this directory: `pio run -e esp32dev`.
No upload is performed by these commands. Provisioned binaries contain credentials and must remain private.

The generated partition table keeps the application at 0x10000 and a dedicated 128 KB NVS journal at
0x310000. A verification script checks the actual binary table and merged image offsets. Do not erase
or migrate that journal merely to retry a command. There is no OTA update implementation.

Run the native tests from BE:

```sh
docker build --target native-test -t florabot-firmware-core-tests ./firmware/esp32
```

The 13 core tests run with AddressSanitizer and UndefinedBehaviorSanitizer, using both OpenSSL and the
firmware's mbedTLS adapter. They verify the shared
backend/Python HMAC vector, tampering, canonical validation, time windows, event domains, persistence
failure, restart at each uncertain phase, sensor ordering, pending queue capacity, unexpired claim
retention, clock rollback, expiration before actuation and portable CRC-protected storage encoding.
Every byte position is corrupted in turn to verify rejection. Multi-door tests cover independent
cycles/recovery, device-wide replay rejection, fair event delivery and global storage failure.
Four additional tests exercise actual
PubSubClient packet parsing/writing using native transport stubs: outgoing QoS 1/DUP, PUBACK, SUBACK,
incoming metadata and malformed packet rejection. These are host tests, not hardware evidence.

## Runtime behavior

The application validates JSON types, exact device topic, QoS and signature before claiming. TLS uses
the configured CA and hostname verification; it never calls setInsecure. NTP must have synchronized
within the last 24 hours. Unknown/rolled-back time cannot authorize a pulse. Heartbeats are signed
and sent every 30 seconds when the connection and event queue permit.

Preferences atomically commits one versioned, fixed-endian, CRC-protected snapshot per relay to the
separate journal partition. Missing state after an initialization marker, corruption or write failure stops
operation. An event remains queued until its matching PUBACK; reconnect resends the same event ID.
The backend's 120-second freshness window still applies: prolonged offline delivery can require manual
reconciliation even when the broker later acknowledges it. PUBACK is not evidence of application commit.

A FreeRTOS task polls/debounces sensors independently of MQTT. A separate ESP timer checks every 10 ms
and de-energizes the relay after the configured pulse (500 ms by default, maximum configured 1000 ms).
These are software scheduling bounds, not a certified hardware interlock. Closed/open/closed sensor
observations, rather than elapsed time, drive pickup events. A cycle timeout or clock loss stops the
pulse and requires local inspection. Recovery preserves claims and does not fabricate CLOSED.

Each configured relay has its own cycle, pulse deadline, recovery state and durable event queue.
One open door does not block another closed door from accepting a different authorized command.
Command IDs and the clock floor are checked across the entire device, so changing the relay cannot
bypass replay protection. Events are round-robin across doors and FIFO within each door; PUBACK
removes only the matching door's event. Any storage failure stops all actuation.

The initialized relay set is persisted. Changing that set, loading an earlier single-journal layout,
or losing an initialized door blob stops startup instead of discarding claims. Upgrading a provisioned
board requires an explicit state-preserving migration procedure; automatic erasure is not provided.
The compilecheck fixture links two doors; simultaneous electrical load still requires bench validation.

An optional active-low local recovery button requires a five-second hold and all configured sensors
closed. It acknowledges physical inspection after an uncertain cycle; backend reconciliation remains
separate. Pin validation rejects duplicate channels/pins and the classic ESP32 flash, strapping, UART
and input-only pins. Actual relay polarity, pull resistors, normally-closed behavior and GPIO levels
must be checked on the team's board before any powered lock test.

## Core integration invariants

- Decode JSON strictly into `flora::Command`; reject missing fields and wrong JSON types.
- Reject retained deliveries and any command not received at QoS 1 on the exact device topic.
- Require a synchronized clock and the provisioned hardware ID, then call `flora::verify`.
- Verify the configured relay mapping and closed sensor before claiming the command.
- Call `DoorBank::claim` only with the returned verified identity. The commit callback must durably
  replace the complete snapshot as one validated NVS blob and return false on any write failure.
- `beginPulse` must commit successfully immediately before energizing the relay. Recheck its physical
  mapping/sensor at that point. An independent timer must limit the relay pulse despite network stalls.
- `opened` and `closed` are fed only by debounced sensor observations. Do not use elapsed time as a
  substitute for sensor evidence. Serialize journal access with one application mutex.
- Sign events from the ordered persistent queue; keep an event until the matching outgoing packet's
  PUBACK. After lost connections retry the same event ID/content. Do not publish inside MQTT callbacks.
- If a queue/storage write fails, stop actuation and require inspection. A full journal does not evict
  unexpired claims. Claims may be reused only after expiry while persisting a wall-clock floor, so a
  backward clock adjustment cannot revive an evicted command.
- On restart during an active cycle, never pulse again. Keep queued evidence and require local physical
  inspection. `clearRecoveryAfterInspection` requires a closed sensor and retains all command claims;
  it must never be exposed as an unauthenticated remote reset or fabricate CLOSED/refund events.
- A clean idle restart preserves claims and pending event IDs without requiring recovery.

PubSubClient is vendored at 2.8 with MIT license/provenance and small transport additions. See
`lib/PubSubClient/UPSTREAM.md`. MQTT 3.1.1 cannot identify a newly retained publication delivered to an
already-subscribed client; HMAC, expiry and durable command identity remain mandatory in that case.
This limitation is distinct from the Python simulator's MQTT 5 Retain As Published behavior.
