[CmdletBinding()]
param(
    [string]$MosquittoDirectory = 'C:\Program Files\mosquitto',
    [string]$BrokerConfiguration = 'C:\Program Files\mosquitto\mosquitto.conf',
    [string]$StateDirectory = 'C:\FloraBot\data\mosquitto\dynamic-security',
    [string]$AdminDirectory = 'C:\FloraBot\secrets\mqtt\provisioning',
    [string]$AdminUsername = 'florabot-provisioner',
    [string]$ApiIdentity = 'IIS AppPool\FloraBot-API',
    [string]$ServiceName = 'mosquitto',
    [switch]$ApplyToBrokerConfiguration,
    [switch]$RestartMosquitto
)

# One-time native Windows bootstrap. This script is intentionally separate from the old
# setup-vps-mqtt.ps1: no embedded keys, secret replacement, installers, firewall changes, or IIS reset.
$ErrorActionPreference = 'Stop'
if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { throw 'This bootstrap requires Windows PowerShell 5.1 or PowerShell 7 on Windows.' }
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this one-time bootstrap from an elevated Windows PowerShell 5.1 or PowerShell 7 terminal.'
}
if ($RestartMosquitto -and -not $ApplyToBrokerConfiguration) { throw 'RestartMosquitto requires ApplyToBrokerConfiguration.' }
if ($AdminUsername -notmatch '^[A-Za-z0-9_-]{3,100}$' -or $ServiceName -notmatch '^[A-Za-z0-9_-]+$') {
    throw 'Use a simple ASCII administrator username and service name.'
}

$brokerPath = [IO.Path]::GetFullPath($BrokerConfiguration)
$statePath = [IO.Path]::GetFullPath($StateDirectory)
$adminPath = [IO.Path]::GetFullPath($AdminDirectory)
$dynamicFile = Join-Path $statePath 'dynamic-security.json'
$adminFile = Join-Path $adminPath 'admin.json'
$snippetFile = Join-Path $statePath 'florabot-dynamic-security.conf'
$pluginPath = Join-Path $MosquittoDirectory 'mosquitto_dynamic_security.dll'
$brokerExe = Join-Path $MosquittoDirectory 'mosquitto.exe'
foreach ($required in @($brokerPath, $pluginPath, $brokerExe)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) { throw "Required installed file is missing: $required" }
}
if ((Test-Path -LiteralPath $dynamicFile) -or (Test-Path -LiteralPath $adminFile)) {
    if ((Test-Path -LiteralPath $dynamicFile) -and (Test-Path -LiteralPath $adminFile)) {
        Write-Output 'Existing Dynamic Security and provisioner credential files preserved. No credentials were regenerated.'
        Write-Output "Review the existing bootstrap configuration at $snippetFile before changing the running broker."
        return
    }
    throw 'Partial provisioning files already exist. Reconcile the existing configuration; this script will not overwrite or rotate it.'
}
$brokerText = [IO.File]::ReadAllText($brokerPath)
if ($brokerText -match '(?im)^\s*plugin\s+.*mosquitto_dynamic_security[.]dll\s*$') {
    throw 'Dynamic Security is already configured. Reconcile its existing administrator credentials instead of initializing another instance.'
}
if ($brokerText -match '(?im)^\s*per_listener_settings\s+true\s*(?:#.*)?$') {
    throw 'This bootstrap expects global authentication settings. Review the existing per-listener configuration before enabling the plugin.'
}
# Preserve every static identity, including broker-only users absent from the application DB.
# Only usernames leave the password-file parser; neither password hashes nor file lines are logged.
$reservedUsernames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
[void]$reservedUsernames.Add('florabot-backend')
[void]$reservedUsernames.Add($AdminUsername)
foreach ($match in [regex]::Matches($brokerText, '(?im)^\s*password_file\s+(.+?)\s*$')) {
    $passwordFile = $match.Groups[1].Value.Trim().Trim('"')
    if (-not [IO.Path]::IsPathRooted($passwordFile) -or -not (Test-Path -LiteralPath $passwordFile -PathType Leaf)) {
        throw 'Every existing password_file must resolve to a readable absolute file before bootstrap.'
    }
    foreach ($line in [IO.File]::ReadLines($passwordFile)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $separator = $line.IndexOf(':')
        if ($separator -le 0) { throw 'An existing password file has an unsupported entry; reconcile it before bootstrap.' }
        $username = $line.Substring(0, $separator)
        if ($username -eq $AdminUsername) { throw 'The provisioner username already exists in a legacy password file. Choose a different administrator username.' }
        [void]$reservedUsernames.Add($username)
    }
}
$version = & $brokerExe -h 2>&1
if (($version -join "`n") -notmatch 'mosquitto version 2\.1\.2(?:\s|$)') {
    throw 'This bootstrap is validated for Mosquitto 2.1.2. Review and test a different version before applying it.'
}

$service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if ($null -eq $service) { throw 'The existing Mosquitto Windows service could not be found.' }
$serviceIdentity = switch ($service.StartName) {
    'LocalSystem' { 'NT AUTHORITY\SYSTEM' }
    'NT AUTHORITY\LocalService' { 'NT AUTHORITY\LOCAL SERVICE' }
    'NT AUTHORITY\NetworkService' { 'NT AUTHORITY\NETWORK SERVICE' }
    default { $service.StartName }
}
$serviceSid = [Security.Principal.NTAccount]::new($serviceIdentity).Translate([Security.Principal.SecurityIdentifier])
$apiSid = [Security.Principal.NTAccount]::new($ApiIdentity).Translate([Security.Principal.SecurityIdentifier])
$systemSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$administratorsSid = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')

