# Changelog

All notable changes to xpp-lens (called xpp-graft up to 1.2.0). Versions follow `<Version>` in
`src/XppLens/XppLens.csproj`; release tags are `vX.Y.Z`.

## 1.3.1 — 2026-10-26

### Fixed
- `xpp_refs name="Table.Field"` answered "no references": the whole text was taken as an object name, while
  `xpp_find` takes that form. `xpp_refs` now reads `Table.Field` / `Class::method` as the object and its member
  (unless an object has that whole name, as extensions named `Base.Model` do) and says how it read the name, so the
  answer equals `name=Table member=Field`: code reads (also after `Table::find().Field`) and form controls.
- `xpp_object` did not show whether a table is per company: the XML leaves out `SaveDataPerCompany` and `TableType`
  when they have their default values. Tables now always list them, marked `(default)` when not in the file, and
  `xpp_find property=SaveDataPerCompany` reports `Yes` for those tables instead of `-`.

## 1.3.0 — 2026-10-26

### Added
- Setup program `xpp-lens-setup-X.Y.Z.exe` (Inno Setup): a wizard in Polish or English for the current user, no
  administrator rights. It finds PackagesLocalDirectory (`xpplens detect`, or choose the folder), asks for the label
  languages and whether to index the Microsoft standard, registers xpp-lens in Claude Desktop and Claude Code and
  offers to build the index. It puts `xpplens` on the user's PATH, adds Start menu entries (status, update the index,
  check for updates, uninstall) and an entry in Windows' installed apps; uninstalling asks whether to remove the index.
  An installation made by `install.ps1` (`C:\Tools\xpp-lens`) is taken over in place with its settings and index.
- `xpplens update --install` runs the setup program of the release silently into the current installation (SHA-256
  checked, `--restart-claude` passed on); releases without one still go through the ZIP and `install.ps1`.
- `restart-claude.ps1`: the Claude Desktop restart of the installers, shared by `install.ps1` and the setup program.
- Test releases: a version can first be published as `X.Y.Z-beta.N`, marked as a pre-release on GitHub.
  `xpplens update` never offers it; `xpplens update --install --pre` installs the newest release including test
  releases, so a new version can be tried on one machine before everyone gets it. The regular `X.Y.Z` later
  replaces a test release through the usual `xpplens update --install`.
- `xpplens version`, `status` and the MCP server report the full version of a test release (`1.3.0-beta.1`); versions
  compare with test releases before their final version (`1.3.0-beta.2` < `1.3.0`).

### Development
- Work happens on the branch `dev`; `main` always holds the last regular release. `release.ps1 -Version X.Y.Z -Pre`
  publishes a test release from `dev`; `release.ps1 -Version X.Y.Z` opens the pull request `dev` → `main`, waits
  for its CI, merges it (merge commit), tags the merge and publishes the release, then moves `dev` to `main`.
  CI also runs for pushes to `dev`. The release notes of a test release come from the section of its final version.

## 1.2.9 — 2026-10-14

### Fixed
- The file properties of `xpplens.exe` showed a garbled title and copyright (`xpp-lens â€” …`, `Copyright Â©`):
  the project file held those characters double-encoded.

### Development
- `release.ps1 -Version X.Y.Z` releases a version end to end and stops at the first failure: CHANGELOG section and
  free tag, `<Version>`, a scan for private names (customers, partners, people) from a local list that is never part
  of the repository, Release build and tests, commit and push, CI, tag, Release workflow, and a check of the
  published package (SHA-256 digest, layout). `-DryRun` runs the checks only. JSON from the GitHub CLI is filtered
  in PowerShell: Windows PowerShell 5.1 drops the inner quotes of a `--jq` filter passed to a native program.

## 1.2.8 — 2026-10-14

