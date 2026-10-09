<#
.SYNOPSIS
    Installs xpp-lens: an X++ (D365 F&O) code index served to Claude over MCP.

.DESCRIPTION
    Copies the files, determines the PackagesLocalDirectory folder (given or detected),
    writes the configuration, registers the MCP server in Claude Desktop and Claude Code
    and builds the index. The AOS repository is only ever read, never written.

    Run it in a regular (non-elevated) PowerShell: the index is created in the
    %LOCALAPPDATA% of the user who runs the installer, and Claude must be able to write to it.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -PackagesDir K:\AosService\PackagesLocalDirectory -Languages en-US,de -FullModels XPL,XPLCore

.NOTES
    Running it again over an existing installation (an update) keeps its settings: languages and the
    PackagesLocalDirectory change only when you pass -Languages / -PackagesDir explicitly.

    Claude may stay open: a running xpplens.exe is renamed to *.old instead of being stopped, so open sessions
    keep working on the old version and new sessions start the new one. At the end the installer offers to
    restart Claude Desktop (-RestartClaude: without asking).

    Upgrading from xpp-graft (the former name): the installer takes over its settings, moves its index
    and usage log to %LOCALAPPDATA%\xpp-lens without rebuilding (or keeps using the old index where it is
    when it cannot be moved), and removes its MCP entries. The old folder (-MigrateFrom, default
    C:\Tools\xpp-graft) is left for you to delete.
#>
[CmdletBinding()]
param(
    [string]   $InstallDir      = "C:\Tools\xpp-lens",
    [string]   $MigrateFrom     = "C:\Tools\xpp-graft",
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
    [switch]   $CodeOnly,
    [switch]   $RestartClaude
)

$ErrorActionPreference = 'Stop'
function Say($m) { Write-Host "  $m" }
function Head($m) { Write-Host "`n$m" -ForegroundColor Cyan }

$src = $PSScriptRoot
if (-not (Test-Path (Join-Path $src 'bin\xpplens.exe'))) {
    throw "$src\bin\xpplens.exe not found - run install.ps1 from an unpacked xpp-lens package."
}

# Upgrade from the former name: settings, index and usage log move over, the old MCP entries go away.
$migrated = $null
$oldConfig = Join-Path $MigrateFrom 'xppgraft.json'
$newConfig = Join-Path $InstallDir 'xpplens.json'
if ((Test-Path $oldConfig) -and -not (Test-Path $newConfig)) {
    Head "0/5 Taking over the former installation in $MigrateFrom (xpp-graft)"
    $oldTarget = [IO.Path]::GetFullPath($MigrateFrom).TrimEnd('\') + '\'
    Get-CimInstance Win32_Process -Filter "Name='xppgraft.exe'" |
        Where-Object { $_.ExecutablePath -and $_.ExecutablePath.StartsWith($oldTarget, [StringComparison]::OrdinalIgnoreCase) } |
        ForEach-Object {
            Say "stopping xppgraft process (PID $($_.ProcessId))"
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }
    Start-Sleep -Milliseconds 500
    $oldExe = Join-Path $MigrateFrom 'bin\xppgraft.exe'
    if (Test-Path $oldExe) { & $oldExe unregister }

    # Settings, index and usage log: moved file by file and checked; if the index cannot be moved,
    # the new configuration keeps using it where it is (no rebuild from scratch).
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    & (Join-Path $src 'bin\xpplens.exe') migrate --from $MigrateFrom --config $newConfig
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $newConfig)) { throw "Taking over the former installation failed." }
    $migrated = $true
}

# An existing configuration (update, reinstall, taken over) keeps its values unless they are passed explicitly.
$hadConfig = Test-Path $newConfig
if ($hadConfig -and -not $PackagesDir) {
    $PackagesDir = [string](Get-Content $newConfig -Raw | ConvertFrom-Json).packagesDir
    if ($PackagesDir) { Say "keeping PackagesLocalDirectory from $newConfig" }
}

Head "1/5 Copying files to $InstallDir"
$binDst = Join-Path $InstallDir 'bin'
New-Item -ItemType Directory -Force -Path $binDst | Out-Null
$srcFull = (Resolve-Path $src).Path.TrimEnd('\')
$dstFull = (Resolve-Path $InstallDir).Path.TrimEnd('\')
if ($srcFull -ieq $dstFull) {
    Say "the package is already in the target folder - skipping copy"
}
else {
    # A running xpplens.exe (an open Claude session) cannot be overwritten, but it can be renamed: the session
    # keeps running from the *.old file, new sessions start the new version, the next start deletes the leftover.
    Get-ChildItem $binDst -Filter '*.old' | ForEach-Object { Remove-Item $_.FullName -Force -ErrorAction SilentlyContinue }
    foreach ($f in Get-ChildItem (Join-Path $src 'bin') -File) {
        $dst = Join-Path $binDst $f.Name
        if (Test-Path $dst) {
            try {
                Remove-Item $dst -Force -ErrorAction Stop
            }
            catch {
                $oldName = "$($f.Name).$((Get-Date).ToString('yyyyMMddHHmmss')).old"
                Rename-Item $dst $oldName
                Say "$($f.Name) is in use by an open Claude session - renamed to $oldName"
            }
        }
        Copy-Item $f.FullName $dst -Force
    }
    Get-ChildItem (Join-Path $src 'bin') -Directory | ForEach-Object { Copy-Item $_.FullName $binDst -Recurse -Force }
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'LICENSE') {
        if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $InstallDir -Force }
    }
}
$exe = Join-Path $InstallDir 'bin\xpplens.exe'
Say "done: $exe"

