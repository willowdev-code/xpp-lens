<#
.SYNOPSIS
    Removes xpp-lens: the MCP entries in Claude, its Claude Code skills and, optionally, the index and program files.

.DESCRIPTION
    Only MCP entries that point to this installation are removed, and only the skills in ~/.claude/skills that
    xpp-lens installed and nobody changed since. The index location is read
    from the configuration (by default %LOCALAPPDATA%\xpp-lens\index); only the index files
    (xpp.db*) are deleted, never other content of that folder. Source code and build outputs
    found in the installation folder (src, dist, build, *.cs, *.csproj) are left untouched.
    Nothing in PackagesLocalDirectory is ever modified.

.EXAMPLE
    .\uninstall.ps1              # unregister + ask before deleting files
.EXAMPLE
    .\uninstall.ps1 -All -Force  # delete everything without asking
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
function Confirm-Step($question) {
    if ($All -or $Force) { return $true }
    return (Read-Host "  $question [y/N]") -match '^[yYtT]'
}
Write-Host "`nRemoving xpp-lens from $InstallDir" -ForegroundColor Cyan

$exe = Join-Path $InstallDir 'bin\xpplens.exe'

# Find the index before the program is removed: the configuration knows where it lives.
$indexFolders = @()
if (Test-Path $exe) {
    $line = & $exe config 2>$null | Where-Object { $_ -match '^indexPath:' } | Select-Object -First 1
    if ($line) {
        $indexPath = ($line -replace '^indexPath:\s*', '').Trim()
        if ($indexPath) { $indexFolders += Split-Path $indexPath -Parent }
    }
}
# The default %LOCALAPPDATA% index may be shared by other installations of this user:
# consider it only when the configuration could not be read.
if ($indexFolders.Count -eq 0) { $indexFolders += Join-Path $env:LOCALAPPDATA 'xpp-lens\index' }
$indexFolders += Join-Path $InstallDir 'index'                    # older installations
$indexFolders = @($indexFolders | Select-Object -Unique | Where-Object { Test-Path (Join-Path $_ 'xpp.db*') })

if (Test-Path $exe) {
    & $exe unregister
    # Only the skills xpp-lens installed and nobody changed; a changed one stays, with a warning.
    & $exe skills remove | ForEach-Object {
        if ($_ -like 'warning:*') { Write-Host "  $_" -ForegroundColor Yellow } else { Say $_ }
    }
}
else { Say "$exe not found - skipping unregistration and skills" }

# The program folder off the user's PATH (install.ps1 and the setup program put it there).
$binFull = [IO.Path]::GetFullPath((Join-Path $InstallDir 'bin')).TrimEnd('\')
$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
try {
    $rawPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $items = @($rawPath -split ';' | Where-Object { $_ })
    $kept = @($items | Where-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') -ine $binFull })
    if ($kept.Count -ne $items.Count) {
        $envKey.SetValue('Path', $kept -join ';', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        Say "removed from your PATH: $binFull"
    }
}
finally {
    $envKey.Close()
}

# The Start menu entries and the installed-apps entry made by install.ps1 (the setup program's own go with its
# uninstaller).
$appKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\xpp-lens'
if ((Test-Path $appKey) -and ([string](Get-ItemProperty $appKey).InstallLocation).TrimEnd('\') -ieq [IO.Path]::GetFullPath($InstallDir).TrimEnd('\')) {
    Remove-Item $appKey -Recurse -Force
    $group = Join-Path ([Environment]::GetFolderPath('Programs')) 'xpp-lens'
    if (Test-Path $group) { Remove-Item $group -Recurse -Force }
    Say "removed the Start menu entries and the entry in installed apps"
}

$target = [IO.Path]::GetFullPath($InstallDir).TrimEnd('\') + '\'
Get-CimInstance Win32_Process -Filter "Name='xpplens.exe'" |
    Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($target, [StringComparison]::OrdinalIgnoreCase) } |
    ForEach-Object {
        Say "stopping xpplens process (PID $($_.ProcessId))"
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }
Start-Sleep -Milliseconds 500

if (-not $KeepIndex) {
    foreach ($folder in $indexFolders) {
        $files = @(Get-ChildItem $folder -Filter 'xpp.db*' -File)
        $size = [math]::Round(($files | Measure-Object Length -Sum).Sum / 1MB)
        if (Confirm-Step "Delete the index in $folder ($size MB)?") {
            $files | Remove-Item -Force
            Say "index deleted: $folder"
            # Remove the folders only when nothing else is left in them.
            foreach ($d in @($folder, (Split-Path $folder -Parent))) {
                if ((Test-Path $d) -and -not (Get-ChildItem $d -Force)) { Remove-Item $d -Force }
            }
        }
    }
}

if (Confirm-Step "Delete the program files in $InstallDir?") {
    $self = $MyInvocation.MyCommand.Path
    # Source code and user content stay untouched - only what the installer put there is removed.
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
    Say "program files deleted (this script stays - delete the folder manually)"
    if ($skipped.Count) { Say "left in place (source / build outputs): $($skipped -join ', ')" }
}

Write-Host "`nDone. No files in PackagesLocalDirectory were changed." -ForegroundColor Cyan
