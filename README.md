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
Claude Code  ──stdio(MCP)──▶  mcp/nx-auto-mcp.js  ──TCP:1977──▶  managed_plugin.dll  ──NXOpen──▶  NX
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

## 🇨🇳 国内下载方式 / Download for China Users

由于网络原因，国内直接访问 GitHub 可能较慢或失败。以下是几种解决方案：

**方式 1：使用代理克隆（推荐）**
```bash
# 使用 ghproxy.com 代理
git clone https://ghproxy.com/https://github.com/cyw2527/nx-auto-mcp.git

# 或使用 gitclone.com 代理
git clone https://gitclone.com/github.com/cyw2527/nx-auto-mcp.git
```

**方式 2：下载 Release 包**
访问 [Releases 页面](https://github.com/cyw2527/nx-auto-mcp/releases) 下载最新的 zip 包。

**方式 3：手动下载 ZIP**
1. 访问 https://github.com/cyw2527/nx-auto-mcp
2. 点击绿色的 "Code" 按钮
3. 选择 "Download ZIP"

---

## Layout

```
nx-auto-mcp/
├── setup.bat              ← generates custom_dirs.dat + sets NX environment variables
├── LICENSE                ← MIT
├── mcp/                   ← MCP adapter layer (pure Node, no npm install needed)
│   ├── nx-auto-mcp.js           MCP stdio entry point
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
    "nx-auto-mcp": {
      "type": "stdio",
      "command": "node",
      "args": ["C:/path/to/nx-auto-mcp/mcp/nx-auto-mcp.js"]
    }
  }
}
```

> **Confirm the client actually read that file**: type `/mcp` in a session (it lists `nx-auto-mcp`
> and its connection state), or run `claude mcp list`; `claude mcp get nx-auto-mcp` also tells you
> which **scope** defines the server, which is how you check that the file you edited is the one
> being read. `.mcp.json` is read **once, at session start** — restart the session after editing.
>
> ⚠️ The example file carries a non-standard top-level `_comment` key on its second line. If your
> client rejects it, the symptom is **silently no servers at all** (no error) — delete that line
> and retry.

> ⚠️ **Order matters: NX first, MCP client second.** The adapter fetches the tool list **once, at
> process start** (`let pendingTools = loadTools();` in `mcp/nx-auto-mcp.js` is module-level) and
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

---

## License

[MIT](LICENSE) © 2026 cyw2527

---

## Troubleshooting

For detailed troubleshooting, see [README full version](docs/TROUBLESHOOTING.md) or run:
```bash
node scripts/check-plugin-status.js
```