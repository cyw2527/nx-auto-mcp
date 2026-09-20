# NX Plug-in Build Scripts Reference

> This file describes the build scripts **of this package** (nx-auto-mcp). The directory layout is `plugin/`.
> For install steps see [`../README.md`](../README.md).

## Script Overview

| Script | Location | Purpose |
|--------|----------|---------|
| `build.bat` | `plugin/` | Compile `bin\managed_plugin.dll` (no deploy) |
| `deploy.bat` | `plugin/` | Compile + sign + deploy to `plugin\startup\` |
| `launch.bat` | `plugin/` | One-click: deploy + launch NX |
| `nx-env.bat` | `plugin/` | Shared NX path detection (called by the above three) |
| `setup.bat` | Package root | Generate `custom_dirs.dat` + set two environment variables |

## Usage

```bat
cd plugin

.\build.bat              # compile only (output to plugin\bin\, no deploy)
.\build.bat clean        # delete build output
.\build.bat run          # compile + deploy (equivalent to deploy.bat)
.\deploy.bat             # compile + sign + deploy
.\deploy.bat deploy-only # skip compile, sign + copy only
.\launch.bat             # deploy + launch NX
.\launch.bat no-deploy   # skip deploy, launch NX only
```

> ⚠️ **The `.\` prefix is required.** PowerShell / Git Bash / cmd.exe do not search the current
> directory; writing bare `build.bat` gives `'build.bat' is not recognized as an internal or external
> command`.
>
> ⚠️ **`build.bat` (no arguments) only compiles into `plugin\bin\`, it does not deploy.**
> Meanwhile `setup.bat` checks for `plugin\startup\managed_plugin.dll`. So running only `build.bat`
> then `setup.bat` exits with "The plugin DLL has not been built yet" — **for installation use
> `.\build.bat run` (or `.\deploy.bat`)**, which place the DLL into `startup\`. This is a difference
> in what each script considers "built", not a build failure.

## NX Path Detection (`nx-env.bat`)

Priority: **`NX_ROOT` environment variable** > auto-scan `%ProgramFiles%\Siemens\NX*`

Output variables (consumed by calling scripts):

| Variable | Value |
|----------|-------|
| `NX_ROOT` | NX install root directory |
| `NX_SDK` | `%NX_ROOT%\NXBIN\managed` |
| `NX_UGRAF` | `%NX_ROOT%\NXBIN\ugraf.exe` |
| `NX_SIGNTOOL` | `%NX_ROOT%\NXBIN\SignDotNet.exe` |

> This file does **not** use `setlocal` — variables must bubble up to the caller.

## Compilation

- **Compiler**: `%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe` (ships with Windows, requires .NET Framework 4.8)
- **Source files**: `plugin/**/*.cs` — collected recursively by `for /r`, excluding paths containing `bridge.bak` / `scripts` / `node_modules` / `extract-dll` / `journal`
- **Output**: `plugin\bin\managed_plugin.dll` (excluded by `.gitignore`, not distributed with the repo)
- **Platform**: `/platform:x64`
- **SDK references**: `NXOpen.dll` / `NXOpen.UF.dll` / `NXOpen.Utilities.dll` / `NXOpenUI.dll` / `ManagedLoader.dll` / `Newtonsoft.Json.dll`, all from `%NX_SDK%` (the user's local NX install, not distributed)

## Signing (Critical)

NX **silently refuses** to load unsigned managed DLLs — no error, no warning. The result is that the
**plug-in simply does not start**: TCP 1977 does not listen (`check-plugin-status.js` reports
"port 1977 not listening"), and the NX startup syslog contains no plug-in load line.
**There is no intermediate state where "the plug-in loaded but tool count is 0"** — if the plug-in
did not start, there is no tool count to display.

`deploy.bat` invokes `%NX_ROOT%\NXBIN\SignDotNet.exe` to sign the DLL. Note that SignDotNet
**rewrites DLL bytes** — the md5 changing after signing is normal.

The `NXSigningResource.res` needed for signing is **not distributed by this repository** —
`build.bat` reads it at compile time from `%NX_ROOT%\UGOPEN\NXSigningResource.res`
(that file ships with your NX installation).

## Deployment (Single-DLL Rule)

**Assembly** deploy target is unique: `plugin\startup\managed_plugin.dll`

Rationale: `custom_dirs.dat` points to this package's `plugin\`; NX only loads from `<custom_dir>\startup`.
Other locations (`plugin\application`, `%LOCALAPPDATA%\Siemens\NX<version>\startup`) are all ignored.

> ⚠️ **Multiple copies = dual-instance / stale-code residue.** Do not create `managed_plugin.dll`
> copies in other locations.

⚠️ **But the deployment is not just this one file**: `deploy.bat` also copies
`plugin\rules\rules_config.json` to `plugin\startup\rules\` — the plug-in resolves
`rules\rules_config.json` relative to **its own assembly location**
(`plugin/rules/RuleEngine.cs`), so the config must travel with the DLL.
**For manual deployment, copy both;** omitting the JSON **does not produce an error**:
the rule engine silently falls back to built-in rules (`RegisterAllRules()`), and from that point
on the config file is inert — editing the JSON has no effect.
(Both rule sets currently have 15 rules with identical ids / severities / applicable intents;
the check thresholds are hardcoded in C# and do not read the JSON `params`, so omitting the copy
does not change check results — but this is why "editing the config has no effect" happens.)

⚠️ **NX locks the DLL at runtime, with no hot-reload** — after changing source you must
**fully exit NX** then `build.bat run`. If the DLL is locked, use `taskkill /F /IM ugraf.exe /T` as fallback.

## Launch Flow

```
launch.bat
  ├── 1. deploy.bat (compile → sign → copy to plugin\startup\: DLL + rules\rules_config.json)
  ├── 2. nx-env.bat detect NX install
  ├── 3. Launch ugraf.exe
  └── 4. NX loads managed_plugin.dll → TcpServer listens on 127.0.0.1:1977
```

## Key File Locations

| File | Location |
|------|----------|
| C# source files | `plugin/**/*.cs` |
| Menu files | `plugin/startup/*.men`, `*.btn` |
| Compile output | `plugin/bin/managed_plugin.dll` |
| Deploy target (assembly) | `plugin/startup/managed_plugin.dll` |
| Deploy target (rule config) | `plugin/startup/rules/rules_config.json` (source: `plugin/rules/rules_config.json`) |
| Signing resource | `%NX_ROOT%\UGOPEN\NXSigningResource.res` (provided by local NX install) |
| Environment file | `custom_dirs.dat` (generated by `setup.bat`) |
