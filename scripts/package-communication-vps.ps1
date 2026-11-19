param([Parameter(Mandatory=$true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = [IO.Path]::GetFullPath($OutputDirectory)
if (Test-Path -LiteralPath $out) { throw 'Output directory must be new.' }
New-Item -ItemType Directory -Path "$out\package" -Force | Out-Null
Push-Location $repo
try {
    & dotnet publish src/FloraBot.Communication -c Release -o "$out\package\Service" --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Service publish failed.' }
    & dotnet publish tools/CommunicationProbe -c Release -o "$out\package\Probe" --nologo --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw 'Probe publish failed.' }
    $package = Join-Path $out 'package'
    $commit = (& git rev-parse HEAD).Trim()
    $files = @(Get-ChildItem -LiteralPath $package -Recurse -File | ForEach-Object {
        [ordered]@{ path=$_.FullName.Substring($package.Length+1).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash }
    })
    [ordered]@{commit=$commit; files=$files} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "$package\MANIFEST.json" -Encoding UTF8
    $zip = Join-Path $out 'FloraBot-Communication-20261120.zip'
    Compress-Archive -Path "$package\*" -DestinationPath $zip
    $hash = (Get-FileHash -LiteralPath $zip).Hash
    $source = Get-Content -LiteralPath "$PSScriptRoot\enable-communication-vps.ps1" -Raw
    $source.Replace('__PACKAGE_SHA256__',$hash) | Set-Content -LiteralPath "$out\FloraBot-Enable-Communication.ps1" -Encoding UTF8
    Copy-Item -LiteralPath "$repo\docs\microservices\communication-activation.md" -Destination "$out\HUONG-DAN.md"
    $tokens=$null; $errors=$null
    [System.Management.Automation.Language.Parser]::ParseFile("$out\FloraBot-Enable-Communication.ps1",[ref]$tokens,[ref]$errors) | Out-Null
    if ($errors) { throw 'Generated activation script syntax check failed.' }
    "Source commit: $commit"
    "Package SHA256: $hash"
    "Output: $out"
} finally { Pop-Location }
