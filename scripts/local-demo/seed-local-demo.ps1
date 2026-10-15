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
    Path to public image manifest JSON (default: 'scripts/local-demo/public-image-manifest.json').
.PARAMETER PrivateKioskOutputPath
    Private local output file for the generated device API key.
    Default: 'C:/Users/tamin/.config/florabot/ui-demo-kiosk.local.json'.
.PARAMETER PrivateAccountsOutputPath
    Private local output file for generated team accounts credentials.
    Default: 'C:/Users/tamin/.config/florabot/ui-demo-accounts.local.json'.
.PARAMETER CreateTeamAccounts
    Opt-in switch: creates 7 local test accounts (2 ADMIN, OPERATIONS_MANAGER, TECHNICIAN, SELLER, SELLER_STAFF, CUSTOMER across 6 roles)
    in reserved namespace 'df000000-*' with random passwords. Reuses existing fixture if present.
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
    [Parameter()][ValidateNotNullOrEmpty()][string]$ManifestPath = 'scripts/local-demo/public-image-manifest.json',
    [Parameter()][ValidateNotNullOrEmpty()][string]$PrivateKioskOutputPath = "$env:USERPROFILE\.config\florabot\ui-demo-kiosk.local.json",
    [Parameter()][ValidateNotNullOrEmpty()][string]$PrivateAccountsOutputPath = "$env:USERPROFILE\.config\florabot\ui-demo-accounts.local.json",
    [Parameter()][switch]$CreateTeamAccounts,
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
$accountsTempStagingPath = $null
$dbCommitted = $false

