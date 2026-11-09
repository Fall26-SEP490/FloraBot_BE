# 0062 - Role-scoped ESP32 configuration downloads

Date: 2026-11-09. Status: accepted.

Expose public device configuration metadata separately from secret-bearing
`config.local.h` downloads. Operations Managers require current kiosk assignment;
Technicians require current assignment to a live installation. Admin has metadata
oversight only. Completed installation history does not extend provisioning rights.
The kiosk screen API key and existing provisioning endpoints are unchanged.

Every download requires the caller's current password. Identity owns this check,
KioskOps owns assignment reads and generation, and Notify owns access audit.
Carry the same transaction through account/assignment checks and success audit;
fail closed if audit cannot commit. Follow scheduling lock order: application,
technician account, kiosk. Recheck scope for every request rather than caching a
grant from a metadata read. Current assignment loss gives the same response as a
missing resource.

Metadata never reads device secrets. Downloads resolve hardware-keyed credentials
from protected server files and compare the HMAC to the active backend signing
key with the existing protocol. Missing or mismatched provisioning fails closed.
There is no automatic Mosquitto user creation, password recovery, rotation, or
device flashing in this endpoint.

Use explicit board-reachable MQTT TLS host/port/public CA configuration; internal
backend DNS settings are insufficient. Header values are escaped as C++ UTF-8
string literals, Wi-Fi values remain blank, and the existing IOT wiring table is
reused. Secret bytes exist only in the explicitly reauthenticated attachment;
metadata, error responses and audit contain no passwords or signing keys.

All responses disable caching. Downloads have auth rate limiting and attachment
headers; the OpenAPI workflow describes file responses and stable error codes.
FE must keep submitted passwords and downloaded firmware configuration out of
persistent storage, analytics, logs, previews and source control.
