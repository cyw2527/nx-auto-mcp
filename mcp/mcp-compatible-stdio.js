"use strict";
/**
 * mcp-compatible-stdio.js — Shared MCP stdio transport module
 *
 * Provides: dual framing (Content-Length / newline auto-detect + respond in same format)
 *           10MB read buffer limit, Content-Length pre-validation, iterative parsing to prevent stack overflow
 *           closed-transport guard, stdout sentinel (non-MCP writes → stderr)
 *
 * Usage:
 *   const { startCompatibleStdio, installStdoutSentinel } = require('./mcp-compatible-stdio');
 *   installStdoutSentinel(); // optional, intercept non-MCP stdout
 *   const transport = startCompatibleStdio({
 *     onmessage: (msg) => ...,
 *     onerror: (err) => ...,
 *     onclose: () => ...,
 *     send: (msg) => Promise<void>  // implemented internally, caller just invokes
 *   });
 *   transport.send(message); // send JSON-RPC message
 */
const process = require("process");

const MAX_BUFFER_SIZE = 10 * 1024 * 1024; // 10 MB
const MAX_CONTENT_LENGTH = MAX_BUFFER_SIZE;
const MAX_SENTINEL_REDIRECTS = 10;
const SENTINEL_TRUNCATE_BYTES = 200;

// ─── stdout sentinel ──────────────────────────────────────────────────
const realStdoutWrite = process.stdout.write.bind(process.stdout);
const realStderrWrite = process.stderr.write.bind(process.stderr);

let sentinelActive = false;
let redirectCount = 0;

/**
 * Install a stdout sentinel that redirects non-MCP writes to stderr.
 * Tagged writes (via withMcpWrite) pass through to real stdout.
 * All other stdout writes are truncated and prefixed with [mcp:stdout-redirect].
 */
function installStdoutSentinel() {
  if (sentinelActive) return;
  sentinelActive = true;

  // Capture the real write before any further monkey-patching
  const capturedWrite = realStdoutWrite;

  // Tag stack: when inMcpWrite > 0, writes go to real stdout
  let inMcpWrite = 0;

  process.stdout.write = function sentinelWrite(chunk, encoding, callback) {
    // If this is an MCP-tagged write, pass through
    if (inMcpWrite > 0) {
      return capturedWrite.call(process.stdout, chunk, encoding, callback);
    }

    // Non-MCP write → redirect to stderr with truncation + limit
    redirectCount++;
    if (redirectCount > MAX_SENTINEL_REDIRECTS) {
      // Suppress after limit — just discard silently
      if (typeof callback === "function") callback();
      return true;
    }

    let msg = typeof chunk === "string" ? chunk : Buffer.from(chunk).toString("utf8");
    if (Buffer.byteLength(msg, "utf8") > SENTINEL_TRUNCATE_BYTES) {
      msg = Buffer.from(msg, "utf8").subarray(0, SENTINEL_TRUNCATE_BYTES).toString("utf8") + "...(truncated)";
    }
    realStderrWrite.call(process.stderr, `[mcp:stdout-redirect] ${msg}\n`);
    if (typeof callback === "function") callback();
    return true;
  }.bind(process.stdout);

  /**
   * Execute a function with MCP write context — writes inside go to real stdout.
   * Returns the return value of fn.
   */
  global.__mcpWithWrite = function withMcpWrite(fn) {
    inMcpWrite++;
    try {
      return fn();
    } finally {
      inMcpWrite--;
    }
  };

  // Flush summary on exit
  process.on("exit", () => {
    if (redirectCount > 0) {
      realStderrWrite.call(process.stderr, `[mcp:sentinel] ${redirectCount} stdout write(s) redirected to stderr during session.\n`);
    }
  });
}

// ─── Framing detection ────────────────────────────────────────────────
function looksLikeContentLength(buffer) {
  if (buffer.length < 14) return false;
  const probe = buffer.toString("utf8", 0, Math.min(buffer.length, 32));
  return /^content-length\s*:/i.test(probe);
}

function findHeaderEnd(buffer) {
  const crlfEnd = buffer.indexOf("\r\n\r\n");
  if (crlfEnd !== -1) return { index: crlfEnd, separatorLength: 4 };
  const lfEnd = buffer.indexOf("\n\n");
  if (lfEnd !== -1) return { index: lfEnd, separatorLength: 2 };
  return null;
}

// ─── Transport ────────────────────────────────────────────────────────
/**
 * Create a compatible stdio transport with dual-framing support.
 * @param {{onmessage: Function, onerror: Function, onclose: Function}} handlers
 * @returns {{send: Function, close: Function, framing: Function}}
 */
