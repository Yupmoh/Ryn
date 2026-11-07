import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { SerializeAddon } from "@xterm/addon-serialize";
import { WebLinksAddon } from "@xterm/addon-web-links";
import { WebglAddon } from "@xterm/addon-webgl";
import {
  RynXtermAdapter,
  createXtermSerializeCheckpointProvider,
} from "@ryn/terminal/xterm";
import "./style.css";

const stage = document.querySelector("#terminal");
const state = document.querySelector("#state");
const renderer = document.querySelector("#renderer");
const metrics = document.querySelector("#metrics");
const stressButton = document.querySelector("#stress");
const checkpointButton = document.querySelector("#checkpoint");
const terminateButton = document.querySelector("#terminate");

let adapter;
const terminal = new Terminal({
  cursorBlink: true,
  cursorStyle: "bar",
  fontFamily: '"SFMono-Regular", "Cascadia Mono", "Liberation Mono", monospace',
  fontSize: 13,
  lineHeight: 1.18,
  scrollback: 10_000,
  minimumContrastRatio: 4.5,
  theme: {
    background: "#0d0f0e",
    foreground: "#eee2ca",
    cursor: "#e8a33d",
    cursorAccent: "#0d0f0e",
    selectionBackground: "#72542f88",
    black: "#15120f",
    red: "#e36a4a",
    green: "#8fc48a",
    yellow: "#e8a33d",
    blue: "#7ca6b8",
    magenta: "#be8ba7",
    cyan: "#73b6ac",
    white: "#f5e9d2",
  },
  linkHandler: {
    activate: (event, uri) => { void adapter.openLink(uri, event); },
  },
  // Host-information CSI features remain disabled. They require explicit policy.
  windowOptions: {},
});
const fitAddon = new FitAddon();
const serializeAddon = new SerializeAddon();
terminal.loadAddon(fitAddon);
terminal.loadAddon(serializeAddon);
terminal.loadAddon(new WebLinksAddon((event, uri) => {
  void adapter.openLink(uri, event);
}));

const checkpointProvider = createXtermSerializeCheckpointProvider({ terminal, serializeAddon });
adapter = new RynXtermAdapter({
  bridge: window.__ryn,
  terminal,
  fitAddon,
  checkpointProvider,
  mode: "controller",
  requestResizeLease: true,
  linkPolicy: async (url) => {
    // This reference app intentionally demonstrates validation without granting
    // a browser-opening capability. A real app may route the URL to a host policy.
    terminal.write(`\r\n\x1b[33mLink blocked by sample policy: ${url.href}\x1b[0m\r\n`);
    return false;
  },
  onStateChange: (next) => setState(next, next === "streaming" ? "ok" : "waiting"),
  onRendererChange: (kind, reason) => {
    renderer.textContent = `Renderer: ${kind}${reason === "active" ? "" : ` (${reason})`}`;
  },
  onError: (error) => {
    const message = error instanceof Error ? error.message : String(error);
    setState(message, "error");
  },
});

let metricsTimer;
let closing = false;

async function start() {
  if (!window.__ryn?.invoke) throw new Error("Ryn bridge unavailable. Launch with dotnet run --project samples/TerminalApp");
  await adapter.open(stage);
  const shell = navigator.userAgent.includes("Windows") ? "cmd.exe" : "/bin/bash";
  await adapter.create(shell, [], {
    cols: terminal.cols,
    rows: terminal.rows,
    retentionMode: "streaming",
  });
  await adapter.attach(stage);
  terminal.focus();
  adapter.useWebgl(() => new WebglAddon());
  metricsTimer = setInterval(refreshMetrics, 1000);
  await refreshMetrics();
}

async function refreshMetrics() {
  if (closing || adapter.client.attachmentId === null) return;
  try {
    const value = await adapter.client.status();
    metrics.textContent = [
      `${value.state}`,
      `offset ${value.acknowledgedOffset}/${value.retainedEnd}`,
      `credit ${formatBytes(value.outstandingBytes)}/${formatBytes(value.creditBytes)}`,
      `retained ${formatBytes(value.retainedBytes)}`,
      `ACK ${value.ackLatencyMilliseconds == null ? "pending" : `${value.ackLatencyMilliseconds.toFixed(1)} ms`}`,
      `paused ${value.pauseMilliseconds} ms`,
      value.needsCheckpoint ? "checkpoint needed" : "checkpoint current",
      value.outputTruncated ? "output truncated" : "complete",
    ].join(" · ");
  } catch (error) {
    metrics.textContent = `metrics unavailable: ${error instanceof Error ? error.message : String(error)}`;
  }
}

stressButton.addEventListener("click", () => {
  const command = navigator.userAgent.includes("Windows")
    ? "powershell -NoProfile -Command \"$s='RYN-STRESS-'; while (($s.Length * 2) -lt 2097152) {$s += $s}; [Console]::Out.Write($s.Substring(0,1048576))\"\r"
    : "LC_ALL=C head -c 2097152 /dev/zero | tr '\\0' 'R'; printf '\\n'\r";
  void adapter.client.writeText(command).catch(reportActionError);
});

checkpointButton.addEventListener("click", async () => {
  checkpointButton.disabled = true;
  try {
    const checkpoint = await adapter.captureCheckpoint();
    metrics.textContent = `checkpoint accepted at ${checkpoint.offset}`;
  } catch (error) {
    reportActionError(error);
  } finally {
    checkpointButton.disabled = false;
  }
});

terminateButton.addEventListener("click", () => {
  void adapter.client.terminate().catch(reportActionError);
});

window.addEventListener("pagehide", () => { void shutdown(); }, { once: true });
window.addEventListener("beforeunload", () => { void shutdown(); }, { once: true });

async function shutdown() {
  if (closing) return;
  closing = true;
  clearInterval(metricsTimer);
  await adapter.dispose();
}

function setState(text, tone) {
  state.textContent = String(text).replaceAll("_", " ");
  state.dataset.tone = tone;
}

function formatBytes(value) {
  const bytes = Number(value ?? 0);
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(0)} KiB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MiB`;
}

function reportActionError(error) {
  setState(error instanceof Error ? error.message : String(error), "error");
}

start().catch(reportActionError);
