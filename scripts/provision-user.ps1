param(
  [Parameter(Mandatory=$true)][Guid]$UserId,
  [Parameter(Mandatory=$true)][string]$Email
)
$ErrorActionPreference = 'Stop'
Push-Location (Join-Path $PSScriptRoot '..')
$secret = $null
$confirmation = $null
$pointer = [IntPtr]::Zero
$confirmationPointer = [IntPtr]::Zero
try {
  if (!$env:DATABASE_URL) { throw 'Set DATABASE_URL for the intended database before provisioning.' }
  dotnet build src/FloraBot.Api --nologo
  if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
  $secret = Read-Host 'New password (12+ characters, maximum 72 UTF-8 bytes)' -AsSecureString
  $confirmation = Read-Host 'Repeat password' -AsSecureString
  $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secret)
  $confirmationPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($confirmation)
  $plainPassword = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
  if ($plainPassword -cne [Runtime.InteropServices.Marshal]::PtrToStringBSTR($confirmationPointer)) { throw 'Passwords do not match.' }
  $plainPassword | dotnet run --project src/FloraBot.Api --no-build --no-launch-profile -- provision-user $UserId $Email
  if ($LASTEXITCODE -ne 0) { throw 'Account was not provisioned. Read the validation message above.' }
} finally {
  $plainPassword = $null
  if ($pointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer) }
  if ($confirmationPointer -ne [IntPtr]::Zero) { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($confirmationPointer) }
  if ($secret) { $secret.Dispose() }
  if ($confirmation) { $confirmation.Dispose() }
  Pop-Location
}
