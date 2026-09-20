#!/usr/bin/env node
/**
 * nx_plugin_status — NX MCP Plugin diagnostics
 *
 * Usage:
 *   node scripts/check-plugin-status.js              # full check
 *   node scripts/check-plugin-status.js --online     # check online status only
 *   node scripts/check-plugin-status.js --log        # view startup logs only
 *   node scripts/check-plugin-status.js --watch      # keep monitoring until online
 *
 * Checks:
 *   1. Is the NX process running
 *   2. Is the TCP port (NX_MCP_PORT, default 1977) listening
 *   3. Plugin status snapshot (nx_mcp_status.json — non-rotating, structured)
 *   4. Plugin version + tool count (TCP query)
 *   5. Startup log analysis (scan all log files, sorted by modification time)
 *   6. GetUI diagnostics (early log)
 *   7. Last 5 runtime log entries
 */

const net = require('net');
const fs = require('fs');
const path = require('path');
const { execSync } = require('child_process');

// ─── NX log directory detection ───────────────────────────────────────
function detectNxVersionDir() {
  const siemens = path.join(process.env.LOCALAPPDATA || '', 'Siemens');
  if (process.env.NX_ROOT) {
    const v = path.basename(process.env.NX_ROOT.replace(/[\\/]+$/, ''));
    const p = path.join(siemens, v);
    if (fs.existsSync(p)) return p;
  }
  try {
    const vers = fs.readdirSync(siemens).filter(d => /^NX\d+$/i.test(d));
    if (vers.length) {
      vers.sort((a, b) => parseInt(a.slice(2), 10) - parseInt(b.slice(2), 10));
      return path.join(siemens, vers[vers.length - 1]);
    }
  } catch { /* Siemens directory does not exist */ }
  return path.join(siemens, 'NX');
}

const LOG_DIR = path.join(detectNxVersionDir(), 'startup');
const TCP_LOG = path.join(LOG_DIR, 'tcp_server.log');
const EARLY_LOG = path.join(LOG_DIR, 'nx_plugin_early.log');
const STATUS_JSON = path.join(LOG_DIR, 'nx_mcp_status.json');

const PORT = parseInt(process.env.NX_MCP_PORT, 10) || 1977;
const HOST = process.env.NX_MCP_HOST || '127.0.0.1';

// ─── Color helpers ───
const R = '\x1b[31m', G = '\x1b[32m', Y = '\x1b[33m', C = '\x1b[36m', W = '\x1b[0m', B = '\x1b[1m';

let FAILURES = 0;

function ok(s) { console.log(`  ${G}✅${W} ${s}`); }
function fail(s) { console.log(`  ${R}❌${W} ${s}`); FAILURES++; }
function warn(s) { console.log(`  ${Y}⚠️${W} ${s}`); }
function info(s) { console.log(`  ${C}ℹ️${W} ${s}`); }
function skip(s) { console.log(`  ${Y}⏭${W}  ${s}`); }
function title(s) { console.log(`\n${B}${s}${W}`); }

// ─── 1. Check NX process ───
function checkNxProcess() {
  title('1. NX Process Status');
  try {
    const out = execSync('tasklist /FI "IMAGENAME eq ugraf.exe" 2>nul', {
      encoding: 'utf8', shell: 'cmd.exe', windowsHide: true
    });
    if (out.includes('ugraf.exe')) {
      const lines = out.split('\n').filter(l => l.includes('ugraf.exe'));
      ok('NX is running (' + lines.length + ' process(es)');
      lines.forEach(l => info(l.trim()));
    } else {
      fail('NX is not running (ugraf.exe not found)');
    }
  } catch {
    fail('NX is not running');
  }
}

// ─── 2. Check TCP port ───
function checkTcp(timeoutMs = 3000) {
  return new Promise((resolve) => {
    title('2. TCP Port ' + PORT);
    const c = new net.Socket();
    c.setTimeout(timeoutMs);
    c.connect(PORT, HOST, () => {
      ok('Port ' + PORT + ' is listening — TCP server is online');
      c.end();
      resolve(true);
    });
    c.on('error', () => {
      fail('Port ' + PORT + ' is not listening — TCP server is not started');
      c.destroy();
      resolve(false);
    });
    c.on('timeout', () => {
      fail('Port ' + PORT + ' connection timed out');
      c.destroy();
      resolve(false);
    });
  });
}

// ─── 3. Read status snapshot ───
function checkStatusSnapshot() {
  title('3. Plugin Status Snapshot (' + STATUS_JSON + ')');
  try {
    if (!fs.existsSync(STATUS_JSON)) {
      warn('Status snapshot does not exist (first startup or old DLL has not generated it)');
      return;
    }
    const raw = fs.readFileSync(STATUS_JSON, 'utf8');
    const s = JSON.parse(raw);
    ok('Startup time: ' + (s.startup_time || 'unknown'));
    ok('Plugin version: ' + (s.plugin_version || 'unknown'));
    ok('Tool count: ' + (s.tool_count || '?'));
    ok('TCP: ' + (s.tcp_host || '?') + ':' + (s.tcp_port || '?'));
    ok('Menu registered: ' + (s.menu_registered ? 'Yes' : 'No'));
    info('NX version: ' + (s.nx_version || 'unknown'));
    info('.NET version: ' + (s.dotnet_version || 'unknown'));
  } catch (e) {
    warn('Failed to parse status snapshot: ' + e.message);
  }
}

