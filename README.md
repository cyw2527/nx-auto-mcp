# nx-auto-mcp — MCP Server for Siemens NX

[中文](README.zh.md) | **English**

> **nx-auto-mcp** is a Siemens NX MCP Server that lets AI assistants drive Siemens NX directly via the [Model Context Protocol](https://modelcontextprotocol.io). It exposes 130+ CAD tools (modeling, sketching, assembly, measurement, inspection, sheet metal, and more) as MCP tools, callable from Claude Code, Cursor, or any MCP client.
>
> - **GitHub**: https://github.com/cyw2527/nx-auto-mcp
> - **Keywords**: Siemens NX MCP, NX MCP Server, NXOpen, CAD automation, AI agent, Model Context Protocol
> - **Architecture**: MCP stdio adapter (Node.js) → TCP 1977 → NX plugin (C# / NXOpen)
> - **Latency**: 10–50 ms per tool call (resident process, no journal startup overhead)

Turn Siemens NX into something an AI agent can drive directly. This project exposes NX modeling, sketching, assembly and measurement capabilities as MCP (Model Context Protocol) tools, callable from Claude Code or any MCP client.

```
Claude Code  ──stdio(MCP)──▶  mcp/nx-exec-mcp.js  ──TCP:1977──▶  managed_plugin.dll  ──NXOpen──▶  NX
              ◀────────────                        ◀───────────                     ◀────────
```

> ### ⚠️ Not usable straight after download — you must build it once
>
> **This repository ships source only; it contains no prebuilt DLL.** NX requires managed
> plug-ins to be signed by **your own** `SignDotNet.exe`, so a binary cannot be reused
> across machines.
>
> Building is **one command** and takes about a minute — see [Install](#install).
> Running `setup.bat` without building fails fast with a clear error, rather than leaving
> you with a plug-in that looks installed but does nothing.

**Why a plug-in instead of journal scripts?** Plug-in calls run in a resident process with a typical round trip of 10–50 ms (depending on tool complexity and NX response time), while each journal run spawns a separate NX process costing at least 1.5 s of fixed overhead. Use tools to modify existing parts or query state; for large from-scratch builds, journals are still the better fit (this package provides an `nx_run_journal` channel, but it **accepts `.cs` only** — see [Known limitations](#known-limitations)).

---

## Layout

```
nx-auto-mcp/
├── setup.bat              ← generates custom_dirs.dat + sets NX environment variables
├── LICENSE                ← MIT
├── mcp/                   ← MCP adapter layer (pure Node, no npm install needed)
│   ├── nx-exec-mcp.js           MCP stdio entry point
│   ├── mcp-compatible-stdio.js  transport (dual framing + stdout sentinel)
│   └── tool-schemas.json        tool parameter contract snapshot
├── plugin/                ← NX plug-in layer (C#, build it yourself)
│   ├── *.cs                     sources (root level)
│   ├── tools/                   tool implementations (by domain + Measure/ Display/ etc.)
│   ├── rules/                   engineering rule engine + rule tables (rules_config.json)
│   ├── protocol/                TCP JSON-Line codec
│   ├── build.bat / deploy.bat / launch.bat
│   ├── nx-env.bat               shared NX path detection
│   └── startup/                 ← where NX actually loads from
│       ├── nx_mcp_plugin.men           main menu bar
│       ├── nx_mcp_plugin_view_popup.men  view context menu
│       ├── nx_mcp_plugin_hotkey.btn    hotkey button definitions
│       (managed_plugin.dll is produced by build.bat; not in this repo)
├── scripts/
│   ├── check-plugin-status.js   prerequisite-chain health check
│   ├── nx-tcp-call.js           talk TCP directly, bypassing MCP (for debugging)
│   ├── extract-param-descriptions.js  extract param descriptions from C# source (dev maintenance)
│   └── translate-schemas.js     translate Chinese in tool-schemas.json (dev maintenance)
└── docs/                  ← architecture / build / migration pitfalls
```

---

## Requirements

| Item | Requirement |
|---|---|
| NX | Installed (developed against NX 2412; other versions need API compatibility testing) |
| Node.js | >= 18 (adapter uses built-in modules only — no `npm install`) |
| Compiler | **Required** — .NET Framework 4.8 `csc.exe` (ships with Windows) |
| NX SDK | `<NX_ROOT>\NXBIN\managed\NXOpen.dll` etc. (comes with your NX install) |

> **NX not in the default location?** Set `NX_ROOT` before building. `build.bat` reads it
> first, otherwise it scans `%ProgramFiles%\Siemens\NX*`. If the detected version is not
> NX 2412 you get a warning, since the APIs may differ.

---

## Install

### 1. Build the plug-in

```bat
cd plugin
.\build.bat run      :: compile + sign + deploy to startup\ (in Git Bash use ./build.bat run)
```

> ⚠️ **All shells need a path prefix.** On Windows, `cmd.exe`, `PowerShell`, and `Git Bash` do
> not search the current directory for executables, so a bare `build.bat` always fails:
>
> | Shell | Correct | Bare `build.bat` gives |
> |---|---|---|
> | cmd.exe | `.\build.bat run` | `'build.bat' is not recognized as an internal or external command, operable program or batch file.` |
> | PowerShell | `.\build.bat run` | "The term 'build.bat' is not recognized as the name of a cmdlet, function, script file, or operable program." |
> | Git Bash | `./build.bat run` | `bash: build.bat: command not found` |
>
> ⚠️ **`.\` is a cmd.exe/PowerShell form.** In Git Bash, `.\build.bat` is wrong too — bash eats
> the `\b` as an escape and actually runs `.build.bat`, giving
> `bash: .build.bat: command not found`. The prefix depends on the shell; the same applies
> to every `.bat` command in this document, including `setup.bat`.

`build.bat` auto-detects the NX install path. If NX lives somewhere non-standard, override it:

```bat
set NX_ROOT=D:\Siemens\NX2412
```

> ⚠️ `set` **only affects the current shell window** — and step 2's `setup.bat` reads `NX_ROOT`
> too, so it is gone the moment you open a new one. Either do both steps in the same window,
> or write it permanently with `setx` (which takes effect in newly opened windows):
>
> ```bat
> setx NX_ROOT "D:\Siemens\NX2412"
> ```

> ⚠️ **The DLL must be signed.** NX silently refuses to load unsigned plug-ins — no error
> message. `deploy.bat` invokes `<NX_ROOT>\NXBIN\SignDotNet.exe` to sign it.
>
> The `NXSigningResource.res` needed for signing is **not distributed by this repository** —
> `build.bat` reads it at compile time from `%NX_ROOT%\UGOPEN\NXSigningResource.res`
> (that file ships with your NX installation).

### 2. Environment variables + custom_dirs.dat

```bat
cd ..
.\setup.bat          :: in Git Bash use ./setup.bat
```

Generates `custom_dirs.dat` (one line, pointing at this package's `plugin\`) and sets the user-level environment variables `UGII_CUSTOM_DIRECTORY_FILE` and `UGII_USER_DIR`.

> ⚠️ `setup.bat` checks for `plugin\startup\managed_plugin.dll`. That is why step 1 must be
> `build.bat run` — **a bare `build.bat` (which only compiles into `plugin\bin\`) makes `setup.bat`
> exit with "the plugin DLL has not been built yet"**. That is a difference in what each script
> checks, not a build failure.

> ⚠️ **`setup.bat` needs `NX_ROOT` too, and it fails very early.** The first thing the script
> does is call `plugin\nx-env.bat` to detect your NX install; if NX lives off the default path
> and `NX_ROOT` is unset, `nx-env.bat` **exits with `/b 1`** — and at that point `custom_dirs.dat`
> and both environment variables **have not been written at all**. You end up stuck on "TCP 1977
> not listening + no menu" while nothing is actually wrong with NX. If step 1 only did a
> session-scoped `set NX_ROOT=...`, running step 2 in a different window lands you right here.

> ⚠️ **These two variables are the only way NX finds the plug-in.** Without them: no menu,
> no TCP 1977 listener, and no error from NX.
>
> ⚠️ `setup.bat` uses `setx` to write **permanent user-level environment variables** and
> will **overwrite** existing values with those names.

### 3. Restart NX

Environment variables are read only at NX startup. **Fully exit every NX window**, then reopen.

### 4. Register the MCP server

`.mcp.json.example` in the repository root is the template. **Claude Code reads `.mcp.json` from the project root** (not from `.claude/`) — copy it there and **replace the absolute path with your own**:

```json
{
  "mcpServers": {
    "nx-exec": {
      "type": "stdio",
      "command": "node",
      "args": ["C:/path/to/nx-auto-mcp/mcp/nx-exec-mcp.js"]
    }
  }
}
```

> **Confirm the client actually read that file**: type `/mcp` in a session (it lists `nx-exec`
> and its connection state), or run `claude mcp list`; `claude mcp get nx-exec` also tells you
> which **scope** defines the server, which is how you check that the file you edited is the one
> being read. `.mcp.json` is read **once, at session start** — restart the session after editing.
>
> ⚠️ The example file carries a non-standard top-level `_comment` key on its second line. If your
> client rejects it, the symptom is **silently no servers at all** (no error) — delete that line
> and retry.

> ⚠️ **Order matters: NX first, MCP client second.** The adapter fetches the tool list **once, at
> process start** (`let pendingTools = loadTools();` in `mcp/nx-exec-mcp.js` is module-level) and
> never refreshes it. If the MCP client starts before NX is up (or before the plug-in has loaded),
> it caches an **empty** list — you see no `nx_*` tools at all, and it never recovers on its own.
>
> **Recovery**: confirm NX is running and TCP 1977 is listening
> (`node scripts/check-plugin-status.js`), then **restart the MCP client** so it fetches again.

---

## Verify

```bash
node scripts/check-plugin-status.js
```

The checks run in this order: **① NX process → ② TCP 1977 → ③ plug-in version + tool count → ④ startup log analysis → ⑤ GetUI diagnostics → ⑥ last 5 run-log lines**.
"Menu registration" is not a step of its own — its verdict comes out of **④** (read from the log's `全部菜单回调注册成功` / `注册失败:` lines).

**Exit code**: any ❌ gives `1`; no failures at all gives `0`. That makes it usable as a gate (CI / batch scripts).
⚠️ Warnings and ⏭ skips **are not failures** and do not affect the exit code.

**When TCP is offline, step ③ does not run at all**, and the script prints an explicit skip line instead of quietly leaving it out:

```
⏭  插件版本 / 工具数 / 菜单注册 —— TCP 未在线, 这三项查不了 (不是"通过")
```

That line means those three checks have **no verdict** — do not read it as a pass.

Expected: `port 1977 listening` + `tool count > 0`.

The tool count is **never hardcoded** — the live `list_tools` result is authoritative:

```bash
# Real plug-in tool list (bypasses the MCP client's connection snapshot)
node scripts/nx-tcp-call.js --list
```

---

## Troubleshooting

**First work out which syslog to read — everything below depends on it.** Every NX launch writes a fresh
`%TEMP%\<username><hex>.syslog`; over time these pile up into the hundreds, and the hex in the name is
**not a timestamp**, so you cannot pick by name. The rule is **take the newest by modification time** —
the one from the restart you just did:

```bash
ls -t "$TEMP"/<username>*.syslog | head -1                 # Git Bash
```

```powershell
Get-ChildItem "$env:TEMP\<username>*.syslog" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

Then check two places: **line 2**, `*** system log created by <username> on <date>`, is when that session
**started**; the **last line**, `@@@ End of session <date>`, is when it **ended** (written even if NX
crashed). If they don't line up, you have the wrong file.

| Symptom | Root cause | Fix |
|---|---|---|
| TCP 1977 not listening, and the newest syslog has **not even a `UGII_CUSTOM_DIRECTORY_FILE` line** | The environment variables were never set or never took effect — the plug-in has no entry point to be found through | Re-run `setup.bat` (which also needs `NX_ROOT` — see step 2 of [Install](#install)), then fully restart NX |
| No "NX MCP Plugin" menu | `custom_dirs.dat` missing or moved | Re-run `setup.bat` |
| TCP 1977 not listening, the syslog's `UGII_CUSTOM_DIRECTORY_FILE` points at the right place, but there is **no `Loaded assembly: managed_plugin` line at all** | **Not just "it isn't signed."** Work through this order: ① the DLL was never built (`plugin\startup\managed_plugin.dll` does not exist) ② the package was moved and `custom_dirs.dat` still names the old path (check the **value** on the `UGII_CUSTOM_DIRECTORY_FILE` line) ③ a `.men` file is missing from `plugin\startup\` ④ **the syslog itself is truncated** — that NX run crashed, or it was a batch session that never loads the interactive plug-in at all ⑤ only if none of those fit, an unsigned DLL (**no plug-in means there is no tool count to display** — do not go looking for a "tool count 0" state) | Work down ①→⑤ (discriminator table below); **only once you have confirmed it really is a signing problem** re-run `plugin\deploy.bat` (build + sign + deploy), then fully restart NX |
| It worked yesterday, NX was never restarted, and TCP 1977 has suddenly gone quiet | Someone clicked the NX menu **NX MCP Plugin ▸ Toggle MCP Server** — it is a **toggle**, and it stops the server. The evidence you get **at the time** is the "Server stopped." message box (a `[CTRL] Server stopped` line is written to `tcp_server.log` as well, but the log rotates at 1 MB and discards the old content, so **by the time you go looking it is usually long gone — finding nothing does not rule this cause out**) | Click the same menu item again to bring the server back (or restart NX). Nothing is broken |
| Plug-in loaded twice (dual instance) | `managed_plugin.dll` exists in several places | Keep only `plugin/startup/`; delete other copies |
| Source changed but behaviour didn't | **First check whether the deploy step errored.** When NX holds the DLL, `deploy.bat`'s copy **fails outright with `exit 1`** — it is not silent. The genuinely silent causes are the other two: an **old-directory** DLL being loaded, or the MCP client's **connection snapshot** | Deploy errored → fully exit NX, then re-run `build.bat run`. No error → check the path on the syslog's `Loaded assembly: managed_plugin` line is the `plugin\startup\` you just deployed to, and delete stale copies if not. Path correct too → reconnect per "MCP client doesn't see new tools" below |
| A tool you added shows up in `--list` but MCP answers `Tool not found` | Its name does **not start with `nx_`** — the adapter requires the prefix, while `--list` talks to the plug-in directly and does not apply that rule (see [Tool coverage](#tool-coverage)) | Rename the tool with an `nx_` prefix. This is not a connection-snapshot problem — reconnecting will never fix it |
| `build.bat` reports "signing resource not found" | No such file under `%NX_ROOT%\UGOPEN\` | Confirm `NX_ROOT` points at the NX install root |
| Compile errors: missing NXOpen types | Not NX 2412 | `set NX_ROOT=<your NX 2412 path>` |
| MCP client doesn't see new tools | Client freezes its tool list **at connect time** | Reconnect MCP; use `nx-tcp-call.js --list` for the real list |

**Telling a truncated log from a real refusal** (the key to row 3 above — don't skip it):

| What you find in that syslog | Conclusion |
|---|---|
| `Assembly <path> is signed with a NXOpen signature` or `DotNet signature is valid for <path>` | **Positive evidence**: the signature is good and the DLL really was loaded. Far more reliable than "I did not see a particular line" |
| Neither line, but you do see `>>>> O/S ERROR: signal <n> caught` | **NX crashed** on that run; the log broke off before it ever got to loading the plug-in. That is a crash, not signing |
| Neither line, and the file is short and reaches `@@@ End of session` quickly | That was a **batch session** (e.g. the separate NX process `nx_run_journal` spawns), which never loads the interactive plug-in. **Read a different syslog** |
| Neither line, the DLL is there and the path is right | *Now* it is an unsigned DLL |

> ⚠️ **"No `Loaded assembly: managed_plugin` line" does not imply "unsigned."** A sample from one machine
> after a few months of use: of the 113 syslogs it had accumulated, 6 lacked that line — 5 were NX crashes
> (`signal 11`) and 1 was a batch session, and **not one** was a signature rejection; all 6 also had a
> `UGII_CUSTOM_DIRECTORY_FILE` value pointing at a **different directory** (that is cause ② above,
> "the package was moved"). Re-signing and rebuilding without running that check is a wasted trip.

**Where `tcp_server.log` lives** (row 3 needs it):

```
%LOCALAPPDATA%\Siemens\<NX version>\startup\tcp_server.log
```

`<NX version>` is **not necessarily `NX2412`** — it is resolved in order from `UGII_BASE_DIR` → `NX_ROOT` →
the highest-numbered `%LOCALAPPDATA%\Siemens\NX<digits>` directory found (the `NxPaths` class in
`plugin/PluginInfo.cs`). Past 1 MB the log rotates to `tcp_server.log.1` in the same directory, and
**anything older is discarded** (`plugin/TcpServer.cs`) — so "it isn't in the log" often just means
"it has rotated away." `check-plugin-status.js` reads both and names the one it used in its step-4 heading.

For deeper troubleshooting see `docs/nx-plugin-relocation-checklist.md` — a full record of plug-in relocation/deployment pitfalls.
**Which of the two tables comes first**: this one covers "did NX find the plug-in, and did it start" — **read it first**; the checklist's table covers deployment and relocation — **read it second**. For the shared symptom "TCP 1977 is dead," the discriminator is **the NX process itself**: if step 1 of `check-plugin-status.js` reports `NX 未运行` (NX not running), or there are **several** `ugraf.exe` instances, you are in the checklist's `NX 崩溃无法连接 1977` row (port held by a stale process / NX not fully loaded) — go there. If NX is running happily and 1977 is simply silent, it is one of the causes in this table.

---

## Calling tools without MCP

```bash
node scripts/nx-tcp-call.js --list                       # full tool list
node scripts/nx-tcp-call.js nx_list_open_parts '{}'      # call a single tool (succeeds with nothing open in NX)
```

> ⚠️ **Geometry tools have a precondition: NX must have a part open, and it must be the work
> part.** Copying the `nx_measure_volume` line on a fresh install gets you
> `No work part is open.` (or `No bodies found in the work part.` when the part is empty).
> That is a missing precondition, **not a broken install**. Open a part first, then measure:
>
> ```bash
> node scripts/nx-tcp-call.js nx_create_part '{"path":"C:/temp/demo.prt"}'   # or nx_open_part
> node scripts/nx-tcp-call.js nx_list_open_parts '{}'      # confirm is_work is true for that part
> node scripts/nx-tcp-call.js nx_measure_volume '{}'
> ```
>
> (`nx_open_part` only makes a part the *display* part — it does **not** make it the work part.
> The `is_work` field returned by `nx_list_open_parts` is how you confirm that.)

This is the most useful channel when debugging the MCP layer — it talks straight to TCP 1977, bypassing the MCP client, the adapter and the connection snapshot.

---

## Tool coverage

The tool registry lives in `plugin/tools/ToolRegistry.cs`. It covers 15 domains and 115 tools:

| Domain | Count | Source file |
|---|---|---|
| Modeling / solids | 24 | `tools/ModelingTools.cs` |
| Sketching | 18 | `tools/SketchTools.cs`, `tools/Sketch/SketchReadbackTools.cs` |
| File operations | 8 | `tools/FileOpsTools.cs` |
| Assembly | 14 | `tools/AssemblyTools.cs`, `tools/Assembly/CreateComponentTool.cs`, `tools/Assembly/FindSameBodiesTool.cs`, `tools/ComponentTree.cs` |
| Inspection / topology | 7 | `tools/InspectTools.cs` |
| Utilities | 12 | `tools/UtilityTools.cs` |
| Measurement | 7 | `tools/Measure/MeasureTools.cs` |
| Synchronous modeling | 6 | `tools/SynchronousTools.cs` |
| Surfaces | 5 | `tools/SurfaceTools.cs` |
| Display | 4 | `tools/Display/DisplayTools.cs` |
| Datum | 4 | `tools/DatumTools.cs` |
| Correction | 4 | `tools/CorrectTools.cs` |
| Sheet metal | 3 | `tools/SheetMetalTools.cs` |
| Curves | 3 | `tools/CurveTools.cs` |
| WAVE / associative | 2 | `tools/WaveTools.cs` |
| Validation | 2 | `tools/ValidateTools.cs` |

Shared files: `tools/IToolHandler.cs` (interface), `tools/ToolResult.cs` (response format), `tools/ToolHelpers.cs` (parameter extraction), `tools/ToolRegistry.cs` (registry), `tools/WorkPartGuard.cs` (work part guard).

The parameter contract lives in `mcp/tool-schemas.json`. It **only supplies parameter metadata to the MCP side — it is not a gate**: a tool that is in the plug-in's `list_tools` but missing from this JSON **is still exposed** to clients, just with a permissive `{type:"object", additionalProperties:true}` `inputSchema` (the plug-in's contract still governs the parameters). So adding a tool **only requires changing the plug-in**; this JSON is optional enrichment, not a second place you must keep in sync.

> ⚠️ **Tool names must start with `nx_`.** That is a hard requirement of the MCP adapter (`mcp/nx-exec-mcp.js` answers `Tool not found` for any name that does not). `nx-tcp-call.js --list`, on the other hand, talks to the **plug-in directly** and has no such rule — it will happily list an unprefixed tool, so **seeing it in the list does not mean MCP can call it**. With a missing prefix, `--list`'s "it's there" is a false positive; do not follow it into the connection-snapshot explanation.

### Rule hints (`data.rule_check`)

A tool result may carry `data.rule_check`, produced by the engineering rule engine in `plugin/rules/`. The set holds **15 rules**, with every criterion hardcoded in C# (`plugin/rules/RuleEngine.cs`); the metadata (id / name / description / applicable intents) lives in `plugin/rules/rules_config.json` and is read at runtime from `rules\rules_config.json` next to the plug-in DLL, falling back to an identical ruleset built into the C# code when that file is absent.

> ⚠️ The JSON's **`severity` is a dead field**: it is loaded into `RuleDefinition.Severity` and then **never read by any code** — the severity that actually decides anything is the one each check function hardcodes in C#. Editing `severity` in the JSON has no effect and reports no error.

**Two properties decide how much to trust it:**

1. **They read only the parameters of the current call — they never measure part geometry** — and they do not read the `params` thresholds in the JSON (the thresholds are hardcoded in C#, so editing the JSON changes no criterion).
2. **A parameter that cannot be read means the rule is skipped — never a violation built on a default.** If the parameter was not passed (or was not numeric), that rule reports "skipped" instead of inventing a violation. This is deliberate: the rules were originally written against the old `create_*` intent parameter names, and the tools' **actual parameter names do not fully match them**, so substituting defaults would raise false alarms on correct calls.

Six of the fifteen:

| Rule | Criterion (matches the `RuleEngine.cs` implementation) |
|---|---|
| Hole edge clearance | `edge_clearance` ≥ 2 × `diameter`; skipped if either is absent |
| Hole wall thickness | `wall_thickness` ≥ 1.5 mm (the real wall thickness is not measured; no tool supplies this parameter yet, so in practice it always skips) |
| Hole spacing | `center_distance` ≥ 2 × the larger of the two diameters — **not** "centre distance − (r₁+r₂) ≥ 2 mm" |
| Depth-to-diameter | `depth` (alias `depth_value`) / `diameter` ≤ 10; skipped if either is absent |
| Fillet vs wall | **checks only that radius > 0** (a radius ≤ 0 is a violation); it never reads wall thickness — no tool supplies `wall_thickness`, so a positive radius always yields the skip message `Missing wall_thickness, skipping fillet wall thickness check` |
| Thread pilot hole | built-in GB/T 196-2003 metric table (M2–M36): pilot diameter within 0.15 mm and pitch within 0.05 mm of the standard values. **In practice it always skips** — no tool supplies `thread_diameter`; and the non-standard-size hint never shows up in the response either |

The other nine (hole diameter range, chamfer wall thickness, chamfer overlap, fillet chain, target body present, extrude distance, shell thickness, draft angle, pattern bounds) are the same kind of parameter range/sign checks.

> ⚠️ **Rules hint only — they never block.** A hit does not change `success` and never refuses execution (error-severity hits are downgraded to hints too). Acting on a hint is the caller's decision: the rules look at parameters, not at the geometry of your part.

The `rule_check` for `nx_extrude` with `distance = -5`. `suggestions` appears only when non-empty — this call hits an error-severity rule, so it carries one:

```json
{
  "success": true,
  "data": {
    "rule_check": {
      "count": 1,
      "issues": ["Extrude distance must be positive, current value: -5mm"],
      "suggestions": ["Please provide a positive extrude distance"],
      "has_error_severity": true
    }
  }
}
```

### Drawing (not yet available)

**Drawing** capability is still under development and is not part of this release. Planned: view creation (base view / projection / section / detail), dimensioning, GD&T, weld symbols, surface finish, BOM and balloons, tabular notes, hatching, PMI and drawing inheritance, title blocks.

Until then, use **`nx_run_journal`** to run arbitrary NXOpen C# scripts for drawing work.

---

## Known limitations

- **Coupled to NXOpen version**: developed on NX 2412, with some reflection fallbacks (`NXOpen.UI` / `MenuBar` / `MessageBox` differ in NX2412). Other versions need real testing.
- **Windows only**: NX is.
- **Plug-in runs on the NX main thread**: long operations block the UI.
- `nx_close_part` **does not save and discards unsaved changes** — save first.
- **`nx_close` shuts down all of NX, not just the current part**: with no arguments it does `Session.Parts.CloseAll()` + `Session.Exit()` (**discarding every unsaved change**), falling back to `Process.Kill()` if that fails; `force=true` kills the process outright. Its tool description only says "Close Siemens NX gracefully or forcefully.", which gives no hint of that.
- **`nx_run_journal` cannot run Python journals**: the `.py` branch only attempts `Session.ExecuteJournal()`, an API NX2412 removed, so it always fails and tells you to convert to `.cs`. Only `.cs` works — to run a Python journal you have to use NX's menu, Tools ▸ Journal ▸ Play.
- **3 tools not implemented due to NX2412 API removal** (calls return failure; tool descriptions carry a `[未实现]` / `[Not implemented]` tag):
  | Tool | Purpose | Workaround |
  |---|---|---|
  | `nx_correct_face_geometry` | Replace problem faces (B_SURFACE) with clean geometry | Use NX GUI face replacement manually |
  | `nx_record_start` | Start recording an NX journal | NX menu Tools ▸ Journal ▸ Record |
  | `nx_record_stop` | Stop recording an NX journal | NX menu Tools ▸ Journal ▸ Stop |

  The underlying APIs (`CreateReplaceFaceBuilder`, `BeginJournalRecording`, `EndJournalRecording`) no longer exist in the NX2412 managed API. If a future NX version restores them, implementations can be added.

---

## License and legal notice

### License

[MIT](LICENSE) © 2026 cyw2527

You may freely use, modify and distribute this software, including commercially, provided the copyright notice is retained.

### Not affiliated with Siemens

> **This is an unofficial, third-party open-source project. It is not affiliated with
> Siemens in any way and is not sponsored, authorized or endorsed by Siemens.**
>
> "Siemens", "NX", "NXOpen" and "Parasolid" are trademarks or registered trademarks of
> Siemens AG or its affiliates. This project uses those names solely to indicate
> **compatibility with the corresponding software products** (nominative fair use); it
> implies no partnership or endorsement.

### What this repository distributes

| Content | Status |
|---|---|
| C# / JavaScript source | Original work, MIT licensed |

**This repository contains and distributes no proprietary Siemens files:**

- ❌ **No** compiled `managed_plugin.dll` (excluded via `.gitignore`) — NX requires signing with **your own** `SignDotNet.exe`; binaries cannot be reused across machines
- ❌ **No** `NXSigningResource.res` — read at compile time by `build.bat` from `%NX_ROOT%\UGOPEN\`
- ❌ **No** `NXOpen.dll` or other NX SDK assemblies — referenced at compile time from your own NX installation

### Configuration is yours to do: everything is generated on your own NX

This repository contains no Siemens files and no prebuilt index or database. Everything Siemens-related is produced **on your machine, from your own NX installation**:

| What is needed | Where it comes from | When |
|---|---|---|
| `NXOpen.dll` and other SDK assemblies | your own NX installation | referenced at compile time |
| `NXSigningResource.res` | `%NX_ROOT%\UGOPEN\` | read at compile time |
| `SignDotNet.exe` | `%NX_ROOT%\NXBIN\` | invoked at deploy time |
| `managed_plugin.dll` | **you build it** | running `build.bat` |

**So this package needs one configuration pass from you** — see [Install](#install). That is not a missing step, it is deliberate: it is the only way to avoid redistributing Siemens files while still guaranteeing version match.

This "**ship the tool, not the data**" approach keeps the repository small and stays clear of redistribution boundaries. The cost is that every user builds once on their own machine — a deliberate trade-off.

**You must hold a valid Siemens NX license.** This software merely calls APIs your local NX already exposes; it does not bypass or defeat any licensing or activation check.

### A note on internal identifier naming

The package is named `nx-auto-mcp`, but the plugin's internal menu resource name (`nx_mcp_plugin.men`), menu action names (`NX_MCP_*`) and port environment variable (`NX_MCP_PORT`) still carry the `nx_mcp` prefix. These are identifiers agreed **in pairs** between the `.men` file and the C# code — renaming them requires changing both sides in lockstep, for more risk than benefit, so they are deliberately left as-is.

### Disclaimer

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND. Running automation inside NX can modify or delete your model files — **verify on a copy first** and keep your own version control. The authors accept no liability for data loss or production incidents.
