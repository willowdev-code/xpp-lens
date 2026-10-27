<#
.SYNOPSIS
    Installs xpp-lens: an X++ (D365 F&O) code index served to Claude over MCP.

.DESCRIPTION
    Copies the files, determines the PackagesLocalDirectory folder (given or detected),
    writes the configuration, registers the MCP server in Claude Desktop and Claude Code,
    copies the Claude Code skills (xpp-ponytail, xpp-ponytail-review) to ~/.claude/skills
    (-NoSkills: not; -SkillsLanguage en|pl, default: the Windows language, then kept for updates) and builds the index. The AOS repository is only ever read, never written.

    Run it in a regular (non-elevated) PowerShell: the index is created in the
    %LOCALAPPDATA% of the user who runs the installer, and Claude must be able to write to it.

.EXAMPLE
    .\install.ps1
.EXAMPLE
    .\install.ps1 -PackagesDir K:\AosService\PackagesLocalDirectory -Languages en-US,de -FullModels ContosoCore,ContosoRetail

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
    [switch]   $NoSkills,
    [ValidateSet('en', 'pl')]
    [string]   $SkillsLanguage,
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
    foreach ($f in 'install.ps1', 'uninstall.ps1', 'restart-claude.ps1', 'README.md', 'README.pl.md', 'CHANGELOG.md', 'LICENSE') {
        if (Test-Path (Join-Path $src $f)) { Copy-Item (Join-Path $src $f) $InstallDir -Force }
    }
    # The package's copy of the skills (installed to ~/.claude/skills in step 4); replaced whole, so a skill
    # dropped from the package does not linger here.
    $skillsDst = Join-Path $InstallDir 'skills'
    if (Test-Path $skillsDst) { Remove-Item $skillsDst -Recurse -Force }
    if (Test-Path (Join-Path $src 'skills')) { Copy-Item (Join-Path $src 'skills') $InstallDir -Recurse -Force }
}
$exe = Join-Path $InstallDir 'bin\xpplens.exe'
Say "done: $exe"

