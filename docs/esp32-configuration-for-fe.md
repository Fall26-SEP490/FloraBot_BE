# ESP32 configuration by kiosk and installation

The ESP32 configuration API is separate from the kiosk screen's existing
`provision-key` API. It neither provisions/rotates MQTT credentials nor changes the
screen API key. No database migration is required: current manager assignments,
installation assignments, and `flow.audit` are reused.

| Role | Read metadata | Download configuration |
| --- | --- | --- |
| `OPERATIONS_MANAGER` | `GET /api/operations/kiosks/{id}/esp32-config` | `POST /api/operations/kiosks/{id}/esp32-config/download` |
| `TECHNICIAN` | `GET /api/staff/kiosk-installations/{installationId}/esp32-config` | `POST /api/staff/kiosk-installations/{installationId}/esp32-config/download` |
| `ADMIN` | `GET /api/admin/kiosks/{id}/esp32-config` | None; operational routes deny Admin with 403 |

All calls require the current portal cookie or Bearer session. Kiosk-scoped
identities are excluded. The server rechecks the active database account and exact
route role on each request. A Manager must currently appear in
`kiosk_ops.manager_kiosks` for the requested kiosk. A Technician must currently be
`assigned_technician_id` on that installation, which must have a linked kiosk and
status `SCHEDULED`, `INSTALLING`, or `AWAITING_HANDOVER`. Completed `ACTIVE` history
does not grant ESP32 configuration access. Assignment and installation locks are
held through the access audit so a concurrent reassignment/handover cannot slip
between authorization and configuration disclosure.
Technician checks lock installation, actor, then kiosk in that order, matching
the scheduling flow and avoiding a reschedule/read lock cycle.

Missing kiosks/installations, foreign assignments, revoked assignments, and ended
installations have the same HTTP 404 response. Other roles receive 403. The route
uses the Account middleware policy so a known portal actor's wrong-role attempts
can also be audited; the handler additionally enforces the exact role above.

Example metadata (`Esp32ConfigMetadata`):

```json
{
  "kioskId": "40000000-0000-0000-0000-000000000099",
  "kioskCode": "K-HCM-04",
  "status": "OFFLINE",
  "hardwareId": "ESP32-EXAMPLE",
  "mqttClientId": "registered-client-id-from-database",
  "firmwareMqttClientId": "esp32-ESP32-EXAMPLE",
  "mqttClientIdsMatch": false,
  "mqttHost": "mqtt.example.test",
  "mqttTlsPort": 8883,
  "caCertificatePem": "-----BEGIN CERTIFICATE-----\n...\n-----END CERTIFICATE-----\n",
  "canDownload": true,
  "requiresReauthentication": true,
  "downloadUnavailableCode": null
}
```

Both Hardware ID and registered MQTT Client ID come from `kiosk_ops.kiosks`.
Firmware independently derives its client ID as `esp32-<hardwareId>`; both values
are provided for comparison and the API does not silently rewrite the database.
Broker/TLS details come from the explicit board-facing deployment settings below.
Only public certificate content is returned from the CA file. Metadata never
reads or returns an MQTT password or device HMAC key.

`canDownload` represents current role/scope and public deployment readiness. It
does not assert that a device secret exists; secret-store access happens only on
an explicit, reauthenticated download. Admin always has `canDownload: false` and
`requiresReauthentication: false`. If public broker/CA settings are unavailable,
connection fields are null and `downloadUnavailableCode` is
`ESP32_CONFIG_DEPLOYMENT_NOT_CONFIGURED`. The normal Admin code when deployment
is configured is `ESP32_CONFIG_DOWNLOAD_NOT_ALLOWED`.

Each download accepts JSON `Esp32ConfigDownloadRequest`:

```json
{ "password": "current account password entered by the user" }
```

The current password is checked for this request only, against the active
account's current hash. No reauthentication ticket is cached or reusable.
Passwordless accounts must establish an account password through the normal
account process before using this flow. Downloads use the existing `auth` rate
limiter. FE should clear the password field after submission and avoid analytics,
console output, persistent storage, or content previews for this request/file.

Success is `200`, `Content-Type: text/x-c++hdr; charset=utf-8`, with attachment
`config.local.h` and `Cache-Control: no-store`. The header includes exact
`hardwareId`, broker, TLS port, MQTT password, HMAC hex, and public CA. Wi-Fi SSID
and password are intentionally blank for local installation. C++ string literals
preserve exact UTF-8 bytes and escape quotes, backslashes, newlines, control bytes,
and question marks using fixed-width octal escapes. Range processing is disabled.
All read/error responses also carry `Cache-Control: no-store`.

