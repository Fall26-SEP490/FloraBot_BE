# Pure configuration construction for a NEW, dedicated Dynamic Security admin only.
# Compatible with Windows PowerShell 5.1/.NET Framework 4.8 and PowerShell 7.
# This library performs no file, service, broker, or network operations.
function New-Esp32DynsecBootstrapConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)][string]$Username,
        [Parameter(Mandatory = $true)][byte[]]$PasswordUtf8
    )
    if ($Username -notmatch '^[A-Za-z0-9_-]{3,100}$' -or $null -eq $PasswordUtf8 -or $PasswordUtf8.Length -lt 12) {
        throw 'A valid dedicated administrator username and password are required.'
    }
    $constructor = [Security.Cryptography.Rfc2898DeriveBytes].GetConstructor([type[]]@(
        [byte[]], [byte[]], [int], [Security.Cryptography.HashAlgorithmName]))
    if ($null -eq $constructor) { throw 'SHA-512 PBKDF2 requires .NET Framework 4.8 or a current PowerShell 7 runtime.' }
    $iterations = 210000
    $salt = New-Object byte[] 12
    $hash = $null
    $random = [Security.Cryptography.RandomNumberGenerator]::Create()
    $derivation = $null
    try {
        $random.GetBytes($salt)
        $derivation = [Security.Cryptography.Rfc2898DeriveBytes]::new(
            $PasswordUtf8, $salt, $iterations, [Security.Cryptography.HashAlgorithmName]::SHA512)
        $hash = $derivation.GetBytes(64)
        $saltBase64 = [Convert]::ToBase64String($salt)
        $hashBase64 = [Convert]::ToBase64String($hash)
    } finally {
        if ($null -ne $derivation) { $derivation.Dispose() }
        $random.Dispose()
        [Array]::Clear($salt, 0, $salt.Length)
        if ($null -ne $hash) { [Array]::Clear($hash, 0, $hash.Length) }
    }
    $roleName = 'florabot-provisioner-control'
    $controlAcls = @(
        @{ acltype = 'publishClientSend'; topic = '$CONTROL/dynamic-security/v1'; priority = 100; allow = $true },
        @{ acltype = 'publishClientReceive'; topic = '$CONTROL/dynamic-security/v1/response'; priority = 100; allow = $true },
        @{ acltype = 'subscribeLiteral'; topic = '$CONTROL/dynamic-security/v1/response'; priority = 100; allow = $true },
        @{ acltype = 'unsubscribeLiteral'; topic = '$CONTROL/dynamic-security/v1/response'; priority = 100; allow = $true }
    )
    foreach ($kind in @('publishClientSend', 'publishClientReceive', 'subscribePattern', 'unsubscribePattern')) {
        foreach ($topic in @('#', '$CONTROL/#', '$SYS/#')) {
            $controlAcls += @{ acltype = $kind; topic = $topic; priority = 0; allow = $false }
        }
    }
    # Mosquitto 2.1.2 clients.c supports this salted PBKDF2 legacy schema as well
    # as encoded_password. The plaintext password never appears in this object.
    return [pscustomobject]@{
        defaultACLAccess = [pscustomobject]@{
            publishClientSend = $false; publishClientReceive = $true
            subscribe = $false; unsubscribe = $true
        }
        clients = @([pscustomobject]@{
            username = $Username; salt = $saltBase64; password = $hashBase64; iterations = $iterations
            groups = @(); roles = @(@{ rolename = $roleName; priority = 0 })
        })
        roles = @([pscustomobject]@{ rolename = $roleName; allowwildcardsubs = $false; acls = $controlAcls })
        groups = @()
    }
}
