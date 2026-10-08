# 0023 - Independent door cycles with device-wide replay protection

Status: implemented; host tests and compilation are separate from physical validation.

A paid checkout can contain several bouquets on one MCU. A single active journal made another
door depend on retries while the first customer kept a door open. Serializing backend delivery
would also couple pickup latency to that customer's behavior.

Use one durable journal and pulse deadline per configured relay. DoorBank enforces command identity
and the persisted clock floor across all journals. Each door preserves its sensor-event order;
round-robin publication prevents one healthy door's queue monopolizing the connection. PUBACK is
routed to the originating journal. An uncertain cycle requires inspection only on that door,
while any storage failure disables the whole device because persistence can no longer be trusted.

Expand the dedicated NVS partition to 128 KB. Persist the configured relay set and reject legacy
single-journal state or changed mappings at startup, without erasing claims. No board migration or
flash operation is performed. Two-door compilation and native tests cover software coordination;
power supply capacity, simultaneous lock load and reset/power-cut behavior require hardware tests.