Head "2/5 PackagesLocalDirectory folder"
if (-not $PackagesDir) {
    $found = @(& $exe detect | Where-Object { $_ -match "`t" })
    if ($found.Count -eq 0) {
        throw "PackagesLocalDirectory was not detected. Run again with -PackagesDir <path>."
    }
    $cands = @($found | ForEach-Object {
        $p = $_ -split "`t"
        [pscustomobject]@{ Path = $p[0]; Packages = $p[1]; Source = $p[2] }
    })
    if ($cands.Count -eq 1 -or $First) {
        $PackagesDir = $cands[0].Path
        Say "detected: $PackagesDir ($($cands[0].Packages), $($cands[0].Source))"
    }
    else {
        Write-Host "  Several folders found:"
        for ($i = 0; $i -lt $cands.Count; $i++) {
            Write-Host ("   [{0}] {1}  ({2}, {3})" -f $i, $cands[$i].Path, $cands[$i].Packages, $cands[$i].Source)
        }
        if (-not [Environment]::UserInteractive -or $Host.Name -eq 'Default Host') {
            throw "Several candidates - run again with -PackagesDir <path> or -First."
        }
        $sel = Read-Host "  Pick a number [0]"
        if (-not $sel) { $sel = 0 }
        $PackagesDir = $cands[[int]$sel].Path
    }
}
Say "using: $PackagesDir"

Head "3/5 Configuration"
$cfgArgs = @('config', '--packages-dir', $PackagesDir)
# Existing settings keep their languages unless -Languages is given explicitly.
if (-not $hadConfig -or $PSBoundParameters.ContainsKey('Languages')) { $cfgArgs += @('--languages', ($Languages -join ',')) }
if ($DisplayLanguage) { $cfgArgs += @('--display-language', $DisplayLanguage) }
if ($FullModels.Count)     { $cfgArgs += @('--full-models', ($FullModels -join ',')) }
if ($StandardModels.Count) { $cfgArgs += @('--standard-models', ($StandardModels -join ',')) }
if ($NoStandard)           { $cfgArgs += @('--index-standard', 'false') }
& $exe @cfgArgs
if ($LASTEXITCODE -ne 0) { throw "Configuration failed." }

Head "4/5 Registering in Claude"
if ($NoRegister) {
    Say "skipped (-NoRegister). Manually: `"$exe`" register"
}
else {
    $regArgs = @('register')
    if ($DesktopOnly) { $regArgs += '--desktop' }
    if ($CodeOnly)    { $regArgs += '--code' }
    & $exe @regArgs
}

Head "5/5 Building the index"
if ($NoBuild) {
    Say "skipped (-NoBuild). Manually: `"$exe`" build"
}
else {
    Say "custom models: about 1 minute; Microsoft standard: 20-90 minutes the first time, then only what changed"
    & $exe build
    if ($LASTEXITCODE -ne 0) { throw "Building the index failed." }
}

Head "Done"
$version = ([string](& $exe version)).Split(' ')[1]
Say "installed xpp-lens $version"
if ($migrated) { Say "The former folder $MigrateFrom is no longer used - delete it when you like." }

# Claude Desktop starts xpplens.exe for its sessions: a restart moves all of them to the new version.
# Only the Desktop app (MSIX or per-user install) - never Claude Code processes in terminals.
$desktop = @(Get-Process -Name claude -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -match '\\WindowsApps\\Claude_|\\AnthropicClaude\\' })
if ($desktop.Count -gt 0) {
    $restart = [bool]$RestartClaude
    $canAsk = [Environment]::UserInteractive -and $Host.Name -ne 'Default Host' -and
        -not ([Environment]::GetCommandLineArgs() | Where-Object { $_ -like '-NonI*' })
    if (-not $restart -and $canAsk) {
        Write-Host ""
        $answer = Read-Host "  Claude Desktop is running. Restart it now so all its sessions use $version? Work in progress there is interrupted. [y/N]"
        $restart = $answer -match '^\s*(y|yes|t|tak)\s*$'
    }
    if ($restart) {
        $appPath = $desktop[0].Path
        $desktop | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        if ($appPath -match '\\WindowsApps\\') {
            # Store / MSIX app: started through its application id, not the exe path.
            $pkg = Get-AppxPackage | Where-Object { $_.InstallLocation -and $appPath.StartsWith($_.InstallLocation, [StringComparison]::OrdinalIgnoreCase) } | Select-Object -First 1
            $appId = @((Get-AppxPackageManifest $pkg).Package.Applications.Application)[0].Id
            Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!$appId"
        }
        else {
            Start-Process $appPath
        }
        Say "Claude Desktop restarted."
    }
    else {
        Say "Claude Desktop: open sessions keep the old version until it is restarted; new sessions use $version."
    }
}
Say "Claude Code in a terminal / VS Code: '/mcp' -> reconnect xpp-lens, or start a new session."
Say "Change settings later:  `"$exe`" config --add-language de --add-full-model XPL"
Say "Index status:           `"$exe`" status"
Say "Uninstall:              $InstallDir\uninstall.ps1"
