import assert from "node:assert/strict";
import test from "node:test";
import xtermPackage from "@xterm/xterm";
import serializePackage from "@xterm/addon-serialize";
const { Terminal } = xtermPackage;
const { SerializeAddon } = serializePackage;
import {
  RynXtermAdapter,
  XTERM_CHECKPOINT_FORMAT,
  createXtermSerializeCheckpointProvider,
  writeXterm,
} from "../src/xterm.js";

const checkpointTerminal = () => ({
  cols: 80,
  rows: 24,
  resetCalls: 0,
  resizeCalls: [],
  writes: [],
  reset() { this.resetCalls += 1; },
  resize(cols, rows) { this.cols = cols; this.rows = rows; this.resizeCalls.push([cols, rows]); },
  write(data, callback) { this.writes.push([...data]); callback?.(); },
});

test("writeXterm completes only from the per-write parser callback", async () => {
  let callback;
  let completed = false;
  const terminal = { write(_bytes, done) { callback = done; } };
  const pending = writeXterm(terminal, Uint8Array.from([1, 2])).then(() => { completed = true; });

  await Promise.resolve();
  assert.equal(completed, false);
  callback();
  await pending;
  assert.equal(completed, true);
});

test("checkpoint capture fails closed without stable-boundary attestation", async () => {
  const terminal = checkpointTerminal();
  const provider = createXtermSerializeCheckpointProvider({
    terminal,
    serializeAddon: { serialize: () => "screen" },
  });

  await assert.rejects(
    provider.capture({ offset: 12n }),
    (error) => error.code === "incompatible_checkpoint",
  );
  await assert.rejects(
    provider.capture({ offset: 12n, attestStableBoundary: async () => false }),
    (error) => error.code === "incompatible_checkpoint",
  );
});

test("checkpoint envelope pins format, bytes, offset and dimensions", async () => {
  const terminal = checkpointTerminal();
  const provider = createXtermSerializeCheckpointProvider({
    terminal,
    serializeAddon: { serialize: () => "\u001b[31mhéllo\u001b[0m" },
  });
  const checkpoint = await provider.capture({
    offset: 99n,
    attestStableBoundary: async (offset) => offset === 99n,
  });

  assert.equal(checkpoint.format, XTERM_CHECKPOINT_FORMAT);
  assert.equal(checkpoint.offset, 99n);
  assert.equal(checkpoint.cols, 80);
  assert.equal(checkpoint.rows, 24);
  assert.equal(Buffer.from(checkpoint.payloadBase64, "base64").toString("utf8"), "\u001b[31mhéllo\u001b[0m");
});

test("restore rejects incompatible formats before resetting xterm", async () => {
  const terminal = checkpointTerminal();
  const provider = createXtermSerializeCheckpointProvider({
    terminal,
    serializeAddon: { serialize: () => "" },
  });

  await assert.rejects(provider.restore({
    offset: 0n,
    format: "other",
    payloadBase64: "AA==",
    cols: 80,
    rows: 24,
  }), (error) => error.code === "incompatible_checkpoint");
  assert.equal(terminal.resetCalls, 0);
});

test("restore applies checkpoint dimensions before serialized VT bytes", async () => {
  const order = [];
  const terminal = checkpointTerminal();
  terminal.reset = () => order.push("reset");
  terminal.resize = (cols, rows) => order.push(`resize:${cols}x${rows}`);
  terminal.write = (data, callback) => { order.push(`write:${Buffer.from(data).toString("utf8")}`); callback(); };
  const provider = createXtermSerializeCheckpointProvider({
    terminal,
    serializeAddon: { serialize: () => "" },
  });

  await provider.restore({
    offset: 8n,
    format: XTERM_CHECKPOINT_FORMAT,
    payloadBase64: Buffer.from("snapshot").toString("base64"),
    cols: 132,
    rows: 40,
  });
  assert.deepEqual(order, ["reset", "resize:132x40", "write:snapshot"]);
});