| HTTP | Problem `code` | FE behavior |
| --- | --- | --- |
| 401 | `ESP32_CONFIG_REAUTHENTICATION_FAILED` | Request the current password again; missing/wrong password or no password hash |
| 403 | `ESP32_CONFIG_FORBIDDEN` | Hide the operation for this role/account |
| 404 | `ESP32_CONFIG_NOT_FOUND` | Resource missing, outside assignment, or installation no longer live |
| 409 | `ESP32_CONFIG_DEPLOYMENT_NOT_CONFIGURED` | Board-facing broker/CA settings need operator attention |
| 409 | `ESP32_CONFIG_NOT_PROVISIONED` | Device password/HMAC or active backend signing key unavailable/malformed |
| 409 | `ESP32_CONFIG_HMAC_MISMATCH` | Stored device HMAC differs from the key currently loaded by Backend |
| 429 | Existing auth rate limit | Wait before retrying |

Errors use ProblemDetails with a stable `code` extension. Existing session
authentication failures, middleware validation, malformed JSON, and rate limiting
may use the standard application response. Error messages never include file
paths, parser excerpts, submitted passwords, stored credentials, or generated
header contents.

## Deployment configuration

| Setting | Meaning |
| --- | --- |
| `ESP32_MQTT_HOST` | Explicit DNS name or IP reachable from the installed board and matching broker certificate SAN |
| `ESP32_MQTT_PORT` | Broker TLS listener, default `8883`; no plaintext fallback |
| `ESP32_MQTT_CA_FILE` | Server-side PEM public CA file used for the physical board |
| `ESP32_CREDENTIALS_FILE` | Optional private hardware-keyed device credential file; defaults to existing `MQTT_CREDENTIALS_FILE` |

The backend-only `MQTT_HOST`/`MQTT_CA_FILE` are deliberately not reused for the
board-facing endpoint. Loopback, wildcard, URL/port-in-host values and invalid
ports/PEM are rejected. Operators must provide a routable host and verify DNS,
reachability and certificate SAN at the installation location; the API cannot
prove those remotely.

Device credential JSON has the existing server-store shape:

```json
{
  "ESP32-EXAMPLE": {
    "password": "synthetic-example-only-do-not-use",
    "hmacKey": "0000000000000000000000000000000000000000000000000000000000000000"
  }
}
```

These are schema examples only. Never use sample values for a real device.
Mount/protect the actual file using the existing secrets deployment procedure;
do not put it in source, public folders, OpenAPI examples, logs, or telemetry.
An entry for another Hardware ID or `florabot-backend` is never used for a kiosk.
The HMAC is compared in constant time via a domain-protocol signature against the
same in-memory `DeviceKeyRing` used by Backend MQTT. After rotation, reconcile the
broker/device secret and active Backend key through the separate MQTT procedure;
this endpoint never generates replacement credentials. When MQTT is disabled or
no current Backend key is loaded, downloads fail closed as not provisioned.
Mosquitto password hashes cannot be converted back to plaintext; a missing
recoverable device password requires that separate rotation/provisioning process.

## Firmware, audit, and contract handoff

Save the response as `IOT/FloraBot_IOT/include/config.local.h`, fill local Wi-Fi,
then follow that repository's existing build/flash instructions. The generated
header uses the established IOT `hardware.h` channel table. For the older BE
`firmware/esp32` template without that file, a conditional compatibility branch
declares the existing empty channel array; its board wiring still needs to be
configured before operation. No GPIO map is invented or changed by this API.
Generated headers and firmware binaries contain secrets and remain private.

Success/failure audit actions are `ESP32_CONFIG_VIEWED`,
`ESP32_CONFIG_VIEW_DENIED`, `ESP32_CONFIG_DOWNLOADED`, and
`ESP32_CONFIG_DOWNLOAD_DENIED`, with actor ID, kiosk UUID (`entity_type=kiosks`),
database timestamp, bounded result code, and installation ID when applicable.
Missing installations have no resolvable kiosk UUID. A success audit must commit
before the file is returned. Admin searches through the existing
`GET /api/admin/audit-logs?entityType=kiosks&entityId={kioskId}` and optional
`action=ESP32_CONFIG_DOWNLOADED` (or the denied action). Existing audit responses
redact payloads. Unauthenticated/malformed requests rejected before a known actor
reaches the handler remain standard middleware failures.

The endpoint declarations export `Esp32ConfigMetadata`,
`Esp32ConfigDownloadRequest`, binary attachment schema, and status/role rules to
the existing OpenAPI export. Export with the repository's schema-only workflow:

```powershell
$output = Join-Path (Get-Location) 'artifacts/openapi.json'
dotnet run --project src/FloraBot.Api -- --export-openapi $output
```

Give this exported schema to FE, replace its `packages/contracts/openapi.json`
through the established workflow, and run `pnpm contracts`. Do not hand-edit
generated FE types. No frontend or IOT source changes are needed for this API.

`Esp32ConfigurationTests` uses isolated in-memory HTTP hosts and synthetic
temporary secret/certificate files. It covers role/scope guards, repeated scope
checks, no metadata secret reads, fresh reauthentication, file formatting,
missing/mismatched keys, deployment settings, and audit-before-disclosure.
`Esp32ConfigurationDatabaseTests` checks real PostgreSQL assignment changes,
live installation states, current password/account status, and audit payloads in
an isolated database. A real board heartbeat/ONLINE result remains installation
acceptance; an API test does not establish physical connectivity.
