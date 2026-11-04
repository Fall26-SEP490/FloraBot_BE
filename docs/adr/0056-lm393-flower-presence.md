# 0056 - Authenticated LM393 observations

Status: implemented; deployment, migration application and physical end-to-end validation pending.

Main firmware reads GPIO21/22/19/23 for relay channels 0..3 in the sensor task.
LOW is detection; HIGH cannot distinguish no object from an unplugged module.
While a door is open or a signal has not settled for 1000 ms, its state is -1.
Stable closed-door states are 0 (not detected) and 1 (detected). No observation
changes order, payment, stock, unlock-token or recovery behavior.

Publish QoS 1, retain false on the existing `kiosk/{hardware_id}/evt` topic.
The separate `FLOWER_PRESENCE` envelope has `version=1`, `hardware_id`, UUID
`event_id`, `event`, Unix **millisecond** `occurred_at`, four integer `states`
in relay order and a hex HMAC-SHA256 `signature`. Unlike order events, this
message does not use `cmd_id`. Canonical ASCII, no trailing newline:

```text
florabot.flower.v1
{hardware_id}
{lowercase_event_id}
{occurred_at_milliseconds}
{state_relay_0}
{state_relay_1}
{state_relay_2}
{state_relay_3}
```

All states and timestamps are signed with the existing per-device key. Receipts
reject invalid IDs, other devices, unknown keys, malformed states, observations
older than 120 seconds or over 5 seconds in the future, retained messages and
QoS 0. The broker is acknowledged only after the database write. Database
failure follows the existing reconnect/redelivery path. Snapshots are not
written to ESP32 NVS: only the latest observation matters. Publish on changes
(at most once/second), reconnect and every 15 seconds; order events and heartbeat
retain priority. Redelivery/older snapshots do not replace a newer timestamp or
refresh its receipt time.

Migration `20261104_flower_presence.sql` adds one latest snapshot per kiosk.
GET `/api/kiosks/{id}/flower-presence` is authenticated, no-store, restricted to
admin/operations managers, the owning seller or the assigned installation
technician. Device sessions, customers and other sellers cannot read it. It
joins the kiosk's actual slots to relay indexes; unsupported relay indexes have
unknown state. Offline kiosks, observations or receipts older than 45 seconds,
and unstable signals return null `hasFlowers`. This is observation telemetry,
not flower identity recognition or proof of pickup.

Web polls every 5 seconds only while the sensor disclosure is open, hides old
results on read failure/offline pause, and shows an installation caveat for HIGH.
See the seller kiosk view, admin slot details and operations available-kiosk
inventory. Existing database/runtime deployment procedures apply; do not flash
the hardware expecting an old backend to understand this new message.
