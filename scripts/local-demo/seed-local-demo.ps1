<#
.SYNOPSIS
    Idempotent local demo data seeder for FloraBot UI verification.
.DESCRIPTION
    Seeds representative artificial flower catalog, simulated tablet kiosk, slots, bouquets,
    tasks, incidents, and preorders into the bounded namespace 'de000000-*'.
    Preserves all 24 existing accounts and credentials. Does not fabricate settled money or ledger entries.
.PARAMETER Database
    Target PostgreSQL database (default: 'florabot').
    Guard: Must be 'florabot', 'florabot_ui_seed_verify_20261014', or match 'florabot_*test*'.
.PARAMETER ContainerName
    Docker container name hosting PostgreSQL (default: 'florabot_demo_20261012-postgres-1').
    Guard: Must match 'florabot_demo_*'.
.PARAMETER ManifestPath
    Path to public image manifest JSON (default: 'artifacts/worker-context/public-image-manifest.json').
.PARAMETER PrivateKioskOutputPath
    Private local output file for the generated device API key.
    Default: 'C:/Users/tamin/.config/florabot/ui-demo-kiosk.local.json'.
.PARAMETER IncludePendingSubscription
    Include an unpaid subscription example. Never activates the seller or grants entitlement.
.PARAMETER ResetDemoState
    Opt-in switch: updates existing demo rows to initial values instead of preserving progress.
.PARAMETER RotateDeviceKey
    Force generation of a new device API key even if private output file exists.
.PARAMETER DryRun
    Preview actions without modifying database or creating/modifying any files.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter()][ValidateNotNullOrEmpty()][string]$Database = 'florabot',
    [Parameter()][ValidateNotNullOrEmpty()][string]$ContainerName = 'florabot_demo_20261012-postgres-1',
    [Parameter()][ValidateNotNullOrEmpty()][string]$ManifestPath = 'artifacts/worker-context/public-image-manifest.json',
    [Parameter()][ValidateNotNullOrEmpty()][string]$PrivateKioskOutputPath = "$env:USERPROFILE\.config\florabot\ui-demo-kiosk.local.json",
    [Parameter()][switch]$IncludePendingSubscription,
    [Parameter()][switch]$ResetDemoState,
    [Parameter()][switch]$RotateDeviceKey,
    [Parameter()][switch]$DryRun
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
Push-Location $repoRoot

$tempStagingPath = $null

