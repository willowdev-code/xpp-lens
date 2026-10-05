<#
.SYNOPSIS
    Buduje xpp-graft z kodu w tym katalogu i podmienia binaria w istniejacej instalacji.

.DESCRIPTION
    Serwery MCP trzymaja xppgraft.exe otwarty, wiec skrypt najpierw zatrzymuje dzialajace procesy.
    Po podmianie zrestartuj Claude Desktop / sesje Claude Code, zeby zaladowaly nowa wersje.

.EXAMPLE
    .\build.ps1                       # kompilacja do .\build (nie rusza instalacji)
.EXAMPLE
    .\build.ps1 -Deploy               # kompilacja i podmiana w C:\Tools\xpp-graft\bin
.EXAMPLE
    .\build.ps1 -Deploy -InstallDir "$env:LOCALAPPDATA\Programs\xpp-graft"
#>
[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\Tools\xpp-graft',
    [switch] $Deploy,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\XppGraft\XppGraft.csproj'
$out = Join-Path $PSScriptRoot 'build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if ($Deploy) {
    Get-CimInstance Win32_Process -Filter "Name='xppgraft.exe'" | ForEach-Object {
        Write-Host "  zatrzymuje xppgraft (PID $($_.ProcessId))"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
    $out = Join-Path $InstallDir 'bin'
}

& dotnet build $proj -c $Configuration -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'kompilacja nie powiodla sie' }

Write-Host "gotowe: $out\xppgraft.exe" -ForegroundColor Cyan
if ($Deploy) {
    Write-Host 'zrestartuj Claude Desktop / sesje Claude Code' -ForegroundColor Yellow
}
else {
    Write-Host "test:  `"$out\xppgraft.exe`" status" -ForegroundColor Gray
}
