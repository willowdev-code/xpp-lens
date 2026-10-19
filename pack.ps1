<#
.SYNOPSIS
    Builds the xpp-lens distribution: the ZIP package and the setup program.

.DESCRIPTION
    By default publishes a self-contained build (single EXE): nothing needs to be installed
    on the target machine. With -FrameworkDependent the package is small but requires the
    .NET 9 Runtime on the target machine.

    Outputs in OutDir:
      xpp-lens-X.Y.Z.zip          program and scripts (install.ps1; updates of versions before 1.3.0)
      xpp-lens-setup-X.Y.Z.exe    setup program with a wizard (Inno Setup 6; skipped when ISCC.exe is not found,
                                  unless -Installer)

.EXAMPLE
    .\pack.ps1
.EXAMPLE
    .\pack.ps1 -Installer          # fail when Inno Setup is missing (CI)
#>
[CmdletBinding()]
param(
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [switch] $FrameworkDependent,
    [switch] $NoZip,
    # Fail when Inno Setup (ISCC.exe) is not found instead of skipping the setup program.
    [switch] $Installer
)

$ErrorActionPreference = 'Stop'
# Absolute: ZIP entry names are cut from full paths (a relative -OutDir, as in the release workflow, nested them).
$OutDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutDir)
$proj = Join-Path $PSScriptRoot 'src\XppLens\XppLens.csproj'
$stage = Join-Path $OutDir 'xpp-lens'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$version = ([xml](Get-Content $proj -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { $version = Get-Date -Format 'yyyyMMdd' }

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

$publishArgs = @(
    'publish', $proj, '-c', 'Release', '-r', 'win-x64',
    '-o', (Join-Path $stage 'bin'), '--nologo',
    # No debug information: a release build would otherwise carry the local path of its .pdb file.
    '-p:DebugType=None', '-p:DebugSymbols=false'
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
foreach ($f in 'install.ps1', 'uninstall.ps1', 'restart-claude.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'LICENSE') {
    Copy-Item (Join-Path $PSScriptRoot $f) $stage -Force
}

$size = [math]::Round((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB, 1)
Write-Host "package: $stage ($size MB)" -ForegroundColor Cyan

if (-not $NoZip) {
    $zip = Join-Path $OutDir "xpp-lens-$version.zip"
    if (Test-Path $zip) { Remove-Item $zip -Force }
    # Entries with '/' as the ZIP format requires (Compress-Archive in Windows PowerShell 5.1 writes '\').
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open($zip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $root = Split-Path $stage -Parent
        foreach ($file in Get-ChildItem $stage -Recurse -File) {
            $entry = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    Write-Host "zip: $zip ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Cyan
}

# Setup program with a wizard (Inno Setup 6).
$iscc = @((Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source,
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($iscc) {
    Write-Host 'building the setup program (Inno Setup)...' -ForegroundColor Cyan
    & $iscc /Q "/DVersion=$version" "/DSource=$stage" "/DOutDir=$OutDir" (Join-Path $PSScriptRoot 'packaging\inno\xpp-lens.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }
    $setup = Join-Path $OutDir "xpp-lens-setup-$version.exe"
    Write-Host "setup: $setup ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Cyan
}
elseif ($Installer) { throw 'Inno Setup 6 (ISCC.exe) not found' }
else { Write-Host 'setup program skipped: Inno Setup 6 (ISCC.exe) not found' -ForegroundColor Yellow }

Write-Host "`nOn the target machine: run xpp-lens-setup-$version.exe (or unzip and run install.ps1)" -ForegroundColor Green
