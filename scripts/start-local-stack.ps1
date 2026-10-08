param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  & (Join-Path $PSScriptRoot 'initialize-mqtt.ps1')
  $keyFile = Join-Path (Get-Location) 'infra/secrets/local-stack.env'
  if (!(Test-Path -LiteralPath $keyFile)) {
    New-Item -ItemType Directory -Force (Split-Path $keyFile) | Out-Null
    $stackSigningKey = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::WriteAllText($keyFile, "JWT_SIGNING_KEY=$stackSigningKey`n")
    $stackSigningKey = $null
  }
  if (!(Select-String -LiteralPath $keyFile -Pattern '^AI_SERVICE_TOKEN=' -Quiet)) {
    $advisorToken = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
    [IO.File]::AppendAllText($keyFile, "AI_SERVICE_TOKEN=$advisorToken`n")
    $advisorToken = $null
  }
  $composeArgs = @()
  if (Test-Path -LiteralPath '.env') { $composeArgs += @('--env-file', '.env') }
  $composeArgs += @('--env-file', $keyFile, '-f', 'docker-compose.yml', '-f', 'docker-compose.app.yml', 'up', '-d')
  if (!$NoBuild) { $composeArgs += '--build' }
  docker compose @composeArgs
  if ($LASTEXITCODE -ne 0) { throw 'Application stack startup failed.' }
  Write-Output 'Stack started: http://localhost:8088/ (landing), /login (portal), /kiosk/ (kiosk).'
} finally { Pop-Location }
