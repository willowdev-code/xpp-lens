<#
.SYNOPSIS
    Releases xpp-lens end to end from the dev branch: a test release (-Pre) or a regular release.

.DESCRIPTION
    Work happens on the branch dev; main always holds the last regular release. Stops at the first failure;
    nothing leaves the machine before the scan, the build and the tests have passed.

    Test release (-Pre), X.Y.Z-beta.N, N counted from the existing tags:
      1. on dev, up to date; CHANGELOG.md has a "## X.Y.Z" section (may stay "unreleased"); X.Y.Z not released yet
      2. <Version> = X.Y.Z-beta.N
      3. private names scan, 4. Release build and tests
      5. commit and push of dev; waits for CI
      6. tag vX.Y.Z-beta.N; waits for the Release workflow, which publishes it as a pre-release
      7. checks the published package (SHA-256 digest, layout, marked as test release)

    Regular release, X.Y.Z:
      1. on dev, up to date and containing main; CHANGELOG section; tag free
      2. dates the CHANGELOG section ("unreleased" -> today), <Version> = X.Y.Z
      3. private names scan, 4. Release build and tests
      5. commit and push of dev; waits for CI
      6. pull request dev -> main, waits for its CI, merges it (merge commit), tags the merge on main and waits
         for the Release workflow
      7. checks the published package (SHA-256 digest, layout, regular release); fast-forwards dev to main

    The scan list holds names that must never reach this public repository (customers, partners, people), so it
    is never part of the repository: a local text file, one .NET regular expression per line (case-insensitive;
    "(?-i)" at the start makes a line case-sensitive), "#" comments, and lines starting with "!" are allowed
    words removed from a line before it is checked (e.g. a Polish word that contains a customer prefix).
    It is looked for in this order: -ScanList, the XPPLENS_SCAN_LIST environment variable,
    %USERPROFILE%\.claude\skills\xpp-lens-release\scan-names.txt. Without it the release stops.

.EXAMPLE
    .\release.ps1 -Version 1.3.0 -Pre       # test release 1.3.0-beta.N
.EXAMPLE
    .\release.ps1 -Version 1.3.0            # regular release
.EXAMPLE
    .\release.ps1 -Version 1.3.0 -DryRun    # steps 1-4 only, changes nothing
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [switch] $Pre,
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
$csproj = 'src\XppLens\XppLens.csproj'
$work = 'dev'

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
        $runs = Gh-Json @('run', 'list', '--workflow', $workflow, '--limit', '30', '--json', 'databaseId,headSha,headBranch,event')
        $id = (@($runs) | Where-Object $match | Select-Object -First 1).databaseId
    }
    if (-not $id) { Fail "$label run did not start within $TimeoutMinutes minutes" }
    Ok "$label run $id ..."
    & $gh run watch $id --exit-status --interval 15 | Out-Null
    $conclusion = (& $gh run view $id --json conclusion --jq '.conclusion').Trim()
    if ($conclusion -ne 'success') { Fail "$label run $id ended with '$conclusion' - see: gh run view $id --log-failed" }
    Ok "${label}: success"
}

# ---------------------------------------------------------------- 1. preconditions
if ($Version -notmatch '^\d+\.\d+\.\d+$') { Fail "version '$Version' is not X.Y.Z (a test release adds -Pre, not a suffix)" }
git fetch --quiet --tags origin; Native 'git fetch'
$finalTag = "v$Version"
if (git tag --list $finalTag) { Fail "$Version is already released (tag $finalTag) - published versions cannot change; use the next version" }
if ($Pre) {
    $n = 1 + (@(git tag --list "v$Version-beta.*") | ForEach-Object { if ($_ -match '-beta\.(\d+)$') { [int]$Matches[1] } } | Measure-Object -Maximum).Maximum
    $fullVersion = "$Version-beta.$n"
}
else { $fullVersion = $Version }
$tag = "v$fullVersion"
Step "1/7 Checks for $tag$(if ($Pre) { ' (test release)' })"

