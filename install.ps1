<#
.SYNOPSIS
    Instaluje xpp-graft: indeks kodu X++ (D365 F&O) udostepniany Claude'owi przez MCP.

.DESCRIPTION
    Kopiuje pliki, ustala katalog PackagesLocalDirectory (podany lub wykryty),
    zapisuje konfiguracje, rejestruje serwer MCP w Claude Desktop i Claude Code
    oraz buduje indeks. Repozytorium AOS jest czytane wylacznie do odczytu.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -PackagesDir K:\AosService\PackagesLocalDirectory -Languages en-US,de -FullModels XPL,XPLCore
#>
[CmdletBinding()]
param(
    [string]   $InstallDir      = "C:\Tools\xpp-graft",
    [string]   $PackagesDir,
    [string[]] $Languages       = @('en-US'),
    [string]   $DisplayLanguage,
    [string[]] $FullModels      = @(),
    [string[]] $StandardModels  = @(),
    [switch]   $First,
    [switch]   $NoStandard,
    [switch]   $NoBuild,
    [switch]   $NoRegister,
    [switch]   $DesktopOnly,
    [switch]   $CodeOnly
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "  $m" }
function Head($m) { Write-Host "`n$m" -ForegroundColor Cyan }

$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'bin\xppgraft.exe'))) {
    throw "Nie znaleziono $src\bin\xppgraft.exe - uruchom install.ps1 z rozpakowanego pakietu xpp-graft."
}

Head "1/5 Kopiowanie plikow do $InstallDir"
Get-CimInstance Win32_Process -Filter "Name='xppgraft.exe'" | ForEach-Object {
    Say "zatrzymuje dzialajacy proces xppgraft (PID $($_.ProcessId))"
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}
New-Item -ItemType Directory -Force -Path (Join-Path $InstallDir 'bin') | Out-Null
$srcFull = (Resolve-Path $src).Path.TrimEnd('\')
$dstFull = (Resolve-Path $InstallDir).Path.TrimEnd('\')
if ($srcFull -ieq $dstFull) {
    Say "pakiet jest juz w katalogu docelowym - pomijam kopiowanie"
}
else {
    Copy-Item (Join-Path $src 'bin\*') (Join-Path $InstallDir 'bin') -Recurse -Force
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md') {
        if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $InstallDir -Force }
    }
}
$exe = Join-Path $InstallDir 'bin\xppgraft.exe'
Say "gotowe: $exe"

Head "2/5 Katalog PackagesLocalDirectory"
if (-not $PackagesDir) {
    $found = @(& $exe detect | Where-Object { $_ -match "`t" })
    if ($found.Count -eq 0) {
        throw "Nie wykryto PackagesLocalDirectory. Uruchom ponownie z -PackagesDir <sciezka>."
    }
    $cands = @($found | ForEach-Object {
        $p = $_ -split "`t"
        [pscustomobject]@{ Path = $p[0]; Packages = $p[1]; Source = $p[2] }
    })
    if ($cands.Count -eq 1 -or $First) {
        $PackagesDir = $cands[0].Path
        Say "wykryto: $PackagesDir ($($cands[0].Packages), $($cands[0].Source))"
    }
    else {
        Write-Host "  Znaleziono kilka katalogow:"
        for ($i = 0; $i -lt $cands.Count; $i++) {
            Write-Host ("   [{0}] {1}  ({2}, {3})" -f $i, $cands[$i].Path, $cands[$i].Packages, $cands[$i].Source)
        }
        if (-not [Environment]::UserInteractive -or $Host.Name -eq 'Default Host') {
            throw "Kilka kandydatow - uruchom ponownie z -PackagesDir <sciezka> albo z -First."
        }
        $sel = Read-Host "  Wybierz numer [0]"
        if (-not $sel) { $sel = 0 }
        $PackagesDir = $cands[[int]$sel].Path
    }
}
Say "uzywam: $PackagesDir"

Head "3/5 Konfiguracja"
$cfgArgs = @('config', '--packages-dir', $PackagesDir, '--languages', ($Languages -join ','))
if ($DisplayLanguage) { $cfgArgs += @('--display-language', $DisplayLanguage) }
if ($FullModels.Count)     { $cfgArgs += @('--full-models', ($FullModels -join ',')) }
if ($StandardModels.Count) { $cfgArgs += @('--standard-models', ($StandardModels -join ',')) }
if ($NoStandard)           { $cfgArgs += @('--index-standard', 'false') }
& $exe @cfgArgs
if ($LASTEXITCODE -ne 0) { throw "Konfiguracja nie powiodla sie." }

Head "4/5 Rejestracja w Claude"
if ($NoRegister) {
    Say "pominieto (-NoRegister). Recznie: `"$exe`" register"
}
else {
    $regArgs = @('register')
    if ($DesktopOnly) { $regArgs += '--desktop' }
    if ($CodeOnly)    { $regArgs += '--code' }
    & $exe @regArgs
}

Head "5/5 Budowa indeksu"
if ($NoBuild) {
    Say "pominieto (-NoBuild). Recznie: `"$exe`" build"
}
else {
    Say "modele wlasne: ok. 1 minuta; standard Microsoftu: 20-40 minut (jednorazowo)"
    & $exe build
    if ($LASTEXITCODE -ne 0) { throw "Budowa indeksu nie powiodla sie." }
}

Head "Gotowe"
Say "Zamknij i uruchom ponownie Claude Desktop; sesje Claude Code startuj od nowa."
Say "Zmiana ustawien pozniej:  `"$exe`" config --add-language de --add-full-model XPL"
Say "Stan indeksu:             `"$exe`" status"
Say "Odinstalowanie:           $InstallDir\uninstall.ps1"