// ─── 4. Query plugin info via TCP ───
function queryPluginInfo() {
  return new Promise((resolve) => {
    title('4. Plugin Info (TCP Query)');
    const c = new net.Socket();
    let buf = '';
    c.setTimeout(5000);
    c.connect(PORT, HOST, () => {
      c.write(JSON.stringify({ method: 'ping', id: '1' }) + '\n');
    });
    c.on('data', (d) => {
      buf += d.toString();
      if (buf.includes('\n')) {
        try {
          const r = JSON.parse(buf.trim().split('\n').pop());
          if (r.result) {
            const v = r.result;
            ok('Version: ' + (v.version || 'unknown'));
            ok('Tool count: ' + (v.tool_count || '?'));
            info('Timestamp: ' + (v.timestamp || 'N/A'));
          }
        } catch {}
        c.end();
        resolve(true);
      }
    });
    c.on('error', () => { fail('Failed to query plugin info'); resolve(false); });
    c.on('timeout', () => { fail('Query timed out'); resolve(false); });
  });
}

// ─── 5. Analyze startup logs ───
function findLogFiles(baseName) {
  const dir = LOG_DIR;
  const files = [];
  try {
    const allFiles = fs.readdirSync(dir);
    for (const f of allFiles) {
      if (f === baseName || new RegExp('^' + baseName.replace(/\./g, '\\.') + '\\.\\d+$').test(f)) {
        const fullPath = path.join(dir, f);
        try {
          const stat = fs.statSync(fullPath);
          files.push({ path: fullPath, mtime: stat.mtimeMs });
        } catch {}
      }
    }
  } catch {}
  files.sort((a, b) => b.mtime - a.mtime);
  return files.map(f => f.path);
}

function analyzeLogs() {
  const logFiles = findLogFiles('tcp_server.log');
  title('5. Startup Log Analysis (scanned ' + logFiles.length + ' log file(s))');

  if (logFiles.length === 0) {
    fail('No tcp_server.log files in log directory: ' + LOG_DIR);
    return;
  }

  info('Log directory: ' + LOG_DIR);
  logFiles.forEach((f, i) => {
    const short = path.basename(f);
    try {
      const stat = fs.statSync(f);
      const age = ((Date.now() - stat.mtimeMs) / 1000 / 60).toFixed(0);
      info(`  [${i}] ${short} (modified ${age} min ago, ${stat.size} bytes)`);
    } catch {
      info(`  [${i}] ${short}`);
    }
  });

  let content = null;
  let logFile = null;
  for (const f of logFiles) {
    try {
      const c = fs.readFileSync(f, 'utf8');
      if (c.includes('DoStartup')) { content = c; logFile = f; break; }
    } catch {}
  }

  if (content === null) {
    try {
      content = fs.readFileSync(logFiles[0], 'utf8');
      logFile = logFiles[0];
      warn('No DoStartup found in any log — using newest file: ' + path.basename(logFile));
    } catch {
      fail('Unable to read any log file');
      return;
    }
  }

  info('Using log: ' + path.basename(logFile));
  const lines = content.split('\n');

  let lastStartupIdx = -1;
  for (let i = lines.length - 1; i >= 0; i--) {
    if (lines[i].includes('DoStartup')) { lastStartupIdx = i; break; }
  }

  if (lastStartupIdx < 0) {
    warn('No DoStartup record found in log — skipping startup block analysis');
  }

  const block = lastStartupIdx >= 0 ? lines.slice(lastStartupIdx, lastStartupIdx + 30) : [];
  let hasMenuSuccess = false, hasMenuFail = false, hasTcpReady = false;
  let failReason = '';

  const tailSize = 80;
  const tail = lines.slice(-tailSize);
  for (const line of tail) {
    if (line.includes('全部菜单回调注册成功') || line.includes('All menu callbacks registered successfully')) hasMenuSuccess = true;
    if (line.includes('注册失败:') || line.includes('Registration failed:')) {
      hasMenuFail = true;
      const m = line.match(/(?:注册失败|Registration failed):\s*(.+)/);
      if (m) failReason = m[1].substring(0, 120);
    }
    if (line.includes('TCP 服务已启动') || line.includes('TCP server started')) hasTcpReady = true;
    if (line.includes('插件已就绪') || line.includes('Plugin is ready')) hasTcpReady = true;
  }

  if (hasMenuSuccess) ok('Menu registration: success');
  else if (hasMenuFail) fail('Menu registration: failed — ' + failReason);
  else warn('Menu registration: not detected (log may be incomplete)');

  if (hasTcpReady) ok('TCP service: started / ready');
  else warn('TCP service: not found in this startup log');

  for (const line of block) {
    if (line.includes('启动失败:') || line.includes('Startup FAIL:')) {
      const m = line.match(/(?:启动失败|Startup FAIL):\s*(.+)/);
      if (m) fail('Startup exception: ' + m[1].substring(0, 150));
    }
  }

  title('6. GetUI Diagnostics (early log)');
  const earlyFiles = findLogFiles('nx_plugin_early.log');
  let earlyContent = null;
  for (const f of earlyFiles) {
    try {
      const c = fs.readFileSync(f, 'utf8');
      if (c.includes('[GetUI]') || c.includes('[EARLY]')) {
        earlyContent = c;
        info('Using early log: ' + path.basename(f));
        break;
      }
    } catch {}
  }

  if (earlyContent) {
    const earlyLines = earlyContent.split('\n');
    let foundGetUI = false;
    for (let i = earlyLines.length - 1; i >= 0 && i >= earlyLines.length - 50; i--) {
      if (earlyLines[i].includes('[GetUI]')) {
        const msg = earlyLines[i].replace(/.*\[GetUI\]\s*/, '');
        if (msg.includes('FOUND')) ok(msg.trim());
        else if (msg.includes('NULL') || msg.includes('NOT FOUND')) fail(msg.trim());
        else if (msg.includes('EXCEPTION')) fail(msg.trim());
        else info(msg.trim());
        foundGetUI = true;
      }
    }
    if (!foundGetUI) {
      info('No [GetUI] diagnostic logs');
      for (let i = earlyLines.length - 1; i >= 0 && i >= earlyLines.length - 20; i--) {
        if (earlyLines[i].includes('Startup FAIL')) {
          fail(earlyLines[i].replace(/.*\[EARLY\]\s*/, '').substring(0, 200));
        }
      }
    }
  } else {
    info('No early log found');
  }

  title('7. Last 5 Runtime Log Entries');
  const lastLines = lines.slice(-10).filter(l => l.trim());
  lastLines.slice(-5).forEach(l => {
    const short = l.replace(/.*\[TcpServer\]\s*/, '[TCP] ')
                   .replace(/.*\[Plugin\]\s*/, '[PLUGIN] ')
                   .replace(/.*\[Dispatcher\]\s*/, '[DISP] ')
                   .replace(/.*\[Inspect\]\s*/, '[INSPECT] ')
                   .substring(0, 160);
    info(short);
  });
}

