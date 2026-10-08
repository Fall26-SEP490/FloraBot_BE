$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  $env:ASPNETCORE_ENVIRONMENT = 'Development'
  if (!$env:DATABASE_URL) { $env:DATABASE_URL = 'Host=localhost;Port=55436;Database=florabot;Username=florabot;Password=local-florabot-only' }
  if (!$env:VALKEY_URL) { $env:VALKEY_URL = 'localhost:56379' }
  dotnet run --project src/FloraBot.Api --no-launch-profile --urls http://127.0.0.1:5080
} finally { Pop-Location }
