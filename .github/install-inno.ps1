# Makes sure Inno Setup 6 (ISCC.exe) is available on a GitHub runner: uses the preinstalled one, else installs it.
$ErrorActionPreference = 'Stop'
$known = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe")
$iscc = $known | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    choco install innosetup --no-progress -y
    if ($LASTEXITCODE -ne 0) { throw 'installing Inno Setup failed' }
    $iscc = $known | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup 6 (ISCC.exe) not found after installing' }
Write-Host "Inno Setup: $iscc"
