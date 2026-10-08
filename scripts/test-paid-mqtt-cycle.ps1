$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$previous = @{}
$started = $false
try {
  if (docker compose -f docker-compose.mqtt-test.yml ps --status running -q) {
    throw 'The isolated MQTT test stack is already running; refusing to take over its simulator.'
  }
  if (!(Test-Path infra/secrets/mqtt/backend.json)) { throw 'Run scripts/initialize-mqtt.ps1 first.' }
  $exists = docker compose exec -T postgres psql -U florabot -d postgres -At -v ON_ERROR_STOP=1 -c "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname='florabot_mqtt_tests')"
  if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the test database.' }
  if ($exists.Trim() -ne 't') {
    docker compose exec -T postgres psql -U florabot -d postgres -v ON_ERROR_STOP=1 -c 'CREATE DATABASE florabot_mqtt_tests'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create the isolated MQTT test database.' }
    foreach ($file in @('01_schema.sql', '02_seed.sql', '03_flows.sql')) {
      docker compose exec -T postgres psql -U florabot -d florabot_mqtt_tests -q -v ON_ERROR_STOP=1 -f "/sql/$file"
      if ($LASTEXITCODE -ne 0) { throw "Test database initialization failed: $file" }
    }
  }
  foreach ($migration in Get-ChildItem db/migrations -Filter '*.sql' | Sort-Object Name) {
    Get-Content $migration.FullName -Raw | docker compose exec -T postgres psql -U florabot -d florabot_mqtt_tests -q -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw "Test migration failed: $($migration.Name)" }
  }
  $settings = @{
    FLORABOT_MQTT_E2E = '1'
    TEST_DATABASE_URL = 'Host=localhost;Port=55436;Database=florabot_mqtt_tests;Username=florabot;Password=local-florabot-only'
    TEST_MQTT_CREDENTIALS = (Join-Path (Get-Location) 'infra/secrets/mqtt/backend.json')
    TEST_MQTT_CA = (Join-Path (Get-Location) 'infra/secrets/mqtt/broker/ca.crt')
  }
  foreach ($entry in $settings.GetEnumerator()) {
    $previous[$entry.Key] = [Environment]::GetEnvironmentVariable($entry.Key)
    [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value)
  }
  $started = $true
  docker compose -f docker-compose.mqtt-test.yml up -d --build
  if ($LASTEXITCODE -ne 0) { throw 'Isolated broker/simulator startup failed.' }
  dotnet test tests/FloraBot.Api.Tests --filter FullyQualifiedName~PaidMqttCycleTests --logger trx --results-directory artifacts
  if ($LASTEXITCODE -ne 0) { throw 'Paid MQTT cycle verification failed; database and simulator state retained.' }
} finally {
  if ($started) { docker compose -f docker-compose.mqtt-test.yml stop }
  foreach ($entry in $previous.GetEnumerator()) { [Environment]::SetEnvironmentVariable($entry.Key, $entry.Value) }
  Pop-Location
}
