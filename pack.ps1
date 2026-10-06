<#
.SYNOPSIS
    Builds the xpp-lens distribution package (ZIP) for installing on another machine.

.DESCRIPTION
    By default publishes a self-contained build (single EXE): nothing needs to be installed
    on the target machine. With -FrameworkDependent the package is small but requires the
    .NET 9 Runtime on the target machine.

.EXAMPLE
    .\pack.ps1
.EXAMPLE
    .\pack.ps1 -FrameworkDependent
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [switch] $FrameworkDependent,
    [switch] $NoZip
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\XppLens\XppLens.csproj'
$stage = Join-Path $OutDir 'xpp-lens'
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

Write-Host "publishing ($(if ($FrameworkDependent) { 'requires .NET 9' } else { 'self-contained' }))..." -ForegroundColor Cyan
& dotnet @publishArgs | Where-Object { $_ -match 'error|warning CS|->' }
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Remove-Item (Join-Path $stage 'bin\*.pdb') -Force -ErrorAction SilentlyContinue
foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'LICENSE') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage -Force
}

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "package: $stage ($size MB)" -ForegroundColor Cyan

if (-not $NoZip) {
    $version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    if (-not $version) { $version = Get-Date -Format 'yyyyMMdd' }
    $zip = Join-Path $OutDir "xpp-lens-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    Compress-Archive -Path $stage -DestinationPath $zip
    Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Cyan
}

Write-Host "`nOn the target machine: unzip and run install.ps1" -ForegroundColor Green
