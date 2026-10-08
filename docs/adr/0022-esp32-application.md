# 0022 - ESP32 application uses durable state and independent sensor timing

Status: implemented; compile verification and physical validation are tracked in REQUIREMENTS.md.

Integrate the tested command/journal core with Arduino 3.3.12, ArduinoJson 7.4.3, mbedTLS,
WiFiClientSecure, Preferences and the vendored PubSubClient QoS 1 extension. Pin pioarduino and
PlatformIO versions. Default credentials/pins are empty and GPIO is not configured until validated.
A separate compile-only fixture links all hardware paths and is never exported as a deployable image.

Store a versioned, fixed-endian snapshot with CRC in a dedicated 128 KB NVS partition. The complete
blob is committed before pulse authorization; failed writes or invalid/missing initialized state
stop operation. Persisted event IDs survive reconnects and are removed only after matching PUBACK.
Broker acknowledgment still cannot prove backend processing, especially after its freshness window.

Sensor sampling runs in its own FreeRTOS task under a short journal mutex. Networking never holds
that mutex. An independent 10 ms ESP timer limits the configured relay pulse, including during TLS
reconnects. Debounced physical sensor observations alone produce OPENED/CLOSED. Clock loss, timeout
or restart during an active phase require local inspection without another pulse. An optional local
button can acknowledge inspection only while all sensors are closed; command claims remain intact.

TLS validates the configured CA/hostname; a synchronized NTP clock and persistent clock floor gate
commands. The current local certificate needs matching DNS/certificate provisioning for a LAN board.
No secrets are built into exported default artifacts. Actual hardware polarity, boot levels, sensor
behavior, reset/power-loss timing, NVS wear and full board-to-backend TLS operation need bench tests.