function startCompatibleStdio(handlers) {
  let readBuffer = undefined;
  let framing = null; // 'content-length' | 'newline' | null
  let started = false;
  let closed = false;

  const { onmessage, onerror, onclose } = handlers;

  // ── Read parsing ──
  function deserializeMessage(raw) {
    return JSON.parse(raw); // JSON-RPC parse; caller validates schema
  }

  function readContentLengthMessage() {
    if (!readBuffer) return null;
    const header = findHeaderEnd(readBuffer);
    if (header === null) return null;

    const headerText = readBuffer.toString("utf8", 0, header.index).replace(/\r\n/g, "\n").replace(/\r/g, "\n");
    const match = headerText.match(/(?:^|\n)content-length\s*:\s*(\d+)/i);
    if (!match) {
      readBuffer = undefined;
      framing = null;
      throw new Error("Missing Content-Length header from MCP client");
    }

    const contentLength = parseInt(match[1], 10);
    if (!Number.isFinite(contentLength) || contentLength < 0) {
      readBuffer = undefined;
      framing = null;
      throw new Error("Invalid Content-Length header from MCP client");
    }
    if (contentLength > MAX_CONTENT_LENGTH) {
      readBuffer = undefined;
      framing = null;
      throw new Error(`Content-Length ${contentLength} exceeds maximum allowed size (${MAX_CONTENT_LENGTH} bytes)`);
    }

    const bodyStart = header.index + header.separatorLength;
    const bodyEnd = bodyStart + contentLength;
    if (readBuffer.length < bodyEnd) return null;

    const body = readBuffer.toString("utf8", bodyStart, bodyEnd);
    readBuffer = readBuffer.subarray(bodyEnd);
    return deserializeMessage(body);
  }

  function readNewlineMessage() {
    if (!readBuffer) return null;
    while (true) {
      const newlineIndex = readBuffer.indexOf("\n");
      if (newlineIndex === -1) return null;
      const line = readBuffer.toString("utf8", 0, newlineIndex).replace(/\r$/, "");
      readBuffer = readBuffer.subarray(newlineIndex + 1);
      if (line.trim().length === 0) continue; // skip empty lines (iterative, not recursive)
      return deserializeMessage(line);
    }
  }

  function readMessage() {
    if (!readBuffer || readBuffer.length === 0) return null;
    if (framing === null) {
      // Auto-detect framing on first message
      const firstByte = readBuffer[0];
      if (firstByte === 0x7b || firstByte === 0x5b) { // '{' or '['
        framing = "newline";
      } else if (looksLikeContentLength(readBuffer)) {
        framing = "content-length";
      } else {
        return null; // not enough data to detect yet
      }
    }
    return framing === "content-length" ? readContentLengthMessage() : readNewlineMessage();
  }

  function processReadBuffer() {
    while (true) {
      try {
        const message = readMessage();
        if (message === null) break;
        onmessage(message);
      } catch (error) {
        onerror(error);
        break;
      }
    }
  }

  function onData(chunk) {
    if (closed) return;
    readBuffer = readBuffer ? Buffer.concat([readBuffer, chunk]) : chunk;
    if (readBuffer.length > MAX_BUFFER_SIZE) {
      onerror(new Error(`Read buffer exceeded maximum size (${MAX_BUFFER_SIZE} bytes)`));
      readBuffer = undefined;
      framing = null;
      return;
    }
    processReadBuffer();
  }

  function close() {
    if (closed) return;
    closed = true;
    started = false;
    readBuffer = undefined;
    process.stdin.off("data", onData);
    process.stdin.off("error", onError);
    process.stdin.off("end", close);
    process.stdin.off("close", close);
    onclose();
  }

  function onError(err) {
    onerror(err);
  }

  // ── Send (uses current framing to respond in same format) ──
  function send(message) {
    return new Promise((resolve, reject) => {
      if (!started) {
        reject(new Error("Transport is closed"));
        return;
      }

      const payload = framing === "newline"
        ? JSON.stringify(message) + "\n"
        : (() => {
            const body = JSON.stringify(message);
            return `Content-Length: ${Buffer.byteLength(body, "utf8")}\r\n\r\n${body}`;
          })();

      const writeFn = global.__mcpWithWrite
        ? () => global.__mcpWithWrite(() => process.stdout.write(payload))
        : () => process.stdout.write(payload);

      const onErrorWrite = (err) => {
        process.stdout.removeListener("error", onErrorWrite);
        reject(err);
      };
      process.stdout.on("error", onErrorWrite);

      const ok = writeFn();
      if (ok) {
        process.stdout.removeListener("error", onErrorWrite);
        resolve();
      } else {
        process.stdout.once("drain", () => {
          process.stdout.removeListener("error", onErrorWrite);
          resolve();
        });
      }
    });
  }

  // ── Start ──
  function start() {
    if (started) throw new Error("Transport already started!");
    if (closed) throw new Error("Transport has been permanently closed");

    started = true;
    // Do NOT call setEncoding("utf8") — we need raw Buffers for reliable framing detection.
    // With setEncoding, chunks become strings and Buffer.concat([undefined, string]) produces garbage.
    // Register listeners BEFORE checking readableEnded.
    // In pipe mode, echo writes data and closes stdin before start() runs;
    // readableEnded would already be true and data would be lost.
    process.stdin.on("data", onData);
    process.stdin.on("error", onError);
    // stdin end/close → notify via onclose, but DON'T auto-close the transport.
    // The caller (proxy) may still need to send pending responses to stdout.
    // The caller is responsible for calling transport.close() when ready.
    process.stdin.on("end", () => { if (!closed) onclose(); });
    process.stdin.on("close", () => { if (!closed) onclose(); });

    // If stdin was already closed (parent died before we registered), close now.
    // The end/close event was already emitted and won't fire again.
    if (process.stdin.readableEnded || process.stdin.destroyed) {
      close();
    }
  }

  start();

  return {
    send,
    close,
    get framing() { return framing; },
    get started() { return started; },
    get closed() { return closed; },
  };
}

module.exports = { startCompatibleStdio, installStdoutSentinel, MAX_BUFFER_SIZE };