$branch = (git rev-parse --abbrev-ref HEAD).Trim(); Native 'git rev-parse'
if ($branch -ne $work) { Fail "on branch '$branch' - releases are made from $work (git switch $work)" }
$behind = (git rev-list --count "HEAD..origin/$work").Trim()
if ([int]$behind -gt 0) { Fail "$work is $behind commit(s) behind origin/$work - pull first" }
if (-not $Pre) {
    $missing = (git rev-list --count 'HEAD..origin/main').Trim()
    if ([int]$missing -gt 0) { Fail "$work lacks $missing commit(s) of main - merge origin/main into $work first" }
}
$changelog = [IO.File]::ReadAllText((Resolve-Path 'CHANGELOG.md'))
$sectionRx = "(?ms)^## $([regex]::Escape($Version))\b.*?(?=^## |\z)"
$section = [regex]::Match($changelog, $sectionRx)
# A dry run also serves as the scan before an ordinary push to dev, so it goes on without the section.
if (-not $section.Success -and $DryRun) { Write-Host "   WARNING: CHANGELOG.md has no '## $Version' section (needed for the release)" -ForegroundColor Yellow }
elseif (-not $section.Success) { Fail "CHANGELOG.md has no '## $Version' section" }
Ok "CHANGELOG section found, tag is free, $work is up to date"

# ---------------------------------------------------------------- 2. version
Step "2/7 <Version> = $fullVersion"
# A regular release dates "## X.Y.Z - unreleased"; a test release leaves it undated.
if (-not $Pre -and $section.Value -match "^## $([regex]::Escape($Version))\b[^\r\n]*unreleased") {
    if ($DryRun) { Ok "would date the CHANGELOG section (dry run)" }
    else {
        $dated = [regex]::Replace($changelog, "(?m)^(## $([regex]::Escape($Version))\b[^\r\n]*?)unreleased", "`${1}$(Get-Date -Format 'yyyy-MM-dd')")
        [IO.File]::WriteAllText((Resolve-Path 'CHANGELOG.md'), $dated, (New-Object Text.UTF8Encoding($false)))
        $section = [regex]::Match($dated, $sectionRx)
        Ok "CHANGELOG section dated"
    }
}
$projText = [IO.File]::ReadAllText((Resolve-Path $csproj))
$newProj = [regex]::Replace($projText, '<Version>[^<]*</Version>', "<Version>$fullVersion</Version>")
if ($newProj -eq $projText) { Ok "already $fullVersion" }
elseif ($DryRun) { Ok "would set <Version>$fullVersion</Version> (dry run)" }
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

# ---------------------------------------------------------------- 5. commit, push, CI on dev
Step "5/7 Commit, push $work, CI"
$title = if ($Pre) { "xpp-lens $fullVersion (test release)" } else { "xpp-lens $fullVersion" }
$body = (($section.Value -split "`n" | Select-Object -Skip 1) -join "`n").Trim()
git add -A; Native 'git add'
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    $msgFile = [IO.Path]::GetTempFileName()
    [IO.File]::WriteAllText($msgFile, "$title`n`n$body`n", (New-Object Text.UTF8Encoding($false)))
    git commit --quiet -F $msgFile; Native 'git commit'
    Remove-Item $msgFile
    Ok "committed"
}
else { Ok "nothing to commit" }
git push --quiet origin $work; Native 'git push'
$sha = (git rev-parse HEAD).Trim()
Ok "pushed $sha"
Wait-Run 'ci.yml' { $_.headSha -eq $sha -and $_.event -eq 'push' } 'CI'

