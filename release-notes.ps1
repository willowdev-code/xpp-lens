<#
.SYNOPSIS
    Writes the release notes of one version: install instructions plus its section of CHANGELOG.md.

.DESCRIPTION
    Used by the release workflow (.github/workflows/release.yml); can be run locally to preview the text.
    Fails when CHANGELOG.md has no (or an empty) section '## <Version>'.

.EXAMPLE
    .\release-notes.ps1 -Version 1.2.2
.EXAMPLE
    .\release-notes.ps1 -Version 1.2.2 -OutFile dist\notes.md
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $OutFile
)

$ErrorActionPreference = 'Stop'
$changelog = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'CHANGELOG.md'), [Text.Encoding]::UTF8) -replace "`r`n", "`n"
$lines = $changelog -split "`n"

$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## ' + [regex]::Escape($Version) + '(\s|$)')) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section '## $Version'" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}
$section = if ($end - 1 -gt $start) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $section) { throw "the CHANGELOG.md section for $Version is empty" }

$notes = @"
X++ (Dynamics 365 F&O) code index served to AI assistants over MCP.

## Install

Download ``xpp-lens-$Version.zip``, unzip it and run in a regular (non-admin) PowerShell:

``````powershell
powershell -ExecutionPolicy Bypass -File .\xpp-lens\install.ps1 -Languages en-US,pl
``````

Self-contained build - no .NET required on the target machine. Already installed? Close Claude and run
``xpplens update --install`` (available from 1.2.2), or ``install.ps1`` from this package - your settings and
index are kept.

## Changes

$section

Released under the MIT License.
"@ -replace "`r`n", "`n"

if ($OutFile) {
    $dir = Split-Path $OutFile -Parent
    if ($dir) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
    [IO.File]::WriteAllText($OutFile, $notes, (New-Object Text.UTF8Encoding($false)))
    Write-Host "notes: $OutFile"
}
else {
    $notes
}
