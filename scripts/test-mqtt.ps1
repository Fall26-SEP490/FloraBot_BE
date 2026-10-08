$ErrorActionPreference = 'Stop'
$secretRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../infra/secrets/mqtt'))
$credentials = Get-Content -LiteralPath (Join-Path $secretRoot 'credentials.json') -Raw | ConvertFrom-Json -AsHashtable
$optionsRoot = Join-Path $secretRoot 'test-options'
New-Item -ItemType Directory -Force $optionsRoot | Out-Null
foreach ($username in $credentials.Keys) {
  $options = "-h mosquitto`n-p 8883`n--cafile /certs/ca.crt`n-V mqttv5`n-u $username`n-P $($credentials[$username].password)`n"
  [IO.File]::WriteAllText((Join-Path $optionsRoot $username), $options)
  if ($username -eq 'florabot-backend') {
    [IO.File]::WriteAllText((Join-Path $optionsRoot 'untrusted'), $options.Replace('--cafile /certs/ca.crt', '--tls-use-os-certs'))
  }
}
$checks = @'
set -eu
probe="__florabot_tls_probe__"
# Repeated non-retained probes avoid racing subscriber startup; none is an unlock command.
mosquitto_sub -o /options/ESP32-A1B2C3 -t kiosk/ESP32-A1B2C3/cmd -C 1 -W 8 > /tmp/command &
sub=$!
mosquitto_pub -o /options/florabot-backend -t kiosk/ESP32-A1B2C3/cmd -q 1 -m "$probe" --repeat 3 --repeat-delay 1
wait "$sub"
test "$(cat /tmp/command)" = "$probe"
echo 'PASS backend command to authorized device over TLS'
mosquitto_sub -o /options/florabot-backend -t kiosk/ESP32-A1B2C3/evt -C 1 -W 8 > /tmp/event &
sub=$!
mosquitto_pub -o /options/ESP32-A1B2C3 -t kiosk/ESP32-A1B2C3/evt -q 1 -m "$probe" --repeat 3 --repeat-delay 1
wait "$sub"
test "$(cat /tmp/event)" = "$probe"
echo 'PASS device event to backend over TLS'
# This client returns zero on a negative PUBACK; assert the broker's reason explicitly.
mosquitto_pub -o /options/ESP32-A1B2C3 -t kiosk/ESP32-D4E5F6/evt -q 1 -m "$probe" > /tmp/denial 2>&1
grep -q 'Not authorized' /tmp/denial
echo 'PASS cross-device event denied'
mosquitto_pub -o /options/ESP32-A1B2C3 -t kiosk/ESP32-A1B2C3/cmd -q 1 -m "$probe" > /tmp/denial 2>&1
grep -q 'Not authorized' /tmp/denial
echo 'PASS device cannot publish commands'
mosquitto_sub -o /options/ESP32-A1B2C3 -t kiosk/ESP32-D4E5F6/cmd -C 1 -W 5 > /tmp/foreign &
sub=$!
mosquitto_pub -o /options/florabot-backend -t kiosk/ESP32-D4E5F6/cmd -q 1 -m "$probe" --repeat 3 --repeat-delay 1
if wait "$sub"; then echo 'FAIL cross-device command delivered'; exit 1; fi
test ! -s /tmp/foreign
echo 'PASS no cross-device command delivery'
if mosquitto_pub -h mosquitto -p 8883 --cafile /certs/ca.crt -V mqttv5 -t kiosk/ESP32-A1B2C3/evt -q 1 -m "$probe"; then
  echo 'FAIL anonymous publication was accepted'; exit 1
fi
echo 'PASS anonymous connection denied'
if mosquitto_pub -o /options/untrusted -t kiosk/ESP32-A1B2C3/cmd -q 1 -m "$probe"; then
  echo 'FAIL untrusted certificate was accepted'; exit 1
fi
echo 'PASS untrusted CA rejected'
'@
[IO.File]::WriteAllText((Join-Path $optionsRoot 'checks.sh'), $checks.Replace("`r`n", "`n"))
docker run --rm --network florabot_default --mount "type=bind,source=$optionsRoot,target=/options,readonly" --mount "type=bind,source=$secretRoot/broker,target=/certs,readonly" eclipse-mosquitto:2.1.2-alpine sh /options/checks.sh
if ($LASTEXITCODE -ne 0) { throw 'MQTT transport/ACL checks failed.' }
