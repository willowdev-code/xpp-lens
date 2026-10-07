# xpp-lens — X++ (D365 F&O) code index as an MCP server

[![CI](https://github.com/willowdev-code/xpp-lens/actions/workflows/ci.yml/badge.svg)](https://github.com/willowdev-code/xpp-lens/actions/workflows/ci.yml)

**English** | [Polski](README.pl.md)

Reads `PackagesLocalDirectory` **read-only** and builds its own index (SQLite) outside the AOS repository.
Claude uses it over MCP instead of reading huge AOT XML files. Changes between versions: [CHANGELOG.md](CHANGELOG.md).

© 2026 WillowDev. Released under the [MIT License](LICENSE).

An independent project, inspired by [Graft](https://github.com/trailhq/Graft) (a code graph for coding agents in many languages); xpp-lens shares no code with it and is built for X++ and the AOT. Until version 1.2.0 it was called xpp-graft — `install.ps1` takes over an existing xpp-graft installation (settings, index, Claude registration).

## Installing on a new machine

The install package is not part of the repository (`dist\` is excluded by `.gitignore`). There are two ways:

**A. From a release package** — nothing needs to be installed on the target machine, not even .NET.

1. Download `xpp-lens-X.Y.Z.zip` from the **Releases** tab of this repository.
2. Unzip it and run in a regular PowerShell window (not as administrator):

```powershell
powershell -ExecutionPolicy Bypass -File .\xpp-lens\install.ps1 -Languages en-US,pl
```

**B. From source** — requires the .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/xpp-lens.git C:\Dev\xpp-lens
cd C:\Dev\xpp-lens
.\pack.ps1                     # creates dist\xpp-lens\ and dist\xpp-lens-X.Y.Z.zip
powershell -ExecutionPolicy Bypass -File .\dist\xpp-lens\install.ps1 -Languages en-US,pl
```

The installer copies the files to `C:\Tools\xpp-lens`, detects `PackagesLocalDirectory` (from the AOS `web.config`
or the folder layout), writes the configuration, registers the server in Claude Desktop and Claude Code, and finally
builds the index (custom models take about a minute, the Microsoft standard 20–90 minutes once, depending on the disk).
Close Claude before installing and start it again afterwards.

Updating an installation: `xpplens update` shows whether a newer release exists; close Claude and run
`xpplens update --install` to download it (SHA-256 checked) and run its installer — settings and index are kept.
Running `install.ps1` from a newer package does the same.

Useful parameters:

```powershell
.\install.ps1 -PackagesDir K:\AosService\PackagesLocalDirectory   # instead of auto-detection
.\install.ps1 -Languages en-US,de -DisplayLanguage de             # label languages
.\install.ps1 -FullModels XPL,XPLRetail                           # force models into the full index
.\install.ps1 -StandardModels HugeIsvModel                        # push a model down to the standard tier
.\install.ps1 -NoStandard                                         # custom models only (fast, small index)
.\install.ps1 -First -NoBuild -NoRegister                         # non-interactive / CI
```

Uninstall: `uninstall.ps1` (removes the MCP entries that point to this installation, asks about the index and files).
New distribution package: `pack.ps1` (add `-FrameworkDependent` if you prefer 5 MB and a .NET 9 requirement).

## Configuration after installation

```powershell
xpplens config                                   # show settings
xpplens config --add-language de                 # add a label language
xpplens config --add-full-model XPL              # model/package into the full index
xpplens config --add-standard-model ContosoIsv   # model into the standard tier
xpplens config --add-standard-publisher "Contoso"
xpplens config --packages-dir K:\AosService\PackagesLocalDirectory
xpplens config --standard-code false             # no call references of Microsoft code (smaller index)
xpplens config --usage-log false                 # do not record MCP calls for 'xpplens stats'
xpplens detect [--set]                           # detect PackagesLocalDirectory
$1
xpplens build --std-only                         # add the Microsoft standard to an installation made with -NoStandard
```

Settings live in `xpplens.json` next to the `bin` folder and can also be edited by hand.

## Three index tiers

| Tier | Models | Contents |
|---|---|---|
| full | all non-Microsoft models (detected by `Publisher` in the descriptor) + those in `extraFullModels` | objects, members, method signatures and source, code references (calls incl. chained, types, fields, intrinsics, labels), metadata references |
| standard | Microsoft models | objects, fields/indexes/relations, method signatures with line ranges, `extends`, CoC and event handlers, and **calls** from method bodies (calls, `new`, intrinsics — no field reads, types or labels; switch off with `--standard-code false`) |
| compiled | packages deployed without XML (e.g. country localizations or ISV modules shipped in compiled form only) | object names from `bin\*.md`, methods, fields, field groups, relations and **compiler references** from `.xref`, CoC from `ChainOfCommand.xml`, inheritance from `ClassExtends.runtime`, labels from `Resources\<language>\*.resources.dll` — no source code |

A standard package can be promoted to the full tier (`--add-full-model ApplicationSuite`) if you need every reference
there (fields, types, labels) and instant `xpp_grep`. The cost is a longer build and a larger database.

Index size: about 0.6 GB without standard calls, about 0.9–1 GB with them (a typical dev VM with ~190k standard files).

Compiled packages rebuild themselves when their `.xref`, `.md` or resources change. Manually:
`xpplens build --compiled-only --force`. Folders that cannot be indexed at all are listed by `xpplens status`
in the "on disk but NOT indexed" line.

## Index permissions

By default the index goes to `%LOCALAPPDATA%\xpp-lens\index\xpp.db`, where the user running Claude can write.
If you put the index in the installation folder and install as administrator, Claude (running without elevation)
cannot update it — responses then show `index is read-only` and results freeze at the time of the build. Fix:

```powershell
xpplens config --index-path "$env:LOCALAPPDATA\xpp-lens\index\xpp.db"
xpplens build
```

In read-only mode `xpp_method` and `xpp_object` still return current code (they parse the file live),
but `xpp_find`, `xpp_callers` and `xpp_refs` use the frozen index.

## Index freshness

- `FileSystemWatcher` on full-tier model folders — a save in Visual Studio is visible on the next query.
- File date scan at startup and every `rescanIntervalSeconds` (default 5 min) — catches Get Latest from Team Explorer.
- Standard: package fingerprint (descriptors + `bin\*.dll`) — rebuilt only after a platform update.
- New version of xpp-lens with a changed code analyzer: custom models are re-parsed once at the next start
  (about a minute); the standard tier is re-indexed once in the background, package by package — an interrupted run
  continues where it stopped. `xpp_status` shows the progress.
- Writes are serialized with a named mutex, so Claude Desktop and Claude Code can run side by side.

## Tool map

What each MCP tool answers, when Claude should reach for it, and the CLI equivalent you can run yourself.

| Tool | Answers | Use it when | CLI example |
|---|---|---|---|
| `xpp_find` | where objects, methods, fields are (`*`, `?`, `Object.member`, dotted extension names) | you know a name or part of it | `xpplens find "Cust*Invoice*; SalesLine.createLine"` |
| `xpp_object` | skeleton of an object: properties with labels, fields, indexes, relations, data sources, control/menu tree, methods with line ranges, extensions | you need the structure, not the code | `xpplens object CustTable --type table` |
| `xpp_method` | the source of a method with file path and line range, CoC wrappers and handlers of it | you need the code; for long methods with `match`/`lines` | `xpplens method SalesTable validateWrite --match "checkFailed" --context 2` |
| `xpp_callers` | who calls a method — custom code first, then compiled packages and Microsoft code; chained calls (`Table::find().m()`) included | impact of a change, "where is this used" | `xpplens callers CustTable creditMax` |
| `xpp_callees` | what a method uses: calls (chained receivers typed), new, fields, enums, intrinsics, labels | understanding a method without reading it | `xpplens callees SalesFormLetter run` |
| `xpp_refs` | every use of a class, table, field, EDT, enum, menu item or label | renaming, removing, finding usages of a field | `xpplens refs CustTable --member CreditMax` |
| `xpp_extensions` | CoC classes (with wrapped methods), table/form extensions, event handlers, derived classes | "what already changes this object" | `xpplens ext SalesTable` |
| `xpp_scaffold` | ready X++: CoC wrapper, table/form/data source/control event handler, delegate subscriber, pre/post handler — exact signature, naming pattern of your models | before writing an extension | `xpplens scaffold coc SalesTable validateWrite --type table` |
| `xpp_build_errors` | errors/warnings of the last Visual Studio build, mapped to the XML file line | after a build, to fix errors without pasting logs | `xpplens build-errors --severity warning` |
| `xpp_security` | menu item / form → privileges (granted access) → duties → roles, and the reverse for privileges, duties, roles | access questions, new menu items | `xpplens security CustTable --type display` |
| `xpp_join` | shortest relation path between two tables as a ready `select … join … where` | writing a query across tables | `xpplens join CustInvoiceTrans CustTable` |
| `xpp_entity` | data entity: public names, staging table, data source tree with joins, field mapping, keys; or the entities that use a table | data management / OData work | `xpplens entity CustCustomerV3Entity` |
| `xpp_changed` | objects changed on disk since a time, per model | after Get Latest, reviewing own work | `xpplens changed --since 3d` |
| `xpp_grep` | regex over method bodies (custom models; standard with a model filter) | text patterns the other tools cannot express | `xpplens grep "ttsbegin" --model Contoso*` |
| `xpp_label` | label id → texts in all languages, or text → existing label ids | reusing labels | `xpplens label "Credit limit"` |
| `xpp_status` | index state, tiers, background work | checking freshness | `xpplens status` |

### Several lookups in one call, and method fragments

- `xpp_find` and `xpp_object` accept several names separated by `;` — one call, one section per name.
- `xpp_method` accepts several methods: `method="insert;update"` for one object, or `objectName="SalesTable.insert;CustTable::find"`.
- `xpp_method` with `match` (regex) and/or `lines` (`120-180`) returns only those lines (± `context`), numbered with the
  file line numbers; the signature and variable declarations are always included and skipped parts are marked
  `… N line(s)`. Without them the whole method is returned as before.

### Control and menu trees

Large trees (more than 60 elements) are collapsed to two levels with a child counter `[+N]`. To expand:

| Parameter | Effect | Example |
|---|---|---|
| `filter` | wildcard on name or path; prints **full paths** | `xpplens object CustTable --type form --filter *PersonalTitle*` |
| `parent` | only the subtree of one element (name or path) | `--parent TabGeneral` |
| `depth` | number of levels (below `parent`, if given) | `--parent UpperGroup --depth 1` |

ReferenceGroup controls show `ref=<datasource>.<ReferenceField>`, `replGroup=<ReplacementFieldGroup>`
and `relPath=<DataRelationPath>`. Menu extension elements show `(under <Parent>)`, `position=<PositionType>`
and `menuitem=<MenuItemName>`.

## Usage statistics

Every MCP call is appended to `%LOCALAPPDATA%\xpp-lens\usage\usage-YYYYMM.jsonl` (tool, arguments, answer size,
time, empty or not). The log never leaves the machine. `xpplens stats` summarizes it:

```powershell
xpplens stats --days 7 --top 10
```

It shows per tool the number of calls, average / p95 / max answer size in tokens (characters / 4), time, the share of
empty answers, plus the largest and slowest calls and the latest empty answers — the places where the tool did not
help and Claude probably fell back to reading files. Switch off with `xpplens config --usage-log false`.

## CLI

```
xpplens find|object|method|callers|callees|refs|ext|scaffold|build-errors|security|join|entity|changed|grep|label …
xpplens build [--full-only] [--std-only] [--compiled-only] [--force]
xpplens status [--counts] | stats | update [--install] | detect | config | register | unregister | mcp | version
```

`xpplens help` lists every option. Environment variables: `XPPLENS_CONFIG` (another configuration),
`XPPLENS_VERBOSE=1` (SQL timings on stderr), `XPPLENS_TIMING=1` (total time of a CLI query).

## Development

The source code lives **separately from the installation**, by default in `C:\Dev\xpp-lens`:

```
C:\Dev\xpp-lens\
  xpp-lens.sln             solution for Visual Studio 2022
  src\XppLens\*.cs         source
  src\XppLens\Properties\launchSettings.json   launch profiles (F5)
  tests\XppLens.Tests\     xUnit tests + sample AOT XML (Fixtures)
  build.ps1                 compile; -Deploy replaces the binaries in the installation
  pack.ps1                  ZIP package for installing elsewhere
$1
  release-notes.ps1         release notes of one version from CHANGELOG.md (used by the release workflow)
  .github\                  CI and release workflows, issue forms
  build\  dist\             outputs (do not keep anything of your own here)
```

### Visual Studio

Open `C:\Dev\xpp-lens\xpp-lens.sln` in **Visual Studio 2022** (17.12 or newer — VS 2019 does not support .NET 9).
Build with Ctrl+Shift+B. Next to the green arrow on the toolbar pick a profile from `launchSettings.json`
(`status`, `stats (MCP usage)`, `find (batch)`, `object (form controls)`, `method (fragment)`, `callers (incl. standard)`,
`scaffold coc`, `security`, `join`, `entity`, `changed (3 days)`, `build-errors`, `build compiled packages`,
`verbose SQL (status)`) and press F5 — the program starts under the debugger against the real configuration and index
(`XPPLENS_CONFIG` is set in the profile). Your own profile: Debug → *XppLens Debug Properties* → new profile,
enter a CLI command in "Command line arguments".

The MCP server cannot be debugged with F5 (it talks to Claude over stdin/stdout). To watch it at work,
deploy a Debug build (`.\build.ps1 -Deploy -Configuration Debug`), restart Claude and in VS use
Debug → *Attach to Process* → `xpplens.exe`.

### Tests

The tests never touch your `PackagesLocalDirectory` or your index. They copy a small sample package set from
`tests\XppLens.Tests\Fixtures\PackagesLocalDirectory` (a "Microsoft" package `StdBase` and a custom package
`ContosoCore`, neutral names only) to a temporary folder, build an index there and check what the tools answer.

**In Visual Studio:** Test → *Test Explorer* (Ctrl+E, T) → *Run All Tests* (Ctrl+R, A). The first run builds the
solution; a test can be debugged with right click → *Debug*.

**From the command line:**

```powershell
cd C:\Dev\xpp-lens
dotnet test                                              # all tests (about 10 s)
dotnet test --filter "FullyQualifiedName~QueryTests"     # only the end-to-end tool tests
dotnet test --filter "Name~Scaffold"                     # tests whose name contains "Scaffold"
dotnet test --logger "console;verbosity=detailed"        # show every test and the failure details
```

| File | What it covers |
|---|---|
| `AnalyzerTests.cs` | lexer, method headers, resolved calls, chained calls (`ret:` chains), unresolved receivers, signatures for scaffolding |
| `AnalyzerTests.cs` → `HelperTests` | method fragments, relation info, `since` parsing, build result paths, batch lists, usage log and report |
| `QueryTests.cs` | every tool end to end on the fixture index: find (underscore, dotted names, batch), object, method (fragment, batch), callers (custom, chained, standard), refs, callees, extensions, scaffold, security, join, entity, changed, build errors, labels |
$1
| `MigrationTests.cs` | taking over an xpp-graft installation (moved, custom location, locked, already there, second run) and `xpplens update` (versions, release JSON, SHA-256, pending notice) |

Adding a test: put the XML the case needs into `Fixtures` (keep names neutral — `Demo*`, `Contoso*`), then add a
`[Fact]` to `QueryTests.cs` that calls the query and asserts on the text. Run the tests before every commit.

### Releasing a new version

1. Set `<Version>` in `src\XppLens\XppLens.csproj` and add its section `## X.Y.Z — date` to `CHANGELOG.md`.
2. Commit, push to `main` and wait for the green CI.
3. Tag and push the tag:

```powershell
git tag vX.Y.Z
git push origin vX.Y.Z
```

The Release workflow then runs the tests, checks that the tag matches `<Version>`, builds `xpp-lens-X.Y.Z.zip`
with `pack.ps1` and publishes the release with its notes from `CHANGELOG.md` (preview them with
`.\release-notes.ps1 -Version X.Y.Z`). Release tags are protected: a published version cannot be changed — fixes
go into a new version.

### Where to change what

| Change | File |
|---|---|
| new MCP tool | `McpTools.cs` (declaration) + `Queries*.cs` (query) + CLI command in `Program.cs` + a test |
| other data from XML (new object type, property, member) | `XmlObjectParser.cs` |
| recognizing X++ constructs (calls, chains, attributes, intrinsics) | `CodeAnalyzer.cs`, `XppLexer.cs` — bump `Indexer.AnalyzerVersion` |
| method fragments | `Fragments.cs` |
| new table or index in the database | `Store.cs` — `EnsureExtras` for in-place changes, `SchemaVersion` only when a rebuild is unavoidable |
| refresh, watcher, model tiers | `IndexService.cs`, `Indexer.cs`, `Catalog.cs` |
| packages without XML (`.xref`, `bin\*.md`, label resources) | `BinaryPackage.cs` |
$1
| `xpplens update`, taking over xpp-graft | `Updater.cs`, `Migration.cs` |
| release automation | `.github\workflows\release.yml`, `release-notes.ps1`, `pack.ps1` |
| CLI commands, configuration, registration in Claude | `Program.cs`, `Config.cs`, `Detect.cs` |

Work loop:

```powershell
.\build.ps1                 # compile into .\build
.\build\xpplens.exe find CustTable   # test from the command line, no Claude restart needed
dotnet test                 # run the tests
.\build.ps1 -Deploy         # replace the installed binaries (stops running processes)
.\build.ps1 -Test -Deploy   # the same, but only when all tests pass
```

After `-Deploy` restart Claude Desktop and Claude Code sessions — MCP loads the binary at startup.

Changing `Store.SchemaVersion` drops the index and requires `xpplens build`.

Documentation is kept in two languages: any change to `README.md` must be mirrored in `README.pl.md` in the same commit.

## Reporting problems

Bugs and ideas: open an issue (the form asks for the version and what you ran). Security problems: **Security → Report a vulnerability**, see [SECURITY.md](SECURITY.md). Please never paste code, object names or data from your own or your customers' projects — reproduce the problem with standard objects or neutral names.

## Limitations

- Receiver types come from variable declarations and method return types (chains); there is no full type inference —
  e.g. elements of containers, `this.field.method()` through a field of another class, or results of `as`/casts in
  expressions stay "receiver type unknown".
- Microsoft code keeps calls only (no field reads, types, labels); for those, promote the package to the full tier or use
  `xpp_grep --standard` with a model filter (reads XML from disk: seconds for a small package, minutes for
  `ApplicationSuite`, refused above 60k files).
- Macros (`#name`) are not expanded. A label is recognized only when a string holds just the label id.
- Compiled packages: no source code; field group contents and object properties are not reconstructed
  (only the header of `bin\*.md` is read), and references cover only what the compiler recorded.
- `xpp_join` follows table relations only (not EDT relations); `xpp_changed` does not list deleted objects.
- Windows and x64 (self-contained package); the index is not portable between machines — it is built locally.
