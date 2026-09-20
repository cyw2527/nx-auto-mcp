// nx-auto-mcp.js — NX-PLUGIN execution layer MCP adapter (MCP v5)
// Purpose: expose NX-PLUGIN tools (TCP 1977, custom JSON-Line protocol) as MCP tools
// Tool contract: reads tool-schemas.json from same directory; actual count from list_tools
// Protocol: Claude Code (stdio MCP) <-> this adapter <-> TCP 1977 (C# DLL -> NXOpen API)
// Usage: register via .mcp.json stdio: node nx-auto-mcp.js
// v5 features: server/discover + negotiateVersion (client version ∩ supported) + instructions
// Dependencies: Node built-in modules only (net/child_process/fs/path) — no npm install
"use strict";
const net = require("net");
const { exec } = require("child_process");
const fs = require("fs");
const path = require("path");

// Keep in sync with the C# plug-in side: the plug-in reads NX_MCP_HOST / NX_MCP_PORT to
// determine its listen address. If only one side changes, the server switches ports while
// the client still connects to 1977 → direct connection failure.
// Default values match the standard configuration; behavior is unchanged when unset.
const NX_HOST = process.env.NX_MCP_HOST || "127.0.0.1";
const NX_PORT = parseInt(process.env.NX_MCP_PORT, 10) || 1977;

// ─── NX install path detection ─────────────────────────────────────────
// Priority: NX_UGRAF env var (full exe path) > NX_ROOT (install root) > scan Program Files\Siemens\NX*
// Returns null on failure — caller reports a readable error, no stack trace.
function detectNxExe() {
  const explicit = process.env.NX_UGRAF;
  if (explicit) return fs.existsSync(explicit) ? explicit : null;

  const root = process.env.NX_ROOT;
  if (root) {
    const exe = path.join(root, "NXBIN", "ugraf.exe");
    if (fs.existsSync(exe)) return exe;
    const alt = path.join(root, "ugraf.exe");   // NX_ROOT points directly to NXBIN
    if (fs.existsSync(alt)) return alt;
    return null;
  }

  const progDirs = [
    process.env.ProgramFiles || "C:\\Program Files",
    process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)",
  ];
  for (const pd of progDirs) {
    const siemens = path.join(pd, "Siemens");
    if (!fs.existsSync(siemens)) continue;
    let versions;
    // Strictly match NX<digits> only — the directory also contains nxsl / San etc.
    // A loose /^NX/i would include nxsl, and string sort places it last → gets tried first.
    try { versions = fs.readdirSync(siemens).filter(d => /^NX\d+$/i.test(d)); } catch { continue; }
    // Sort by version number descending (newest first: NX2412 > NX2306 > NX2206)
    versions.sort((a, b) => parseInt(b.slice(2), 10) - parseInt(a.slice(2), 10));
    for (const v of versions) {
      const exe = path.join(siemens, v, "NXBIN", "ugraf.exe");
      if (fs.existsSync(exe)) return exe;
    }
  }
  return null;
}

const NX_EXE_HINT =
  "NX installation not found. Set environment variable NX_UGRAF=<...\\NXBIN\\ugraf.exe> " +
  "or NX_ROOT=<NX install root>, or pass the nx_path parameter at call time.";

// ─── nx_open local launch (bypasses TCP) ───────────────────────────────
// Chicken-and-egg: nx_open needs to start NX, but TCP 1977 only exists when NX is running.
// Solution: intercept nx_open and launch NX directly via ugraf.exe.

function isNxRunning() {
  return new Promise(resolve => {
    const sock = net.connect(NX_PORT, NX_HOST);
    sock.on("connect", () => { sock.destroy(); resolve(true); });
    sock.on("error", () => { sock.destroy(); resolve(false); });
    sock.setTimeout(2000, () => { sock.destroy(); resolve(false); });
  });
}

