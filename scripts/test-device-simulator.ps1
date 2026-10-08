$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  $runningSimulator = docker compose ps --status running -q device-simulator
  if ($runningSimulator) { throw 'Stop the interactive device-simulator before running its isolated integration test.' }
  docker build --target test -t florabot-simulator-tests ./firmware/simulator
  if ($LASTEXITCODE -ne 0) { throw 'Simulator build or unit tests failed.' }
  $mqttSecrets = [IO.Path]::GetFullPath((Join-Path (Get-Location) 'infra/secrets/mqtt'))
  docker run --rm --network florabot_default --mount "type=bind,source=$mqttSecrets,target=/secrets,readonly" florabot-simulator-tests python -B -m unittest -v test_mqtt
  if ($LASTEXITCODE -ne 0) { throw 'Simulator MQTT integration failed.' }
} finally { Pop-Location }