try {
    Write-Host "=== FloraBot Local Demo Data Seeding ===" -ForegroundColor Cyan

    # 1. Strict Local-Only Target Guards
    if ($ContainerName -notmatch '^florabot_demo_[0-9a-zA-Z_-]+$') {
        throw "Target container '$ContainerName' rejected. Target must match local demo pattern 'florabot_demo_*'."
    }

    $validDb = ($Database -eq 'florabot') -or
               ($Database -eq 'florabot_ui_seed_verify_20261014') -or
               ($Database -match '^florabot_[a-zA-Z0-9_]*test[a-zA-Z0-9_]*$')

    if (-not $validDb) {
        throw "Target database '$Database' rejected. Target must be 'florabot', 'florabot_ui_seed_verify_20261014', or match 'florabot_*test*'."
    }

    Write-Host "Target Container      : $ContainerName"
    Write-Host "Target Database       : $Database"
    Write-Host "Image Manifest        : $ManifestPath"
    Write-Host "Private Config Target : $PrivateKioskOutputPath"
    Write-Host "Subscription Opt-in   : $(if ($IncludePendingSubscription) { 'PENDING PAYMENT ONLY (no entitlement)' } else { 'DISABLED (approved seller baseline)' })"
    Write-Host "State Mode            : $(if ($ResetDemoState) { 'RESET (reverts demo progress)' } else { 'PRESERVE (idempotent, retains in-flight holds/tasks)' })"

    # 2. Validate Container and Database connectivity
    $running = docker ps --filter "name=$ContainerName" --filter "status=running" -q
    if (-not $running) {
        throw "Docker container '$ContainerName' is not running. Start the demo stack first."
    }

    $userCountRaw = docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT count(*) FROM identity.users;"
    if ($LASTEXITCODE -ne 0 -or -not $userCountRaw) {
        throw "Cannot connect to database '$Database' inside container '$ContainerName'."
    }
    $userCount = [int]$userCountRaw.Trim()
    if ($userCount -lt 12) {
        throw "Target database '$Database' has unexpected user count ($userCount). Expected at least 12 baseline accounts."
    }
    Write-Host "Pre-existing accounts : $userCount verified intact." -ForegroundColor Green

    # 3. Validate Image Manifest
    $resolvedManifestPath = if ([System.IO.Path]::IsPathRooted($ManifestPath)) {
        $ManifestPath
    } else {
        Join-Path $repoRoot $ManifestPath
    }

    if (-not (Test-Path -LiteralPath $resolvedManifestPath)) {
        throw "Image manifest file not found at: $resolvedManifestPath"
    }

    $manifestContent = Get-Content -LiteralPath $resolvedManifestPath -Raw | ConvertFrom-Json
    $manifestItems = @($manifestContent)
    if ($manifestItems.Count -ne 4) {
        throw "Manifest must contain exactly 4 items. Found: $($manifestItems.Count)"
    }

    $requiredSlugs = @('rose', 'pastel', 'sunflower', 'mini')
    foreach ($slug in $requiredSlugs) {
        $item = $manifestItems | Where-Object { $_.slug -eq $slug }
        if (-not $item -or -not $item.url -or -not $item.sha256 -or -not $item.sizeBytes -or -not $item.mimeType) {
            throw "Manifest item '$slug' is missing or incomplete (required: url, sha256, sizeBytes, mimeType)."
        }
        if ($item.sha256.Length -ne 64 -or $item.sha256 -notmatch '^[0-9a-f]{64}$') {
            throw "Manifest item '$slug' contains an invalid sha256 hash."
        }
        if ($item.slug -ne $slug -or $item.url -notmatch '^https://res\.cloudinary\.com/[a-zA-Z0-9_-]+/image/upload/v[0-9]+/florabot/[a-zA-Z0-9_-]+\.(png|jpg|jpeg|webp)$' -or
            $item.publicId -notmatch '^florabot/[a-zA-Z0-9_-]+$' -or $item.mimeType -notmatch '^image/(png|jpeg|webp)$' -or
            [long]$item.sizeBytes -lt 1 -or [long]$item.sizeBytes -gt 5242880) {
            throw "Manifest item '$slug' has invalid public media metadata."
        }
    }
    Write-Host "Image Manifest        : 4 verified artificial bouquet originals ready." -ForegroundColor Green

    # 4. Kiosk Local Test Device Key Provisioning & Binding
    $kioskId = 'de000000-0000-0000-0001-000000000001'
    $kioskCode = 'K-DEMO-SIM'
    $apiKey = $null

    if (Test-Path -LiteralPath $PrivateKioskOutputPath) {
        try {
            $existingConfig = Get-Content -LiteralPath $PrivateKioskOutputPath -Raw | ConvertFrom-Json
        } catch {
            throw "Existing private kiosk config is invalid; preserve it and choose another output path."
        }
        if ($existingConfig.containerName -ne $ContainerName -or $existingConfig.database -ne $Database -or $existingConfig.kioskId -ne $kioskId) {
            throw "Private config belongs to another target. Use a separate output file; it will not be overwritten."
        }
        if (-not $RotateDeviceKey) {
            if ($existingConfig.apiKey -notmatch '^[0-9a-fA-F]{64}$') { throw 'Existing device key is invalid; explicit rotation is required.' }
            $apiKey = $existingConfig.apiKey.ToLowerInvariant()
            Write-Host "Kiosk Device Key      : Reusing key bound to this target." -ForegroundColor Yellow
        }
    }

    if (-not $apiKey) {
        $keyBytes = [byte[]]::new(32)
        $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
        $rng.GetBytes($keyBytes)
        $apiKey = ($keyBytes | ForEach-Object { $_.ToString('x2') }) -join ''
        Write-Host "Kiosk Device Key      : Generated fresh cryptographically secure 32-byte key." -ForegroundColor Green
    }

    # Compute SHA-256 lowercase hash for database storage (no printing/logging)
    $hasher = [System.Security.Cryptography.SHA256]::Create()
    $hashBytes = $hasher.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($apiKey))
    $deviceKeyHash = ($hashBytes | ForEach-Object { $_.ToString('x2') }) -join ''
    $deviceBinding = docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT CASE WHEN api_key_hash='$deviceKeyHash' THEN 'MATCH' ELSE 'MISMATCH' END FROM kiosk_ops.kiosks WHERE id='$kioskId';"
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify existing device binding.' }
    if ($deviceBinding -and $deviceBinding.Trim() -eq 'MISMATCH' -and -not $RotateDeviceKey) {
        throw 'Existing device uses another key. Recover its private config or explicitly rotate; no rows or files were changed.'
    }

    # 5. DryRun Check (Zero mutations and zero files created)
    if ($DryRun -or -not $PSCmdlet.ShouldProcess("Database '$Database' in container '$ContainerName'", "Seed local demo data")) {
        Write-Host "`n[DryRun] Planned operations (NO mutations performed):" -ForegroundColor Yellow
        Write-Host " - Target Container: $ContainerName"
        Write-Host " - Target Database: $Database"
        Write-Host " - Demo Kiosk: $kioskId ($kioskCode), status OFFLINE (Simulated - No Hardware)"
        Write-Host " - Private Config Target: $PrivateKioskOutputPath"
        Write-Host " - Products to seed: 4 active artificial bouquets + 2 draft bouquets"
        Write-Host " - Slots to seed: 8 slots (W01-W04 WALK_IN, P01-P04 PREORDER) all leased to Hoa Sai Gon"
        Write-Host " - Bouquets to seed: 4 stocked artificial bouquets"
        Write-Host " - Staff tasks: 2 incident tasks for Technician, 2 delivery tasks for Seller Staff"
        Write-Host " - Disputes: 2 OPEN + 1 RESOLVED_FIXED device fault incidents"
        Write-Host " - Preorders: 1 QUOTED + 1 REQUESTED custom web requests"
        Write-Host " - Order: 1 AWAITING_PAYMENT representative order"
        Write-Host " - Subscription Opt-in: $(if ($IncludePendingSubscription) { 'Unpaid pending subscription' } else { 'None' })"
        Write-Host "`nDryRun completed. Zero files written and zero database rows modified." -ForegroundColor Yellow
        return
    }

    # 6. Secure Temporary Staging of Private Kiosk Config (DO NOT overwrite target until committed)
    $outputDir = Split-Path -Parent $PrivateKioskOutputPath
    if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $tempStagingPath = "$PrivateKioskOutputPath.tmp.$([System.Guid]::NewGuid().ToString('N'))"

    $privateKioskConfig = [ordered]@{
        kioskId = $kioskId
        kioskCode = $kioskCode
        kioskName = 'Kiosk Demo (Mô phỏng - Không phần cứng)'
        containerName = $ContainerName
        database = $Database
        apiKey = $apiKey
        authHeader = 'X-Kiosk-Key'
        hardwareStatus = 'OFFLINE'
        updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
        notes = 'Local demo test device. No physical hardware attached. Truthful API availability. NEVER print or commit this file.'
    }

    $privateKioskConfig | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $tempStagingPath -Encoding UTF8

    # 7. Construct Parameterized SQL Stream with Temporary Input Tables
    $sqlStream = [System.Text.StringBuilder]::new()
    [void]$sqlStream.AppendLine("BEGIN;")
    [void]$sqlStream.AppendLine("CREATE TEMP TABLE demo_input_config (key text PRIMARY KEY, value text NOT NULL) ON COMMIT DROP;")
    [void]$sqlStream.AppendLine("CREATE TEMP TABLE demo_input_manifest (slug text PRIMARY KEY, name text NOT NULL, url text NOT NULL, public_id text NOT NULL, sha256 text NOT NULL, size_bytes bigint NOT NULL, mime_type text NOT NULL) ON COMMIT DROP;")

    [void]$sqlStream.AppendLine("INSERT INTO demo_input_config (key, value) VALUES")
    [void]$sqlStream.AppendLine("  ('device_api_key_hash', '$deviceKeyHash'),")
    [void]$sqlStream.AppendLine("  ('enable_subscription', '$(if ($IncludePendingSubscription) { 'true' } else { 'false' })'),")
    [void]$sqlStream.AppendLine("  ('reset_state', '$(if ($ResetDemoState) { 'true' } else { 'false' })');")

    $manifestSqlRows = foreach ($item in $manifestItems) {
        $safeName = $item.name.Replace("'", "''")
        $safeUrl = $item.url.Replace("'", "''")
        $safePublicId = $item.publicId.Replace("'", "''")
        "  ('{0}', '{1}', '{2}', '{3}', '{4}', {5}, '{6}')" -f $item.slug, $safeName, $safeUrl, $safePublicId, $item.sha256, [long]$item.sizeBytes, $item.mimeType
    }
    [void]$sqlStream.AppendLine("INSERT INTO demo_input_manifest (slug, name, url, public_id, sha256, size_bytes, mime_type) VALUES")
    [void]$sqlStream.AppendLine(($manifestSqlRows -join ",`n") + ";")

    # Read Core SQL Logic
    $coreSqlPath = Join-Path $PSScriptRoot 'seed-demo-data.sql'
    if (-not (Test-Path -LiteralPath $coreSqlPath)) {
        throw "Core SQL file not found at: $coreSqlPath"
    }
    $coreSql = Get-Content -LiteralPath $coreSqlPath -Raw
    [void]$sqlStream.AppendLine($coreSql)
    [void]$sqlStream.AppendLine("COMMIT;")

    # 8. Execute in Single Transaction with Rollback on Error
    Write-Host "Executing database transaction against $Database..." -ForegroundColor Cyan
    $fullSql = $sqlStream.ToString()
    $fullSql | docker exec -i $ContainerName psql -U florabot -d $Database -v ON_ERROR_STOP=1 -q
    if ($LASTEXITCODE -ne 0) {
        throw "Database seeding transaction failed. PostgreSQL automatically rolled back all changes."
    }

    # 9. Atomic Publish of Private Kiosk Config ONLY AFTER Successful Commit
    try {
        [IO.File]::Move($tempStagingPath, [IO.Path]::GetFullPath($PrivateKioskOutputPath), $true)
        $tempStagingPath = $null
        Write-Host "Device Key Config     : Staged and published atomically to: $PrivateKioskOutputPath" -ForegroundColor Green
    } catch {
        Write-Warning "Recovery key file retained at: $tempStagingPath (DO NOT delete manually until recovered)"
        $tempStagingPath = $null
        throw 'Database committed, but private config could not be published. Recover the retained staging file before using this kiosk.'
    }

    # 10. Post-Seed Verification Counts
    Write-Host "`n=== Seeding Verification Summary ===" -ForegroundColor Green
    $countsQuery = @"
