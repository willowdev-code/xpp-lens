# xpp-graft — X++ (D365 F&O) code index as an MCP server

**English** | [Polski](README.pl.md)

Reads `PackagesLocalDirectory` **read-only** and builds its own index (SQLite) outside the AOS repository.
Claude uses it over MCP instead of reading huge AOT XML files.

© 2026 WillowDev. Released under the [MIT License](LICENSE).

## Installing on a new machine

The install package is not part of the repository (`dist\` is excluded by `.gitignore`). There are two ways:

**A. From a release package** — nothing needs to be installed on the target machine, not even .NET.

1. Download `xpp-graft-YYYYMMDD.zip` from the **Releases** tab of this repository.
2. Unzip it and run in a regular PowerShell window (not as administrator):

```powershell
powershell -ExecutionPolicy Bypass -File .\xpp-graft\install.ps1 -Languages en-US,pl
```

**B. From source** — requires the .NET 9 SDK.

```powershell
git clone https://github.com/willowdev-code/xpp-graft.git C:\Dev\xpp-graft
cd C:\Dev\xpp-graft
.\pack.ps1                     # creates dist\xpp-graft\ and dist\xpp-graft-YYYYMMDD.zip
powershell -ExecutionPolicy Bypass -File .\dist\xpp-graft\install.ps1 -Languages en-US,pl
```

The installer copies the files to `C:\Tools\xpp-graft`, detects `PackagesLocalDirectory` (from the AOS `web.config`
or the folder layout), writes the configuration, registers the server in Claude Desktop and Claude Code, and finally
builds the index (custom models take about a minute, the Microsoft standard 20–40 minutes, once). Close Claude before
installing and start it again afterwards.

New release: run `.\pack.ps1` and attach the resulting ZIP to a new release in the Releases tab (tag `vX.Y.Z`
matching `<Version>` in `src\XppGraft\XppGraft.csproj`).

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
xppgraft config                                   # show settings
xppgraft config --add-language de                 # add a label language
xppgraft config --add-full-model XPL              # model/package into the full index
xppgraft config --add-standard-model ContosoIsv   # model into the standard tier
xppgraft config --add-standard-publisher "Contoso"
xppgraft config --packages-dir K:\AosService\PackagesLocalDirectory
xppgraft detect [--set]                           # detect PackagesLocalDirectory
xppgraft build                                    # apply changes
```

Settings live in `xppgraft.json` next to the `bin` folder and can also be edited by hand.

## Three index tiers

| Tier | Models | Contents |
|---|---|---|
| full | all non-Microsoft models (detected by `Publisher` in the descriptor) + those in `extraFullModels` | objects, members, method signatures and source, code references (calls, types, fields, intrinsics, labels), metadata references |
| standard | Microsoft models | objects, fields/indexes/relations, method signatures with line ranges, `extends`, CoC and event handlers — no references from method bodies |
| compiled | packages deployed without XML (e.g. country localizations or ISV modules shipped in compiled form only) | object names from `bin\*.md`, methods, fields, field groups, relations and **compiler references** from `.xref`, CoC from `ChainOfCommand.xml`, inheritance from `ClassExtends.runtime`, labels from `Resources\<language>\*.resources.dll` — no source code |

A standard package can be promoted to the full tier (`--add-full-model ApplicationSuite`) if you need its call graph
and instant `xpp_grep`. The cost is a longer build and a larger database.

Compiled packages rebuild themselves when their `.xref`, `.md` or resources change. Manually:
`xppgraft build --compiled-only --force`. Folders that cannot be indexed at all are listed by `xppgraft status`
in the "on disk but NOT indexed" line.

## Index permissions

By default the index goes to `%LOCALAPPDATA%\xpp-graft\index\xpp.db`, where the user running Claude can write.
If you put the index in the installation folder and install as administrator, Claude (running without elevation)
cannot update it — responses then show `index is read-only` and results freeze at the time of the build. Fix:

```powershell
xppgraft config --index-path "$env:LOCALAPPDATA\xpp-graft\index\xpp.db"
xppgraft build
```

In read-only mode `xpp_method` and `xpp_object` still return current code (they parse the file live),
but `xpp_find`, `xpp_callers` and `xpp_refs` use the frozen index.

## Index freshness

- `FileSystemWatcher` on full-tier model folders — a save in Visual Studio is visible on the next query.
- File date scan at startup and every `rescanIntervalSeconds` (default 5 min) — catches Get Latest from Team Explorer.
- Standard: package fingerprint (descriptors + `bin\*.dll`) — rebuilt only after a platform update.
- Writes are serialized with a named mutex, so Claude Desktop and Claude Code can run side by side.

## MCP tools

| Tool | Purpose | Standard tier |
|---|---|---|
| `xpp_find` | objects, methods, fields by name (`*`, `?`, `Object.member`; a dotted name is also tried as a full object name, e.g. `*Staging.Contoso` with `type=tableext`) | yes |
| `xpp_object` | object skeleton: properties with labels, fields, indexes, relations, data sources, control and menu tree, methods with line ranges, extensions | yes |
| `xpp_method` | method source + file path and line range + CoC wrappers and handlers | yes |
| `xpp_extensions` | CoC classes, table/form extensions, event handlers, derived classes | yes |
| `xpp_callers` / `xpp_callees` | who calls a method / what a method uses | no (CoC and handlers only) |
| `xpp_refs` | every use of a class, table, field, EDT, enum, menu item, label | no |
| `xpp_grep` | regex over method bodies | `standard=true` + model filter (reads files from disk) |
| `xpp_label` | resolve `@SYS…`/`@Model:Key` or search by text | yes |
| `xpp_status` | index status | — |

### Control and menu trees

Large trees (more than 60 elements) are collapsed to two levels with a child counter `[+N]`. To expand:

| Parameter | Effect | Example |
|---|---|---|
| `filter` | wildcard on name or path; prints **full paths** | `xppgraft object CustTable --type form --filter *PersonalTitle*` |
| `parent` | only the subtree of one element (name or path) | `--parent TabGeneral` |
| `depth` | number of levels (below `parent`, if given) | `--parent UpperGroup --depth 1` |

ReferenceGroup controls show `ref=<datasource>.<ReferenceField>`, `replGroup=<ReplacementFieldGroup>`
and `relPath=<DataRelationPath>`. Menu extension elements show `(under <Parent>)`, `position=<PositionType>`
and `menuitem=<MenuItemName>`.

## CLI

```
xppgraft find|object|method|callers|callees|refs|ext|grep|label …
xppgraft build [--full-only] [--std-only] [--compiled-only] [--force]
xppgraft status | detect | config | register | unregister | mcp | version
```

Environment variables: `XPPGRAFT_CONFIG` (another configuration), `XPPGRAFT_VERBOSE=1` (SQL timings on stderr).

## Development

The source code lives **separately from the installation**, by default in `C:\Dev\xpp-graft`:

```
C:\Dev\xpp-graft\
  xpp-graft.sln             solution for Visual Studio 2022
  src\XppGraft\*.cs         source (14 files)
  src\XppGraft\Properties\launchSettings.json   launch profiles (F5)
  build.ps1                 compile; -Deploy replaces the binaries in the installation
  pack.ps1                  ZIP package for installing elsewhere
  install.ps1 uninstall.ps1 README.md README.pl.md
  build\  dist\             outputs (do not keep anything of your own here)
```

### Visual Studio

Open `C:\Dev\xpp-graft\xpp-graft.sln` in **Visual Studio 2022** (17.12 or newer — VS 2019 does not support .NET 9).
Next to the green arrow on the toolbar pick a profile from `launchSettings.json` (`status`, `find`,
`object (form controls)`, `method`, `build compiled packages`, `verbose SQL (status)`) and press F5 —
the program starts under the debugger against the real configuration and index (`XPPGRAFT_CONFIG` is set in the profile).
Your own profile: Debug → *XppGraft Debug Properties* → new profile, enter a CLI command in "Command line arguments".

The MCP server cannot be debugged with F5 (it talks to Claude over stdin/stdout). To watch it at work,
deploy a Debug build (`.\build.ps1 -Deploy -Configuration Debug`), restart Claude and in VS use
Debug → *Attach to Process* → `xppgraft.exe`.

Where to change what:

| Change | File |
|---|---|
| new MCP tool | `McpTools.cs` (declaration) + `Queries.cs` (query) |
| other data from XML (new object type, property, member) | `XmlObjectParser.cs` |
| recognizing X++ constructs (calls, attributes, intrinsics) | `CodeAnalyzer.cs`, `XppLexer.cs` |
| new table or index in the database | `Store.cs` — bump `SchemaVersion`, which forces a rebuild |
| refresh, watcher, model tiers | `IndexService.cs`, `Indexer.cs`, `Catalog.cs` |
| packages without XML (`.xref`, `bin\*.md`, label resources) | `BinaryPackage.cs` |
| CLI commands, configuration, registration in Claude | `Program.cs`, `Config.cs`, `Detect.cs` |

Work loop:

```powershell
.\build.ps1                 # compile into .\build
.\build\xppgraft.exe find CustTable   # test from the command line, no Claude restart needed
.\build.ps1 -Deploy         # replace the installed binaries (stops running processes)
```

After `-Deploy` restart Claude Desktop and Claude Code sessions — MCP loads the binary at startup.

Changing `Store.SchemaVersion` drops the index and requires `xppgraft build`.

Documentation is kept in two languages: any change to `README.md` must be mirrored in `README.pl.md` in the same commit.

## Limitations

- Call receiver types are resolved from variable declarations, without full type analysis; chains like `a.b().c()`
  end up in the "receiver type unknown" section of `xpp_callers`.
- `xpp_grep --standard` reads XML from disk: a small package takes seconds, `ApplicationSuite` minutes (or is refused
  above 60k files). Workaround: a `type`/`object` filter or promoting the package to the full tier.
- Macros (`#name`) are not expanded.
- Compiled packages: no source code; field group contents and object properties are not reconstructed
  (only the header of `bin\*.md` is read), and references cover only what the compiler recorded.
- Windows and x64 (self-contained package); the index is not portable between machines — it is built locally.
