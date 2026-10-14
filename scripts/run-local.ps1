$ErrorActionPreference = 'Stop'
$envFile = Join-Path $PSScriptRoot '..\.env'
if (Test-Path $envFile) {
  foreach ($line in Get-Content $envFile) {
    $line = $line.Trim()
    if (!$line -or $line.StartsWith('#')) { continue }
    $separator = $line.IndexOf('=')
    if ($separator -lt 1) { continue }
    $name = $line.Substring(0, $separator).Trim()
    $value = $line.Substring($separator + 1).Trim()
    if ($value.Length -ge 2 -and (($value.StartsWith("'") -and $value.EndsWith("'")) -or ($value.StartsWith('"') -and $value.EndsWith('"')))) {
      $value = $value.Substring(1, $value.Length - 2)
    }
    if ([string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($name, 'Process'))) {
      [Environment]::SetEnvironmentVariable($name, $value, 'Process')
    }
  }
}
Push-Location (Join-Path $PSScriptRoot '..')
try {
  $env:ASPNETCORE_ENVIRONMENT = 'Development'
  if (!$env:DATABASE_URL) { $env:DATABASE_URL = 'Host=localhost;Port=55436;Database=florabot;Username=florabot;Password=local-florabot-only' }
  if (!$env:VALKEY_URL) { $env:VALKEY_URL = 'localhost:56379' }
  dotnet run --project src/FloraBot.Api --no-launch-profile --urls http://127.0.0.1:5080
} finally { Pop-Location }
