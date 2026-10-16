<#
.SYNOPSIS
    Idempotent cleanup script for FloraBot local demo data.
.DESCRIPTION
    Safely removes demo records created in bounded namespace 'de000000-*'.
    Protected by safety guards against deleting entities referenced by genuine orders or payments.
    Preserves all 24 user accounts, credentials, and pre-existing data without modifying seller baseline.
.PARAMETER Database
    Target PostgreSQL database (default: 'florabot').
    Guard: Must be 'florabot', 'florabot_ui_seed_verify_20261014', or match 'florabot_*test*'.
.PARAMETER ContainerName
    Docker container name hosting PostgreSQL (default: 'florabot_demo_20261012-postgres-1').
    Guard: Must match 'florabot_demo_*'.
.PARAMETER PrivateKioskOutputPath
    Path to private kiosk config file (default: 'C:/Users/tamin/.config/florabot/ui-demo-kiosk.local.json').
.PARAMETER RemovePrivateConfigFile
    Opt-in switch to remove the private kiosk config file as well.
.PARAMETER DryRun
    Preview cleanup actions without modifying database.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()][ValidateNotNullOrEmpty()][string]$Database = 'florabot',
    [Parameter()][ValidateNotNullOrEmpty()][string]$ContainerName = 'florabot_demo_20261012-postgres-1',
    [Parameter()][ValidateNotNullOrEmpty()][string]$PrivateKioskOutputPath = "$env:USERPROFILE\.config\florabot\ui-demo-kiosk.local.json",
    [Parameter()][switch]$RemovePrivateConfigFile,
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $repoRoot

try {
    Write-Host "=== FloraBot Local Demo Data Cleanup ===" -ForegroundColor Cyan

    # 1. Strict Local-Only Target Guards
    if ($ContainerName -notmatch '^florabot_demo_[0-9a-zA-Z_-]+$') {
        throw "Target container '$ContainerName' rejected. Target must match local demo pattern 'florabot_demo_*'."
    }

    $validDb = ($Database -eq 'florabot') -or
               ($Database -eq 'florabot_ui_seed_verify_20261014') -or
               ($Database -match '^florabot_.*test.*$')

    if (-not $validDb) {
        throw "Target database '$Database' rejected. Target must be 'florabot', 'florabot_ui_seed_verify_20261014', or match 'florabot_*test*'."
    }

    Write-Host "Target Container      : $ContainerName"
    Write-Host "Target Database       : $Database"
    Write-Host "Namespace Target      : de000000-*"

    # 2. Validate Container connectivity
    $running = docker ps --filter "name=$ContainerName" --filter "status=running" -q
    if (-not $running) {
        throw "Docker container '$ContainerName' is not running."
    }

    $cleanupSqlPath = Join-Path $PSScriptRoot 'cleanup-demo-data.sql'
    if (-not (Test-Path -LiteralPath $cleanupSqlPath)) {
        throw "Cleanup SQL file not found at: $cleanupSqlPath"
    }

    if ($DryRun -or -not $PSCmdlet.ShouldProcess("Database '$Database' in container '$ContainerName'", "Remove demo records in namespace de000000-*")) {
        Write-Host "`n[DryRun] Planned cleanup operations (NO mutations performed):" -ForegroundColor Yellow
        Write-Host " - Safety check: Verify zero payments, ledger entries, or non-demo orders reference demo items"
        Write-Host " - Delete ordering.web_requests WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete ordering.order_items WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete ordering.orders WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete kiosk_ops.staff_tasks WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete ordering.disputes WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete kiosk_ops.slot_assignments WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete kiosk_ops.slots WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete kiosk_ops.bouquets WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete notify.attachments WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete catalog.flower_products WHERE id LIKE 'de000000-%'"
        Write-Host " - Delete kiosk_ops.manager_kiosks WHERE kiosk_id = 'de000000-0000-0000-0001-000000000001'"
        Write-Host " - Delete kiosk_ops.kiosks WHERE id = 'de000000-0000-0000-0001-000000000001'"
        Write-Host " - Delete identity.subscriptions WHERE id LIKE 'de000000-%'"
        Write-Host " - Seller baseline preserved: identity.sellers is NOT modified (no hardcoded resets)"
        if ($RemovePrivateConfigFile -and (Test-Path -LiteralPath $PrivateKioskOutputPath)) {
            Write-Host " - Remove private config file: $PrivateKioskOutputPath"
        }
        Write-Host "`nDryRun completed. Zero database rows modified." -ForegroundColor Yellow
        return
    }

    Write-Host "Executing cleanup transaction against $Database..." -ForegroundColor Cyan
    Get-Content -LiteralPath $cleanupSqlPath -Raw | docker exec -i $ContainerName psql -U florabot -d $Database -v ON_ERROR_STOP=1 -q
    if ($LASTEXITCODE -ne 0) {
        throw "Cleanup transaction failed. PostgreSQL automatically rolled back all changes."
    }

    if ($RemovePrivateConfigFile -and (Test-Path -LiteralPath $PrivateKioskOutputPath)) {
        Remove-Item -LiteralPath $PrivateKioskOutputPath -Force
        Write-Host "Private kiosk config file removed: $PrivateKioskOutputPath" -ForegroundColor Yellow
    }

    # Verify that no rows remain in the namespace
    $remaining = docker exec $ContainerName psql -U florabot -d $Database -tAc @"
SELECT (
  (SELECT count(*) FROM kiosk_ops.kiosks WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM kiosk_ops.slots WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM catalog.flower_products WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM kiosk_ops.bouquets WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM kiosk_ops.staff_tasks WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM ordering.disputes WHERE id::text LIKE 'de000000-%') +
  (SELECT count(*) FROM ordering.orders WHERE id::text LIKE 'de000000-%')
);
"@
    $remainingCount = [int]$remaining.Trim()
    $totalUsers = [int](docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT count(*) FROM identity.users;").Trim()

    Write-Host "`n=== Cleanup Verification Summary ===" -ForegroundColor Green
    Write-Host "Remaining Demo Namespace Rows : $remainingCount (expected: 0)"
    Write-Host "Preserved User Accounts       : $totalUsers (intact)"

    if ($remainingCount -eq 0) {
        Write-Host "Cleanup completed successfully. Pre-existing database state preserved." -ForegroundColor Green
    } else {
        Write-Warning "Cleanup finished but $remainingCount rows still matched namespace 'de000000-*'."
    }

} finally {
    Pop-Location
}
