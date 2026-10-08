# 0018 - Durable authenticated MQTT event receipt

Status: inbound transport implemented; command dispatch implemented separately in ADR 0019.

MQTT QoS 1 can redeliver messages, and broker PUBACK alone does not prove application processing.
MQTTnet 5.2 uses TLS with the configured CA and hostname validation, a stable persistent-session client ID,
and manual acknowledgments. A valid event is acknowledged only after durable PostgreSQL receipt.
Malformed, retained, stale, unknown-device, wrong-command-owner and invalid-signature messages are terminal.
Database failures leave delivery unacknowledged and force reconnection for broker redelivery.

Migration 007 adds a transport inbox in kiosk_ops without editing source SQL. Event IDs are unique and a
normalized payload hash prevents ID reuse with changed content. Receipt validates hardware-to-command
ownership. The processor locks each pending receipt and its token, runs the existing flow and publishes
CAP events in the same transaction as marking the receipt processed. Device timestamps never become p_now.

Out-of-order ACK/OPENED/CLOSED events remain pending for up to two minutes awaiting predecessors.
Terminal or already-advanced states ignore duplicate transitions. An expired token cannot reopen;
a real CLOSED transition can finish an already OPENED token after its unlock deadline.
Stale queued heartbeats do not resurrect offline devices. Fresh heartbeats use the server's timestamp.
After commit, portal invalidation uses the captured slot seller and existing live authorization checks.

Key files are loaded once; key rotation requires worker restart. Each deployment must use its own stable
MQTT client ID; test hosts disable the worker to avoid stealing the application's session. More than one
active receiver using the same client ID is unsupported. The broker session expires after 24 hours.
Pending rows survive API restart and database errors. Permanently failing source flows remain visible in
the inbox and logs for operator recovery; no blind replay of financial side effects occurs outside the transaction.

Tests cover concurrent receipt/processing, event-ID conflicts, cross-kiosk commands, ordering, expiration,
server-clock heartbeats and a paid order completing through the inbox with a unique pickup event.
The live Docker probe proves MQTT -> API -> PostgreSQL, duplicate heartbeat receipt and rejection of invalid
signatures/foreign commands. Outage fault injection and ESP32 hardware remain to be verified.
