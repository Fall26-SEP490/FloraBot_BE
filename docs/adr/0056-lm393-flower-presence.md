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

## Technician door observations (version 2)

The standalone IoT firmware publishes version 2 after deploying migration
`20261117_technician_door_telemetry.sql` and the updated API. The receiver still
accepts version 1 for existing kiosks. Version 2 uses `florabot.flower.v2`
as its canonical first line. After the four flower states it signs the four
entries of `door_states`, then `door_cycles`, then `door_cycle_times`, in relay
order. All numbers are decimal. `door_states` uses -1 unknown, 0 open and 1 closed
as reported by MC-38. The cycle codes are 0 none, 1 relay pulsed, 2 opening observed,
3 closing observed, 4 opening timeout, 5 closing timeout and 6 inspection needed.
Cycle timestamps are Unix seconds, with 0 reserved for an unavailable timestamp.
The last cycle is recorded in RAM and resets when ESP32 restarts.

GET `/api/staff/tasks/{taskId}/iot-health` checks the requesting technician's
task assignment and returns only the disputed slot when a slot is specified.
It hides readings after 45 seconds or while the kiosk is offline. The relay
outcome records software activation and sensor observations. Electrical and
mechanical diagnosis of the lock requires feedback from additional hardware.
