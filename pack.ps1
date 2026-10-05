<#
.SYNOPSIS
    Buduje pakiet dystrybucyjny xpp-graft (ZIP) do instalacji na innej maszynie.

.DESCRIPTION
    Domyslnie publikuje wersje samodzielna (self-contained, jeden plik EXE) - na maszynie
    docelowej nie trzeba niczego instalowac. Z -FrameworkDependent powstaje wersja mala,
    ktora wymaga zainstalowanego .NET 9 Runtime.

.EXAMPLE
    .\pack.ps1
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [switch] $FrameworkDependent,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\XppGraft\XppGraft.csproj'
$stage = Join-Path $OutDir 'xpp-graft'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$publishArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64',
    '-o', (Join-Path $stage 'bin'), '--nologo'
)
if ($FrameworkDependent) {
    $publishArgs += @('--self-contained', 'false')
}
else {
    $publishArgs += @('--self-contained', 'true', '-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:EnableCompressionInSingleFile=true')
}

Write-Host "publikowanie ($(if ($FrameworkDependent) { 'wymaga .NET 9' } else { 'samodzielna' }))..." -ForegroundColor Cyan
& dotnet @publishArgs | Where-Object { $_ -match 'error|warning CS|->' }
if ($LASTEXITCODE -ne 0) { throw "dotnet publish nie powiodlo sie" }

Remove-Item (Join-Path $stage 'bin\*.pdb') -Force -ErrorAction SilentlyContinue
foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage -Force
}

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "pakiet: $stage ($size MB)" -ForegroundColor Cyan

if (-not $NoZip) {
    $version = (Get-Date -Format 'yyyyMMdd')
    $zip = Join-Path $OutDir "xpp-graft-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Cyan
}

Write-Host "`nNa maszynie docelowej: rozpakuj i uruchom install.ps1" -ForegroundColor Green
