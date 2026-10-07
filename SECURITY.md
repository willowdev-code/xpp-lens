# Security policy

## Supported versions

Only the latest release receives fixes. Please check the
[Releases](https://github.com/willowdev-code/xpp-lens/releases) page and upgrade before reporting.

## Reporting a vulnerability

Please **do not open a public issue** for security problems.

Report them privately through GitHub: open the **Security** tab of this repository and choose
**Report a vulnerability**. You will get an answer within a few days; once a fix is released the report can be
published with credit to you, if you wish.

When describing the problem, **do not include code, object names or data from your own or your customers'
Dynamics 365 projects**. A minimal reproduction with neutral names (for example `Contoso*` objects or standard
`CustTable`) is enough.

## Scope

xpp-lens runs locally: it reads `PackagesLocalDirectory` read-only, keeps its index in `%LOCALAPPDATA%\xpp-lens`
and talks to Claude over stdin/stdout (MCP). It opens no network ports and sends no data anywhere; the usage log
stays on the machine. The only network access is `xpplens update`, and only when you run it: it reads the latest
release from the GitHub API and, with `--install`, downloads its package and checks its SHA-256 digest before
running the installer. Relevant reports are, for example, ways to make it write outside its own folders, to execute
code from indexed files, or to expose the index to other users of the machine.