function launchNx(nxPath, partPath, customDir) {
  return new Promise((resolve, reject) => {
    const exe = nxPath || detectNxExe();
    if (!exe) { reject(new Error(NX_EXE_HINT)); return; }
    const args = ["-nx"];
    if (partPath) args.push(partPath);

    const opts = { detached: true, stdio: "ignore" };
    if (customDir) opts.cwd = customDir;

    console.error("[nx-auto-mcp] nx_open: launching " + exe + " " + args.join(" "));
    const child = exec(`"${exe}" ${args.join(" ")}`, opts, (err) => {
      if (err && err.code !== 0 && err.code !== null) {
        console.error("[nx-auto-mcp] nx_open: launch error:", err.message);
      }
    });
    child.unref();

    // Wait for TCP 1977 to be ready (up to 60 seconds)
    const deadline = Date.now() + 60000;
    const poll = setInterval(async () => {
      if (Date.now() > deadline) {
        clearInterval(poll);
        resolve({ success: true, message: "NX launched (TCP not yet ready after 60s, may still be loading)" });
        return;
      }
      const running = await isNxRunning();
      if (running) {
        clearInterval(poll);
        resolve({ success: true, message: "NX launched and TCP 1977 ready" });
      }
    }, 3000);
  });
}
const SUPPORTED_VERSIONS = ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26", "2024-11-05"];
function negotiateVersion(clientVersion) {
  if (clientVersion && SUPPORTED_VERSIONS.includes(clientVersion)) return clientVersion;
  return "2025-11-25"; // Default to the most conservative stable version (Claude Code will always recognize it)
}

const INSTRUCTIONS = [
  "nx-auto-mcp = NX-PLUGIN execution layer (TCP 1977 -> C# DLL -> NXOpen API), NX operation tools.",
  "Tool names use nx_ prefix: nx_create_part/nx_open_part/nx_extrude/nx_create_sketch/nx_sketch_line/nx_hole/nx_measure_distance/nx_inspect_topology etc.",
  "Tools execute on the NX main thread; all parameters passed via TCP JSON; returns {success, message, data}.",
  "Prerequisite: NX must be running and plug-in loaded (node scripts/check-plugin-status.js to verify).",
  "Tip: use nx_run_journal to execute arbitrary NXOpen C# journals (public class Program + Main, strong typing, no dynamic).",
  "Troubleshooting: TCP unreachable -> check NX is running + port 1977; unknown tool -> use list_tools for full inventory."
].join("\n");

// ─── Tool schemas (tool-schemas.json, static) ────────────────────────
// Static snapshot, no runtime database — intersected with NX plug-in's list_tools.
let toolSchemas = {};
try {
  toolSchemas = JSON.parse(fs.readFileSync(path.join(__dirname, "tool-schemas.json"), "utf8"));
} catch (e) { /* schema file missing — fall back to loose schemas */ }

// ─── TCP 1977 client (JSON-Line protocol) ─────────────────────────────
let toolList = [];  // [{name, description}]
function nxCall(method, params, timeoutMs = 120000) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(NX_PORT, NX_HOST);
    let buf = "";
    const timer = setTimeout(() => { sock.destroy(); reject(new Error("NX TCP timeout")); }, timeoutMs);
    sock.on("data", d => {
      buf += d.toString();
      let nl;
      while ((nl = buf.indexOf("\n")) >= 0) {
        const line = buf.slice(0, nl); buf = buf.slice(nl + 1);
        if (!line.trim()) continue;
        clearTimeout(timer);
        sock.destroy();
        try { resolve(JSON.parse(line)); } catch (e) { reject(new Error("Bad JSON: " + line.slice(0, 120))); }
      }
    });
    sock.on("error", e => { clearTimeout(timer); reject(new Error("NX TCP error: " + e.message)); });
    sock.on("close", () => { clearTimeout(timer); reject(new Error("NX TCP closed")); });
    sock.write(JSON.stringify({ method, id: 1, params: params || {} }) + "\n");
  });
}

// Fetch tool list at startup
async function loadTools() {
  try {
    const r = await nxCall("list_tools", {}, 10000);
    if (r && r.result && Array.isArray(r.result.tools)) {
      toolList = r.result.tools;
      return toolList;
    }
  } catch (e) { /* NX not running; tools/list returns empty + error hint */ }
  return [];
}

// ─── MCP v5 handler (uses compatible-stdio transport module) ───────────
const { startCompatibleStdio, installStdoutSentinel } = require("./mcp-compatible-stdio");
installStdoutSentinel();