# The program folder on the user's PATH, as the setup program does: "xpplens" then works in any new terminal.
# Read and written raw (REG_EXPAND_SZ), so entries such as %USERPROFILE%\... keep expanding.
$binFull = (Resolve-Path $binDst).Path.TrimEnd('\')
$envKey = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Environment', $true)
try {
    $rawPath = [string]$envKey.GetValue('Path', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    $items = @($rawPath -split ';' | Where-Object { $_ })
    if ($items | Where-Object { [Environment]::ExpandEnvironmentVariables($_).TrimEnd('\') -ieq $binFull }) {
        Say "already on your PATH: $binFull"
    }
    else {
        $envKey.SetValue('Path', (@($items) + $binFull) -join ';', [Microsoft.Win32.RegistryValueKind]::ExpandString)
        # Tell Explorer, so terminals opened from now on see it (WM_SETTINGCHANGE "Environment").
        Add-Type -Namespace XppLensSetup -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet = CharSet.Unicode)]
public static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, UIntPtr wParam, string lParam, uint flags, uint timeout, out UIntPtr result);
'@
        $ignored = [UIntPtr]::Zero
        [void][XppLensSetup.Native]::SendMessageTimeout([IntPtr]0xffff, 0x1A, [UIntPtr]::Zero, 'Environment', 2, 5000, [ref]$ignored)
        $env:Path = "$env:Path;$binFull"
        Say "added to your PATH: $binFull (open a new terminal to use 'xpplens')"
    }
}
finally {
    $envKey.Close()
}

# Start menu entries and an entry in Windows' installed apps, as the setup program makes them. When the setup
# program installed xpp-lens for this user, its own entries stay (they are the same, and its uninstaller owns
# them); the setup program removes these when it takes over a folder installed by this script.
$setupKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{B3C9E031-73CD-4356-AC30-B8EC91C3C528}_is1'
if (Test-Path $setupKey) {
    Say "Start menu and installed apps: kept from the setup program ($((Get-ItemProperty $setupKey).InstallLocation))"
}
else {
    $pl = if ($SkillsLanguage) { $SkillsLanguage -eq 'pl' } else { (Get-UICulture).TwoLetterISOLanguageName -eq 'pl' }
    # The setup program's names (Polish without diacritics in this ASCII script: "sprawdz" gets its z-acute here).
    $names = if ($pl) {
        @{ status = 'xpp-lens - status'; build = 'xpp-lens - aktualizacja indeksu'; update = "xpp-lens - sprawd$([char]0x17A) aktualizacje"
           uninstall = 'Odinstaluj xpp-lens'; console = 'Konsola z poleceniami xpplens' }
    }
    else {
        @{ status = 'xpp-lens - status'; build = 'xpp-lens - update the index'; update = 'xpp-lens - check for updates'
           uninstall = 'Uninstall xpp-lens'; console = 'Console with the xpplens commands' }
    }
    $group = Join-Path ([Environment]::GetFolderPath('Programs')) 'xpp-lens'
    if (Test-Path $group) { Remove-Item $group -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $group | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    function New-Shortcut($name, $target, $arguments, $workDir, $description) {
        $lnk = $shell.CreateShortcut((Join-Path $group "$name.lnk"))
        $lnk.TargetPath = $target
        $lnk.Arguments = $arguments
        $lnk.WorkingDirectory = $workDir
        $lnk.IconLocation = "$exe,0"
        if ($description) { $lnk.Description = $description }
        $lnk.Save()
    }
    New-Shortcut 'xpp-lens' $env:ComSpec "/k `"title xpp-lens & set `"PATH=$binFull;%PATH%`" & xpplens help`"" $env:USERPROFILE $names.console
    foreach ($c in 'status', 'build', 'update') {
        New-Shortcut $names[$c] $env:ComSpec "/k `"`"$exe`" $c`"" $InstallDir $null
    }
    $uninstallCmd = "-NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $InstallDir 'uninstall.ps1')`""
    New-Shortcut $names.uninstall 'powershell.exe' $uninstallCmd $InstallDir $null

    $appKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\xpp-lens'
    New-Item -Path $appKey -Force | Out-Null
    $appVersion = ([string](& $exe version)).Split(' ')[1]
    $sizeKb = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    $values = @{
        DisplayName = 'xpp-lens'; DisplayVersion = $appVersion; Publisher = 'WillowDev'; DisplayIcon = $exe
        InstallLocation = $dstFull; URLInfoAbout = 'https://github.com/willowdev-code/xpp-lens'
        UninstallString = "powershell.exe $uninstallCmd"
    }
    foreach ($v in $values.GetEnumerator()) { Set-ItemProperty $appKey -Name $v.Key -Value $v.Value }
    foreach ($v in 'NoModify', 'NoRepair') { New-ItemProperty $appKey -Name $v -Value 1 -PropertyType DWord -Force | Out-Null }
    New-ItemProperty $appKey -Name EstimatedSize -Value $sizeKb -PropertyType DWord -Force | Out-Null
    Say "Start menu entries and an entry in installed apps (xpp-lens $appVersion)"
}

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

Head "4/5 Registering in Claude (MCP server, skills)"
if ($NoRegister) {
    Say "skipped (-NoRegister). Manually: `"$exe`" register"
}
else {
    $regArgs = @('register')
    if ($DesktopOnly) { $regArgs += '--desktop' }
    if ($CodeOnly)    { $regArgs += '--code' }
    & $exe @regArgs
}
# Claude Code skills (xpp-ponytail): a skill changed locally is kept and reported with a warning.
if ($NoSkills) {
    Say "skills skipped (-NoSkills). Manually: `"$exe`" skills install"
}
else {
    # Language of the skill texts: -SkillsLanguage, else kept from the last installation, else that of Windows.
    $skillArgs = @('skills', 'install')
    if ($SkillsLanguage) { $skillArgs += @('--lang', $SkillsLanguage) }
    elseif (-not (Test-Path (Join-Path $InstallDir 'skills-installed.json'))) {
        $skillArgs += @('--lang', $(if ((Get-UICulture).TwoLetterISOLanguageName -eq 'pl') { 'pl' } else { 'en' }))
    }
    & $exe @skillArgs | ForEach-Object {
        if ($_ -like 'warning:*') { Write-Host "  $_" -ForegroundColor Yellow } else { Say $_ }
    }
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
        $answer = Read-Host "  Claude Desktop is running. Restart it now so all its sessions use ${version}? Work in progress there is interrupted. [y/N]"
        $restart = $answer -match '^\s*(y|yes|t|tak)\s*$'
    }
    if ($restart) {
        # Shared with the setup program: works out how to start Claude before stopping it.
        & (Join-Path $src 'restart-claude.ps1') -Version $version
    }
    else {
        Say "Claude Desktop: open sessions keep the old version until it is restarted; new sessions use $version."
    }
}
Say "Claude Code in a terminal / VS Code: '/mcp' -> reconnect xpp-lens, or start a new session."
Say "Change settings later:  `"$exe`" config --add-language de --add-full-model Contoso"
Say "Index status:           `"$exe`" status"
Say "Uninstall:              $InstallDir\uninstall.ps1"
