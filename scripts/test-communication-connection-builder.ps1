$ErrorActionPreference = 'Stop'
# Execute only the pure connection-builder fragments from the real activation
# script. No IIS, network, database provisioning or production config reads.
$source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'enable-communication-vps.ps1'))
$start = $source.IndexOf('    $builder = New-Object System.Data.Common.DbConnectionStringBuilder')
$end = $source.IndexOf('    $dbArgs = ', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Connection parser fragment not found.' }
$fragment = $source.Substring($start, $end-$start)
$settings = @{ DATABASE_URL='Host=127.0.0.1;Port=5432;Database=florabot;Username=florabot;Password=fake-local-test-only' }
& ([scriptblock]::Create($fragment + @'

if ($businessDatabase -ne 'florabot' -or $dbHost -ne '127.0.0.1' -or $dbPort -ne 5432) {
    throw 'Business database parsing regression.'
}
'@))
$start = $source.IndexOf('    $serviceDb = New-Object System.Data.Common.DbConnectionStringBuilder')
$end = $source.IndexOf('    $vars = ', $start)
if ($start -lt 0 -or $end -lt 0) { throw 'Service connection builder fragment not found.' }
$fragment = $source.Substring($start, $end-$start)
$dbHost='127.0.0.1'; $dbPort=5432; $dbName='communication_test'; $dbRole='communication_test'; $dbPassword='fake-local-test-only'
& ([scriptblock]::Create($fragment + @'

$parsed = New-Object System.Data.Common.DbConnectionStringBuilder
$parsed.set_ConnectionString($serviceDb.get_ConnectionString())
if ($parsed['Database'] -ne 'communication_test' -or $parsed['Username'] -ne 'communication_test' -or $parsed['Maximum Pool Size'] -ne '10') {
    throw 'Service connection serialization regression.'
}
'@))
'PASS: activation connection parsing and serialization; no production actions performed.'
