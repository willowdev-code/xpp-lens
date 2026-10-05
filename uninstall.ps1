<#
.SYNOPSIS
    Usuwa xpp-graft: wpisy MCP w Claude, a opcjonalnie indeks i pliki programu.

.EXAMPLE
    .\uninstall.ps1              # wyrejestrowanie + pytanie o usuniecie plikow
.EXAMPLE
    .\uninstall.ps1 -All -Force  # usuwa wszystko bez pytania
#>
[CmdletBinding()]
param(
    [string] $InstallDir = $PSScriptRoot,
    [switch] $KeepIndex,
    [switch] $All,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "  $m" }
Write-Host "`nUsuwanie xpp-graft z $InstallDir" -ForegroundColor Cyan

$exe = Join-Path $InstallDir 'bin\xppgraft.exe'
if (Test-Path $exe) { & $exe unregister } else { Say "brak $exe - pomijam wyrejestrowanie" }

Get-CimInstance Win32_Process -Filter "Name='xppgraft.exe'" | ForEach-Object {
    Say "zatrzymuje proces xppgraft (PID $($_.ProcessId))"
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}
Start-Sleep -Milliseconds 500

$index = Join-Path $InstallDir 'index'
if (-not $KeepIndex -and (Test-Path $index)) {
    $size = [math]::Round((Get-ChildItem $index -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
    if ($All -or $Force -or (Read-Host "  Usunac indeks ($size MB)? [t/N]") -match '^[tTyY]') {
        Remove-Item $index -Recurse -Force
        Say "usunieto indeks"
    }
}

if ($All -or $Force -or (Read-Host "  Usunac pliki programu z $InstallDir? [t/N]") -match '^[tTyY]') {
    $self = $MyInvocation.MyCommand.Path
    # Kod zrodlowy i wlasne pliki uzytkownika zostaja nietkniete - kasujemy tylko to, co instalator wgral.
    $keep = @('src', 'dist', 'build', 'build.ps1', 'pack.ps1', 'nuget.config')
    $skipped = @()
    foreach ($item in Get-ChildItem $InstallDir -Force) {
        if ($item.FullName -eq $self) { continue }
        if ($keep -contains $item.Name -or $item.Extension -eq '.cs' -or $item.Extension -eq '.csproj') {
            $skipped += $item.Name
            continue
        }
        Remove-Item $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
    }
    Say "usunieto pliki programu (ten skrypt zostaje - skasuj katalog recznie)"
    if ($skipped.Count) { Say "pominieto (kod/wyniki budowy): $($skipped -join ', ')" }
}

Write-Host "`nGotowe. Zadne pliki w PackagesLocalDirectory nie byly zmieniane." -ForegroundColor Cyan
