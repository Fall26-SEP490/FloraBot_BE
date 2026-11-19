param([string]$ProvisionUser = 'postgres', [switch]$PreflightOnly)
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration
$root = 'C:\FloraBotMigration'
$zip = Join-Path $root 'FloraBot-Communication-20261120.zip'
$expectedZip = '__PACKAGE_SHA256__'
$expectedApi = '1.0.0+747a766089dddf3bea87b0e65033423bd2d98801'
$service = 'FloraBot-Communication'
$serviceRoot = 'C:\FloraBot\staging\communication-20261120'
$private = 'C:\FloraBot\secrets\communication-20261120'
$pg = Join-Path $env:ProgramFiles 'PostgreSQL\16\bin'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $root "Backups\Before-Communication-$stamp"
$dbName = 'florabot_communication'
$dbRole = 'florabot_communication'
$site = Get-Website -Name 'FloraBot-API'
$api = [Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath).TrimEnd('\')
$pool = [string]$site.ApplicationPool
$settings = @{}
$envSettings = @{}
$apiChanged = $false
$serviceCreated = $false
$previousPassword = [Environment]::GetEnvironmentVariable('PGPASSWORD','Process')
$previousEncoding = [Environment]::GetEnvironmentVariable('PGCLIENTENCODING','Process')
$secret = $null
$phase = 'preflight'

function Assert-Native([string]$Message) { if ($LASTEXITCODE -ne 0) { throw $Message } }
function Protect-AdminDirectory([string]$Path) {
    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    & icacls.exe $Path /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' | Out-Null
    Assert-Native 'Private directory ACL failed.'
}
function Merge-Settings($Object, [string]$Prefix) {
    foreach ($property in $Object.PSObject.Properties) {
        $key = if ($Prefix) { "$Prefix`:$($property.Name)" } else { $property.Name }
        if ($property.Value -is [pscustomobject]) { Merge-Settings $property.Value $key }
        else { $settings[$key] = [string]$property.Value }
    }
}
function Set-XmlVariable($Document, [string]$Name, [string]$Value) {
    $asp = $Document.SelectSingleNode('//aspNetCore')
    if (!$asp) { throw 'Missing aspNetCore configuration.' }
    $vars = $asp.SelectSingleNode('environmentVariables')
    if (!$vars) { $vars = $Document.CreateElement('environmentVariables'); [void]$asp.AppendChild($vars) }
    $entry = @($vars.ChildNodes | Where-Object { $_.name -ieq $Name })
    if ($entry.Count -gt 1) { throw 'Duplicate environment variable; stop to inspect.' }
    if ($entry.Count -eq 0) {
        $node = $Document.CreateElement('environmentVariable'); $node.SetAttribute('name',$Name); [void]$vars.AppendChild($node)
    } else { $node = $entry[0] }
    $node.SetAttribute('value',$Value)
}
function Wait-Pool([string]$Name, [string]$State) {
    for ($i=0; $i -lt 120; $i++) {
        if ((Get-WebAppPoolState -Name $Name).Value -eq $State) { return }
        Start-Sleep -Seconds 1
    }
    throw "Pool did not reach $State."
}
function Stop-Pool([string]$Name) {
    $state = (Get-WebAppPoolState -Name $Name).Value
    if ($state -eq 'Starting') { Wait-Pool $Name 'Started' }
    if ((Get-WebAppPoolState -Name $Name).Value -eq 'Started') { Stop-WebAppPool -Name $Name }
    Wait-Pool $Name 'Stopped'
}
function Wait-Http([string]$Url) {
    for ($i=0; $i -lt 15; $i++) {
        try { if ((Invoke-WebRequest $Url -UseBasicParsing -TimeoutSec 4).StatusCode -eq 200) { return } } catch {}
        Start-Sleep -Seconds 2
    }
    throw 'HTTP readiness failed.'
}
function New-RandomHex {
    $bytes = New-Object byte[] 32
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes); return ([BitConverter]::ToString($bytes)).Replace('-','').ToLowerInvariant() }
    finally { $rng.Dispose(); [Array]::Clear($bytes,0,$bytes.Length) }
}
function Assert-EmailQueueDrained {
    $pending = 'SELECT count(*) FROM cap.published WHERE "Name"=''florabot.seller.email-requested.v1'' AND "StatusName"<>''Succeeded'';' |
        & "$pg\psql.exe" -X @dbArgs -d $businessDatabase -w -qAt -v ON_ERROR_STOP=1 2> $errorLog
    Assert-Native 'Cannot check legacy email outbox.'
    $received = 'SELECT count(*) FROM cap.received WHERE "Name"=''florabot.seller.email-requested.v1'' AND "StatusName"<>''Succeeded'';' |
        & "$pg\psql.exe" -X @dbArgs -d $businessDatabase -w -qAt -v ON_ERROR_STOP=1 2> $errorLog
    Assert-Native 'Cannot check legacy email inbox.'
    if (($pending -join '').Trim() -ne '0' -or ($received -join '').Trim() -ne '0') {
        throw 'Legacy seller email events remain pending/failed; reconcile them before cutover.'
    }
}