function send(id, r) {
  // compatible-stdio transport handles framing; just write newline-delimited
  const payload = JSON.stringify({ jsonrpc: "2.0", id, ...r }) + "\n";
  if (global.__mcpWithWrite) { global.__mcpWithWrite(() => process.stdout.write(payload)); }
  else { process.stdout.write(payload); }
}
function respond(id, text, structured) {
  const content = [{ type: "text", text: String(text) }];
  if (structured !== undefined) return send(id, { result: { content, structuredContent: structured } });
  return send(id, { result: { content } });
}

let pendingTools = loadTools();  // async preload

function handle(req) {
  const { id, method, params } = req;
  if (method === "server/discover") {
    const clientVer = params?._meta?.io?.modelcontextprotocol?.protocolVersion || params?.protocolVersion;
    send(id, { result: { protocolVersion: negotiateVersion(clientVer), capabilities: { tools: { unordered: true } }, serverInfo: { name: "nx-auto-mcp", version: "1.0.1" } } });
  } else if (method === "initialize") {
    const clientVer = params?._meta?.io?.modelcontextprotocol?.protocolVersion || params?.protocolVersion;
    send(id, { result: { protocolVersion: negotiateVersion(clientVer), capabilities: { tools: { unordered: true } }, serverInfo: { name: "nx-auto-mcp", version: "1.0.1" }, instructions: INSTRUCTIONS } });
  } else if (method === "notifications/initialized" || method === "notifications/cancelled" || method === "logging/setLevel") {
    // no-op
  } else if (method === "ping") {
    send(id, { result: {} });
  } else if (method === "resources/list") {
    send(id, { result: { resources: [] } });
  } else if (method === "prompts/list") {
    send(id, { result: { prompts: [] } });
  } else if (method === "tools/list") {
    pendingTools.then(tools => {
      const list = tools.map(t => {
        const s = toolSchemas[t.name];
        return {
          name: t.name,
          description: (s && s.description) ? s.description + " | " + (t.description || "") : (t.description || "NX tool"),
          inputSchema: s ? {
            type: "object",
            properties: s.properties,
            required: s.required,
            additionalProperties: true  // pass through parameters outside the contract (permissive fallback)
          } : { type: "object", properties: {}, additionalProperties: true },
          annotations: { readOnlyHint: false }  // nx-auto-mcp tools modify NX model state
        };
      });
      send(id, { result: { tools: list, _meta: { ttlMs: 3600000, cacheScope: "global" } } });
    }).catch(() => send(id, { result: { tools: [] } }));
  } else if (method === "tools/call") {
    const { name, arguments: args } = params;
    if (!name || !name.startsWith("nx_")) { send(id, { error: { code: -32601, message: "Tool not found: " + (name || "(empty)") } }); return; }

    // ── nx_open interception: launch NX directly, bypass TCP ──
    if (name === "nx_open") {
      isNxRunning().then(running => {
        if (running) {
          respond(id, "NX OK: already_running — NX is already running on TCP 1977");
        } else {
          launchNx(args?.nx_path, args?.part_path, args?.custom_dir)
            .then(r => respond(id, "NX OK: " + r.message))
            .catch(e => respond(id, "NX FAIL: " + e.message));
        }
      });
      return;
    }

    nxCall("tool", { ...(args || {}), tool: name })
      .then(r => {
        if (r.error) return respond(id, "NX Error: " + r.error);
        const res = r.result || {};
        const text = (res.success === false ? "NX FAIL: " : "NX OK: ") + (res.message || "");
        const extra = res.data && Object.keys(res.data).length ? "\n" + JSON.stringify(res.data, null, 2) : "";
        // Structured output: attach data object as-is (v5 structuredContent)
        respond(id, text + extra, res.data && Object.keys(res.data).length ? res.data : undefined);
      })
      .catch(e => respond(id, "NX Error: " + e.message));
  } else {
    send(id, { error: { code: -32601, message: "Method not found" } });
  }
}

// v2.0: use compatible-stdio transport module (dual framing + 10MB buffer limit + stdout sentinel)
const nxAutoMcpTransport = startCompatibleStdio({
  onmessage: (msg) => {
    try { handle(msg); } catch (e) { console.error("[nx-auto-mcp] handle error:", e.message); }
  },
  onerror: (err) => {
    console.error("[nx-auto-mcp] stdio error:", err.message);
  },
  onclose: () => {
    console.error("[nx-auto-mcp] stdin closed - exiting.");
    process.exit(0);
  },
});
// send/respond in handle() use newline framing (compatible-stdio auto-responds in client's framing)