### Fixed
- Restarting Claude Desktop at the end of an update could close Claude and fail to start it again ("Cannot process
  argument transformation on parameter 'Package'"): after Claude had updated itself, the running process was in an
  older version folder than the installed package, so the package was not found. The application is now found by
  its package family (from the Start menu, then the package list) and the way to start it is worked out before Claude
  is stopped — if it cannot be, Claude is left running with a note to restart it yourself. Per-user installations
  start through their launcher, which always runs the current version.
- The restart question did not show the new version (`$version?` is a different variable in PowerShell).
- Release packages since 1.2.5 nested their files as `xpp-lens/xpp-lens/dist/xpp-lens/…` because the release
  workflow passes a relative output folder; `pack.ps1` now works with the full path. Updating was not affected (the
  updater finds `install.ps1` at any depth), unpacking by hand was.

## 1.2.7 — 2026-10-13

### Added
- X++ kernel types are indexed as the compiled model `Kernel`: `QueryBuildDataSource`, `QueryBuildRange`,
  `FormDataObject`, `xFormRun`, `xRecord`, `Map` and the other system classes with complete method signatures
  (parameter types, defaults, static / final), base classes, system enums, EDTs and tables — about 3 400 objects read
  from the resources of `bin\Microsoft.Dynamics.AX.Metadata.dll` (the metadata the X++ compiler uses), without
  loading the assembly. Indexed in the background with the standard tier; re-read when the assembly changes.
- `xpp_object` data sources show their behaviour properties (`InsertIfEmpty`, `InsertAtEnd`, `AllowCreate` /
  `AllowEdit` / `AllowDelete`, `LinkType`, `OnlyFetchActive`, `DelayActive`, `Index`, `CounterField`…) and the
  number of fields; `parent=<data source>` shows one data source with all its fields.

### Fixed
- `xpp_find "Table.member"` did not find fields and methods added in extensions of the table (found only with
  `*.member` or the extension name). The object part now also matches extensions that name the object as their
  target; the base object's own members come first.

## 1.2.6 — 2026-10-13

### Changed
- Faster answers, mostly after a pause, when the OS has dropped the index from its file cache and the disk is
  slow. Measured on a real installation before / after: a substring search (`xpp_find *Text*`) right after the server starts 41 s → 1 s,
  later 1.3 s → 0.2 s; earlier usage logs showed single calls of 100–180 s.
  - Queries run in parallel, each on its own connection: one slow query no longer holds up the others (they used
    to finish together, all waiting for the first one).
  - The periodic rescan of custom model files runs on a timer in the MCP server instead of inside a query; files
    changed on disk (seen by the watchers) are still indexed before the next query.
  - Wildcard and substring name searches (`xpp_find *Invoice*`, "Similar" suggestions) are matched against the
    distinct names of objects, methods and members kept in memory (about 730 000 names, read in under a second at
    start and after index changes); the index then answers only exact names instead of scanning whole tables.
  - Results of `xpp_find` are ordered completely (ties broken by name), so the same question gives the same answer.

### Added
- `xpp_find property=…` lists root properties of every object matching the name, type and model filters, from the
  index: `query="*" type=entity model=Contoso property="PublicEntityName;PublicCollectionName"`;
  `property="PublicEntityName=*Order*"` filters by value (`xpplens find "*" --type entity --property …`).
- The usage log records, per call, the time spent waiting for the index refresh and the query time;
  `xpplens stats` shows them (`p95 wait`, `p95 query`, and per slowest call). `status` shows the names held in memory.

## 1.2.5 — 2026-10-09

### Changed
- Updating no longer needs Claude to be closed. The installer does not stop running `xpplens.exe` processes any
  more: a file in use is renamed to `*.old`, so open sessions keep working on the old version and new sessions start
  the new one; the next start of xpplens deletes the leftovers. At the end the installer offers to restart Claude
  Desktop; `xpplens update --install --restart-claude` (`install.ps1 -RestartClaude`) does it without asking.
  Claude Code processes in terminals are never stopped.
- `xpp_grep` accepts several names separated by `;` in `objectName` and `model`, like the other tools. The answer
  for standard code without a scope, or with too large a scope, points to `xpp_refs` / `xpp_callers` for names.
- The release package stores its paths with `/`, as the ZIP format requires.
- GitHub Actions: `actions/checkout` v7 and `actions/setup-dotnet` v6 (Node 24).

### Fixed
- Command line options: `-install` was silently ignored (only `--install` was known), so `xpplens update -install`
  only checked for the update. Options now work with one or two dashes and as `--name=value`, may come before the
  arguments, and an unknown option or a missing value stops with an error that lists the options of the command.

## 1.2.4 — 2026-10-07

### Added
- `xpp_build_errors` reads the result of a Visual Studio project build (`BuildProjectResult.xml`) next to that of the
  model build. The header shows both build times and the time of the model DLL. Diagnostics of the newer build come
  first and replace those of the model build for the same objects.
- `xpp_build_errors` lists the objects changed after the newest build, which are not compiled yet. After a project
  build it also lists the objects changed between the model build and the project build, which are compiled only if
  they are in the project. This answers "did my build take everything?" in one call.
- `xpp_changed since=build` (`xpplens changed --since build`): per package, the objects changed after its last build.

### Changed
- `xpplens status` / `xpp_status` start with the xpp-lens version (and an available update) and show the index tiers
  and counts as tables. The console gets box-drawing frames; output redirected to a file or a pipe, or `--ascii`, gets
  `+-|` frames; `xpp_status` returns Markdown tables. Numbers are grouped by thousands; long model lists come last.

### Fixed
- After a project build, `xpp_build_errors` showed the older model build as the latest result. Objects changed after
  the build were marked only when they had a diagnostic; with a clean build they were not mentioned at all.

## 1.2.3 — 2026-10-07

### Fixed
- `xpp_build_errors` pointed at wrong lines: the compiler numbers lines through the object's whole code (the
  declaration without its closing brace, then every method in file order), not from the start of the method. Lines
  are now translated that way and accepted only when they fall into the method the compiler named. Metadata
  diagnostics (form designs, controls, form extensions) show the object's file and the element instead of a raw path.
  Methods of form data sources and controls show where the method starts; their exact line is not mapped yet.