# ---------------------------------------------------------------- 6. (merge to main,) tag, release workflow
if ($Pre) {
    Step "6/7 Tag $tag on $work, Release workflow"
    $tagSha = $sha
}
else {
    Step "6/7 Pull request $work -> main, merge, tag $tag, Release workflow"
    $open = Gh-Json @('pr', 'list', '--base', 'main', '--head', $work, '--state', 'open', '--json', 'number')
    $pr = (@($open) | Select-Object -First 1).number
    if (-not $pr) {
        $bodyFile = [IO.Path]::GetTempFileName()
        [IO.File]::WriteAllText($bodyFile, "$body`n", (New-Object Text.UTF8Encoding($false)))
        & $gh pr create --base main --head $work --title $title --body-file $bodyFile | Out-Null; Native 'gh pr create'
        Remove-Item $bodyFile
        $pr = (@(Gh-Json @('pr', 'list', '--base', 'main', '--head', $work, '--state', 'open', '--json', 'number')) | Select-Object -First 1).number
        if (-not $pr) { Fail "the pull request $work -> main was not found after creating it" }
        Ok "pull request #$pr"
    }
    else { Ok "pull request #$pr (already open)" }
    Wait-Run 'ci.yml' { $_.headSha -eq $sha -and $_.event -eq 'pull_request' } 'PR CI'
    & $gh pr merge $pr --merge --subject $title | Out-Null; Native 'gh pr merge'
    $merged = Gh-Json @('pr', 'view', "$pr", '--json', 'mergeCommit,state')
    if ($merged.state -ne 'MERGED' -or -not $merged.mergeCommit.oid) { Fail "pull request #$pr is not merged (state $($merged.state))" }
    $tagSha = $merged.mergeCommit.oid
    git fetch --quiet origin; Native 'git fetch'
    Ok "merged into main as $tagSha"
}
git tag $tag $tagSha; Native 'git tag'
git push --quiet origin $tag; Native 'git push tag'
Wait-Run 'release.yml' { $_.headBranch -eq $tag } 'Release'

# ---------------------------------------------------------------- 7. published package
Step "7/7 Published package"
$asset = "xpp-lens-$fullVersion.zip"
$release = Gh-Json @('release', 'view', $tag, '--json', 'assets,isPrerelease,url')
$digest = (@($release.assets) | Where-Object { $_.name -eq $asset } | Select-Object -First 1).digest
if (-not $digest) { Fail "release $tag has no $asset" }
if ([bool]$release.isPrerelease -ne [bool]$Pre) { Fail "release $tag is $(if ($release.isPrerelease) { 'a test release' } else { 'a regular release' }) - expected the other kind" }
$tmp = Join-Path ([IO.Path]::GetTempPath()) "xpp-lens-release-check-$fullVersion"
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
Ok "${asset}: SHA-256 matches, layout ok, $(if ($Pre) { 'test release' } else { 'regular release' })"

# The setup program: present, with a digest that matches what is downloaded (xpplens update --install uses it).
$setupName = "xpp-lens-setup-$fullVersion.exe"
$setupDigest = (@($release.assets) | Where-Object { $_.name -eq $setupName } | Select-Object -First 1).digest
if (-not $setupDigest) { Fail "release $tag has no $setupName" }
New-Item -ItemType Directory $tmp | Out-Null
& $gh release download $tag --pattern $setupName --dir $tmp; Native 'gh release download (setup)'
$setupSha = (Get-FileHash (Join-Path $tmp $setupName) -Algorithm SHA256).Hash.ToLowerInvariant()
Remove-Item $tmp -Recurse -Force
if ("sha256:$setupSha" -ne $setupDigest) { Fail "downloaded $setupName does not match the published digest ($setupDigest)" }
Ok "${setupName}: SHA-256 matches"

if (-not $Pre) {
    # main = dev + the merge commit: dev moves forward to it, so the next pull request starts clean.
    git merge --quiet --ff-only origin/main
    if ($LASTEXITCODE -eq 0) { git push --quiet origin $work; Ok "$work fast-forwarded to main" }
    else { Write-Host "   NOTE: $work could not be fast-forwarded to main - merge origin/main into $work by hand" -ForegroundColor Yellow }
}

Write-Host "`nReleased xpp-lens ${fullVersion}: $($release.url)" -ForegroundColor Green
if ($Pre) { Write-Host "Install on a test machine: xpplens update --install --pre" }
else { Write-Host "Install: xpplens update --install" }
