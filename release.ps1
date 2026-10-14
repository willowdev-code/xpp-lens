<#
.SYNOPSIS
    Releases a new xpp-lens version end to end: checks, commit, push, CI, tag, published package.

.DESCRIPTION
    Stops at the first failure; nothing leaves the machine before the scan, the build and the tests have passed.
      1. CHANGELOG.md has a "## X.Y.Z" section; the tag vX.Y.Z does not exist yet
      2. <Version> in src/XppLens/XppLens.csproj is set to X.Y.Z
      3. private names scan of every file of the repository (see -ScanList)
      4. Release build and tests
      5. commit (when something changed) and push of main; waits for the CI workflow
      6. tag vX.Y.Z and push; waits for the Release workflow
      7. downloads the published package and checks its SHA-256 digest and its layout

    The scan list holds names that must never reach this public repository (customers, partners, people), so it
    is never part of the repository: a local text file, one .NET regular expression per line (case-insensitive;
    "(?-i)" at the start makes a line case-sensitive), "#" comments, and lines starting with "!" are allowed
    words removed from a line before it is checked (e.g. a Polish word that contains a customer prefix).
    It is looked for in this order: -ScanList, the XPPLENS_SCAN_LIST environment variable,
    %USERPROFILE%\.claude\skills\xpp-lens-release\scan-names.txt. Without it the release stops.

.EXAMPLE
    .\release.ps1 -Version 1.2.9
.EXAMPLE
    .\release.ps1 -Version 1.2.9 -DryRun      # steps 1-4 only, changes nothing
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [string] $ScanList,
    [switch] $DryRun,
    [int]    $TimeoutMinutes = 30
)

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
function Step($m) { Write-Host "`n== $m" -ForegroundColor Cyan }
function Ok($m) { Write-Host "   $m" }
function Fail($m) { Write-Host "`nRELEASE STOPPED: $m" -ForegroundColor Red; exit 1 }
function Native([string]$what) { if ($LASTEXITCODE -ne 0) { Fail "$what failed (exit code $LASTEXITCODE)" } }

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = Join-Path $env:ProgramFiles 'GitHub CLI\gh.exe' }
if (-not (Test-Path $gh)) { Fail "GitHub CLI (gh) not found" }
$tag = "v$Version"
$csproj = 'src\XppLens\XppLens.csproj'

