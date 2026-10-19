<#
.SYNOPSIS
    Restarts Claude Desktop so all its sessions start the newly installed xpp-lens.

.DESCRIPTION
    Used by install.ps1 and by the setup program (xpp-lens-setup-X.Y.Z.exe) after an update. Does nothing when Claude
    Desktop is not running. Works out how to start Claude again *before* stopping it; when that is not possible,
    Claude is left running. Only the Desktop app (Store / MSIX or per-user install) is touched - never Claude Code
    processes in terminals.
#>
[CmdletBinding()]
param([string] $Version)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "  $m" }

# How to start Claude Desktop again, from the path of its running process; $null when it cannot be worked out.
function Get-ClaudeStart([string]$appPath) {
    if ($appPath -match '\\WindowsApps\\(?<folder>[^\\]+)\\') {
        # Store / MSIX app, started through its application id. The folder ("Claude_2.1.0.0_x64__pzs8sxrjxfjjc") gives
        # the package family ("Claude_pzs8sxrjxfjjc"); after Claude updated itself the running folder is an older
        # version than the installed package, so the family is matched, never the path.
        $folder = $Matches['folder']
        if ($folder -notmatch '^(?<name>[^_]+)_.*__(?<publisher>[^_]+)$') { return $null }
        $family = "$($Matches['name'])_$($Matches['publisher'])"
        $aumid = $null
        try {
            $aumid = (Get-StartApps -ErrorAction Stop | Where-Object { $_.AppID -like "$family!*" } | Select-Object -First 1).AppID
        }
        catch { }
        if (-not $aumid) {
            try {
                $pkg = Get-AppxPackage -ErrorAction Stop | Where-Object { $_.PackageFamilyName -eq $family } | Select-Object -First 1
                if ($pkg) { $aumid = "$family!" + @((Get-AppxPackageManifest $pkg).Package.Applications.Application)[0].Id }
            }
            catch { }
        }
        if ($aumid) { return "shell:AppsFolder\$aumid" }
        return $null
    }
    # Per-user install: the launcher above the versioned "app-x.y.z" folders starts the current version.
    if ($appPath -match '^(?<root>.+)\\app-[^\\]+\\[^\\]+$' -and (Test-Path (Join-Path $Matches['root'] 'claude.exe'))) {
        return (Join-Path $Matches['root'] 'claude.exe')
    }
    if ($appPath -and (Test-Path $appPath)) { return $appPath }
    return $null
}

$desktop = @(Get-Process -Name claude -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -match '\\WindowsApps\\Claude_|\\AnthropicClaude\\' })
if ($desktop.Count -eq 0) { Say "Claude Desktop is not running - nothing to restart."; exit 0 }

$start = Get-ClaudeStart $desktop[0].Path
if (-not $start) {
    Say "Claude Desktop could not be located for a restart - it was left running; restart it yourself$(if ($Version) { " to use $Version" })."
    exit 0
}
$desktop | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
try {
    Start-Process $start -ErrorAction Stop
    Say "Claude Desktop restarted."
}
catch {
    Say "Claude Desktop was closed but did not start again ($($_.Exception.Message)) - start it from the Start menu."
    exit 1
}
