# Automatic ESP32 provisioning on the native Windows broker

When enabled, kiosk enrollment commits a durable provisioning job with the new
kiosk. A worker creates its MQTT identity and isolated topic permissions, activates
the same per-device HMAC in Backend, then marks configuration `READY`. The user can
download `config.local.h`, fill local Wi-Fi, and flash the existing IOT firmware.
The kiosk stays `OFFLINE` until Backend receives its real signed heartbeat.

This extends the read/download contract in
[esp32-configuration-for-fe.md](esp32-configuration-for-fe.md). The screen's
`provision-key` flow is separate and unchanged.

## Why Dynamic Security

The implementation targets Mosquitto **2.1.2**, running as the existing native
Windows service. Dynamic Security updates users and roles through MQTT while the
broker runs; Windows uses `mosquitto_dynamic_security.dll`. Initial installation
requires adding the plugin and restarting the service once. Normal enrollment
does not restart Mosquitto or grant IIS permission to change broker files/services.
[Official plugin documentation](https://mosquitto.org/documentation/dynamic-security/)

Existing `password_file`, `acl_file`, TLS certificates, backend credentials, and
manual device HMACs are preserved. In the pinned source, unknown Dynamic Security
users defer authentication and ACL checks to other configured mechanisms.
Explicit deny ACLs on managed devices prevent that fallback from granting broader
access to a new managed identity. [Authentication source](https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/plugins/dynamic-security/auth.c),
[ACL source](https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/plugins/dynamic-security/acl.c)

Dynamic Security cannot list usernames known only to the static password file.
The private provisioner credential file therefore requires a `reservedUsernames`
array, collected during bootstrap. Backend rejects those usernames before broker
I/O. Preserve this list and update it whenever adding another manual static user;
otherwise a new Dynamic Security identity could shadow a broker-only legacy user.
The backend and provisioner usernames are always reserved.

## One-time bootstrap

Use an elevated **PowerShell 7** terminal on the intended VPS. The existing broker,
matching Dynamic Security DLL, `mosquitto_ctrl.exe`, IIS app pool, and Windows
service must already exist. Confirm `BrokerConfiguration` is the configuration
the service actually loads, and review any `include_dir` files to ensure the plugin
is not already configured elsewhere. The script expects global authentication
settings and refuses `per_listener_settings true`.

The new script has no embedded keys, installer downloads, firewall changes, or IIS
reset. It prepares dedicated private directories before creating any credentials:

```powershell
pwsh -File .\scripts\enable-esp32-auto-provisioning-windows.ps1
```

It calls `mosquitto_ctrl dynsec init` with a file path and username only. Enter a
new unique administrator password at the utility's secure prompts; enter the same
password again at the script's secure prompt to create the private Backend file.
Use at least 12 characters. Passwords are never passed in process arguments or
printed. The fresh administrator role is limited to the Dynamic Security command
and response topics; it does not subscribe to normal kiosk traffic. This control
credential can administer broker identities, so only Backend and administrators
should be able to read it. [Initialization and TLS CLI documentation](https://mosquitto.org/man/mosquitto_ctrl-1.html)

The default prepared paths are:

| Path | Access and use |
| --- | --- |
| `C:\FloraBot\data\mosquitto\dynamic-security\dynamic-security.json` | Broker-owned runtime state; service Modify, SYSTEM/Administrators FullControl |
| `C:\FloraBot\data\mosquitto\dynamic-security\florabot-dynamic-security.conf` | Prepared plugin snippet |
| `C:\FloraBot\secrets\mqtt\provisioning\admin.json` | SYSTEM/Administrators FullControl; `IIS AppPool\FloraBot-API` Read |

The initializer preserves existing files and refuses partial initialization rather
than rotating passwords. Existing target directories must be empty on the first
run. For a different app pool/service/account/path, pass the corresponding script
parameters. The broker service account needs write access to its dedicated state
directory because the plugin persists changes there.

Review the prepared snippet. To apply it and perform the one-time restart in the
same invocation, use the explicit switches on the **initial** run:

```powershell
pwsh -File .\scripts\enable-esp32-auto-provisioning-windows.ps1 `
  -ApplyToBrokerConfiguration -RestartMosquitto
```

Applying preserves a private copy of the previous `mosquitto.conf`. The appended
lines load the installed DLL and point it at the new runtime state. Existing
password/ACL/TLS lines remain intact. A completed initializer run is never rerun to
replace its files; if only preparing initially, apply the snippet through the
normal reviewed service configuration process and restart Mosquitto once.

The private admin JSON contract is:

```json
{
  "username": "florabot-provisioner",
  "password": "synthetic-schema-example-only",
  "reservedUsernames": ["florabot-backend", "florabot-provisioner", "ESP32-LEGACY"]
}
```

Do not use the example password. Do not run the older `setup-vps-mqtt.ps1` as part
of this rollout: that script writes fixed encoded credential material and replaces
existing broker files. The new bootstrap creates and preserves a separate runtime
configuration.

## Backend configuration and database

Apply the additive `db/migrations/20261109_esp32_auto_provisioning.sql` through the
existing migration workflow; do not recreate an existing database.

Configure these for the API app pool, then recycle it through the normal release
procedure:

| Setting | Required value/purpose |
| --- | --- |
| `MQTT_ENABLED` | `true` |
| `MQTT_AUTO_PROVISIONING_ENABLED` | `true` after broker bootstrap and verification |
| `MQTT_AUTO_PROVISIONING_POLL_SECONDS` | Optional worker polling interval; default 5 |
| `MQTT_PROVISIONING_ADMIN_FILE` | Private `admin.json` prepared above, including `reservedUsernames` |
| `MQTT_HOST`, `MQTT_PORT`, `MQTT_CA_FILE` | Backend-accessible TLS host/port and trusted CA; hostname verification remains enabled |
| `MQTT_CREDENTIALS_FILE` | Existing backend MQTT credential/HMAC file, preserved |
| `MQTT_CLIENT_ID` | Existing stable Backend MQTT identity |
| `DATA_PROTECTION_KEYS_PATH` | Persistent, protected key directory shared by intended API instances |
| `ESP32_MQTT_HOST`, `ESP32_MQTT_PORT`, `ESP32_MQTT_CA_FILE` | Board-facing TLS endpoint and public CA for downloaded firmware configuration |

The Backend endpoint may be loopback if its certificate covers that name/IP. The
board-facing endpoint must resolve and be reachable at the kiosk and match the
broker certificate SAN. Do not disable hostname/CA validation to bypass a mismatch.

Generated MQTT passwords and 32-byte HMACs are created once and stored together in
an ASP.NET Data Protection protected envelope in `kiosk_ops.esp32_provisioning_jobs`,
bound to kiosk UUID and Hardware ID. They are not stored as plaintext database
columns or returned by status endpoints. Preserve the Data Protection key ring
with database backups: losing it makes stored device credentials unrecoverable.
Do not replace these protected secrets with the broker's one-way password hash.

## Broker policy and retry behavior

The managed MQTT username is the exact Hardware ID. A role is named
`florabot-device-<kiosk UUID without hyphens>`. Both client and role carry the exact
ownership marker `FloraBot:<kiosk UUID without hyphens>:<Hardware ID>`.

Each device has exactly one role and no groups. Its role allows:

- `publishClientSend` only on `kiosk/<Hardware ID>/evt`.
- `publishClientReceive`, literal subscribe, and literal unsubscribe only on
  `kiosk/<Hardware ID>/cmd`.

The allow rules have priority 100. Lower-priority explicit deny rules cover `#`,
`$CONTROL/#`, and `$SYS/#` for sending, receiving, subscribing, and unsubscribing.
Wildcard subscriptions are disabled. Global defaults and existing roles are never
modified by enrollment. The exact wire API is documented in the
[pinned protocol README](https://github.com/eclipse-mosquitto/mosquitto/blob/v2.1.2/plugins/dynamic-security/README.md).

Every command is sent separately over TLS with MQTT QoS 1, retain false, a unique
JSON `correlationData`, expected command-name validation, bounded response size,
timeout, and cancellation. A response for another request cannot complete it.
Creation races are handled by rereading and validating ownership and exact policy.

An existing foreign marker, extra role/group, altered ACL, reserved username, or
disabled managed client fails as a permanent conflict. Valid retries do not call
`setClientPassword`, `modifyClient`, or `modifyRole`. They prove the saved password
through a real TLS connection and QoS-1 subscription to that device's command
topic. A unique probe client ID avoids disconnecting a live board. The MQTT
username and topic policy enforce isolation; the client ID is deliberately not
bound by Dynamic Security. Firmware still derives `esp32-<Hardware ID>` and the
new database registration defaults to that value.

The probe publishes neither commands nor events/heartbeats. Broker readiness is
separate from physical device readiness. Backend only reports provisioning
`READY` after broker verification and active HMAC installation succeed. A process
restart recovers READY device keys from the protected store; another API instance
loads the same protected device keys using the shared Data Protection key ring.

Transient failures retry using the same credentials within the persisted attempt
budget. Operator reconciliation is required for permanent ownership/key conflicts.
Status and audits contain bounded codes and identifiers, never passwords, HMACs,
configuration headers, broker response payloads, or raw transport exceptions.

Deleting an unused automatically provisioned kiosk disables only its exact owned
broker identity. Missing identities and already disabled identities are idempotent.
Foreign clients/roles are not deleted or changed. Retrying provisioning cannot
silently re-enable an administrator-disabled device.

## Verification and recovery

Before enabling automatic enrollment, verify the existing Backend and manual
devices still authenticate and retain their original topic permissions. Enroll a
new test kiosk through the authorized operations flow and check:

1. Enrollment returns `OFFLINE` with public provisioning state `PENDING`.
2. Provisioning progresses through `PROVISIONING` to `READY`; metadata exposes no
   MQTT/HMAC secret.
3. The assigned Manager or live-installation Technician reauthenticates and
   downloads `config.local.h`; Admin remains oversight only.
4. A broker test verifies own-topic access and cross-device/control-topic denial.
5. Fill Wi-Fi, flash the existing IOT firmware, and observe a real signed heartbeat
   moving the correct kiosk to `ONLINE`.

The isolated `Esp32AutoProvisioningBrokerTests` exercises a disposable Mosquitto
2.1.2 broker with synthetic TLS/admin/device credentials, including legacy
coexistence, idempotence, conflicts, TLS rejection, policy isolation, and revocation.
It does not operate the VPS service or prove a physical ESP32 connection.

If initial activation fails, retain the private state and credential files for
reconciliation. The saved pre-plugin broker configuration supports an operator
rollback during initial setup. After managed devices exist, removing the plugin
revokes their authentication and is not a transparent rollback. Do not restore a
stale plugin state or remove Data Protection keys as a way to retry enrollment;
reconcile the recorded error and reuse the existing per-device secret generation.