# ---------------------------------------------------------------- 1. preconditions
Step "1/7 Checks for $tag"
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version '$Version' is not X.Y.Z" }
$branch = (git rev-parse --abbrev-ref HEAD).Trim(); Native 'git rev-parse'
if ($branch -ne 'main') { Fail "on branch '$branch', releases are made from main" }
git fetch --quiet --tags origin; Native 'git fetch'
if (git tag --list $tag) { Fail "tag $tag already exists - published versions cannot change; use the next version" }
$behind = (git rev-list --count 'HEAD..origin/main').Trim()
if ([int]$behind -gt 0) { Fail "main is $behind commit(s) behind origin/main - pull first" }
$changelog = [IO.File]::ReadAllText((Resolve-Path 'CHANGELOG.md'))
$section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\b.*?(?=^## |\z)")
if (-not $section.Success) { Fail "CHANGELOG.md has no '## $Version' section" }
Ok "CHANGELOG section found, tag is free, main is up to date"

# ---------------------------------------------------------------- 2. version
Step "2/7 <Version> = $Version"
# "## X.Y.Z — unreleased" gets today's date.
if ($section.Value -match "^## $([regex]::Escape($Version))\b[^\r\n]*unreleased") {
    if ($DryRun) { Ok "would date the CHANGELOG section (dry run)" }
    else {
        $dated = [regex]::Replace($changelog, "(?m)^(## $([regex]::Escape($Version))\b[^\r\n]*?)unreleased", "`${1}$(Get-Date -Format 'yyyy-MM-dd')")
        [IO.File]::WriteAllText((Resolve-Path 'CHANGELOG.md'), $dated, (New-Object Text.UTF8Encoding($false)))
        $changelog = $dated
        $section = [regex]::Match($changelog, "(?ms)^## $([regex]::Escape($Version))\b.*?(?=^## |\z)")
        Ok "CHANGELOG section dated"
    }
}
$projText = [IO.File]::ReadAllText((Resolve-Path $csproj))
$newProj = [regex]::Replace($projText, '<Version>[^<]*</Version>', "<Version>$Version</Version>")
if ($newProj -eq $projText) { Ok "already $Version" }
elseif ($DryRun) { Ok "would set <Version>$Version</Version> (dry run)" }
else { [IO.File]::WriteAllText((Resolve-Path $csproj), $newProj, (New-Object Text.UTF8Encoding($false))); Ok "set" }

# ---------------------------------------------------------------- 3. private names scan
Step "3/7 Private names scan"
if (-not $ScanList) { $ScanList = $env:XPPLENS_SCAN_LIST }
if (-not $ScanList) { $ScanList = Join-Path $env:USERPROFILE '.claude\skills\xpp-lens-release\scan-names.txt' }
if (-not (Test-Path $ScanList)) { Fail "scan list not found ($ScanList) - without it the repository cannot be checked for private names" }
$patterns = @(); $allowed = @()
foreach ($line in Get-Content $ScanList -Encoding UTF8) {
    $l = $line.Trim()
    if ($l.Length -eq 0 -or $l.StartsWith('#')) { continue }
    if ($l.StartsWith('!')) { $allowed += $l.Substring(1); continue }
    $patterns += New-Object regex ($l, ([Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant))
}
if ($patterns.Count -eq 0) { Fail "scan list $ScanList has no patterns" }
$files = @(git ls-files) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique
$binary = '\.(exe|dll|pdb|zip|png|jpg|ico|snk)$'
$hits = New-Object System.Collections.Generic.List[string]
foreach ($f in $files) {
    if ($f -match $binary -or -not (Test-Path $f -PathType Leaf)) { continue }
    $n = 0
    foreach ($text in [IO.File]::ReadAllLines((Resolve-Path $f))) {
        $n++
        $check = $text
        foreach ($a in $allowed) { $check = [regex]::Replace($check, [regex]::Escape($a), '', 'IgnoreCase') }
        foreach ($p in $patterns) { if ($p.IsMatch($check)) { $hits.Add("${f}:${n}: $($text.Trim())"); break } }
    }
}
if ($hits.Count -gt 0) {
    $hits | Select-Object -First 20 | ForEach-Object { Write-Host "   $_" -ForegroundColor Yellow }
    Fail "$($hits.Count) line(s) with private names (above) - remove them or add an allowed word ('!word') to the scan list"
}
Ok "$($files.Count) files, $($patterns.Count) patterns: clean"

# ---------------------------------------------------------------- 4. build and tests
Step "4/7 Release build and tests"
dotnet test xpp-lens.sln -c Release --nologo -v q; Native 'dotnet test'
Ok "tests passed"

if ($DryRun) { Write-Host "`nDry run: checks passed, nothing committed, pushed or tagged." -ForegroundColor Green; exit 0 }

# ---------------------------------------------------------------- 5. commit, push, CI
Step "5/7 Commit, push, CI"
git add -A; Native 'git add'
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    # Title and the release's own notes from CHANGELOG as the body.
    $body = ($section.Value -split "`n" | Select-Object -Skip 1) -join "`n"
    $msgFile = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($msgFile, "xpp-lens $Version`n`n$($body.Trim())`n", (New-Object Text.UTF8Encoding($false)))
    git commit --quiet -F $msgFile; Native 'git commit'
    Remove-Item $msgFile
    Ok "committed"
}
else { Ok "nothing to commit" }
git push --quiet origin main; Native 'git push'
$sha = (git rev-parse HEAD).Trim()
Ok "pushed $sha"

# JSON from gh is filtered here, not with --jq: Windows PowerShell 5.1 drops the inner double quotes of an argument
# passed to a native program, so a jq filter like select(.headSha=="abc") would arrive broken.
function Gh-Json([string[]]$ghArgs) {
    $out = & $gh @ghArgs
    if ($LASTEXITCODE -ne 0) { return $null }
    return (($out -join "`n") | ConvertFrom-Json)
}

function Wait-Run([string]$workflow, [scriptblock]$match, [string]$label) {
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $id = $null
    while (-not $id -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $runs = Gh-Json @('run', 'list', '--workflow', $workflow, '--limit', '20', '--json', 'databaseId,headSha,headBranch')
        $id = (@($runs) | Where-Object $match | Select-Object -First 1).databaseId
    }
    if (-not $id) { Fail "$label run did not start within $TimeoutMinutes minutes" }
    Ok "$label run $id ..."
    & $gh run watch $id --exit-status --interval 15 | Out-Null
    $conclusion = (& $gh run view $id --json conclusion --jq '.conclusion').Trim()
    if ($conclusion -ne 'success') { Fail "$label run $id ended with '$conclusion' - see: gh run view $id --log-failed" }
    Ok "${label}: success"
}
Wait-Run 'ci.yml' { $_.headSha -eq $sha } 'CI'

# ---------------------------------------------------------------- 6. tag, release workflow
Step "6/7 Tag $tag, Release workflow"
git tag $tag $sha; Native 'git tag'
git push --quiet origin $tag; Native 'git push tag'
Wait-Run 'release.yml' { $_.headBranch -eq $tag } 'Release'

# ---------------------------------------------------------------- 7. published package
Step "7/7 Published package"
$asset = "xpp-lens-$Version.zip"
$release = Gh-Json @('release', 'view', $tag, '--json', 'assets')
$digest = (@($release.assets) | Where-Object { $_.name -eq $asset } | Select-Object -First 1).digest
if (-not $digest) { Fail "release $tag has no $asset" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) "xpp-lens-release-check-$Version"
if (Test-Path $tmp) { Remove-Item $tmp -Recurse -Force }
New-Item -ItemType Directory $tmp | Out-Null
& $gh release download $tag --pattern $asset --dir $tmp; Native 'gh release download'
$zip = Join-Path $tmp $asset
$sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
if ("sha256:$sha256" -ne $digest) { Fail "downloaded $asset does not match the published digest ($digest)" }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($zip)
$entries = @($archive.Entries | ForEach-Object { $_.FullName })
$archive.Dispose()
Remove-Item $tmp -Recurse -Force
foreach ($e in 'xpp-lens/install.ps1', 'xpp-lens/bin/xpplens.exe', 'xpp-lens/CHANGELOG.md') {
    if ($entries -notcontains $e) { Fail "$asset has no $e (entries: $($entries -join ', '))" }
}
Ok "${asset}: SHA-256 matches, layout ok"

$url = (& $gh release view $tag --json url --jq '.url').Trim()
Write-Host "`nReleased xpp-lens ${Version}: $url" -ForegroundColor Green
Write-Host "Install: xpplens update --install"