SELECT 'Kiosks' AS entity, count(*)::text FROM kiosk_ops.kiosks WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Manager Mappings', count(*)::text FROM kiosk_ops.manager_kiosks WHERE kiosk_id::text LIKE 'de000000-%'
UNION ALL SELECT 'Flower Products', count(*)::text FROM catalog.flower_products WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Product Attachments', count(*)::text FROM notify.attachments WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Bouquets', count(*)::text FROM kiosk_ops.bouquets WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Slots (Whole Kiosk)', count(*)::text FROM kiosk_ops.slots WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Slot Assignments', count(*)::text FROM kiosk_ops.slot_assignments WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Disputes (Incidents)', count(*)::text FROM ordering.disputes WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Staff Tasks', count(*)::text FROM kiosk_ops.staff_tasks WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Preorders (Web Requests)', count(*)::text FROM ordering.web_requests WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Orders (Awaiting Payment)', count(*)::text FROM ordering.orders WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Subscriptions (Opt-in)', count(*)::text FROM identity.subscriptions WHERE id::text LIKE 'de000000-%'
UNION ALL SELECT 'Total User Accounts (Preserved)', count(*)::text FROM identity.users;
"@
    docker exec $ContainerName psql -U florabot -d $Database -c $countsQuery

    Write-Host "`nLocal demo data seeded successfully in bounded namespace 'de000000-*'." -ForegroundColor Green
    Write-Host "Private kiosk config saved to: $PrivateKioskOutputPath" -ForegroundColor Yellow
    Write-Host "To roll back this demo data, run: pwsh scripts/local-demo/cleanup-local-demo.ps1 -Database $Database" -ForegroundColor Cyan

} catch {
    # If transaction failed, clean up staging file and preserve prior working fixture
    if ($tempStagingPath -and (Test-Path -LiteralPath $tempStagingPath)) {
        Remove-Item -LiteralPath $tempStagingPath -Force -ErrorAction SilentlyContinue
    }
    throw
} finally {
    Pop-Location
}
