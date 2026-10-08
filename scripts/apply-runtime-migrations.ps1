param([ValidateSet('florabot','florabot_api_tests','florabot_jobs_tests')][string]$Database = 'florabot')
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  $migrationFiles = Get-ChildItem db/migrations -Filter '*.sql' | Sort-Object Name
  if (!$migrationFiles.Count) { throw 'No runtime migrations found.' }
  $migrationSql = @("SELECT pg_advisory_xact_lock(hashtext('florabot-runtime-migrations'));")
  $migrationSql += $migrationFiles | ForEach-Object { Get-Content -LiteralPath $_.FullName -Raw }
  # One transaction keeps function replacements and schema additions consistent on failure.
  ($migrationSql -join "`n") | docker compose exec -T postgres psql -U florabot -d $Database -v ON_ERROR_STOP=1 --single-transaction -q -f -
  if ($LASTEXITCODE -ne 0) { throw 'Runtime migrations failed; the transaction was rolled back.' }
  Write-Output "Applied $($migrationFiles.Count) runtime migrations to $Database. Existing data was preserved."
} finally { Pop-Location }