## 1.2.2 — 2026-10-07

### Added
- `xpplens update` checks GitHub for a newer release; `xpplens update --install` downloads it, verifies its SHA-256
  digest and runs its installer, keeping settings and index. `status` mentions an available update found by the
  last check. This is the only network access of xpp-lens and happens only when the command is run.
- Automated releases: pushing a tag `vX.Y.Z` runs the tests, checks the tag against the project version, builds
  the package and publishes the release with its notes taken from this file (`release-notes.ps1`).
- Continuous integration: GitHub Actions builds the solution and runs the tests on Windows for every push and pull request.
- `SECURITY.md` and issue forms for bug reports and feature requests, which ask not to share customer code or data.

### Changed
- `xpplens status` / `xpp_status` no longer count all references and labels (tens of seconds on a cold disk);
  `--counts` / `counts=true` shows them. A standard tier that is listed but not indexed is explained, with the
  command that indexes it.
- `xpplens build --std-only` switches `indexStandard` on when an installation was made without the standard tier.
- Running `install.ps1` over an existing installation keeps its label languages and `PackagesLocalDirectory`
  unless `-Languages` / `-PackagesDir` are given.

### Fixed
- Taking over an xpp-graft installation could leave the index behind and build a new one from scratch. The index
  files are now moved one by one by `xpplens migrate` and checked; when they cannot be moved (in use, or the new
  folder already holds an index) the new configuration keeps using the old index where it is.

## 1.2.1 — 2026-10-06

