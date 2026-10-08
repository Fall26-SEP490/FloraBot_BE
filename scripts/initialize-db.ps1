param([switch]$Verify, [switch]$ApiTests, [switch]$JobsTests, [switch]$BrowserTests)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
try {
  if (@($Verify, $ApiTests, $JobsTests, $BrowserTests).Where({ $_ }).Count -gt 1) { throw 'Choose only one verification target.' }
  $targetDb = if ($Verify) { 'florabot_verification' } elseif ($ApiTests) { 'florabot_api_tests' } elseif ($JobsTests) { 'florabot_jobs_tests' } elseif ($BrowserTests) { 'florabot_browser_tests' } else { 'florabot' }
  if ($BrowserTests) {
    docker compose exec -T postgres psql -U florabot -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS florabot_browser_tests' -c 'CREATE DATABASE florabot_browser_tests'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated browser test database.' }
  }
  if ($JobsTests) {
    docker compose exec -T postgres psql -U florabot -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS florabot_jobs_tests' -c 'CREATE DATABASE florabot_jobs_tests'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated jobs test database.' }
  }
  if ($ApiTests) {
    docker compose exec -T postgres psql -U florabot -d postgres -v ON_ERROR_STOP=1 -c 'DROP DATABASE IF EXISTS florabot_api_tests' -c 'CREATE DATABASE florabot_api_tests'
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated API test database.' }
  }
  if ($Verify) {
    $testRole = docker compose exec -T postgres psql -U florabot -d postgres -tAc "SELECT EXISTS(SELECT 1 FROM pg_roles WHERE rolname='postgres')"
    if ($testRole.Trim() -ne 't') { docker compose exec -T postgres psql -U florabot -d postgres -c 'CREATE ROLE postgres LOGIN SUPERUSER' }
    docker compose exec -T postgres psql -U florabot -d postgres -v ON_ERROR_STOP=1 -c "DROP DATABASE IF EXISTS florabot_verification" -c "CREATE DATABASE florabot_verification"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot create isolated verification database.' }
  } elseif (!$ApiTests -and !$JobsTests -and !$BrowserTests) {
    $existing = docker compose exec -T postgres psql -U florabot -d florabot -tAc "SELECT EXISTS(SELECT 1 FROM pg_namespace WHERE nspname='identity')"
    if ($existing.Trim() -eq 't') { throw 'Database already initialized; refusing to replace existing data.' }
  }
  $files = @('01_schema.sql', '02_seed.sql', '03_flows.sql')
  if ($Verify) { $files += @('04_scenarios.sql', '05_demo_history.sql', '06_boundaries.sql', '07_regression.sql') }
  foreach ($file in $files) {
    docker compose exec -T postgres psql -U florabot -d $targetDb -v ON_ERROR_STOP=1 -q -f "/sql/$file"
    if ($LASTEXITCODE -ne 0) { throw "SQL failed: $file" }
  }
  if (!$Verify) {
    foreach ($migration in Get-ChildItem db/migrations -Filter '*.sql' | Sort-Object Name) {
      Get-Content $migration.FullName -Raw | docker compose exec -T postgres psql -U florabot -d $targetDb -v ON_ERROR_STOP=1 -q
      if ($LASTEXITCODE -ne 0) { throw "Migration failed: $($migration.Name)" }
    }
  }
} finally { Pop-Location }