test("real xterm provider rejects SGR mouse state omitted by SerializeAddon", async () => {
  const terminal = new Terminal({ cols: 20, rows: 10 });
  const serializeAddon = new SerializeAddon();
  terminal.loadAddon(serializeAddon);
  const client = {
    attachmentId: "attachment",
    withReadsPaused: (callback) => callback({ offset: 16n }),
    saveCheckpoint: async () => assert.fail("unsafe checkpoint must not be stored"),
  };
  const adapter = new RynXtermAdapter({
    terminal,
    client,
    requestResizeLease: false,
    checkpointProvider: createXtermSerializeCheckpointProvider({ terminal, serializeAddon }),
  });
  const sequence = new TextEncoder().encode("\u001b[?1000h\u001b[?1006h");
  await writeXterm(terminal, sequence);
  adapter.boundaryTracker.accept(sequence, 0n, 16n);

  await assert.rejects(adapter.captureCheckpoint(), (error) => error.code === "incompatible_checkpoint");
  terminal.dispose();
});

test("real xterm serializer restores supported bracketed paste mode", async () => {
  const source = new Terminal({ cols: 20, rows: 10, allowProposedApi: true });
  const restored = new Terminal({ cols: 20, rows: 10, allowProposedApi: true });
  const serializeAddon = new SerializeAddon();
  source.loadAddon(serializeAddon);
  const provider = createXtermSerializeCheckpointProvider({ terminal: source, serializeAddon });
  await writeXterm(source, new TextEncoder().encode("BASE\r\n\u001b[?2004h"));
  const checkpoint = await provider.capture({ offset: 15n, attestStableBoundary: () => true });
  const restoreProvider = createXtermSerializeCheckpointProvider({ terminal: restored, serializeAddon: { serialize: () => "" } });
  await restoreProvider.restore(checkpoint);

  assert.equal(source.modes.bracketedPasteMode, true);
  assert.equal(restored.modes.bracketedPasteMode, true);
  terminalDispose(source, restored);
});

function terminalDispose(...terminals) {
  for (const terminal of terminals) terminal.dispose();
}

test("successful lease claim sends unchanged fitted dimensions to the host", async () => {
  const previous = installLayoutGlobals();
  const resizeCalls = [];
  const client = fakeResizeClient({ resizeCalls });
  const terminal = fakeAdapterTerminal();
  const adapter = new RynXtermAdapter({ terminal, client, fitAddon: { fit() {} } });
  adapter.element = visibleElement();

  assert.equal(await adapter.claimResize(), true);
  assert.deepEqual(resizeCalls, [["lease-1", 80, 24]]);
  await adapter.dispose();
  previous();
});

test("stale in-flight claim releases its host lease without assigning it", async () => {
  const previous = installLayoutGlobals();
  let resolveClaim;
  const released = [];
  const client = fakeResizeClient({
    claimResize: () => new Promise((resolve) => { resolveClaim = resolve; }),
    released,
  });
  const adapter = new RynXtermAdapter({ terminal: fakeAdapterTerminal(), client, fitAddon: { fit() {} } });
  adapter.element = visibleElement();

  const pending = adapter.claimResize();
  await adapter.releaseResize();
  resolveClaim({ leaseId: "late", leaseGeneration: "2" });
  assert.equal(await pending, false);
  assert.equal(adapter.resizeLease, null);
  assert.deepEqual(released, ["late"]);
  await adapter.dispose();
  previous();
});

test("mirror fit does not locally reflow or claim host dimensions", async () => {
  const previous = installLayoutGlobals();
  let fits = 0;
  const client = fakeResizeClient({ mode: "mirror" });
  const adapter = new RynXtermAdapter({ terminal: fakeAdapterTerminal(), client, fitAddon: { fit() { fits += 1; } } });
  adapter.element = visibleElement();

  assert.equal(await adapter.fit(), false);
  assert.equal(fits, 0);
  await adapter.dispose();
  previous();
});

function fakeResizeClient(overrides = {}) {
  return {
    attachmentId: "attachment",
    mode: overrides.mode ?? "controller",
    claimResize: overrides.claimResize ?? (async () => ({ leaseId: "lease-1", leaseGeneration: "1" })),
    resize: async (lease, cols, rows) => overrides.resizeCalls?.push([lease.leaseId, cols, rows]),
    releaseResize: async (lease) => overrides.released?.push(lease.leaseId),
    dispose: async () => {},
    writeText: async () => {},
    writeBytes: async () => {},
  };
}

function fakeAdapterTerminal() {
  return {
    cols: 80, rows: 24,
    onData: () => ({ dispose() {} }), onBinary: () => ({ dispose() {} }),
    refresh() {}, dispose() {},
  };
}

function visibleElement() {
  return { isConnected: true, getBoundingClientRect: () => ({ width: 800, height: 400 }) };
}

