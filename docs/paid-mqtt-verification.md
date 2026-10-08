# Local paid MQTT cycle verification

Verified on 2026-10-08 against the current backend after the module-read relocation.

Command: `pwsh -NoProfile -File scripts/test-paid-mqtt-cycle.ps1`.
Result: two passed, zero failed, zero skipped; test duration 42 seconds.
Evidence: `artifacts/paid-mqtt-current.log` and
`artifacts/Admin_ADMINISTRATOR_2026-10-08_03_51_57.trx`.

The two cases purchase one and two bouquets. Each exercises seller approval,
subscription purchase, slot assignment, stocking, checkout, signed payment webhook,
MQTT dispatch, simulated pickup and settlement through application endpoints/jobs.

The test uses the dedicated `florabot_mqtt_tests` database and
`florabot-mqtt-test` Compose stack, with TLS broker port 58884 and a separate
simulator state volume. It waits for an authenticated heartbeat received by the
backend instead of trusting a seeded ONLINE status. The script stops its test
broker/simulator in its finally block; no running test services remained afterward.

Per slot, assertions confirm one unlock attempt, broker PUBACK, CLOSED token,
three applied device events and one pickup outbox event. For each completed order,
available funds remain zero before the dispute window. Only that isolated fixture's
completion timestamp is then aged; settlement yields 350000 or 700000 VND, remains
unchanged when rerun and has two balanced settlement entries totaling zero.

Limits: payOS HTTP responses and payment callbacks are simulated using a test
checksum key. This proves local MQTT/TLS transport and backend accounting, not a
live payOS sandbox charge, physical ESP32 relay operation, delivery of real flowers
or production infrastructure. It does not cover every outage/power-loss scenario.