### Changed
- **Renamed from xpp-graft to xpp-lens**, to avoid confusion with the unrelated
  [Graft](https://github.com/trailhq/Graft) project. Program `xpplens.exe`, MCP server `xpp-lens`, configuration
  `xpplens.json`, default folders `C:\Tools\xpp-lens` and `%LOCALAPPDATA%\xpp-lens`, environment variables
  `XPPLENS_CONFIG` / `XPPLENS_VERBOSE` / `XPPLENS_TIMING` / `XPPLENS_USAGE`. The MCP tool names (`xpp_find`, …)
  are unchanged.
- `install.ps1` takes over an existing xpp-graft installation: settings (incl. label languages), the index and the
  usage log move over without a rebuild, and the old MCP entries are removed. The old folder is left for you to delete.
- Release packages are named after the version (`xpp-lens-1.2.1.zip`).

## 1.2.0 — 2026-10-06

### Added
- **Usage statistics**: every MCP call is logged locally (`%LOCALAPPDATA%\xpp-graft\usage`); `xppgraft stats` shows
  tokens, time and empty answers per tool, the largest and slowest calls and the latest empty answers.
  `xppgraft config --usage-log false` switches it off.
- **Several lookups in one call**: `xpp_find` and `xpp_object` take names separated by `;`; `xpp_method` takes
  `method="a;b"` or `objectName="Obj.method;Obj2::method"`.
- **Method fragments**: `xpp_method` with `match` (regex) and/or `lines` returns only those lines with context,
  numbered with file lines; the signature and variable declarations are always included, skipped parts are marked.
  Long methods returned in full carry a hint about it.
- **Callers inside Microsoft code**: the standard tier now stores call references (calls, `new`, intrinsics) —
  `xpp_callers` lists them after custom code; `standard=false` leaves them out. About +300 MB of index;
  `xppgraft config --standard-code false` keeps the old dictionary-only standard tier.
- **Chained calls**: `Table::find(x).method()`, `a.b().c()`, `new T().m()` and `var x = A::b()` are typed from method
  return types — in `xpp_callers`, `xpp_refs` (with `member`) and `xpp_callees`.
- New tools:
  - `xpp_scaffold` — CoC wrapper (exact signature, default values removed, warnings for private / final /
    non-wrappable methods), table / form / data source / control / field event handlers with parameters learned from
    existing handlers, delegate subscribers, pre/post handlers; class names follow the pattern found in your models.
  - `xpp_build_errors` — errors and warnings from `BuildModelResult.xml` mapped to XML file lines, flags files changed
    after the build.
  - `xpp_security` — menu item / form → privileges (granted access) → duties → roles, and privilege / duty / role views.
  - `xpp_join` — shortest relation path between two tables as a ready X++ join.
  - `xpp_entity` — data entity overview (public names, staging, data source tree with joins, field mapping, keys)
    or the entities that use a table.
  - `xpp_changed` — objects changed on disk since a time, per model.
- Matching CLI commands: `scaffold`, `build-errors`, `security`, `join`, `entity`, `changed`, `stats`;
  `method --match/--lines/--context`, `callers --standard false`.
- xUnit test project (`tests/XppGraft.Tests`) with neutral sample AOT XML; runs in Visual Studio Test Explorer or
  with `dotnet test`.
- Visual Studio launch profiles for the new commands.

### Changed
- Analyzer upgrades re-parse custom models once at start-up and re-index the standard tier once in the background,
  package by package (an interrupted run resumes) — no manual rebuild after updating.
- Parser: data entity keys, query / entity data source joins (`on Field=DataSource.Field`) and data source tree,
  privilege entry point grants (`grant=Read,Update,…`).
- Tool descriptions no longer contain installation-specific examples.
- The release package also ships `CHANGELOG.md` and `LICENSE`; `build.ps1 -Test` runs the tests before building.
- The WAL file is trimmed after checkpoints (`journal_size_limit`), so a large rebuild does not leave it at peak size.

## 1.1.0 — 2026-10-06

First published release, under the MIT License.

- Three index tiers: custom models (full code references), Microsoft standard (dictionary), compiled-only packages
  (from `.xref`, `bin\*.md` and resource DLLs); `xpp_status` lists packages on disk that are not indexed.
- Tools: `xpp_find`, `xpp_object`, `xpp_method`, `xpp_callers`, `xpp_callees`, `xpp_refs`, `xpp_extensions`,
  `xpp_grep`, `xpp_label`, `xpp_status`.
- Control and menu trees with `parent` / `depth` / `filter`, full control paths, ReferenceField / ReplacementFieldGroup,
  menu extension parent and position.
- Literal underscore in name searches; dotted extension names (e.g. `*Staging.Contoso`) found as objects.
- Index follows the files on disk (watcher + periodic scan); read-only index detection with live parsing.
- Portable installer / uninstaller, configurable languages, models and `PackagesLocalDirectory` (given or detected).