try {
    Write-Host "=== FloraBot Local Demo Data Seeding ===" -ForegroundColor Cyan

    # 1. Strict Local-Only Target Guards & Path Normalization
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

    $resolvedKioskPath = [System.IO.Path]::GetFullPath($PrivateKioskOutputPath)
    $resolvedAccountsPath = [System.IO.Path]::GetFullPath($PrivateAccountsOutputPath)

    if ($resolvedKioskPath -eq $resolvedAccountsPath) {
        throw "Private kiosk path and private accounts path cannot be identical ('$resolvedKioskPath')."
    }

    $normalizedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    if ($resolvedKioskPath.StartsWith($normalizedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Private kiosk output path '$resolvedKioskPath' cannot reside inside the repository worktree ('$normalizedRepoRoot'). Store private credentials outside Git."
    }

    if ($CreateTeamAccounts -and $resolvedAccountsPath.StartsWith($normalizedRepoRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Private accounts output path '$resolvedAccountsPath' cannot reside inside the repository worktree ('$normalizedRepoRoot'). Store private credentials outside Git."
    }

    Write-Host "Target Container      : $ContainerName"
    Write-Host "Target Database       : $Database"
    Write-Host "Image Manifest        : $ManifestPath"
    Write-Host "Private Kiosk Target  : $resolvedKioskPath"
    Write-Host "Team Accounts Opt-in  : $(if ($CreateTeamAccounts) { "ENABLED ($resolvedAccountsPath)" } else { 'DISABLED (reusing baseline accounts)' })"
    Write-Host "Subscription Opt-in   : $(if ($IncludePendingSubscription) { 'PENDING PAYMENT ONLY (no entitlement)' } else { 'DISABLED (approved seller baseline)' })"
    Write-Host "State Mode            : $(if ($ResetDemoState) { 'RESET (reverts demo progress)' } else { 'PRESERVE (idempotent, retains in-flight holds/tasks)' })"

    # 2. Validate Container and Database connectivity
    $running = docker ps --filter "name=$ContainerName" --filter "status=running" -q
    if (-not $running) {
        throw "Docker container '$ContainerName' is not running. Start the demo stack first."
    }

    $rawUserCount = @(docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT count(*) FROM identity.users;")
    if ($LASTEXITCODE -ne 0) {
        throw "Cannot connect to database '$Database' inside container '$ContainerName'."
    }
    $userCountText = ($rawUserCount -join "`n").Trim()
    if (-not $userCountText) {
        throw "Cannot read user count from database '$Database' inside container '$ContainerName'."
    }
    $userCount = [int]$userCountText
    if ($userCount -lt 12) {
        throw "Target database '$Database' has unexpected user count ($userCount). Expected at least 12 baseline accounts."
    }
    Write-Host "Pre-existing accounts : $userCount verified intact." -ForegroundColor Green

    # 3. Validate Image Manifest
    $resolvedManifestPath = if ([System.IO.Path]::IsPathRooted($ManifestPath)) {
        $ManifestPath
    } else {
        $candidateRoot = Join-Path $repoRoot $ManifestPath
        if (Test-Path -LiteralPath $candidateRoot) {
            $candidateRoot
        } else {
            Join-Path $PSScriptRoot (Split-Path -Leaf $ManifestPath)
        }
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

    if (Test-Path -LiteralPath $resolvedKioskPath) {
        try {
            $existingConfig = Get-Content -LiteralPath $resolvedKioskPath -Raw | ConvertFrom-Json
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
    $rawDeviceBinding = @(docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT CASE WHEN api_key_hash='$deviceKeyHash' THEN 'MATCH' ELSE 'MISMATCH' END FROM kiosk_ops.kiosks WHERE id='$kioskId';")
    if ($LASTEXITCODE -ne 0) { throw 'Could not verify existing device binding.' }
    $deviceBinding = ($rawDeviceBinding -join "`n").Trim()
    if ($deviceBinding -eq 'MISMATCH' -and -not $RotateDeviceKey) {
        throw 'Existing device uses another key. Recover its private config or explicitly rotate; no rows or files were changed.'
    }

    # 4b. Team Accounts Provisioning & Binding
    $accountsTempStagingPath = $null
    $teamAccountsToSeed = @()
    $reusingAccountsFixture = $false

    if ($CreateTeamAccounts) {
        $teamAccountDefs = @(
            @{ role = 'ADMIN'; id = 'df000000-0000-0000-0000-000000000001'; email = 'ui-admin@florabot.test'; fullName = 'Admin Demo (Quản trị viên)'; sellerId = $null; loginRoute = '/api/auth/admin/login'; uiRoute = '/admin' }
            @{ role = 'ADMIN'; id = 'df000000-0000-0000-0000-000000000007'; email = 'ui-admin2@florabot.test'; fullName = 'Admin 2 Demo (Quản trị viên 2)'; sellerId = $null; loginRoute = '/api/auth/admin/login'; uiRoute = '/admin' }
            @{ role = 'OPERATIONS_MANAGER'; id = 'df000000-0000-0000-0000-000000000002'; email = 'ui-operations-manager@florabot.test'; fullName = 'Operations Manager Demo (Quản lý Vận hành)'; sellerId = $null; loginRoute = '/api/auth/admin/login'; uiRoute = '/operations' }
            @{ role = 'TECHNICIAN'; id = 'df000000-0000-0000-0000-000000000003'; email = 'ui-technician@florabot.test'; fullName = 'Technician Demo (Kỹ thuật viên)'; sellerId = $null; loginRoute = '/api/auth/admin/login'; uiRoute = '/technician' }
            @{ role = 'SELLER'; id = 'df000000-0000-0000-0000-000000000004'; email = 'ui-seller@florabot.test'; fullName = 'Seller Demo (Chủ shop Hoa Sài Gòn)'; sellerId = '20000000-0000-0000-0000-000000000001'; loginRoute = '/api/auth/login'; uiRoute = '/seller/products' }
            @{ role = 'SELLER_STAFF'; id = 'df000000-0000-0000-0000-000000000005'; email = 'ui-seller-staff@florabot.test'; fullName = 'Seller Staff Demo (Nhân viên Hoa Sài Gòn)'; sellerId = '20000000-0000-0000-0000-000000000001'; loginRoute = '/api/auth/login'; uiRoute = '/staff' }
            @{ role = 'CUSTOMER'; id = 'df000000-0000-0000-0000-000000000006'; email = 'ui-customer@florabot.test'; fullName = 'Customer Demo (Khách hàng)'; sellerId = $null; loginRoute = '/api/auth/login'; uiRoute = '/tai-khoan' }
        )

        $rawTeamUsers = @(docker exec $ContainerName psql -U florabot -d $Database -tAc "SELECT count(*) FROM identity.users WHERE id::text LIKE 'df000000-%';")
        if ($LASTEXITCODE -ne 0) { throw "Could not verify team accounts presence in database." }
        $existingTeamUsersCount = [int](($rawTeamUsers -join "`n").Trim())

        if (Test-Path -LiteralPath $resolvedAccountsPath) {
            try {
                $existingAccountsFixture = Get-Content -LiteralPath $resolvedAccountsPath -Raw | ConvertFrom-Json
            } catch {
                throw "Existing private accounts config at '$resolvedAccountsPath' is invalid; preserve it and choose another output path."
            }

            if ($existingAccountsFixture.containerName -ne $ContainerName -or $existingAccountsFixture.database -ne $Database) {
                throw "Private accounts config belongs to another target ($($existingAccountsFixture.containerName)/$($existingAccountsFixture.database)). Cross-DB reuse is prohibited; use a separate output path."
            }

            $existingAccountsList = @($existingAccountsFixture.accounts)
            if ($existingAccountsList.Count -ne 6 -and $existingAccountsList.Count -ne 7) {
                throw "Existing private accounts fixture does not contain 6 or 7 accounts (found: $($existingAccountsList.Count))."
            }

            $charSet = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*+='
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

            foreach ($def in $teamAccountDefs) {
                $matching = @($existingAccountsList | Where-Object { $_.id -eq $def.id })
                if ($matching.Count -gt 1) {
                    throw "Existing private accounts fixture contains duplicate entries for ID '$($def.id)'."
                }

                if ($matching.Count -eq 1) {
                    $acc = $matching[0]
                    if ($acc.role -ne $def.role) {
                        throw "Existing private accounts fixture contains mismatched role for account '$($def.id)' (found: '$($acc.role)', expected: '$($def.role)')."
                    }
                    if ($acc.email -ne $def.email) {
                        throw "Existing private accounts fixture contains mismatched email for account '$($def.id)' (found: '$($acc.email)', expected: '$($def.email)')."
                    }
                    if (-not $acc.password) {
                        throw "Existing private accounts fixture is missing password for account '$($def.id)'."
                    }
                    $pwBytes = [System.Text.Encoding]::UTF8.GetByteCount([string]$acc.password)
                    if ($pwBytes -lt 12 -or $pwBytes -gt 72) {
                        throw "Existing private accounts fixture password for account '$($def.id)' must be between 12 and 72 UTF-8 bytes (found: $pwBytes bytes)."
                    }

                    $teamAccountsToSeed += [ordered]@{
                        role = $def.role
                        id = $def.id
                        email = $def.email
                        fullName = $def.fullName
                        sellerId = $def.sellerId
                        password = [string]$acc.password
                        loginRoute = $def.loginRoute
                        uiRoute = $def.uiRoute
                    }
                } else {
                    $passBytes = [byte[]]::new(20)
                    $rng.GetBytes($passBytes)
                    $passChars = foreach ($b in $passBytes) { $charSet[$b % $charSet.Length] }
                    $generatedPassword = ($passChars -join '')

                    $teamAccountsToSeed += [ordered]@{
                        role = $def.role
                        id = $def.id
                        email = $def.email
                        fullName = $def.fullName
                        sellerId = $def.sellerId
                        password = $generatedPassword
                        loginRoute = $def.loginRoute
                        uiRoute = $def.uiRoute
                    }
                }
            }

            $reusingAccountsFixture = $true
            Write-Host "Team Accounts         : Reusing existing private fixture bound to this target ($(if ($existingAccountsList.Count -eq 6) { 'extended to 7 accounts' } else { '7 accounts verified' }))." -ForegroundColor Yellow
        } else {
            # Missing fixture for already-generated users must fail rather than reset
            if ($existingTeamUsersCount -gt 0) {
                throw "Missing private fixture at '$resolvedAccountsPath' for already-generated team accounts ($existingTeamUsersCount found in database). Rerun must reuse valid private fixture rather than resetting credentials."
            }

            # Check for collisions with reserved IDs or reserved emails
            $collisionCheckQuery = "SELECT id || ' ' || email FROM identity.users WHERE id::text LIKE 'df000000-%' OR lower(trim(email)) IN ('ui-admin@florabot.test', 'ui-admin2@florabot.test', 'ui-operations-manager@florabot.test', 'ui-operations_manager@florabot.test', 'ui-technician@florabot.test', 'ui-seller@florabot.test', 'ui-seller-staff@florabot.test', 'ui-seller_staff@florabot.test', 'ui-customer@florabot.test');"
            $rawCollisions = @(docker exec $ContainerName psql -U florabot -d $Database -tAc $collisionCheckQuery)
            if ($LASTEXITCODE -ne 0) { throw "Could not verify database account collisions." }
            $collisions = ($rawCollisions -join "`n").Trim()
            if ($collisions) {
                throw "Reserved ID or email collision detected in database: $collisions. Existing accounts will not be overwritten."
            }

            # Cryptographically generate per-account passwords (12..72 UTF-8 bytes, no single quotes or backslashes)
            $charSet = 'abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789!@#$%&*+='
            $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()

            foreach ($def in $teamAccountDefs) {
                $passBytes = [byte[]]::new(20)
                $rng.GetBytes($passBytes)
                $passChars = foreach ($b in $passBytes) { $charSet[$b % $charSet.Length] }
                $generatedPassword = ($passChars -join '')

                $teamAccountsToSeed += [ordered]@{
                    role = $def.role
                    id = $def.id
                    email = $def.email
                    fullName = $def.fullName
                    sellerId = $def.sellerId
                    password = $generatedPassword
                    loginRoute = $def.loginRoute
                    uiRoute = $def.uiRoute
                }
            }

            Write-Host "Team Accounts         : Generated cryptographically secure passwords for 7 test accounts (2 Admins, 6 roles)." -ForegroundColor Green
        }
    }

    # 5. DryRun Check (Zero mutations and zero files created)
    if ($DryRun -or -not $PSCmdlet.ShouldProcess("Database '$Database' in container '$ContainerName'", "Seed local demo data")) {
        Write-Host "`n[DryRun] Planned operations (NO mutations performed):" -ForegroundColor Yellow
        Write-Host " - Target Container: $ContainerName"
        Write-Host " - Target Database: $Database"
        Write-Host " - Demo Kiosk: $kioskId ($kioskCode), status OFFLINE (Simulated - No Hardware)"
        Write-Host " - Private Config Target: $resolvedKioskPath"
        if ($CreateTeamAccounts) {
            Write-Host " - Team Accounts: 7 test accounts (2 ADMIN, OPERATIONS_MANAGER, TECHNICIAN, SELLER, SELLER_STAFF, CUSTOMER) in df000000-*"
            Write-Host " - Private Accounts Target: $resolvedAccountsPath"
        }
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

    # 6. Secure Temporary Staging of Private Configs (DO NOT overwrite targets until committed)
    $outputDir = Split-Path -Parent $resolvedKioskPath
    if ($outputDir -and -not (Test-Path -LiteralPath $outputDir)) {
        New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
    }

    $tempStagingPath = "$resolvedKioskPath.tmp.$([System.Guid]::NewGuid().ToString('N'))"

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

    if ($CreateTeamAccounts) {
        $accountsOutputDir = Split-Path -Parent $resolvedAccountsPath
        if ($accountsOutputDir -and -not (Test-Path -LiteralPath $accountsOutputDir)) {
            New-Item -ItemType Directory -Path $accountsOutputDir -Force | Out-Null
        }

        $accountsTempStagingPath = "$resolvedAccountsPath.tmp.$([System.Guid]::NewGuid().ToString('N'))"
        $privateAccountsConfig = [ordered]@{
            containerName = $ContainerName
            database = $Database
            createdAt = if ($reusingAccountsFixture -and $existingAccountsFixture.createdAt) { $existingAccountsFixture.createdAt } else { [DateTimeOffset]::UtcNow.ToString("o") }
            updatedAt = [DateTimeOffset]::UtcNow.ToString("o")
            accounts = $teamAccountsToSeed
            notes = 'Local demo test accounts. NEVER commit, print, screenshot, or share credentials.'
        }
        $privateAccountsConfig | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $accountsTempStagingPath -Encoding UTF8
    }

    # 7. Construct Parameterized SQL Stream with Temporary Input Tables
    $sqlStream = [System.Text.StringBuilder]::new()
    [void]$sqlStream.AppendLine("BEGIN;")
    [void]$sqlStream.AppendLine("CREATE TEMP TABLE demo_input_config (key text PRIMARY KEY, value text NOT NULL) ON COMMIT DROP;")
    [void]$sqlStream.AppendLine("CREATE TEMP TABLE demo_input_manifest (slug text PRIMARY KEY, name text NOT NULL, url text NOT NULL, public_id text NOT NULL, sha256 text NOT NULL, size_bytes bigint NOT NULL, mime_type text NOT NULL) ON COMMIT DROP;")

    [void]$sqlStream.AppendLine("INSERT INTO demo_input_config (key, value) VALUES")
    [void]$sqlStream.AppendLine("  ('device_api_key_hash', '$deviceKeyHash'),")
    [void]$sqlStream.AppendLine("  ('enable_subscription', '$(if ($IncludePendingSubscription) { 'true' } else { 'false' })'),")
    [void]$sqlStream.AppendLine("  ('enable_team_accounts', '$(if ($CreateTeamAccounts) { 'true' } else { 'false' })'),")
    [void]$sqlStream.AppendLine("  ('reset_state', '$(if ($ResetDemoState) { 'true' } else { 'false' })');")

    $manifestSqlRows = foreach ($item in $manifestItems) {
        $safeName = $item.name.Replace("'", "''")
        $safeUrl = $item.url.Replace("'", "''")
        $safePublicId = $item.publicId.Replace("'", "''")
        "  ('{0}', '{1}', '{2}', '{3}', '{4}', {5}, '{6}')" -f $item.slug, $safeName, $safeUrl, $safePublicId, $item.sha256, [long]$item.sizeBytes, $item.mimeType
    }
    [void]$sqlStream.AppendLine("INSERT INTO demo_input_manifest (slug, name, url, public_id, sha256, size_bytes, mime_type) VALUES")
    [void]$sqlStream.AppendLine(($manifestSqlRows -join ",`n") + ";")

    if ($CreateTeamAccounts) {
        [void]$sqlStream.AppendLine("CREATE TEMP TABLE demo_input_team_accounts (id uuid PRIMARY KEY, email text NOT NULL, role text NOT NULL, full_name text NOT NULL, seller_id uuid, raw_password text NOT NULL) ON COMMIT DROP;")
        $accountSqlRows = foreach ($acc in $teamAccountsToSeed) {
            $safeFullName = $acc.fullName.Replace("'", "''")
            $safeEmail = $acc.email.Replace("'", "''")
            $safePassword = $acc.password.Replace("'", "''")
            $sellerIdSql = if ($acc.sellerId) { "'$($acc.sellerId)'::uuid" } else { "NULL" }
            "  ('{0}', '{1}', '{2}', '{3}', {4}, '{5}')" -f $acc.id, $safeEmail, $acc.role, $safeFullName, $sellerIdSql, $safePassword
        }
        [void]$sqlStream.AppendLine("INSERT INTO demo_input_team_accounts (id, email, role, full_name, seller_id, raw_password) VALUES")
        [void]$sqlStream.AppendLine(($accountSqlRows -join ",`n") + ";")

        # Read Team Accounts SQL Logic
        $teamSqlPath = Join-Path $PSScriptRoot 'team-accounts.sql'
        if (-not (Test-Path -LiteralPath $teamSqlPath)) {
            throw "Team accounts SQL file not found at: $teamSqlPath"
        }
        $teamSql = Get-Content -LiteralPath $teamSqlPath -Raw
        [void]$sqlStream.AppendLine($teamSql)
    }

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
    $dbCommitted = $true

    # 9. Atomic Publish of Private Configs ONLY AFTER Successful Commit
    try {
        [IO.File]::Move($tempStagingPath, $resolvedKioskPath, $true)
        $tempStagingPath = $null
        Write-Host "Device Key Config     : Staged and published atomically to: $resolvedKioskPath" -ForegroundColor Green
    } catch {
        Write-Warning "Database committed, but private kiosk config could not be published. Staged recovery file retained at: $tempStagingPath"
        throw "Database committed, but private kiosk config could not be published. Retained recovery file at '$tempStagingPath'."
    }

    if ($accountsTempStagingPath) {
        try {
            [IO.File]::Move($accountsTempStagingPath, $resolvedAccountsPath, $true)
            $accountsTempStagingPath = $null
            Write-Host "Team Accounts Config  : Staged and published atomically to: $resolvedAccountsPath" -ForegroundColor Green
        } catch {
            Write-Warning "Database committed, but private accounts config could not be published. Staged recovery file retained at: $accountsTempStagingPath"
            throw "Database committed, but private accounts config could not be published. Retained recovery file at '$accountsTempStagingPath'."
        }
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
UNION ALL SELECT 'Team Test Accounts (df000000-*)', count(*)::text FROM identity.users WHERE id::text LIKE 'df000000-%'
UNION ALL SELECT 'Total User Accounts (Preserved)', count(*)::text FROM identity.users;
"@
    docker exec $ContainerName psql -U florabot -d $Database -c $countsQuery

    Write-Host "`nLocal demo data seeded successfully in bounded namespace 'de000000-*'." -ForegroundColor Green
    Write-Host "Private kiosk config saved to: $resolvedKioskPath" -ForegroundColor Yellow
    if ($CreateTeamAccounts) {
        Write-Host "Private team accounts saved to: $resolvedAccountsPath" -ForegroundColor Yellow
    }
    Write-Host "To roll back this demo data, run: pwsh scripts/local-demo/cleanup-local-demo.ps1 -Database $Database" -ForegroundColor Cyan

} catch {
    if (-not $dbCommitted) {
        # If transaction failed before commit, clean up staging files and preserve prior working fixtures
        if ($tempStagingPath -and (Test-Path -LiteralPath $tempStagingPath)) {
            Remove-Item -LiteralPath $tempStagingPath -Force -ErrorAction SilentlyContinue
        }
        if ($accountsTempStagingPath -and (Test-Path -LiteralPath $accountsTempStagingPath)) {
            Remove-Item -LiteralPath $accountsTempStagingPath -Force -ErrorAction SilentlyContinue
        }
    } else {
        # Database was committed! Preserve ALL remaining private staging files for recovery. Report paths only (no credentials).
        if ($tempStagingPath -and (Test-Path -LiteralPath $tempStagingPath)) {
            Write-Warning "Preserved device key recovery staging file at: $tempStagingPath"
        }
        if ($accountsTempStagingPath -and (Test-Path -LiteralPath $accountsTempStagingPath)) {
            Write-Warning "Preserved team accounts recovery staging file at: $accountsTempStagingPath"
        }
    }
    throw
} finally {
    Pop-Location
}
