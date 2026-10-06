# Changelog

All notable changes to xpp-graft. Versions follow `<Version>` in `src/XppGraft/XppGraft.csproj`;
release tags are `vX.Y.Z`.

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
