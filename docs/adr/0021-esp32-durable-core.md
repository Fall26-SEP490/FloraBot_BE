# 0021 - Firmware claims precede actuation; event delivery uses QoS 1

Status: portable core and transport tests implemented; Arduino integration continues in ADR 0022.

The required Arduino/PubSubClient stack must match the signed v1 backend protocol and tolerate resets.
The portable C++ core canonicalizes commands/events and verifies HMAC with an injected crypto adapter.
The host tests use OpenSSL; the ESP32 adapter will use its bundled mbedTLS implementation.

A bounded journal stores verified command identities, original expirations, an ordered event queue,
active sensor phase and a persisted clock floor. A complete cycle's queue capacity is reserved before
claiming. An application-provided atomic NVS commit must succeed before a command can energize a relay.
No unexpired claim is evicted. Restart during any active phase requires inspection and never repeats
the pulse; recovery preserves claims and cannot synthesize a successful pickup.

PubSubClient 2.8 can subscribe at QoS 1 but only publishes QoS 0 upstream, which the backend rejects.
Rather than change the specified library, vendor the MIT source and add bounded QoS 1 publication,
packet-ID allocation, PUBACK/SUBACK callbacks, incoming flags and receive bounds checks. Application
code owns durable retries with one inflight event. Native packet tests validate those additions.
MQTT 3.1.1 cannot expose the original retained flag to live subscribers; document that limit explicitly
and retain cryptographic identity/expiry checks. Do not claim the Python MQTT 5 behavior for firmware.

Fourteen host tests pass under address/undefined-behavior sanitizers. NVS bytes/CRC, board pin mapping,
timer-limited pulses, actual sensors, TLS/NTP, ArduinoJson and an ESP32 build still require integration.
