#!/usr/bin/env node
// nx-tcp-call.js — direct TCP caller to the NX plugin (127.0.0.1:1977)
//
// ══ Why it's needed: MCP tool list is a "connection snapshot" ══
// MCP clients pull tools/list once at **connection time** and it stays fixed.
// Newly deployed tools from the NX plugin won't appear in the client's tool
// list until MCP reconnects (restart Claude Code session / reload MCP server).
// Use this script to bypass MCP and connect directly to the plugin for
// **tool-level validation** and **immediate access to new tools**.
//
// ══ Relationship with MCP server ══
// mcp/nx-auto-mcp.js also uses nxCall("list_tools", {}) to get the list — note
// it passes the **method name** directly; while normal tool calls use
// method:"tool" + params.tool=<name>. Both forms are supported by this script
// (see --raw and default usage).
//
// ══ Usage ══
//   node scripts/nx-tcp-call.js <toolName> ['{json params}']   call a tool (most common)
//   node scripts/nx-tcp-call.js --list                         list all tools (including newly deployed)
//   node scripts/nx-tcp-call.js --methods                      list plugin-supported methods
//   node scripts/nx-tcp-call.js --ping                         connectivity check
//   node scripts/nx-tcp-call.js --raw <method> ['{json}']      call any method directly
//
// ══ Examples ══
//   node scripts/nx-tcp-call.js nx_measure_volume '{}'
//   node scripts/nx-tcp-call.js nx_inspect_feature_tree '{}'
//   node scripts/nx-tcp-call.js nx_extrude '{"sketch_name":"SKETCH_1","distance":10}'
//
// ══ Known pitfalls ══
//   - GBK Chinese output may appear as mojibake (terminal encoding); JSON structure itself is correct
//   - Default timeout is 120s (NX main thread execution, large ops are slow)
//   - nx_open: when called via MCP, it's intercepted by the adapter layer (mcp/nx-auto-mcp.js uses
//     child_process to launch ugraf.exe), not via TCP. This script connects directly to the plugin,
//     using the plugin's internal PowerShell implementation — two different paths, but both start NX.
//   - nx_close: ★ no interception at all, this script behaves **exactly the same** as calling via MCP —
//     without arguments it calls CloseAll() then Exit() on the entire NX (discards all unsaved changes),
//     force=true kills the process directly. Save your work before using it.
const net = require('net');

const PORT = parseInt(process.env.NX_MCP_PORT, 10) || 1977;
const HOST = process.env.NX_MCP_HOST || '127.0.0.1';
const TIMEOUT_MS = 120000;

const PLUGIN_METHODS = [
  'ping', 'get_selection', 'get_model_summary', 'tool', 'list_tools',
  'measure', 'list_features', 'modify_expression',
  'export', 'undo', 'get_parameters'
];

function usage() {
  console.error([
    'Usage:',
    '  node nx-tcp-call.js <toolName> [\'{json params}\']   call a tool',
    '  node nx-tcp-call.js --list                          list all tools',
    '  node nx-tcp-call.js --methods                       list plugin methods',
    '  node nx-tcp-call.js --ping                          connectivity check',
    '  node nx-tcp-call.js --raw <method> [\'{json}\']       call any method directly',
  ].join('\n'));
}

function call(payload) {
  return new Promise((resolve, reject) => {
    const sock = net.connect(PORT, HOST);
    let buf = '';
    let done = false;
    const finish = (fn, v) => { if (done) return; done = true; try { sock.destroy(); } catch (e) {} fn(v); };
    const timer = setTimeout(() => finish(reject, new Error('TIMEOUT after ' + TIMEOUT_MS + 'ms; buf=' + buf.slice(0, 200))), TIMEOUT_MS);
    sock.on('data', d => {
      buf += d.toString();
      let nl;
      while ((nl = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, nl).trim();
        buf = buf.slice(nl + 1);
        if (!line) continue;
        clearTimeout(timer);
        try { finish(resolve, JSON.parse(line)); }
        catch (e) { finish(reject, new Error('Bad JSON: ' + line.slice(0, 200))); }
        return;
      }
    });
    sock.on('error', e => { clearTimeout(timer); finish(reject, new Error('TCP ERROR: ' + e.message)); });
    sock.on('close', () => { clearTimeout(timer); finish(reject, new Error('CLOSED early; buf=' + buf.slice(0, 200))); });
    sock.write(JSON.stringify(payload) + '\n');
  });
}

function parseJsonArg(raw, what) {
  if (!raw) return {};
  try { return JSON.parse(raw); }
  catch (e) { throw new Error(what + ' JSON parse failed: ' + e.message); }
}

async function main() {
  const argv = process.argv.slice(2);
  const first = argv[0];
  if (!first) { usage(); process.exit(2); }

  if (first === '--list') {
    const r = await call({ method: 'list_tools', id: 1, params: {} });
    if (r.result && Array.isArray(r.result.tools)) {
      const names = r.result.tools.map(t => t.name || t);
      console.log('Tool count = ' + names.length);
      names.forEach(n => console.log('  ' + n));
      return 0;
    }
    const m = JSON.stringify(r).match(/nx_[a-z_]+/g);
    const uniq = m ? Array.from(new Set(m)) : [];
    console.log('Tool count = ' + uniq.length + ' (fallback parse from error message)');
    uniq.forEach(n => console.log('  ' + n));
    return uniq.length ? 0 : 1;
  }

  if (first === '--methods') {
    PLUGIN_METHODS.forEach(m => console.log('  ' + m));
    return 0;
  }

  if (first === '--ping') {
    const r = await call({ method: 'ping', id: 1, params: {} });
    console.log(JSON.stringify(r, null, 2));
    return r.error ? 1 : 0;
  }

  if (first === '--raw') {
    const method = argv[1];
    if (!method) { usage(); process.exit(2); }
    const params = parseJsonArg(argv[2], 'params');
    const r = await call({ method, id: 1, params });
    console.log(JSON.stringify(r, null, 2));
    return r.error ? 1 : 0;
  }

  if (first.startsWith('--')) { usage(); process.exit(2); }

  const tool = first;
  const params = parseJsonArg(argv[1], 'params');
  const payload = Object.assign({ tool }, params);
  const r = await call({ method: 'tool', id: 1, params: payload });
  console.log(JSON.stringify(r, null, 2));

  if (r.error) return 1;
  if (r.result && r.result.success === false) return 1;
  return 0;
}

main()
  .then(code => process.exit(code))
  .catch(e => { console.error('ERROR: ' + e.message); process.exit(1); });
