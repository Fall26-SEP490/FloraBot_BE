$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  docker build --target test -t florabot-simulator-tests ./firmware/simulator
  if ($LASTEXITCODE -ne 0) { throw 'Simulator image build failed.' }
  $mqttSecrets = [IO.Path]::GetFullPath((Join-Path (Get-Location) 'infra/secrets/mqtt'))
  $probeOutput = docker run --rm --network florabot_default --mount "type=bind,source=$mqttSecrets,target=/secrets,readonly" florabot-simulator-tests python -B probe_backend.py
  if ($LASTEXITCODE -ne 0) { throw 'MQTT inbox probe failed.' }
  $probe = $probeOutput | ConvertFrom-Json
  $good = [Guid]::Parse($probe.good).ToString()
  $bad = [Guid]::Parse($probe.bad).ToString()
  $foreign = [Guid]::Parse($probe.foreign).ToString()
  $query = "SELECT count(*) FILTER (WHERE event_id='$good' AND disposition='APPLIED'),count(*) FILTER (WHERE event_id IN ('$bad','$foreign')) FROM kiosk_ops.mqtt_inbox WHERE event_id IN ('$good','$bad','$foreign');"
  for ($attempt = 0; $attempt -lt 15; $attempt++) {
    $result = docker compose exec -T postgres psql -U florabot -d florabot -At -v ON_ERROR_STOP=1 -c $query
    if ($LASTEXITCODE -ne 0) { throw 'MQTT inbox verification query failed.' }
    if ($result.Trim() -eq '1|0') {
      Write-Output 'PASS actual MQTT -> API -> PostgreSQL: one applied heartbeat after repeated delivery; invalid signature and foreign command rejected.'
      return
    }
    Start-Sleep -Seconds 1
  }
  throw 'Backend did not process the MQTT heartbeat with the expected isolation/deduplication.'
} finally { Pop-Location }