// ─── Watch mode ───
async function watchMode() {
  console.log(B + '⏳ Waiting for NX plugin to come online (checking every 5 seconds)...' + W);
  let attempts = 0;
  while (true) {
    attempts++;
    const c = new net.Socket();
    const online = await new Promise(resolve => {
      c.setTimeout(3000);
      c.connect(PORT, HOST, () => { c.end(); resolve(true); });
      c.on('error', () => { c.destroy(); resolve(false); });
      c.on('timeout', () => { c.destroy(); resolve(false); });
    });
    if (online) {
      process.stdout.write('\n');
      ok('NX plugin is online! (' + attempts + ' checks)');
      await analyzeLogs();
      process.exit(0);
    }
    process.stdout.write('.');
    await new Promise(r => setTimeout(r, 5000));
  }
}

// ─── Main ───
async function main() {
  const args = process.argv.slice(2);
  if (args.includes('--help') || args.includes('-h')) {
    console.log(`check-plugin-status — NX MCP Plugin diagnostics

Usage: node scripts/check-plugin-status.js [options]

Options:
  (none)      Full check (process+TCP+status snapshot+version+logs)
  --online    Check online status only (process+TCP)
  --log       View startup log analysis only
  --watch     Keep monitoring until plugin comes online

Checks: NX process, TCP port, status snapshot, plugin version, tool count, menu registration, startup logs`);
    return;
  }

  if (args.includes('--watch')) {
    return watchMode();
  }

  console.log(B + 'NX MCP Plugin Diagnostics' + W);
  console.log('='.repeat(50));

  if (args.includes('--log')) {
    analyzeLogs();
    finish();
    return;
  }

  if (args.includes('--online')) {
    checkNxProcess();
    const online = await checkTcp();
    if (online) await queryPluginInfo();
    else skip('Plugin version / tool count / menu registration — TCP is offline, these 3 checks cannot run (not "passed")');
    finish();
    return;
  }

  checkNxProcess();
  const online = await checkTcp();
  checkStatusSnapshot();
  if (online) await queryPluginInfo();
  else skip('Plugin version / tool count / menu registration — TCP is offline, these 3 checks cannot run (not "passed")');
  analyzeLogs();

  console.log('\n' + '='.repeat(50));
  finish();
}

function finish() {
  if (FAILURES > 0) {
    console.log(`${R}${FAILURES} check(s) failed${W} — exit code 1`);
    process.exit(1);
  }
  console.log(`${G}No failures${W} — exit code 0`);
  process.exit(0);
}

main().catch(err => { console.error(err); process.exit(1); });
