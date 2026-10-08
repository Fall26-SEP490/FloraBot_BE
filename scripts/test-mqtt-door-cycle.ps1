$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$startedSimulator = $false
try {
  $runningSimulator = docker compose ps --status running -q device-simulator
  if ($runningSimulator) { throw 'Stop the interactive simulator before running this local door-cycle test.' }
  docker compose --profile simulator up -d --build device-simulator
  if ($LASTEXITCODE -ne 0) { throw 'Simulator startup failed.' }
  $startedSimulator = $true
  $ready = $false
  for ($attempt = 0; $attempt -lt 15; $attempt++) {
    $heartbeat = docker compose exec -T postgres psql -U florabot -d florabot -At -v ON_ERROR_STOP=1 -c "SELECT status='ONLINE' AND last_heartbeat_at>CURRENT_TIMESTAMP-interval '5 seconds' FROM kiosk_ops.kiosks WHERE hardware_id='ESP32-A1B2C3'"
    if ($LASTEXITCODE -ne 0) { throw 'Could not read simulator readiness.' }
    if ($heartbeat.Trim() -eq 't') { $ready = $true; break }
    Start-Sleep -Seconds 1
  }
  if (!$ready) { throw 'Simulator heartbeat did not reach the backend.' }
  $commandId = [Guid]::NewGuid().ToString()
  # Create/reuse only an empty, unassigned transport-test slot. No paid order or GPIO is involved.
  $fixture = @"
BEGIN;
WITH available AS (
  SELECT min(channel) AS relay FROM generate_series(0,63) channel
  WHERE NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id='40000000-0000-0000-0000-000000000001' AND relay_channel=channel)
)
INSERT INTO kiosk_ops.slots(kiosk_id,slot_code,relay_channel)
SELECT '40000000-0000-0000-0000-000000000001','MQTT-TRANSPORT-TEST',relay FROM available
WHERE relay IS NOT NULL AND NOT EXISTS (SELECT 1 FROM kiosk_ops.slots WHERE kiosk_id='40000000-0000-0000-0000-000000000001' AND slot_code='MQTT-TRANSPORT-TEST');
INSERT INTO kiosk_ops.unlock_tokens(kiosk_id,slot_id,purpose,issued_to_user_id,token_hash,cmd_id,issued_at,expires_at)
SELECT kiosk_id,id,'SELLER_ACCESS','10000000-0000-0000-0000-000000000001',
  encode(sha256(convert_to('$commandId','UTF8')),'hex'),'$commandId',CURRENT_TIMESTAMP,CURRENT_TIMESTAMP+interval '1 minute'
FROM kiosk_ops.slots s WHERE kiosk_id='40000000-0000-0000-0000-000000000001' AND slot_code='MQTT-TRANSPORT-TEST'
  AND status='FREE' AND current_seller_id IS NULL AND bouquet_id IS NULL
  AND NOT EXISTS (SELECT 1 FROM kiosk_ops.unlock_tokens t WHERE t.slot_id=s.id AND t.status IN ('ISSUED','SENT','ACKED','OPENED'))
RETURNING cmd_id;
COMMIT;
"@
  $created = $fixture | docker compose exec -T postgres psql -U florabot -d florabot -Atq -v ON_ERROR_STOP=1 -f -
  if ($LASTEXITCODE -ne 0 -or $created -notcontains $commandId) { throw 'Transport fixture could not be created safely.' }
  $query = "SELECT t.status,t.attempts,(SELECT count(*) FROM kiosk_ops.mqtt_inbox i WHERE i.cmd_id=t.cmd_id AND disposition='APPLIED'),d.pubacked_at IS NOT NULL FROM kiosk_ops.unlock_tokens t JOIN kiosk_ops.mqtt_dispatches d USING(cmd_id) WHERE t.cmd_id='$commandId'"
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $result = docker compose exec -T postgres psql -U florabot -d florabot -At -v ON_ERROR_STOP=1 -c $query
    if ($LASTEXITCODE -ne 0) { throw 'Door-cycle verification query failed.' }
    if ($result -and $result.Trim() -eq 'CLOSED|1|3|t') {
      Write-Output 'PASS API dispatch -> MQTT TLS -> persistent simulator -> signed inbox -> CLOSED, one send and three applied sensor events.'
      return
    }
    Start-Sleep -Seconds 1
  }
  throw 'The signed door cycle did not complete exactly once.'
} finally {
  if ($startedSimulator) { docker compose stop device-simulator }
  Pop-Location
}