try {
    $phase = 'API v53 and provisioning username checks'
    if ($ProvisionUser -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') { throw 'Invalid PostgreSQL provisioning username.' }
    if ($api -ne 'C:\FloraBot\staging\v53\API' -or $site.State -ne 'Started' -or
        (Get-Item -LiteralPath "$api\FloraBot.Api.dll").VersionInfo.ProductVersion -ne $expectedApi) { throw 'Expected confirmed API v53.' }
    $phase = 'new Communication resources, port, RAM and package checks'
    if ((Test-Path -LiteralPath $serviceRoot) -or (Test-Path -LiteralPath $private) -or
        (Get-Website | Where-Object Name -eq $service) -or (Test-Path "IIS:\AppPools\$service")) { throw 'Communication resources already exist; do not rerun activation.' }
    if (@(Get-NetTCPConnection -LocalPort 5090 -State Listen -ErrorAction SilentlyContinue).Count) { throw 'Port 5090 is occupied.' }
    if ((Get-CimInstance Win32_OperatingSystem).FreePhysicalMemory -lt 768KB) { throw 'Less than 768 MiB RAM free.' }
    if ((Get-FileHash -LiteralPath $zip).Hash -ne $expectedZip) { throw 'Package hash mismatch.' }
    $phase = 'effective Production SMTP and broker settings'
    [xml]$web = Get-Content -LiteralPath "$api\web.config" -Raw
    foreach ($e in [Environment]::GetEnvironmentVariables('Machine').GetEnumerator()) { $envSettings[([string]$e.Key).Replace('__',':')] = [string]$e.Value }
    $filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables/add"
    foreach ($e in Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter) { if ($e.name) { $envSettings[([string]$e.name).Replace('__',':')] = [string]$e.value } }
    foreach ($e in $web.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')) { $envSettings[([string]$e.name).Replace('__',':')] = [string]$e.value }
    $runtimeEnvironment = $envSettings['DOTNET_ENVIRONMENT']
    if (!$runtimeEnvironment) { $runtimeEnvironment = $envSettings['ASPNETCORE_ENVIRONMENT'] }
    if (!$runtimeEnvironment) { $runtimeEnvironment = 'Production' }
    if ($runtimeEnvironment -ne 'Production') { throw 'Expected Production environment.' }
    foreach ($f in @('appsettings.json','appsettings.Production.json')) {
        if (Test-Path -LiteralPath "$api\$f") { Merge-Settings (Get-Content -LiteralPath "$api\$f" -Raw | ConvertFrom-Json) '' }
    }
    foreach ($e in $envSettings.GetEnumerator()) { $settings[$e.Key] = $e.Value }
    if ($settings['EMAIL_PROVIDER'] -ine 'GoogleSmtp' -or $settings['GOOGLE_SMTP_ALLOWED_IN_PRODUCTION'] -ine 'true' -or
        !$settings['EMAIL_SENDER'] -or !$settings['GOOGLE_SMTP_APP_PASSWORD'] -or !$settings['RABBITMQ_PASSWORD'] -or
        $settings['COMMUNICATION_BROKER_ENABLED'] -ieq 'true') { throw 'Email/broker configuration differs from preflight.' }
    $phase = 'PostgreSQL connection string validation'
    $builder = New-Object System.Data.Common.DbConnectionStringBuilder
    # PowerShell's IDictionary adapter treats .ConnectionString assignment as a
    # dictionary key. Call the .NET setter explicitly to actually parse it.
    $builder.set_ConnectionString([string]$settings['DATABASE_URL'])
    $businessDatabase = [string]$builder['Database']
    if (!$businessDatabase -or $businessDatabase -notmatch '^[A-Za-z_][A-Za-z0-9_]{0,62}$') { throw 'Expected simple business DB name.' }
    $dbHost = [string]$builder['Host']; $dbPort = 5432
    if ($builder.ContainsKey('Port')) { $dbPort = [int]$builder['Port'] }
    if ($dbHost -notin @('127.0.0.1','localhost','::1')) { throw 'Only the confirmed local PostgreSQL instance is supported.' }
    $dbArgs = @('-h',$dbHost,'-p',[string]$dbPort,'-U',$ProvisionUser)
    if ($PreflightOnly) {
        [ordered]@{ ReadOnly=$true; ActivationPreflight='PASS'; ApiPath=$api; ApiBuild=$expectedApi;
            DatabaseConnectionParsed=$true; DatabaseIsLocal=$true; Provider='GoogleSmtp';
            Port5090Free=$true; NewCommunicationResourcesAbsent=$true } | ConvertTo-Json
        return
    }
    $phase = 'protected backup and package preparation'
    Protect-AdminDirectory $backup
    Copy-Item -LiteralPath "$api\web.config" -Destination "$backup\web.config.before"
    Backup-WebConfiguration -Name "FloraBot-before-communication-$stamp" | Out-Null
    $stage = Join-Path $root "Communication-stage-$stamp"
    Expand-Archive -LiteralPath $zip -DestinationPath $stage
    $manifest = Get-Content -LiteralPath "$stage\MANIFEST.json" -Raw | ConvertFrom-Json
    foreach ($file in $manifest.files) {
        if ((Get-FileHash -LiteralPath (Join-Path $stage $file.path)).Hash -ne $file.sha256) { throw 'Package member hash mismatch.' }
    }
    $secret = Read-Host "PostgreSQL password for provisioning user $ProvisionUser (needs CREATE ROLE and CREATE DATABASE)" -AsSecureString
    if ($secret.Length -eq 0) { throw 'Password is empty.' }
    $env:PGPASSWORD = ([pscredential]::new($ProvisionUser,$secret)).GetNetworkCredential().Password
    $env:PGCLIENTENCODING = 'UTF8'
    $errorLog = Join-Path $backup 'postgres-errors.log'
    $exists = & "$pg\psql.exe" -X @dbArgs -d postgres -w -qAt -v ON_ERROR_STOP=1 -c "SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname='$dbName') OR EXISTS(SELECT 1 FROM pg_roles WHERE rolname='$dbRole')" 2> $errorLog
    Assert-Native 'PostgreSQL preflight failed; inspect protected backup logs locally.'
    if (($exists -join '').Trim() -ne 'f') { throw 'Communication DB or role already exists; stop to inspect.' }
    $capable = & "$pg\psql.exe" -X @dbArgs -d postgres -w -qAt -v ON_ERROR_STOP=1 -c 'SELECT rolsuper OR (rolcreatedb AND rolcreaterole) FROM pg_roles WHERE rolname=current_user' 2> $errorLog
    Assert-Native 'Could not check provisioning privileges.'
    if (($capable -join '').Trim() -ne 't') { throw 'Provisioning login needs CREATE ROLE and CREATE DATABASE.' }
    $schema = & "$pg\psql.exe" -X @dbArgs -d $businessDatabase -w -qAt -v ON_ERROR_STOP=1 -c "SELECT to_regclass('notify.communication_deliveries') IS NOT NULL" 2> $errorLog
    Assert-Native 'Business schema check failed.'
    if (($schema -join '').Trim() -ne 't') { throw 'Communication API metadata migration is missing.' }
    Assert-EmailQueueDrained
    $phase = 'backup'
    & "$pg\pg_dump.exe" @dbArgs -d $businessDatabase -w -Fc -f "$backup\florabot.dump" 2> $errorLog
    Assert-Native 'Database backup failed.'
    & "$pg\pg_restore.exe" --list "$backup\florabot.dump" | Out-Null
    Assert-Native 'Backup catalog could not be read.'
    Write-Host "Backup verified: $backup\florabot.dump"
    Protect-AdminDirectory $private
    $dbPassword = New-RandomHex
    $serviceToken = New-RandomHex
    [pscredential]::new($dbRole,(ConvertTo-SecureString $dbPassword -AsPlainText -Force)) |
        Export-Clixml -LiteralPath "$private\database-credential.xml"
    $phase = 'dedicated database provisioning'
    # Generated hexadecimal secrets are SQL-literal safe and are sent via stdin, never process arguments.
    "CREATE ROLE $dbRole LOGIN PASSWORD '$dbPassword' NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT;" |
        & "$pg\psql.exe" -X @dbArgs -d postgres -w -q -v ON_ERROR_STOP=1 2> $errorLog | Out-Null
    Assert-Native 'Dedicated role creation failed; protected logs only.'
    "CREATE DATABASE $dbName OWNER $dbRole;" |
        & "$pg\psql.exe" -X @dbArgs -d postgres -w -q -v ON_ERROR_STOP=1 2> $errorLog | Out-Null
    Assert-Native 'Dedicated database creation failed. Role retained for inspection.'
    "REVOKE ALL ON DATABASE $dbName FROM PUBLIC; GRANT CONNECT ON DATABASE $dbName TO $dbRole;" |
        & "$pg\psql.exe" -X @dbArgs -d postgres -w -q -v ON_ERROR_STOP=1 2> $errorLog | Out-Null
    Assert-Native 'Dedicated database privilege setup failed.'
    $isolated = & "$pg\psql.exe" -X @dbArgs -d $businessDatabase -w -qAt -v ON_ERROR_STOP=1 -c "SELECT NOT has_table_privilege('$dbRole','identity.users','SELECT') AND NOT has_table_privilege('$dbRole','payment.payments','SELECT')" 2> $errorLog
    Assert-Native 'Could not check business-table isolation.'
    if (($isolated -join '').Trim() -ne 't') { throw 'PUBLIC business-table privileges require review before activating the new role.' }
    Write-Host 'Dedicated Communication database provisioned; business-table read access denied.'
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$previousPassword,'Process')
    New-Item -ItemType Directory -Path $serviceRoot | Out-Null
    Copy-Item -Path "$stage\Service\*" -Destination $serviceRoot -Recurse
    Copy-Item -LiteralPath "$stage\Probe" -Destination $private -Recurse
    $serviceDb = New-Object System.Data.Common.DbConnectionStringBuilder
    $serviceDb['Host']=$dbHost; $serviceDb['Port']=$dbPort; $serviceDb['Database']=$dbName
    $serviceDb['Username']=$dbRole; $serviceDb['Password']=$dbPassword; $serviceDb['Maximum Pool Size']=10
    $serviceDb['Application Name']='FloraBot.Communication'
    $vars = @{
        ASPNETCORE_ENVIRONMENT='Production'; DOTNET_ENVIRONMENT='Production'
        EMAIL_PROVIDER='GoogleSmtp'; EMAIL_SENDER=$settings['EMAIL_SENDER']
        EMAIL_SENDER_NAME=$settings['EMAIL_SENDER_NAME']; GOOGLE_SMTP_APP_PASSWORD=$settings['GOOGLE_SMTP_APP_PASSWORD']
        GOOGLE_SMTP_ALLOWED_IN_PRODUCTION='true'; COMMUNICATION_DATABASE_URL=$serviceDb.get_ConnectionString()
        COMMUNICATION_SERVICE_TOKEN=$serviceToken; COMMUNICATION_BROKER_ENABLED='true'
        COMMUNICATION_API_URL='http://127.0.0.1:5080'; DATA_PROTECTION_KEYS_PATH=(Join-Path $private 'keys')
        RABBITMQ_HOST=$settings['RABBITMQ_HOST']; RABBITMQ_PORT=$settings['RABBITMQ_PORT']
        RABBITMQ_USER=$settings['RABBITMQ_USER']; RABBITMQ_PASSWORD=$settings['RABBITMQ_PASSWORD']
        RABBITMQ_EXCHANGE=$settings['RABBITMQ_EXCHANGE']
    }
    if (!$vars.RABBITMQ_HOST) { $vars.RABBITMQ_HOST='127.0.0.1' }
    if (!$vars.RABBITMQ_PORT) { $vars.RABBITMQ_PORT='55672' }
    if (!$vars.RABBITMQ_USER) { $vars.RABBITMQ_USER='florabot' }
    if (!$vars.RABBITMQ_EXCHANGE) { $vars.RABBITMQ_EXCHANGE='florabot.'+$businessDatabase }
    [xml]$serviceWeb = '<configuration><location path="." inheritInChildApplications="false"><system.webServer><handlers><add name="aspNetCore" path="*" verb="*" modules="AspNetCoreModuleV2" resourceType="Unspecified" /></handlers><aspNetCore processPath="dotnet" arguments=".\FloraBot.Communication.dll" hostingModel="inprocess" stdoutLogEnabled="false" /></system.webServer></location></configuration>'
    foreach ($entry in $vars.GetEnumerator()) { Set-XmlVariable $serviceWeb $entry.Key ([string]$entry.Value) }
    $serviceWeb.Save((Join-Path $serviceRoot 'web.config'))
    # Recoverable credential/config backup, readable by administrators only.
    $serviceWeb.Save((Join-Path $private 'service.web.config'))
    New-WebAppPool -Name $service | Out-Null
    $phase = 'service IIS configuration'
    Set-ItemProperty -Path "IIS:\AppPools\$service" -Name managedRuntimeVersion -Value ''
    Set-ItemProperty -Path "IIS:\AppPools\$service" -Name startMode -Value 'AlwaysRunning'
    Set-ItemProperty -Path "IIS:\AppPools\$service" -Name processModel.idleTimeout -Value ([TimeSpan]::Zero)
    & icacls.exe $serviceRoot /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' "IIS AppPool\${service}:(OI)(CI)RX" | Out-Null
    Assert-Native 'Service binary ACL failed.'
    New-Item -ItemType Directory -Path "$private\keys" | Out-Null
    & icacls.exe $private /grant "IIS AppPool\${service}:RX" | Out-Null
    Assert-Native 'Service private traversal ACL failed.'
    & icacls.exe "$private\keys" /grant "IIS AppPool\${service}:(OI)(CI)M" | Out-Null
    Assert-Native 'Service key directory ACL failed.'
    New-Website -Name $service -PhysicalPath $serviceRoot -ApplicationPool $service -IPAddress '127.0.0.1' -Port 5090 | Out-Null
    $serviceCreated=$true
    Wait-Http 'http://127.0.0.1:5090/ready'
    $phase = 'service broker readiness before API cutover'
    & dotnet "$private\Probe\CommunicationProbe.dll" "$private\service.web.config" --broker-ready
    Assert-Native 'Service has no active broker consumer; API configuration not changed.'
    Write-Host 'Communication service HTTP readiness: PASS. Preparing guarded API cutover.'
    # Construct the only intended API changes; everything else is retained in the same XML.
    $next = New-Object Xml.XmlDocument; $next.PreserveWhitespace=$true; $next.Load("$api\web.config")
    Set-XmlVariable $next 'EMAIL_PROVIDER' 'Communication'
    Set-XmlVariable $next 'COMMUNICATION_SERVICE_URL' 'http://127.0.0.1:5090'
    Set-XmlVariable $next 'COMMUNICATION_SERVICE_TOKEN' $serviceToken
    Set-XmlVariable $next 'COMMUNICATION_BROKER_ENABLED' 'true'
    $next.Save((Join-Path $private 'api.web.config.next'))
    if ((Get-FileHash "$api\web.config").Hash -ne (Get-FileHash "$backup\web.config.before").Hash) { throw 'API config changed concurrently.' }
    $phase = 'email cutover'
    $apiChanged=$true
    Stop-Pool $pool
    # Recheck while no new API business request can enqueue a legacy email.
    $env:PGPASSWORD = ([pscredential]::new($ProvisionUser,$secret)).GetNetworkCredential().Password
    Assert-EmailQueueDrained
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$previousPassword,'Process')
    [IO.File]::WriteAllBytes("$api\web.config",[IO.File]::ReadAllBytes("$private\api.web.config.next"))
    Start-WebAppPool -Name $pool
    Wait-Http 'http://127.0.0.1:5080/api/auth/config'
    $phase = 'broker and eligibility canary'
    & dotnet "$private\Probe\CommunicationProbe.dll" "$private\service.web.config"
    Assert-Native 'Broker/callback probe failed; switching API back.'
    'Communication activation: PASS'
    'Internal binding: 127.0.0.1:5090; provider: GoogleSmtp; queue enabled.'
    'No test email sent. PayOS, AI, MQTT, artifacts and API Data Protection configuration retained.'
    "Rollback configuration: $backup\web.config.before"
    "Service private configuration and persistent keys: $private"
} catch {
    $safeReasons = @(
        'Invalid PostgreSQL provisioning username.', 'Expected confirmed API v53.',
        'Communication resources already exist; do not rerun activation.', 'Port 5090 is occupied.',
        'Less than 768 MiB RAM free.', 'Package hash mismatch.', 'Expected Production environment.',
        'Email/broker configuration differs from preflight.', 'Expected simple business DB name.',
        'Only the confirmed local PostgreSQL instance is supported.',
        'Provisioning login needs CREATE ROLE and CREATE DATABASE.',
        'Legacy seller email events remain pending/failed; reconcile them before cutover.'
    )
    $failureMessage = [string]$_.Exception.Message
    if ($failureMessage -in $safeReasons) { Write-Host "Safe failure reason: $failureMessage" }
    else { Write-Host "Failure type: $($_.Exception.GetType().Name); raw details suppressed." }
    if ($apiChanged) {
        Stop-Pool $pool
        [IO.File]::WriteAllBytes("$api\web.config",[IO.File]::ReadAllBytes("$backup\web.config.before"))
        Start-WebAppPool -Name $pool
        Wait-Http 'http://127.0.0.1:5080/api/auth/config'
        Write-Host 'API original configuration restored.'
    }
    if ($serviceCreated) {
        Stop-Website -Name $service
        Stop-Pool $service
    }
    # Never delete databases, roles, secrets or queues during rollback.
    throw "Activation did not complete during $phase. Created resources and protected backups were retained; do not rerun blindly."
} finally {
    [Environment]::SetEnvironmentVariable('PGPASSWORD',$previousPassword,'Process')
    [Environment]::SetEnvironmentVariable('PGCLIENTENCODING',$previousEncoding,'Process')
    if ($secret) { $secret.Dispose() }
    $dbPassword=$null; $serviceToken=$null; $settings.Clear(); $envSettings.Clear()
}
