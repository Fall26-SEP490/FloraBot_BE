<#
.SYNOPSIS
    Seeds unassigned offline kiosk inventory for annual kiosk onboarding UI demonstration.
.DESCRIPTION
    Seeds exactly 3 labelled fictional OFFLINE/unassigned whole kiosks matching Trial (1+1),
    Pro (4+4), and Max (6+6) explicit zone counts with FREE slots into reserved namespace 'da000000-*'.
    No seller lease, paid status/entitlement, photo/online heartbeat, or external services.
    Device authentication remains disabled (api_key_hash = NULL) until the Operations Manager
    explicitly provisions a key via the existing API endpoint POST /api/operations/kiosks/{id}/provision-key.
.PARAMETER Database
    Target PostgreSQL database (default: 'florabot').
    Guard: Must be 'florabot', 'florabot_ui_seed_verify_20261014', 'florabot_onboarding_migration_final_20261015', or match 'florabot_*test*'.
.PARAMETER ContainerName
    Docker container name hosting PostgreSQL (default: 'florabot_demo_20261012-postgres-1').
    Guard: Must match 'florabot_demo_*'.
.PARAMETER DryRun
    Preview actions without modifying database or creating any files.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()][ValidateNotNullOrEmpty()][string]$Database = 'florabot',
    [Parameter()][ValidateNotNullOrEmpty()][string]$ContainerName = 'florabot_demo_20261012-postgres-1',
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $repoRoot

try {
    Write-Host "=== FloraBot Annual Onboarding Assets Seeding ===" -ForegroundColor Cyan

    # 1. Strict Local-Only Target Guards
    if ($ContainerName -notmatch '^florabot_demo_[0-9a-zA-Z_-]+$') {
        throw "Target container '$ContainerName' rejected. Target must match local demo pattern 'florabot_demo_*'."
    }

    $validDb = ($Database -eq 'florabot') -or
               ($Database -eq 'florabot_ui_seed_verify_20261014') -or
               ($Database -eq 'florabot_onboarding_migration_final_20261015') -or
               ($Database -match '^florabot_[a-zA-Z0-9_]*test[a-zA-Z0-9_]*$')

    if (-not $validDb) {
        throw "Target database '$Database' rejected. Target must be 'florabot', 'florabot_ui_seed_verify_20261014', 'florabot_onboarding_migration_final_20261015', or match 'florabot_*test*'."
    }

    Write-Host "Target Container      : $ContainerName"
    Write-Host "Target Database       : $Database"
    Write-Host "Asset Namespace       : da000000-*"

    # 2. Validate Container and Database connectivity
    $running = docker ps --filter "name=$ContainerName" --filter "status=running" -q
    if (-not $running) {
        throw "Docker container '$ContainerName' is not running. Start the demo stack first."
    }

    $pingResult = @(docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT 1;")
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot connect to database '$Database' inside container '$ContainerName'."
    }

    # 3. DryRun Check (Zero mutations and zero files created)
    if ($DryRun -or -not $PSCmdlet.ShouldProcess("Database '$Database' in container '$ContainerName'", "Seed onboarding kiosk assets")) {
        Write-Host "`n[DryRun] Planned operations (NO mutations performed):" -ForegroundColor Yellow
        Write-Host " - Target Container: $ContainerName"
        Write-Host " - Target Database: $Database"
        Write-Host " - Kiosk 1 (Trial): da000000-0000-0000-0001-000000000001 (K-ONBOARD-TRIAL), 1 WALK_IN + 1 PREORDER (2 FREE slots)"
        Write-Host " - Kiosk 2 (Pro)  : da000000-0000-0000-0001-000000000002 (K-ONBOARD-PRO),   4 WALK_IN + 4 PREORDER (8 FREE slots)"
        Write-Host " - Kiosk 3 (Max)  : da000000-0000-0000-0001-000000000003 (K-ONBOARD-MAX),   6 WALK_IN + 6 PREORDER (12 FREE slots)"
        Write-Host " - Total Slots    : 22 slots across 3 unassigned OFFLINE kiosks"
        Write-Host " - Status         : All OFFLINE, no hardware heartbeat, auth disabled until Manager provision-key API call"
        Write-Host " - Assignments    : Zero seller leases, zero slot assignments, zero payments or ledger entries"
        Write-Host "`nDryRun completed. Zero files written and zero database rows modified." -ForegroundColor Yellow
        return
    }

    # 4. Read and Execute SQL Logic in a Single Transaction
    $sqlPath = Join-Path $PSScriptRoot 'seed-onboarding-assets.sql'
    if (-not (Test-Path -LiteralPath $sqlPath)) {
        throw "Onboarding assets SQL file not found at: $sqlPath"
    }

    $sqlContent = Get-Content -LiteralPath $sqlPath -Raw
    $sqlStream = [System.Text.StringBuilder]::new()
    [void]$sqlStream.AppendLine("BEGIN;")
    [void]$sqlStream.AppendLine($sqlContent)
    [void]$sqlStream.AppendLine("COMMIT;")

    Write-Host "Executing database transaction against $Database..." -ForegroundColor Cyan
    $fullSql = $sqlStream.ToString()
    $fullSql | docker exec -i $ContainerName psql -U florabot -d $Database -v ON_ERROR_STOP=1 -q
    if ($LASTEXITCODE -ne 0) {
        throw "Database onboarding assets seeding transaction failed. PostgreSQL automatically rolled back all changes."
    }

    # 5. Post-Seed Verification Counts
    Write-Host "`n=== Onboarding Assets Verification Summary ===" -ForegroundColor Green
    $countsQuery = @"
SELECT 'Onboarding Kiosks (da000000-*)' AS entity, count(*)::text FROM kiosk_ops.kiosks WHERE id::text LIKE 'da000000-%'
UNION ALL SELECT 'Onboarding Slots (Total)', count(*)::text FROM kiosk_ops.slots WHERE id::text LIKE 'da000000-%'
UNION ALL SELECT 'Trial Slots (1W+1P)', count(*)::text FROM kiosk_ops.slots WHERE kiosk_id = 'da000000-0000-0000-0001-000000000001'::uuid
UNION ALL SELECT 'Pro Slots (4W+4P)', count(*)::text FROM kiosk_ops.slots WHERE kiosk_id = 'da000000-0000-0000-0001-000000000002'::uuid
UNION ALL SELECT 'Max Slots (6W+6P)', count(*)::text FROM kiosk_ops.slots WHERE kiosk_id = 'da000000-0000-0000-0001-000000000003'::uuid
UNION ALL SELECT 'FREE Slots (Unassigned)', count(*)::text FROM kiosk_ops.slots WHERE id::text LIKE 'da000000-%' AND status = 'FREE' AND current_seller_id IS NULL
UNION ALL SELECT 'Slot Assignments (da000000-*)', count(*)::text FROM kiosk_ops.slot_assignments WHERE slot_id::text LIKE 'da000000-%'
UNION ALL SELECT 'Payments / Charges', count(*)::text FROM payment.payments WHERE id::text LIKE 'da000000-%'
UNION ALL SELECT 'Ledger Entries', count(*)::text FROM payment.ledger_entries WHERE id::text LIKE 'da000000-%';
"@
    docker exec $ContainerName psql -U florabot -d $Database -c $countsQuery

    Write-Host "`nOnboarding kiosk assets seeded successfully in bounded namespace 'da000000-*'." -ForegroundColor Green
    Write-Host "New devices have no authentication key. Existing keys and progress are preserved. Manager provisions keys via POST /api/operations/kiosks/{id}/provision-key." -ForegroundColor Yellow
} finally {
    Pop-Location
}
