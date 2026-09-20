# nx-auto-mcp — Siemens NX 的 MCP 服务器

**中文** | [English](README.md)

把 Siemens NX 变成 AI Agent 可以直接操作的对象。通过 MCP（Model Context Protocol）把 NX 的建模、草图、装配、测量等能力暴露成工具，Claude Code / 任何 MCP 客户端即可直接调用。

```
Claude Code  ──stdio(MCP)──▶  mcp/nx-exec-mcp.js  ──TCP:1977──▶  managed_plugin.dll  ──NXOpen──▶  NX
              ◀────────────                        ◀───────────                     ◀────────
```

> ### ⚠️ 下载后不能直接用，必须先编译一次
>
> **本仓库只发源码，不含预编译的 DLL。** 原因：NX 要求托管插件必须由**你本机的**
> `SignDotNet.exe` 签名，二进制无法跨机器复用。
>
> 编译是**一条命令**，约 1 分钟，见 [安装](#安装)。不编译就 `setup.bat` 会直接报错退出，
> 不会给你一个"看起来装好了但不能用"的假象。

**为什么走插件而不是 journal 脚本**：工具调用常驻进程、单次往返通常在 10–50 ms 级别（取决于工具复杂度和 NX 响应）；journal 每次起独立 NX 进程，固定成本 1.5 s 起。改现有件、查状态用工具；从零大批量建模仍建议走 journal（本包提供 `nx_run_journal` 通道，但**只写 `.cs`**，见[已知限制](#已知限制)）。

---

## 目录结构

```
nx-auto-mcp/
├── setup.bat              ← 生成 custom_dirs.dat + 设置 NX 环境变量
├── LICENSE                ← MIT
├── mcp/                   ← MCP 适配层（纯 Node，无需 npm install）
│   ├── nx-exec-mcp.js           MCP stdio 入口
│   ├── mcp-compatible-stdio.js  传输层（双 framing + stdout sentinel）
│   └── tool-schemas.json        工具参数契约快照
├── plugin/                ← NX 插件层（C#，需自行编译）
│   ├── *.cs                     源文件（根级）
│   ├── tools/                   工具实现（按域分文件 + Measure/ Display/ 等子目录）
│   ├── rules/                   工程规则引擎 + 规则表（rules_config.json）
│   ├── protocol/                TCP JSON-Line 编解码
│   ├── build.bat / deploy.bat / launch.bat
│   ├── nx-env.bat               共享 NX 路径探测
│   └── startup/                 ← NX 实际加载位置
│       ├── nx_mcp_plugin.men          主菜单栏
│       ├── nx_mcp_plugin_view_popup.men  视图右键菜单
│       ├── nx_mcp_plugin_hotkey.btn   快捷键按钮定义
│       （managed_plugin.dll 由 build.bat 生成，本仓库不含）
├── scripts/
│   ├── check-plugin-status.js   前提链体检
│   ├── nx-tcp-call.js           绕过 MCP 直连 TCP（排查用）
│   ├── extract-param-descriptions.js  从 C# 源码提取参数描述（开发维护用）
│   └── translate-schemas.js     翻译 tool-schemas.json 中的中文（开发维护用）
└── docs/                  ← 架构 / 构建 / 迁移陷阱
```

---

## 前提

| 项 | 要求 |
|---|---|
| NX | 已安装（开发环境为 NX 2412；其他版本需自行验证 API 兼容性） |
| Node.js | >= 18（适配层只用内置模块，无需 `npm install`） |
| 编译器 | **必需** —— .NET Framework 4.8 的 `csc.exe`（Windows 自带） |
| NX SDK | `<NX_ROOT>\NXBIN\managed\NXOpen.dll` 等（随 NX 安装） |

> **不在标准路径装 NX**：编译前设 `NX_ROOT`。`build.bat` 会优先读它，否则自动扫
> `%ProgramFiles%\Siemens\NX*`。检测到的版本不是 NX 2412 时会给出警告（API 可能不兼容）。

---

## 安装

### 1. 编译插件

```bat
cd plugin
.\build.bat run      :: 编译 + 签名 + 部署到 startup\（Git Bash 里换成 ./build.bat run）
```

> ⚠️ **所有 shell 都需要路径前缀。** Windows 下 `cmd.exe` / `PowerShell` / `Git Bash` 都不会
> 搜索当前目录来执行文件，裸写 `build.bat` 一定失败：
>
> | Shell | 正确写法 | 裸写 `build.bat` 会报 |
> |---|---|---|
> | cmd.exe | `.\build.bat run` | `'build.bat' 不是内部或外部命令，也不是可运行的程序或批处理文件` |
> | PowerShell | `.\build.bat run` | 无法将 "build.bat" 项识别为 cmdlet、函数、脚本文件或可运行程序 |
> | Git Bash | `./build.bat run` | `bash: build.bat: command not found` |
>
> ⚠️ **`.\` 只在 cmd.exe 和 PowerShell 里成立。** 在 Git Bash 里写 `.\build.bat` 同样是错的——bash 会把
> `\b` 当转义符吃掉，实际执行的是 `.build.bat`，报 `bash: .build.bat: command not found`。
> 前缀按 shell 分，本文所有 `.bat` 命令（含 `setup.bat`）同理。

`build.bat` 会自动探测 NX 安装路径。装在非标准位置时，先设 `NX_ROOT` 覆盖：

```bat
set NX_ROOT=D:\Siemens\NX2412
```

> ⚠️ `set` **只对当前这个 shell 窗口有效**。第 2 步的 `setup.bat` 同样要读 `NX_ROOT`——换个窗口
> 就没了。要么在同一个窗口里连着做完两步，要么用 `setx` 写成永久变量（新开的窗口才生效）：
>
> ```bat
> setx NX_ROOT "D:\Siemens\NX2412"
> ```

> ⚠️ **DLL 必须签名**，NX 会静默拒绝加载未签名的插件（不报错）。`deploy.bat` 会调用 `<NX_ROOT>\NXBIN\SignDotNet.exe` 完成签名。
>
> 签名所需的 `NXSigningResource.res` **不由本仓库分发**——`build.bat` 在编译时从
> `%NX_ROOT%\UGOPEN\NXSigningResource.res` 读取（该文件随 NX 安装提供）。

### 2. 环境变量 + custom_dirs.dat

```bat
cd ..
.\setup.bat          :: Git Bash 里换成 ./setup.bat
```

生成 `custom_dirs.dat`（单行，指向本包的 `plugin\`），并设置用户级环境变量 `UGII_CUSTOM_DIRECTORY_FILE` 与 `UGII_USER_DIR`。

> ⚠️ `setup.bat` 检查的是 `plugin\startup\managed_plugin.dll`。所以第 1 步要用 `build.bat run`
> ——**只跑不带参数的 `build.bat`（只编译到 `plugin\bin\`）会被它判为「尚未编译」而退出**，
> 那是判据不同，不是编译失败。

> ⚠️ **`setup.bat` 也要 `NX_ROOT`，而且它失败得很早。** 这个脚本第一件事就是调 `plugin\nx-env.bat`
> 探测 NX 安装；装在非标准路径又没设 `NX_ROOT` 时，`nx-env.bat` **直接 `exit /b 1`**——此时
> `custom_dirs.dat` 和那两个环境变量**一个都还没写**。你会卡在「TCP 1977 不监听 + 菜单不出现」，
> 而 NX 本身没有任何毛病。第 1 步若只用 `set NX_ROOT=...`（会话级），换个窗口跑第 2 步就会踩到这里。

> ⚠️ **这两个环境变量是 NX 找到插件的唯一途径**。不设 = 菜单不出现、TCP 1977 不监听，且 NX 不会报错。
>
> ⚠️ `setup.bat` 用 `setx` 写**用户级永久环境变量**，并会**覆盖**你已有的同名变量。

### 3. 重启 NX

环境变量只在 NX 启动时读取。**完全退出所有 NX 窗口**再重新打开。

### 4. 注册 MCP 服务器

仓库根目录的 `.mcp.json.example` 是模板。**Claude Code 读的是项目根目录的 `.mcp.json`**（不是
`.claude/` 下）——把它复制成项目根目录的 `.mcp.json`，**把里面的绝对路径改成你本机的实际路径**：

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

> **确认客户端真的读到了这个文件**：在会话里敲 `/mcp`（能看到 `nx-exec` 及其连接状态），或
> 命令行 `claude mcp list`；`claude mcp get nx-exec` 还会告诉你该服务器由**哪个作用域**定义，
> 用来确认你改的正是它读的那个文件。`.mcp.json` **只在会话启动时读一次**，改完要重开会话。
>
> ⚠️ 示例文件第二行有一个非标准的顶层 `_comment` 键。若你的客户端不认它，症状是**静默地
> 一个服务器都没出现**（不报错）——删掉那一行再试即可。

> ⚠️ **顺序要求：先起 NX，再起 MCP 客户端。** 适配层在**进程启动时**拉一次工具清单
> （`mcp/nx-exec-mcp.js` 里 `let pendingTools = loadTools();` 是模块级的），此后不再刷新。
> 如果 MCP 客户端先于 NX 启动（或 NX 还没把插件加载完），它拉到的就是**空清单**——
> 表现为 MCP 里一个 `nx_*` 工具都看不到，且**不会自己恢复**。
>
> **恢复办法**：先确认 NX 在运行且 TCP 1977 已监听（`node scripts/check-plugin-status.js`），
> 然后**重启 MCP 客户端**（重开 Claude Code 会话），让它重新拉一次。

---

## 验证

```bash
node scripts/check-plugin-status.js
```

检查顺序：**① NX 进程 → ② TCP 1977 → ③ 插件版本 + 工具数量 → ④ 启动日志分析 → ⑤ GetUI 诊断 → ⑥ 最近 5 条运行日志**。
其中「菜单注册」不是独立一步——它的结论出自 **④**（从日志里读 `全部菜单回调注册成功` / `注册失败:`）。

**退出码**：有任意一项 ❌ → `1`；一项失败都没有 → `0`。可以直接当闸门用（CI / 批处理）。
⚠️ 警告和 ⏭ 跳过**不算失败**，不影响退出码。

**TCP 不在线时，③ 整步不执行**，并显式打印一行跳过提示，而不是默默省略：

```
⏭  插件版本 / 工具数 / 菜单注册 —— TCP 未在线, 这三项查不了 (不是"通过")
```

看到这行就意味着这三项**没有结论**——别当成通过。

期望结果：`端口 1977 已监听` + `工具数 > 0`。

工具总数**不做硬编码**，以实际 `list_tools` 为准：

```bash
# 插件真实工具清单（绕过 MCP 客户端的连接快照）
node scripts/nx-tcp-call.js --list
```

---

## 故障排除

**先确定看哪一份 syslog —— 这是下面几条的前提。** 每次 NX 启动都会在 `%TEMP%` 新写一份
`<用户名><十六进制>.syslog`，用久了能累积到上百份；名字里的十六进制**不是时间戳**，按名字挑不出来。
规则是**取修改时间最新的那一份**（就是你刚重启的那次）：

```bash
ls -t "$TEMP"/<用户名>*.syslog | head -1                 # Git Bash
```

```powershell
Get-ChildItem "$env:TEMP\<用户名>*.syslog" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
```

打开后对两处：**第 2 行** `*** system log created by <用户名> on <日期>` 是这个会话的**启动**时间；
**末行** `@@@ End of session <日期>` 是**结束**时间（NX 崩溃也照写）。对不上就是拿错文件了。

| 症状 | 根因 | 处理 |
|---|---|---|
| TCP 1977 不监听，且最新那份 syslog 里**连 `UGII_CUSTOM_DIRECTORY_FILE` 行都没有** | 环境变量没设或没生效 —— 插件连被找到的入口都没有 | 重跑 `setup.bat`（它同样依赖 `NX_ROOT`，见 [安装](#安装) 第 2 步），完全重启 NX |
| 菜单栏没有 "NX MCP Plugin" | `custom_dirs.dat` 丢了或被移走 | 重跑 `setup.bat` |
| TCP 1977 不监听，syslog 里 `UGII_CUSTOM_DIRECTORY_FILE` 指对了，但**没有任何 `Loaded assembly: managed_plugin` 行** | **不止「没签名」一种**，按顺序排除：① DLL 根本没编译过（`plugin\startup\managed_plugin.dll` 不存在）② 包被搬过，`custom_dirs.dat` 里还是旧路径（看 `UGII_CUSTOM_DIRECTORY_FILE` 的**值**）③ `.men` 不在 `plugin\startup\` ④ **syslog 本身是截断的**——那次 NX 崩了，或只是个压根不加载交互式插件的批处理会话 ⑤ 以上都不是，才是 DLL 未签名（**没有插件就没有工具数可显示**，不要去找"工具数 0"这种状态） | 按 ①→⑤ 逐条对（判别表见下）；**只有确定是签名问题**才重跑 `plugin\deploy.bat`（编译 + 签名 + 部署），然后完全重启 NX |
| 昨天还能用、NX 也没重启，TCP 1977 突然不监听 | 误点了 NX 菜单 **NX MCP Plugin ▸ Toggle MCP Server** —— 它是个**开关**，会把服务停掉。**当场**的凭据是弹出的 "Server stopped." 提示框（`tcp_server.log` 里当时也会留一行 `[CTRL] Server stopped`，但日志按 1 MB 轮转、旧内容直接丢，**事后多半已经找不到它——grep 不到不等于这个原因被排除**） | 再点一次同一个菜单项即可重新起服务（或重启 NX）；插件本身没坏 |
| 插件加载了两次（双实例） | 多个位置存在 `managed_plugin.dll` | 只用 `plugin/startup/` 一处，删掉其余副本 |
| 改了源码但行为没变 | **先看部署那步有没有报错。** DLL 被 NX 占用时 `deploy.bat` 的复制会**直接失败并 `exit 1`**（不静默）。真正静默的是另外两种：加载了**旧目录**的 DLL、或 MCP 客户端的**连接快照** | 部署报错了 → 完全退出 NX 后重跑 `build.bat run`；没报错 → 用 syslog 的 `Loaded assembly: managed_plugin` 行核对路径是不是你刚部署的 `plugin\startup\`，不是就删掉旧副本；路径也对 → 按下面「MCP 客户端看不到新工具」一行重连 |
| 自己加的工具在 `--list` 里有、MCP 里调却报 `Tool not found` | 工具名**不以 `nx_` 开头** —— 适配层硬性要求前缀，而 `--list` 直连插件、不受此限制（见 [工具覆盖](#工具覆盖)） | 给工具名加 `nx_` 前缀；这不是连接快照问题，重连多少次都没用 |
| `build.bat` 报 "未找到签名资源" | `%NX_ROOT%\UGOPEN\` 下没有该文件 | 确认 `NX_ROOT` 指向的是 NX 安装根目录 |
| 编译报一堆找不到的 NXOpen 类型 | 装的不是 NX 2412 | `set NX_ROOT=<你的 NX 2412 路径>` |
| MCP 客户端看不到新工具 | 客户端在**连接时**拉一次清单就冻结 | 重连 MCP；用 `nx-tcp-call.js --list` 看插件真实清单 |

**判别「日志被截断」和「真被拒载」**（上面第 3 行的关键，别跳过）：

| 你在那份 syslog 里看到 | 结论 |
|---|---|
| `Assembly <路径> is signed with a NXOpen signature` 或 `DotNet signature is valid for <路径>` | **正面证据**：签名有效，DLL 也确实被加载了。比"没看到某行"可靠得多 |
| 这两行都没有，但有 `>>>> O/S ERROR: signal <n> caught` | 那次 **NX 崩了**，日志在加载插件之前就断了——是崩溃，不是签名 |
| 这两行都没有，文件也短、很快就 `@@@ End of session` | 那是个**批处理会话**（例如 `nx_run_journal` 起的独立 NX 进程），它本来就不加载交互式插件。**换一份 syslog 再看** |
| 两行都没有，DLL 在、路径也对 | 这才轮到「DLL 未签名」 |

> ⚠️ **"没有 `Loaded assembly: managed_plugin` 行"推不出"没签名"。** 抽样一台用了几个月的机器：
> 它累积的 113 份 syslog 里 6 份没有这行——5 份是 NX 崩溃（`signal 11`）、1 份是批处理会话，
> **没有一份**是签名被拒；而且这 6 份的 `UGII_CUSTOM_DIRECTORY_FILE` 值都指向**另一个目录**
> （即上面第 ② 条「包被搬过」）。不加判别就去重新签名、重新编译，是白跑一趟。

**`tcp_server.log` 在哪**（第 3 行要用它）：

```
%LOCALAPPDATA%\Siemens\<NX 版本>\startup\tcp_server.log
```

`<NX 版本>` **不一定是 `NX2412`**——它由 `UGII_BASE_DIR` → `NX_ROOT` → 扫
`%LOCALAPPDATA%\Siemens\NX<数字>` 取最高版本，依次解析出来（`plugin/PluginInfo.cs` 的 `NxPaths`）。
超过 1 MB 会轮转成同目录下的 `tcp_server.log.1`，**再旧的直接丢弃**（`plugin/TcpServer.cs`）——
所以"日志里没有"经常只是"已经轮转掉了"。`check-plugin-status.js` 会两份都读，并在第 4 步标题里标出用的是哪一份。

排查看 `docs/nx-plugin-relocation-checklist.md`——收录了插件搬迁/部署的全部踩坑记录。
**两张表谁先谁后**：本表管「插件有没有被 NX 找到、有没有起来」，**先看**；清单那表管「部署与搬迁」，**后看**。
同样是「TCP 1977 不通」，判别点是 **NX 进程本身**：`check-plugin-status.js` 第 1 步若报 `NX 未运行`，
或 `ugraf.exe` 有**多个**实例，症状属于清单表的 `NX 崩溃无法连接 1977`（端口被旧进程占用 /
NX 未完全加载），去那边；NX 好好地跑着而 1977 是哑的，就是本表这几种。

---

## 手工调用（不走 MCP）

```bash
node scripts/nx-tcp-call.js --list                       # 全量工具清单
node scripts/nx-tcp-call.js nx_list_open_parts '{}'      # 调用单个工具（NX 里什么都没开也能成功）
```

> ⚠️ **几何类工具都有前提：NX 里要有打开且是 work part 的零件。** 刚装好就照抄
> `nx_measure_volume` 会拿到 `No work part is open.`（零件是空的则拿到
> `No bodies found in the work part.`）——那是前提没满足，**不是安装坏了**。
> 先把件开起来，再量：
>
> ```bash
> node scripts/nx-tcp-call.js nx_create_part '{"path":"C:/temp/demo.prt"}'   # 或 nx_open_part
> node scripts/nx-tcp-call.js nx_list_open_parts '{}'      # 确认要量的那件 is_work 是 true
> node scripts/nx-tcp-call.js nx_measure_volume '{}'
> ```
>
> （`nx_open_part` 只把件变成 display part，**不自动设为 work part**——`nx_list_open_parts` 返回的
> `is_work` 字段就是用来确认这一点的。）

排查 MCP 层问题时，这是最有用的通道——它直连 TCP 1977，绕过了 MCP 客户端、适配器和连接快照三层。

---

## 工具覆盖

工具注册表在 `plugin/tools/ToolRegistry.cs`。覆盖 15 个域、115 个工具：

| 域 | 工具数 | 源文件 |
|---|---|---|
| 建模 / 实体 | 24 | `tools/ModelingTools.cs` |
| 草图 | 18 | `tools/SketchTools.cs`、`tools/Sketch/SketchReadbackTools.cs` |
| 文件操作 | 8 | `tools/FileOpsTools.cs` |
| 装配 | 14 | `tools/AssemblyTools.cs`、`tools/Assembly/CreateComponentTool.cs`、`tools/Assembly/FindSameBodiesTool.cs`、`tools/ComponentTree.cs` |
| 检查 / 拓扑 | 7 | `tools/InspectTools.cs` |
| 实用工具 | 12 | `tools/UtilityTools.cs` |
| 测量 | 7 | `tools/Measure/MeasureTools.cs` |
| 同步建模 | 6 | `tools/SynchronousTools.cs` |
| 曲面 | 5 | `tools/SurfaceTools.cs` |
| 显示 | 4 | `tools/Display/DisplayTools.cs` |
| 基准 | 4 | `tools/DatumTools.cs` |
| 修正 | 4 | `tools/CorrectTools.cs` |
| 钣金 | 3 | `tools/SheetMetalTools.cs` |
| 曲线 | 3 | `tools/CurveTools.cs` |
| WAVE / 关联 | 2 | `tools/WaveTools.cs` |
| 验证 | 2 | `tools/ValidateTools.cs` |

共用基础文件：`tools/IToolHandler.cs`（接口）、`tools/ToolResult.cs`（返回格式）、`tools/ToolHelpers.cs`（参数提取）、`tools/ToolRegistry.cs`（注册表）、`tools/WorkPartGuard.cs`（工作部件守护）。

参数契约在 `mcp/tool-schemas.json`。它**只给 MCP 侧补参数元数据，不是准入闸门**：插件 `list_tools`
里有、而这份 JSON 里没有的工具**照样会暴露**给客户端，只是 `inputSchema` 退化成宽松的
`{type:"object", additionalProperties:true}`（参数仍以插件契约为准）。所以新增工具**只需要改插件**，
这份 JSON 是可选补全，不是必须同步的第二处。

> ⚠️ **工具名必须以 `nx_` 开头**，这是 MCP 适配层的硬性要求（`mcp/nx-exec-mcp.js` 对不以 `nx_`
> 开头的名字直接返回 `Tool not found`）。而 `nx-tcp-call.js --list` 是**直连插件**的，没有这个限制，
> 会照实列出不带前缀的工具——**看到它在清单里 ≠ MCP 调得到**。名字少写前缀时，`--list` 的"有"
> 是假阳性，别顺着它去查连接快照。

### 规则提示（`data.rule_check`）

工具返回时可能附带一条 `data.rule_check`，来自 `plugin/rules/` 的工程规则检查。规则集共 **15 条**，判据全部写死在 C#（`plugin/rules/RuleEngine.cs`）；元数据（id / 名称 / 说明 / 适用意图）放在 `plugin/rules/rules_config.json`，运行时从插件 DLL 同级的 `rules\rules_config.json` 读取，文件不在就回退到 C# 内置的同名规则集。

> ⚠️ JSON 里的 **`severity` 是死字段**：它被读进 `RuleDefinition.Severity` 之后**没有任何代码再用它**——判定实际走的是各检查函数硬编码在 C# 里的严重级。改 JSON 的 `severity` 既不生效也不报错。

**两条前提，决定了它有多可信：**

1. **只读本次调用的参数，不测量零件几何** —— 也不读 JSON 里的 `params` 阈值（阈值写死在 C# 里，改 JSON 不改变判据）。
2. **读不到参数就跳过，绝不拿默认值报警** —— 该参数没传（或传了非数值）时，这条规则直接判"跳过"，不会凭空造出一条违规。这条是有意为之：规则最初是按旧的 `create_*` 意图参数名写的，而工具**实际的参数名与之并不完全一致**，硬套默认值会对着合法调用报假警。

下表是其中 6 条：

| Rule | Criterion (matches the `RuleEngine.cs` implementation) |
|---|---|
| Hole edge clearance | `edge_clearance` ≥ 2 × `diameter`; skipped if either is absent |
| Hole wall thickness | `wall_thickness` ≥ 1.5 mm (the real wall thickness is not measured; no tool supplies this parameter yet, so in practice it always skips) |
| Hole spacing | `center_distance` ≥ 2 × the larger of the two diameters — **not** "centre distance − (r₁+r₂) ≥ 2 mm" |
| Depth-to-diameter | `depth` (alias `depth_value`) / `diameter` ≤ 10; skipped if either is absent |
| Fillet vs wall | **checks only that radius > 0** (a radius ≤ 0 is a violation); it never reads wall thickness — no tool supplies `wall_thickness`, so a positive radius always yields the skip message `Missing wall_thickness, skipping fillet wall thickness check` |
| Thread pilot hole | built-in GB/T 196-2003 metric table (M2–M36): pilot diameter within 0.15 mm and pitch within 0.05 mm of the standard values. **In practice it always skips** — no tool supplies `thread_diameter`; and the non-standard-size hint never shows up in the response either |

The other nine (hole diameter range, chamfer wall thickness, chamfer overlap, fillet chain, target body present, extrude distance, shell thickness, draft angle, pattern bounds) are the same kind of parameter range/sign checks.

> ⚠️ **规则只提示，不阻断。** 命中与否都不改变 `success`，也绝不拒绝执行（error 级也降级成提示）。是否采纳由调用方判断——规则只看参数，不看你零件的实际几何。

`nx_extrude` 传 `distance = -5` 时的 `rule_check`。`suggestions` 只在非空时出现——这一条命中了
error 级规则，所以它带着一条：

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

### 工程图（待开发）

**工程图（Drawing）** 能力仍在开发中，本版本暂未提供。规划中的能力包括：建视图（基本视图 / 投影 / 剖视 / 局部放大）、尺寸标注、GD&T、焊接符号、表面粗糙度、BOM 与气球、表格注释、剖面线、PMI 及图纸继承、标题栏。

在补齐之前，可先用 **`nx_run_journal`** 执行任意 NXOpen C# 脚本完成工程图相关操作。

---

## 已知限制

- **NXOpen 版本耦合**：基于 NX 2412 开发，用到部分反射兜底（`NXOpen.UI`/`MenuBar`/`MessageBox` 在 NX2412 的差异）。其他版本需实测。
- **仅 Windows**：NX 本身如此。
- **插件在 NX 主线程执行**：长耗时操作会阻塞 UI。
- `nx_close_part` **不保存且丢弃未保存修改**，用前先存盘。
- **`nx_close` 会关掉整个 NX，不只是当前零件**：不带参数 = `Session.Parts.CloseAll()` + `Session.Exit()`（**丢弃全部未保存修改**），失败还会退化成 `Process.Kill()`；`force=true` 直接杀进程。它的工具描述只写 "Close Siemens NX gracefully or forcefully."，看不出这个后果。
- **`nx_run_journal` 不能跑 Python journal**：`.py` 分支只会去调 `Session.ExecuteJournal()`，而这个 API 在 NX2412 已被移除，必然失败并提示改用 `.cs`。只有 `.cs` 能跑——想执行 Python journal 得走 NX 菜单 Tools ▸ Journal ▸ Play。
- **3 个工具因 NX2412 API 移除而未实现**（调用会返回失败，工具描述带 `[未实现]` 标记）：
  | 工具 | 用途 | 替代方案 |
  |---|---|---|
  | `nx_correct_face_geometry` | 把碎面（B_SURFACE）替换成干净几何面 | NX GUI 中手动替换面 |
  | `nx_record_start` | 开始录制 NX Journal | NX 菜单 Tools ▸ Journal ▸ Record |
  | `nx_record_stop` | 停止录制 NX Journal | NX 菜单 Tools ▸ Journal ▸ Stop |

  三个工具的 API（`CreateReplaceFaceBuilder`、`BeginJournalRecording`、`EndJournalRecording`）在 NX2412 的 managed API 中已不存在，无法通过代码调用。后续若 NX 新版本恢复这些 API，可补上实现。

---

## 许可证与法律声明

### 许可证

[MIT](LICENSE) © 2026 cyw2527

你可以自由使用、修改、分发本软件（含商用），只需保留版权声明。

### 与 Siemens 无关

> **本项目是非官方的第三方开源项目，与 Siemens 无任何关联，未获 Siemens 的赞助、授权或认可。**
>
> "Siemens"、"NX"、"NXOpen"、"Parasolid" 是 Siemens AG 或其关联公司的商标或注册商标。
> 本项目对这些名称的使用仅用于**指明兼容的软件产品**（合理使用），不表示任何形式的
> 合作或背书关系。

### 本仓库分发的内容

| 内容 | 说明 |
|---|---|
| C# / JavaScript 源码 | 本项目原创，MIT 授权 |

**本仓库不包含、也不分发任何 Siemens 的专有文件**：

- ❌ **不含** 编译产物 `managed_plugin.dll`（`.gitignore` 已排除）——NX 要求用**你本机**的 `SignDotNet.exe` 签名，二进制无法跨机器复用
- ❌ **不含** `NXSigningResource.res`——编译时由 `build.bat` 从 `%NX_ROOT%\UGOPEN\` 读取
- ❌ **不含** `NXOpen.dll` 等 NX SDK 程序集——编译时从你本机的 NX 安装引用

### 配置责任：一切都在你自己的 NX 上现场产生

本仓库不含任何 Siemens 文件，也不含任何预生成的索引/数据库。所有 Siemens 相关内容都**在你的机器上、用你自己的 NX 安装**产生：

| 需要的东西 | 从哪来 | 何时产生 |
|---|---|---|
| `NXOpen.dll` 等 SDK 程序集 | 你自己的 NX 安装 | 编译时引用 |
| `NXSigningResource.res` | `%NX_ROOT%\UGOPEN\` | 编译时读取 |
| `SignDotNet.exe` | `%NX_ROOT%\NXBIN\` | 部署时调用 |
| `managed_plugin.dll` | **你自己编译** | 运行 `build.bat` |

**所以本包开箱后需要你自己配置一次**——见 [安装](#安装)。这不是省略了步骤，而是有意为之：唯一能避免再分发 Siemens 文件、又能保证版本匹配的做法。

这套「**发工具，不发数据**」的做法，好处是包体积小、不触碰再分发边界，代价是每个使用者都要在自己机器上跑一次编译——这是有意选择的取舍。

**使用者需自行持有合法的 Siemens NX 授权**。本软件只是调用你本机 NX 已提供的 API，不绕过、不破解任何授权或许可证校验机制。

### 关于内部标识符命名

包名是 `nx-auto-mcp`，但插件内部的菜单资源名（`nx_mcp_plugin.men`）、菜单动作名（`NX_MCP_*`）和端口环境变量（`NX_MCP_PORT`）仍保留 `nx_mcp` 字样。这些是 `.men` 文件与 C# 代码之间**成对约定**的标识符，改名必须两边同步，风险高于收益，故有意保留。

### 免责

本软件按"现状"提供，不附带任何明示或暗示的担保。在 NX 中执行自动化操作可能修改或删除你的模型文件——**请先在副本上验证**，并自行做好版本控制。作者不对任何数据丢失或生产事故负责。