function New-PrivateDirectory([string]$Path, [Security.Principal.SecurityIdentifier]$AdditionalSid,
    [Security.AccessControl.FileSystemRights]$AdditionalRights) {
    # Only dedicated empty directories are eligible; never rewrite ACLs on a populated secrets root.
    if (Test-Path -LiteralPath $Path) {
        if (@(Get-ChildItem -LiteralPath $Path -Force).Count -ne 0) { throw "Bootstrap directory must be empty: $Path" }
    } else {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
    $acl = [Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    $propagation = [Security.AccessControl.PropagationFlags]::None
    foreach ($sid in @($systemSid, $administratorsSid)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid, 'FullControl', $inherit, $propagation, 'Allow'))
    }
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($AdditionalSid, $AdditionalRights, $inherit, $propagation, 'Allow'))
    Set-Acl -LiteralPath $Path -AclObject $acl
}

. (Join-Path $PSScriptRoot 'lib\esp32-bootstrap-crypto.ps1')
New-PrivateDirectory $statePath $serviceSid ([Security.AccessControl.FileSystemRights]::Modify)
New-PrivateDirectory $adminPath $apiSid ([Security.AccessControl.FileSystemRights]::ReadAndExecute)

# The single secure prompt supplies both the new salted broker hash and the protected
# Backend file, so there is no second entry that can disagree with the generated hash.
$securePassword = $null
$passwordUtf8 = $null
$passwordPointer = [IntPtr]::Zero
try {
    $securePassword = Read-Host 'Choose a NEW unique provisioner administrator password (at least 12 characters)' -AsSecureString
    $passwordPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($securePassword)
    $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($passwordPointer)
    if ([string]::IsNullOrEmpty($plainPassword) -or $plainPassword.Length -lt 12 -or $plainPassword.Contains([char]0)) {
        throw 'The administrator password must contain at least 12 characters and no NUL.'
    }
    $passwordUtf8 = [Text.Encoding]::UTF8.GetBytes($plainPassword)
    $dynamic = New-Esp32DynsecBootstrapConfiguration -Username $AdminUsername -PasswordUtf8 $passwordUtf8
    $dynamicJson = $dynamic | ConvertTo-Json -Depth 15
    $dynamicStream = [IO.FileStream]::new($dynamicFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $dynamicWriter = [IO.StreamWriter]::new($dynamicStream, [Text.UTF8Encoding]::new($false))
        try { $dynamicWriter.Write($dynamicJson) } finally { $dynamicWriter.Dispose() }
    } finally { $dynamicStream.Dispose() }
    $adminJson = @{ username = $AdminUsername; password = $plainPassword; reservedUsernames = @($reservedUsernames | Sort-Object) } | ConvertTo-Json
    # CreateNew fails closed rather than replacing credentials in a concurrent bootstrap.
    $fileStream = [IO.FileStream]::new($adminFile, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try {
        $writer = [IO.StreamWriter]::new($fileStream, [Text.UTF8Encoding]::new($false))
        try { $writer.Write($adminJson) } finally { $writer.Dispose() }
    } finally { $fileStream.Dispose() }
} finally {
    if ($passwordPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($passwordPointer) }
    $plainPassword = $null
    $adminJson = $null
    if ($null -ne $passwordUtf8) { [Array]::Clear($passwordUtf8, 0, $passwordUtf8.Length) }
    if ($null -ne $securePassword) { $securePassword.Dispose() }
}

$pluginLine = 'plugin ' + $pluginPath.Replace('\', '/')
$configLine = 'plugin_opt_config_file ' + $dynamicFile.Replace('\', '/')
$snippet = $pluginLine + "`r`n" + $configLine + "`r`n"
[IO.File]::WriteAllText($snippetFile, $snippet, [Text.UTF8Encoding]::new($false))
if ($ApplyToBrokerConfiguration) {
    # Refuse an intervening edit rather than overwriting concurrent operator changes.
    if ([IO.File]::ReadAllText($brokerPath) -cne $brokerText) { throw 'Broker configuration changed during initialization; apply the prepared snippet manually.' }
    $backupPath = Join-Path $statePath ('mosquitto.conf.before-dynsec-' + [Guid]::NewGuid().ToString('N'))
    [IO.File]::Copy($brokerPath, $backupPath, $false)
    [IO.File]::AppendAllText($brokerPath, "`r`n# FloraBot ESP32 Dynamic Security`r`n" + $snippet, [Text.UTF8Encoding]::new($false))
    if ($RestartMosquitto) {
        Restart-Service -Name $ServiceName -ErrorAction Stop
        (Get-Service -Name $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(20))
    }
}
Write-Output "Prepared Dynamic Security configuration: $dynamicFile"
Write-Output "Prepared plugin snippet: $snippetFile"
Write-Output "Set MQTT_PROVISIONING_ADMIN_FILE to $adminFile for the API app pool."
Write-Output 'Legacy password/ACL/TLS files were preserved. Verify existing clients and a new test kiosk over TLS before enabling automatic enrollment.'
