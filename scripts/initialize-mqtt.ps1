$ErrorActionPreference = 'Stop'
$brokerImage = 'eclipse-mosquitto:2.1.2-alpine'
$secretRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../infra/secrets/mqtt'))
function Export-DeviceCredentials {
  $saved = Get-Content -LiteralPath (Join-Path $secretRoot 'credentials.json') -Raw | ConvertFrom-Json -AsHashtable
  $deviceDirectory = Join-Path $secretRoot 'devices'
  New-Item -ItemType Directory -Force $deviceDirectory | Out-Null
  foreach ($hardware in @('ESP32-A1B2C3', 'ESP32-D4E5F6')) {
    $devicePath = Join-Path $deviceDirectory "$hardware.json"
    if (!(Test-Path -LiteralPath $devicePath)) {
      $device = @{ hardware_id = $hardware; password = $saved[$hardware].password; hmacKey = $saved[$hardware].hmacKey }
      [IO.File]::WriteAllText($devicePath, ($device | ConvertTo-Json))
    }
  }
  $backendPath = Join-Path $secretRoot 'backend.json'
  if (!(Test-Path -LiteralPath $backendPath)) {
    $backend = @{ 'florabot-backend' = @{ password = $saved['florabot-backend'].password } }
    foreach ($hardware in @('ESP32-A1B2C3', 'ESP32-D4E5F6')) { $backend[$hardware] = @{ hmacKey = $saved[$hardware].hmacKey } }
    [IO.File]::WriteAllText($backendPath, ($backend | ConvertTo-Json -Depth 3))
  }
}
if (Test-Path -LiteralPath $secretRoot) {
  foreach ($file in @('credentials.json', 'broker/ca.crt', 'broker/server.crt', 'broker/server.key', 'broker/passwords')) {
    if (!(Test-Path -LiteralPath (Join-Path $secretRoot $file))) { throw "Incomplete MQTT provisioning: $file is missing." }
  }
  Export-DeviceCredentials
  Write-Output 'Existing local MQTT credentials preserved.'
  return
}
$openssl = (Get-Command openssl -ErrorAction Stop).Source
$stage = "$secretRoot-stage-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Force (Join-Path $stage 'broker') | Out-Null
Push-Location $stage
try {
  & $openssl req -x509 -newkey rsa:3072 -nodes -keyout ca.key -out broker/ca.crt -days 365 -subj '/CN=FloraBot Local MQTT CA' 2>$null
  if ($LASTEXITCODE -ne 0) { throw 'MQTT CA generation failed.' }
  & $openssl req -newkey rsa:3072 -nodes -keyout broker/server.key -out server.csr -subj '/CN=mosquitto' 2>$null
  if ($LASTEXITCODE -ne 0) { throw 'MQTT server key generation failed.' }
  [IO.File]::WriteAllText((Join-Path $stage 'server.ext'), "subjectAltName=DNS:mosquitto,DNS:localhost,IP:127.0.0.1`nextendedKeyUsage=serverAuth`n")
  & $openssl x509 -req -in server.csr -CA broker/ca.crt -CAkey ca.key -CAcreateserial -out broker/server.crt -days 365 -extfile server.ext 2>$null
  if ($LASTEXITCODE -ne 0) { throw 'MQTT server certificate generation failed.' }
  $credentials = @{}
  $passwordRows = foreach ($username in @('florabot-backend', 'ESP32-A1B2C3', 'ESP32-D4E5F6')) {
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    $credentials[$username] = @{ password = $password }
    if ($username -ne 'florabot-backend') {
      $credentials[$username].hmacKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    }
    "${username}:$password"
  }
  [IO.File]::WriteAllText((Join-Path $stage 'credentials.json'), ($credentials | ConvertTo-Json -Depth 3))
  [IO.File]::WriteAllLines((Join-Path $stage 'broker/passwords'), $passwordRows)
  # Hash via a private mounted file; credentials never appear in process arguments.
  docker run --rm --mount "type=bind,source=$stage/broker,target=/secure" $brokerImage mosquitto_passwd -U /secure/passwords
  if ($LASTEXITCODE -ne 0) { throw 'MQTT password hashing failed.' }
} finally { Pop-Location }
Move-Item -LiteralPath $stage -Destination $secretRoot
Export-DeviceCredentials
Write-Output 'Local MQTT TLS and per-device credentials provisioned in ignored infra/secrets/mqtt.'