function installLayoutGlobals() {
  const priorDocument = globalThis.document;
  const priorStyle = globalThis.getComputedStyle;
  globalThis.document = { visibilityState: "visible", hasFocus: () => true };
  globalThis.getComputedStyle = () => ({ display: "block", visibility: "visible" });
  return () => { globalThis.document = priorDocument; globalThis.getComputedStyle = priorStyle; };
}

test("checkpoint restore suppresses query replies but live queries still forward", async () => {
  const terminal = new Terminal({ cols: 20, rows: 10 });
  const writes = [];
  const client = fakeResizeClient();
  client.writeText = async (value) => writes.push(value);
  const adapter = new RynXtermAdapter({
    terminal,
    client,
    requestResizeLease: false,
    checkpointProvider: {
      format: "test",
      capture: async () => assert.fail("not used"),
      restore: async () => writeXterm(terminal, new TextEncoder().encode("\u001b[6n")),
    },
  });

  await adapter._restoreCheckpoint({ offset: 0n, format: "test", payloadBase64: "AA==", cols: 20, rows: 10 });
  assert.deepEqual(writes, []);
  await writeXterm(terminal, new TextEncoder().encode("\u001b[6n"));
  await Promise.resolve();
  assert.equal(writes.length, 1);
  assert.match(writes[0], /^\u001b\[/);
  terminal.dispose();
});

test("restore failure always reenables live terminal input", async () => {
  const terminal = new Terminal({ cols: 20, rows: 10 });
  const writes = [];
  const client = fakeResizeClient();
  client.writeText = async (value) => writes.push(value);
  const adapter = new RynXtermAdapter({
    terminal,
    client,
    requestResizeLease: false,
    checkpointProvider: { format: "test", capture: async () => ({}), restore: async () => { throw new Error("restore failed"); } },
  });

  await assert.rejects(adapter._restoreCheckpoint({ offset: 0n }), /restore failed/);
  await writeXterm(terminal, new TextEncoder().encode("\u001b[6n"));
  await Promise.resolve();
  assert.equal(writes.length, 1);
  terminal.dispose();
});

test("adapter restores and finishes owner resize before reading live bytes", async () => {
  const order = [];
  let finishResize;
  let resizing;
  const entered = new Promise((resolve) => { resizing = resolve; });
  const terminal = fakeAdapterTerminal();
  terminal.write = (_data, done) => { order.push("write"); done(); };
  const adapter = new RynXtermAdapter({
    terminal, sessionId: "session", accessToken: "token",
    checkpointProvider: { restore: async () => { order.push("restore"); } },
    bridge: { invoke: async (command) => {
      if (command === "terminal.attach") return { attachmentId: "a", generation: "1", state: "streaming", nextOffset: "0", retainedStart: "0", retainedEnd: "1", checkpoint: { offset: "0", format: "test", payloadBase64: "QQ==", cols: 80, rows: 24 } };
      if (command === "terminal.read") { order.push("read"); return { state: "exited", startOffset: "0", endOffset: "1", dataBase64: "QQ==", processExited: true, finalOffset: "1", error: null }; }
      return { ok: true };
    } },
  });
  adapter.open = async () => {};
  adapter._isActiveEligibleView = () => true;
  adapter.claimResize = async () => { order.push("resize"); resizing(); await new Promise((resolve) => { finishResize = resolve; }); return true; };
  const attaching = adapter.attach();
  await entered;
  assert.deepEqual(order, ["restore", "resize"]);
  finishResize();
  await attaching;
  await adapter.client.closed();
  assert.deepEqual(order, ["restore", "resize", "read", "write"]);
  await adapter.dispose();
});

test("failed WebGL activation disposes partially initialized addon", async () => {
  const restore = installLayoutGlobals();
  globalThis.document.createElement = () => ({ getContext: () => ({}) });
  let disposals = 0;
  const terminal = fakeAdapterTerminal();
  terminal.loadAddon = () => { throw new Error("activation failed"); };
  const adapter = new RynXtermAdapter({ terminal, client: fakeResizeClient(), onError() {} });
  try {
    assert.equal(adapter.useWebgl(() => ({ dispose() { disposals++; } })), false);
    assert.equal(disposals, 1);
    await adapter.dispose();
    assert.equal(disposals, 1);
  } finally {
    restore();
  }
});

test("checkpoint plus split live tail matches reference through resize", async () => {
  const source = new Terminal({ cols: 20, rows: 10, allowProposedApi: true });
  const restored = new Terminal({ cols: 40, rows: 12, allowProposedApi: true });
  const serializer = new SerializeAddon();
  source.loadAddon(serializer);
  const provider = createXtermSerializeCheckpointProvider({ terminal: source, serializeAddon: serializer });
  const restoreProvider = createXtermSerializeCheckpointProvider({ terminal: restored, serializeAddon: { serialize: () => "" } });
  try {
    const prefix = new TextEncoder().encode("one\r\ntwo\r\n\u001b[31mred\u001b[0m\u001b[?2004h");
    await writeXterm(source, prefix);
    const checkpoint = await provider.capture({ offset: BigInt(prefix.length), attestStableBoundary: () => true });
    await restoreProvider.restore(checkpoint);
    source.resize(16, 8);
    restored.resize(16, 8);
    const tail = new TextEncoder().encode("\r\nUnicode: hé世界\r\n\u001b[32mTAIL\u001b[0m");
    for (const byte of tail) {
      await writeXterm(source, Uint8Array.of(byte));
      await writeXterm(restored, Uint8Array.of(byte));
    }
    const snapshot = (terminal) => {
      const buffer = terminal.buffer.active;
      return { cursor: [buffer.cursorX, buffer.cursorY], base: buffer.baseY,
        lines: Array.from({ length: buffer.length }, (_, y) => {
          const line = buffer.getLine(y);
          return { wrapped: line.isWrapped, cells: Array.from({ length: terminal.cols }, (_, x) => {
            const cell = line.getCell(x);
            return [cell.getChars(), cell.getWidth(), cell.getFgColorMode(), cell.getFgColor(), cell.getBgColorMode(), cell.getBgColor()];
          }) };
        }), paste: terminal.modes.bracketedPasteMode };
    };
    assert.deepEqual(snapshot(restored), snapshot(source));
  } finally {
    terminalDispose(source, restored);
  }
});

test("alternate-screen checkpoint retains normal buffer on return", async () => {
  const source = new Terminal({ cols: 20, rows: 10, allowProposedApi: true });
  const restored = new Terminal({ cols: 20, rows: 10, allowProposedApi: true });
  const serializer = new SerializeAddon();
  source.loadAddon(serializer);
  try {
    await writeXterm(source, new TextEncoder().encode("NORMAL\r\nPROMPT>\u001b[?1049hALT SCREEN"));
    const provider = createXtermSerializeCheckpointProvider({ terminal: source, serializeAddon: serializer });
    const checkpoint = await provider.capture({ offset: 1n, attestStableBoundary: () => true });
    await createXtermSerializeCheckpointProvider({ terminal: restored, serializeAddon: { serialize: () => "" } }).restore(checkpoint);
    assert.equal(restored.buffer.active.type, "alternate");
    assert.equal(restored.buffer.active.getLine(0).translateToString(true), source.buffer.active.getLine(0).translateToString(true));
    await writeXterm(source, new TextEncoder().encode("\u001b[?1049lAFTER"));
    await writeXterm(restored, new TextEncoder().encode("\u001b[?1049lAFTER"));
    assert.equal(restored.buffer.active.type, "normal");
    assert.deepEqual(Array.from({length:10},(_,i)=>restored.buffer.active.getLine(i).translateToString(true)),
      Array.from({length:10},(_,i)=>source.buffer.active.getLine(i).translateToString(true)));
    assert.equal(restored.buffer.active.cursorX, source.buffer.active.cursorX);
    assert.equal(restored.buffer.active.cursorY, source.buffer.active.cursorY);
  } finally { terminalDispose(source, restored); }
});

test("late stale resize cannot clear a newer resize lease", async () => {
  const restore = installLayoutGlobals();
  let failOld;
  let claims = 0;
  const sent = [];
  const client = fakeResizeClient({ claimResize: async () => ({leaseId: String(++claims),leaseGeneration:String(claims)}) });
  client.resize = async (lease) => {
    sent.push(lease.leaseId);
    if (lease.leaseId === "1") await new Promise((_resolve,reject)=>{failOld=reject;});
  };
  const adapter = new RynXtermAdapter({terminal:fakeAdapterTerminal(),client,fitAddon:{fit(){}}});
  adapter.element=visibleElement();
  try {
    const first=adapter.claimResize();
    while(!failOld) await Promise.resolve();
    await adapter.claimResize();
    const {RynTerminalError}=await import('../src/client.js');
    failOld(new RynTerminalError('stale_generation','old lease'));
    await first;
    assert.equal(adapter.resizeLease.leaseId,'2');
    assert.deepEqual(sent,['1','2']);
    await adapter.dispose();
  } finally {restore();}
});

test("mirror adapters never forward keyboard or terminal response bytes", () => {
  const handlers = [];
  const terminal = fakeAdapterTerminal();
  terminal.onData = terminal.onBinary = handler => {
    handlers.push(handler);
    return { dispose() {} };
  };
  let writes = 0;
  const client = fakeResizeClient({mode:"mirror"});
  client.writeText = client.writeBytes = async () => { writes++; };
  new RynXtermAdapter({terminal,client});
  for (const handler of handlers) handler("response");
  assert.equal(writes,0);
});

test("input before attachment reports failure without escaping the xterm callback", async () => {
  const handlers = [];
  const terminal = fakeAdapterTerminal();
  terminal.onData = terminal.onBinary = handler => {
    handlers.push(handler);
    return { dispose() {} };
  };
  const errors = [];
  const adapter = new RynXtermAdapter({terminal,bridge:{async invoke(){assert.fail("unexpected IPC");}},onError:error=>errors.push(error)});
  for (const handler of handlers) await handler("early input");
  assert.equal(adapter.client.attachmentId,null);
  assert.equal(errors.length,2);
  await adapter.dispose();
});

test("renderer notification failure cannot prevent transport and terminal disposal", async () => {
  const terminal = fakeAdapterTerminal();
  const client = fakeResizeClient();
  let detached = false;
  let destroyed = false;
  client.dispose = async () => { detached = true; };
  terminal.dispose = () => { destroyed = true; };
  const failure = new Error("renderer callback failed");
  const adapter = new RynXtermAdapter({terminal,client,onRendererChange(){throw failure;}});
  await assert.rejects(adapter.dispose(),error=>error===failure);
  assert.equal(detached,true);
  assert.equal(destroyed,true);
});

test("renderer teardown is reentrant without disposing an addon twice", async () => {
  const terminal = fakeAdapterTerminal();
  terminal.loadAddon = () => {};
  const adapter = new RynXtermAdapter({ terminal, client: fakeResizeClient() });
  let disposals = 0;
  adapter.useWebgl(() => ({
    dispose() {
      disposals++;
      if (disposals === 1) adapter.disableWebgl("nested");
    },
  }));
  adapter.disableWebgl();
  assert.equal(disposals, 1);
  await adapter.dispose();
});

test("an unfocused lease holder cannot send host resizes", async () => {
  const restore = installLayoutGlobals();
  const calls = [];
  const terminal = fakeAdapterTerminal();
  const adapter = new RynXtermAdapter({
    terminal, element: visibleElement(), client: fakeResizeClient({ resizeCalls: calls }),
    fitAddon: { fit() { terminal.cols++; } },
  });
  try {
    await adapter.claimResize();
    const before = calls.length;
    document.hasFocus = () => false;
    await adapter.fit();
    assert.equal(calls.length, before);
  } finally {
    await adapter.dispose();
    restore();
  }
});

test("window focus resynchronizes only the focused terminal owner", async () => {
  const restore = installLayoutGlobals();
  const previousWindow = globalThis.window;
  const windowEvents = new EventTarget();
  globalThis.window = windowEvents;
  document.addEventListener = () => {};
  document.removeEventListener = () => {};
  const element = Object.assign(new EventTarget(), visibleElement());
  const active = {};
  element.contains = candidate => candidate === active;
  document.activeElement = active;
  const calls = [];
  const terminal = fakeAdapterTerminal();
  terminal.open = () => {};
  const adapter = new RynXtermAdapter({ terminal, element,
    client: fakeResizeClient({ resizeCalls: calls }), fitAddon: { fit() {} } });
  try {
    await adapter.open();
    await adapter.claimResize();
    const before = calls.length;
    document.hasFocus = () => false;
    terminal.cols = 100;
    await adapter.fit();
    document.hasFocus = () => true;
    windowEvents.dispatchEvent(new Event("focus"));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.deepEqual(calls.slice(before), [["lease-1", 100, 24]]);
    document.activeElement = {};
    windowEvents.dispatchEvent(new Event("focus"));
    await new Promise(resolve => setTimeout(resolve, 0));
    assert.equal(calls.length, before + 1);
  } finally {
    await adapter.dispose();
    globalThis.window = previousWindow;
    restore();
  }
});
