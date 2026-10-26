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

# A test release (1.3.0-beta.2) is described by the section of the version it leads to (## 1.3.0 - unreleased).
$isTest = $Version -match '-'
$sectionVersion = ($Version -split '-')[0]
$start = -1
for ($i = 0; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match ('^## ' + [regex]::Escape($sectionVersion) + '(\s|$)')) { $start = $i; break }
}
if ($start -lt 0) { throw "CHANGELOG.md has no section '## $sectionVersion'" }
$end = $lines.Count
for ($i = $start + 1; $i -lt $lines.Count; $i++) {
    if ($lines[$i] -match '^## ') { $end = $i; break }
}
$section = if ($end - 1 -gt $start) { ($lines[($start + 1)..($end - 1)] -join "`n").Trim() } else { '' }
if (-not $section) { throw "the CHANGELOG.md section for $Version is empty" }

$testNote = if ($isTest) {
    @"
**Test release** of $sectionVersion, for trying it out before the regular release. ``xpplens update`` does not offer
it; install it with ``xpplens update --install --pre`` (available from 1.3.0). The regular $sectionVersion replaces it
through the usual ``xpplens update --install``.


"@
} else { '' }

$notes = @"
$($testNote)X++ (Dynamics 365 F&O) code index served to AI assistants over MCP.

## Install

Run ``xpp-lens-setup-$Version.exe`` - a wizard for the current user, no administrator rights: it finds
PackagesLocalDirectory, asks for the label languages, registers xpp-lens in Claude and builds the index. The program
is not signed yet: when Windows shows "Windows protected your PC", choose "More info" > "Run anyway".

Without the wizard: unzip ``xpp-lens-$Version.zip`` and run in a regular (non-admin) PowerShell:

``````powershell
powershell -ExecutionPolicy Bypass -File .\xpp-lens\install.ps1 -Languages en-US,pl
``````

Self-contained build - no .NET required on the target machine. Already installed? Run
``xpplens update --install`` - your settings and index are kept, Claude may stay open. From 1.3.0 it updates through
the setup program; the setup program also takes over an installation made by install.ps1 (C:\Tools\xpp-lens).

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
