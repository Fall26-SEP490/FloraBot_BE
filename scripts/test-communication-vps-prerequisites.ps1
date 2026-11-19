param([string]$SiteName = 'FloraBot-API')
$ErrorActionPreference = 'Stop'
Import-Module WebAdministration

# Read only. Never emit config objects, connection strings, credentials or tokens.
function Merge-JsonSettings($Object, [string]$Prefix, [hashtable]$Target) {
    if ($null -eq $Object) { return }
    foreach ($property in $Object.PSObject.Properties) {
        $key = if ($Prefix) { "$Prefix`:$($property.Name)" } else { $property.Name }
        if ($property.Value -is [pscustomobject]) {
            Merge-JsonSettings $property.Value $key $Target
        } else { $Target[$key] = [string]$property.Value }
    }
}
function Test-TcpEndpoint([string]$Server, [int]$Port) {
    $client = New-Object Net.Sockets.TcpClient
    try {
        $pending = $client.BeginConnect($Server, $Port, $null, $null)
        if (!$pending.AsyncWaitHandle.WaitOne(1500)) { return $false }
        $client.EndConnect($pending)
        return $true
    } catch { return $false } finally { $client.Dispose() }
}

$site = Get-Website -Name $SiteName
if (!$site) { throw 'API site not found.' }
$live = [Environment]::ExpandEnvironmentVariables([string]$site.PhysicalPath)
$pool = [string]$site.ApplicationPool
$environment = @{}
$settings = @{}
try {
    foreach ($entry in [Environment]::GetEnvironmentVariables('Machine').GetEnumerator()) {
        $environment[([string]$entry.Key).Replace('__', ':')] = [string]$entry.Value
    }
    $filter = "system.applicationHost/applicationPools/add[@name='$pool']/environmentVariables/add"
    foreach ($entry in Get-WebConfiguration -PSPath 'MACHINE/WEBROOT/APPHOST' -Filter $filter) {
        if ($entry.name) { $environment[([string]$entry.name).Replace('__', ':')] = [string]$entry.value }
    }
    [xml]$web = Get-Content -LiteralPath (Join-Path $live 'web.config') -Raw
    foreach ($entry in $web.SelectNodes('//aspNetCore/environmentVariables/environmentVariable')) {
        $environment[([string]$entry.name).Replace('__', ':')] = [string]$entry.value
    }
    $runtimeEnvironment = $environment['DOTNET_ENVIRONMENT']
    if (!$runtimeEnvironment) { $runtimeEnvironment = $environment['ASPNETCORE_ENVIRONMENT'] }
    if (!$runtimeEnvironment) { $runtimeEnvironment = 'Production' }
    if ($runtimeEnvironment -notmatch '^[A-Za-z0-9_-]+$') { throw 'Unexpected environment name.' }
    foreach ($file in @('appsettings.json', "appsettings.$runtimeEnvironment.json")) {
        $path = Join-Path $live $file
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            Merge-JsonSettings (Get-Content -LiteralPath $path -Raw | ConvertFrom-Json) '' $settings
        }
    }
    foreach ($entry in $environment.GetEnumerator()) { $settings[$entry.Key] = $entry.Value }
    $provider = [string]$settings['EMAIL_PROVIDER']
    if (!$provider) { $provider = 'Disabled' }
    $providerLabel = if ($provider -in @('Disabled','Brevo','GoogleSmtp','Communication')) { $provider } else { 'UNKNOWN' }
    $brokerHost = [string]$settings['RABBITMQ_HOST']
    if (!$brokerHost) { $brokerHost = '127.0.0.1' }
    $brokerPort = 55672
    if ($settings['RABBITMQ_PORT']) { $brokerPort = [int]$settings['RABBITMQ_PORT'] }
    $os = Get-CimInstance Win32_OperatingSystem
    $disk = Get-Volume -DriveLetter C
    $poolState = (Get-WebAppPoolState -Name $pool).Value
    $result = [ordered]@{
        ReadOnly = $true
        ApiPath = $live
        ApiBuild = (Get-Item -LiteralPath (Join-Path $live 'FloraBot.Api.dll')).VersionInfo.ProductVersion
        SiteState = [string]$site.State
        PoolState = [string]$poolState
        RuntimeEnvironment = $runtimeEnvironment
        EmailProvider = $providerLabel
        EmailSenderPresent = ![string]::IsNullOrWhiteSpace($settings['EMAIL_SENDER'])
        BrevoKeyPresent = ![string]::IsNullOrWhiteSpace($settings['BREVO_API_KEY'])
        SmtpPasswordPresent = ![string]::IsNullOrWhiteSpace($settings['GOOGLE_SMTP_APP_PASSWORD'])
        SmtpProductionOptIn = ($settings['GOOGLE_SMTP_ALLOWED_IN_PRODUCTION'] -ieq 'true')
        DatabaseConfigured = ![string]::IsNullOrWhiteSpace($settings['DATABASE_URL'])
        BrokerPasswordPresent = ![string]::IsNullOrWhiteSpace($settings['RABBITMQ_PASSWORD'])
        BrokerExchangeExplicit = ![string]::IsNullOrWhiteSpace($settings['RABBITMQ_EXCHANGE'])
        BrokerTcpReachable = Test-TcpEndpoint $brokerHost $brokerPort
        Port5090Occupied = (@(Get-NetTCPConnection -State Listen -LocalPort 5090 -ErrorAction SilentlyContinue).Count -gt 0)
        CommunicationSiteExists = [bool](Get-Website | Where-Object Name -eq 'FloraBot-Communication')
        QueueEnabled = ($settings['COMMUNICATION_BROKER_ENABLED'] -ieq 'true')
        PayOsSellerVirtualAccounts = ($settings['PAYOS_CHANNELS:seller_tam:AllowVirtualAccounts'] -ieq 'true')
        AiUrlPresent = ![string]::IsNullOrWhiteSpace($settings['AI_SERVICE_URL'])
        AiTokenPresent = ![string]::IsNullOrWhiteSpace($settings['AI_SERVICE_TOKEN'])
        MqttEnabled = ($settings['MQTT_ENABLED'] -ieq 'true')
        DataProtectionPathConfigured = ![string]::IsNullOrWhiteSpace($settings['DATA_PROTECTION_KEYS_PATH'])
        RamFreeGB = [math]::Round($os.FreePhysicalMemory / 1MB, 2)
        DiskCFreeGB = [math]::Round($disk.SizeRemaining / 1GB, 2)
    }
    $result | ConvertTo-Json
} finally {
    $settings.Clear()
    $environment.Clear()
    $web = $null
}
