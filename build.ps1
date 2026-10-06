<#
.SYNOPSIS
    Builds xpp-lens from the source in this folder and, optionally, replaces the binaries of an installation.

.DESCRIPTION
    MCP servers keep xpplens.exe open, so -Deploy first stops the running processes.
    After deploying, restart Claude Desktop / Claude Code sessions so they load the new version.

.EXAMPLE
    .\build.ps1                       # compile into .\build (the installation is not touched)
.EXAMPLE
    .\build.ps1 -Deploy               # compile and replace C:\Tools\xpp-lens\bin
.EXAMPLE
    .\build.ps1 -Deploy -InstallDir "$env:LOCALAPPDATA\Programs\xpp-lens"
.EXAMPLE
    .\build.ps1 -Test -Deploy         # run the tests first; deploy only when they pass
#>
[CmdletBinding()]
param(
    [string] $InstallDir = 'C:\Tools\xpp-lens',
    [switch] $Deploy,
    [switch] $Test,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot 'src\XppLens\XppLens.csproj'
$out = Join-Path $PSScriptRoot 'build'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'

if ($Test) {
    & dotnet test (Join-Path $PSScriptRoot 'tests\XppLens.Tests\XppLens.Tests.csproj') --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw 'tests failed - nothing was built or deployed' }
}

if ($Deploy) {
    $target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -Filter "Name='xpplens.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Write-Host "  stopping xpplens (PID $($_.ProcessId))"
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Milliseconds 500
    $out = Join-Path $InstallDir 'bin'
}

& dotnet build $proj -c $Configuration -o $out --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'build failed' }

Write-Host "done: $out\xpplens.exe" -ForegroundColor Cyan
if ($Deploy) {
    Write-Host 'restart Claude Desktop / Claude Code sessions' -ForegroundColor Yellow
}
else {
    Write-Host "test:  `"$out\xpplens.exe`" status" -ForegroundColor Gray
}
