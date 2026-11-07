import { decodeBase64, encodeBase64 } from "./base64.js";

const DEFAULT_CREDIT_BYTES = 256 * 1024;
const DEFAULT_MAX_READ_BYTES = 64 * 1024;
const DEFAULT_READ_WAIT_MS = 20_000;
const DEFAULT_ACK_THRESHOLD_BYTES = 128 * 1024;
const DEFAULT_ACK_DELAY_MS = 100;
const MAX_INPUT_BYTES = 8 * 1024 * 1024;
const MAX_INPUT_ITEMS = 64;
const TERMINAL_ERRORS = new Set([
  "unknown_session",
  "unauthorized_attachment",
  "stale_generation",
  "invalid_offset",
  "budget_exceeded",
  "history_unavailable",
  "incompatible_checkpoint",
  "terminal_exited",
  "cancelled",
  "invalid_request",
  "resync_required",
]);

export class RynTerminalError extends Error {
  /** @param {string} code @param {string} [message] @param {unknown} [cause] */
  constructor(code, message = code, cause) {
    super(message, cause === undefined ? undefined : { cause });
    this.name = "RynTerminalError";
    this.code = code;
  }
}

/**
 * Pull-based, decoded-byte-counted terminal client. The consumer promise is the
 * consumption boundary: bytes are never acknowledged before it resolves.
 */
export class RynTerminalClient {
  /** @param {RynTerminalClientOptions} options */
  constructor(options) {
    if (!options?.bridge || typeof options.bridge.invoke !== "function") {
      throw new TypeError("A Ryn bridge with invoke(command, args) is required");
    }
    if (!options.consumer || typeof options.consumer.write !== "function") {
      throw new TypeError("A terminal consumer with write(bytes, context) is required");
    }

    this._bridge = options.bridge;
    this._consumer = options.consumer;
    this._onStateChange = options.onStateChange ?? (() => {});
    this._onError = options.onError ?? (() => {});
    this._creditBytes = positiveInteger(options.creditBytes, DEFAULT_CREDIT_BYTES, "creditBytes");
    this._maxReadBytes = positiveInteger(options.maxReadBytes, DEFAULT_MAX_READ_BYTES, "maxReadBytes");
    this._readWaitMilliseconds = nonNegativeInteger(options.readWaitMilliseconds, DEFAULT_READ_WAIT_MS, "readWaitMilliseconds");
    this._ackThresholdBytes = positiveInteger(options.ackThresholdBytes, DEFAULT_ACK_THRESHOLD_BYTES, "ackThresholdBytes");
    this._ackDelayMilliseconds = nonNegativeInteger(options.ackDelayMilliseconds, DEFAULT_ACK_DELAY_MS, "ackDelayMilliseconds");
    this._sessionId = options.sessionId ?? null;
    this._accessToken = options.accessToken ?? null;
    this._cursor = parseNullableOffset(options.cursor ?? null, "cursor");
    this._mode = options.mode ?? "controller";
    if (this._mode !== "controller" && this._mode !== "mirror") throw new TypeError("mode must be controller or mirror");

    this._attachmentId = null;
    this._generation = null;
    this._nextOffset = this._cursor ?? 0n;
    this._consumedOffset = this._nextOffset;
    this._acknowledgedOffset = this._nextOffset;
    this._ackTimer = null;
    this._ackChain = Promise.resolve();
    this._readTask = null;
    this._attachTask = null;
    this._writeChain = Promise.resolve();
    this._queuedInputBytes = 0;
    this._queuedInputItems = 0;
    this._barriers = [];
    this._lifecycle = 0;
    this._state = "created";
    this._disposed = false;
    this._stopped = false;
    this._processExited = false;
    this._exitCode = null;
    this._finalOffset = null;
    this._outputTruncated = false;
  }

  get sessionId() { return this._sessionId; }
  get accessToken() { return this._accessToken; }
  get attachmentId() { return this._attachmentId; }
  get generation() { return this._generation; }
  get mode() { return this._mode; }
  get state() { return this._state; }
  get nextOffset() { return this._nextOffset; }
  get consumedOffset() { return this._consumedOffset; }
  get acknowledgedOffset() { return this._acknowledgedOffset; }
  get processExited() { return this._processExited; }
  get exitCode() { return this._exitCode; }
  get finalOffset() { return this._finalOffset; }
  get outputTruncated() { return this._outputTruncated; }
  get disposed() { return this._disposed; }

  /** Create a host session when no session identity was supplied. */
  async create(command, args = [], dimensions = {}) {
    this._assertUsable();
    if (this._sessionId !== null) throw new RynTerminalError("invalid_request", "This client already has a session");
    if (typeof command !== "string" || command.length === 0) throw new TypeError("command must be a non-empty string");
    const cols = positiveInteger(dimensions.cols, 80, "cols");
    const rows = positiveInteger(dimensions.rows, 24, "rows");
    const retentionMode = dimensions.retentionMode ?? "streaming";
    if (retentionMode !== "streaming" && retentionMode !== "reattachable") {
      throw new TypeError("retentionMode must be streaming or reattachable");
    }
    const result = await this._invoke("terminal.create", {
      command,
      argsJson: JSON.stringify(args),
      cols,
      rows,
      retentionMode,
    });
    this._sessionId = requiredString(result?.sessionId, "terminal.create sessionId");
    this._accessToken = requiredString(result?.accessToken, "terminal.create accessToken");
    this._setState("waiting_for_attachment");
    return { sessionId: this._sessionId, accessToken: this._accessToken };
  }

  /** Attach and start the single ordered read loop. */
  async attach() {
    this._assertUsable();
    if (this._readTask) return;
    if (this._attachTask) return this._attachTask;
    this._attachTask = this._attach().finally(() => { this._attachTask = null; });
    return this._attachTask;
  }

  async _attach() {
    const identity = this._sessionIdentity();
    const lifecycle = ++this._lifecycle;
    const attached = await this._invoke("terminal.attach", {
      ...identity,
      cursor: this._cursor === null ? null : this._cursor.toString(),
      creditBytes: this._creditBytes,
      mode: this._mode,
    });
    if (!this._isCurrent(lifecycle)) {
      await this._invoke("terminal.detach", {
        sessionId: identity.sessionId,
        attachmentId: requiredString(attached?.attachmentId, "terminal.attach attachmentId"),
        generation: decimalString(attached?.generation, "terminal.attach generation"),
      });
      throw new RynTerminalError("cancelled", "Attachment was disposed before it completed");
    }

    this._attachmentId = requiredString(attached?.attachmentId, "terminal.attach attachmentId");
    this._generation = decimalString(attached?.generation, "terminal.attach generation");

    try {
    this._nextOffset = parseOffset(attached?.nextOffset, "terminal.attach nextOffset");
    this._consumedOffset = this._nextOffset;
    this._acknowledgedOffset = this._nextOffset;
    this._setState(String(attached?.state ?? "streaming"));
      if (typeof this._consumer.prepareAttachment === "function") {
        await this._consumer.prepareAttachment({
          nextOffset: this._nextOffset,
          retainedStart: parseOffset(attached?.retainedStart, "terminal.attach retainedStart"),
          retainedEnd: parseOffset(attached?.retainedEnd, "terminal.attach retainedEnd"),
          checkpointAvailable: attached?.checkpoint !== null && attached?.checkpoint !== undefined,
          client: this,
          lifecycle,
        });
        if (!this._isCurrent(lifecycle)) throw new RynTerminalError("cancelled", "Attachment preparation became stale");
      }
      if (attached?.checkpoint !== null && attached?.checkpoint !== undefined) {
        if (typeof this._consumer.restoreCheckpoint !== "function") {
          throw new RynTerminalError("incompatible_checkpoint", "The consumer cannot restore the checkpoint required for this attachment");
        }
        const checkpoint = parseCheckpoint(attached.checkpoint);
        if (this._nextOffset !== checkpoint.offset) {
          throw new RynTerminalError("invalid_offset", "Attachment checkpoint does not match nextOffset");
        }
        await this._consumer.restoreCheckpoint(checkpoint, { client: this, lifecycle });
        if (!this._isCurrent(lifecycle)) throw new RynTerminalError("cancelled", "Checkpoint callback became stale");
      }
      if (typeof this._consumer.attachmentReady === "function") {
        await this._consumer.attachmentReady({ client: this, lifecycle });
        if (!this._isCurrent(lifecycle)) throw new RynTerminalError("cancelled", "Attachment completion became stale");
      }
    } catch (error) {
      if (this._isCurrent(lifecycle)) {
        try {
          await this._detachIdentity();
        } finally {
          this._attachmentId = null;
          this._generation = null;
        }
      }
      throw error;
    }

    this._readTask = this._readLoop(lifecycle).catch((error) => {
      if (this._isCurrent(lifecycle)) this._fail(error, true);
    }).finally(() => {
      if (this._lifecycle === lifecycle) this._readTask = null;
    });
  }

  /** Wait until the active read loop ends. */
  async closed() {
    await this._readTask;
  }

  /** UTF-8 terminal input from xterm onData or another text source. */
  writeText(text) {
    return this.writeBytes(new TextEncoder().encode(text));
  }

  /** Exact byte input, including xterm onBinary mouse reports. */
  writeBytes(bytes) {
    this._assertAttached();
    if (!(bytes instanceof Uint8Array)) throw new TypeError("writeBytes expects a Uint8Array");
    if (bytes.byteLength === 0) return this._writeChain;
    if (bytes.byteLength > MAX_INPUT_BYTES - this._queuedInputBytes || this._queuedInputItems >= MAX_INPUT_ITEMS) {
      return Promise.reject(new RynTerminalError("budget_exceeded", "Terminal input queue is full"));
    }
    const identity = this._attachmentIdentity();
    const dataBase64 = encodeBase64(bytes);
    const length = bytes.byteLength;
    this._queuedInputBytes += length;
    this._queuedInputItems++;
    const write = this._writeChain.then(async () => {
      try {
        this._assertUsable();
        const result = await this._invoke("terminal.write", { ...identity, dataBase64 });
        assertOperation(result, "terminal.write");
      } finally {
        this._queuedInputBytes -= length;
        this._queuedInputItems--;
      }
    });
    // Report this failure to its caller without poisoning subsequent input.
    this._writeChain = write.catch(() => {});
    return write;
  }

  async terminate() {
    this._assertAttached();
    const result = await this._invoke("terminal.terminate", this._attachmentIdentity());
    assertOperation(result, "terminal.terminate");
  }

  async status() {
    this._assertAttached();
    return this._invoke("terminal.status", this._attachmentIdentity());
  }

  async claimResize() {
    this._assertAttached();
    const result = await this._invoke("terminal.claimResize", this._attachmentIdentity());
    if (!result?.ok) throw operationError(result, "terminal.claimResize");
    return {
      leaseId: requiredString(result.leaseId, "terminal.claimResize leaseId"),
      leaseGeneration: decimalString(result.leaseGeneration, "terminal.claimResize leaseGeneration"),
    };
  }

  async resize(lease, cols, rows) {
    this._assertAttached();
    const result = await this._invoke("terminal.resize", {
      ...this._attachmentIdentity(),
      leaseId: requiredString(lease?.leaseId, "resize leaseId"),
      leaseGeneration: decimalString(lease?.leaseGeneration, "resize leaseGeneration"),
      cols: positiveInteger(cols, undefined, "resize cols"),
      rows: positiveInteger(rows, undefined, "resize rows"),
    });
    assertOperation(result, "terminal.resize");
  }

  async releaseResize(lease) {
    this._assertAttached();
    const result = await this._invoke("terminal.releaseResize", {
      ...this._attachmentIdentity(),
      leaseId: requiredString(lease?.leaseId, "resize leaseId"),
      leaseGeneration: decimalString(lease?.leaseGeneration, "resize leaseGeneration"),
    });
    assertOperation(result, "terminal.releaseResize");
  }

  /** Store a provider-created checkpoint at an already-consumed offset. */
  async saveCheckpoint(checkpoint) {
    this._assertAttached();
    const parsed = parseCheckpoint(checkpoint);
    if (parsed.offset > this._consumedOffset) {
      throw new RynTerminalError("invalid_offset", "A checkpoint cannot be ahead of consumed terminal output");
    }
    const result = await this._invoke("terminal.checkpoint", {
      ...this._attachmentIdentity(),
      offset: parsed.offset.toString(),
      format: parsed.format,
      payloadBase64: parsed.payloadBase64,
      cols: parsed.cols,
      rows: parsed.rows,
    });
    assertOperation(result, "terminal.checkpoint");
  }

  /** Run after all delivered bytes are consumed and acknowledged, before the next host read. */
  withReadsPaused(callback) {
    this._assertAttached();
    if (typeof callback !== "function") throw new TypeError("withReadsPaused expects a callback");
    return new Promise((resolve, reject) => {
      this._barriers.push({
        callback: async (context) => {
          await this._flushAck();
          this._assertAttached();
          return callback(context);
        },
        resolve,
        reject,
      });
    });
  }

  /** Flush consumed credit, detach, and invalidate pending consumer callbacks. */
  async dispose() {
    if (this._disposed) return;
    this._disposed = true;
    this._stopped = true;
    ++this._lifecycle;
    const barriers = this._barriers.splice(0);
    for (const barrier of barriers) barrier.reject(new RynTerminalError("cancelled", "Terminal client was disposed"));
    this._clearAckTimer();
    try {
      await this._flushAck(true);
    } finally {
      try {
        await this._detachIdentity();
      } finally {
        this._attachmentId = null;
        this._generation = null;
        this._setState("disposed");
      }
    }
  }

  async _readLoop(lifecycle) {
    while (this._isCurrent(lifecycle) && !this._stopped) {
      await this._runBarriers(lifecycle);
      if (!this._isCurrent(lifecycle) || this._stopped) return;
      let packet;
      try {
        packet = await this._invoke("terminal.read", {
          ...this._attachmentIdentity(),
          maxBytes: this._maxReadBytes,
          waitMilliseconds: this._readWaitMilliseconds,
        });
      } catch (error) {
        if (!this._isCurrent(lifecycle)) return;
        this._fail(error, false);
        return;
      }
      if (!this._isCurrent(lifecycle)) return;
      this._setState(String(packet?.state ?? this._state));
      if (packet?.error) {
        this._fail(new RynTerminalError(normalizeCode(packet.error), `terminal.read: ${packet.error}`), false);
        return;
      }

      const startOffset = parseOffset(packet?.startOffset, "terminal.read startOffset");
      const endOffset = parseOffset(packet?.endOffset, "terminal.read endOffset");
      if (endOffset < startOffset) {
        this._fail(new RynTerminalError("invalid_offset", "terminal.read endOffset precedes startOffset"), true);
        return;
      }
      const finalOffset = packet?.finalOffset === null || packet?.finalOffset === undefined
        ? null
        : parseOffset(packet.finalOffset, "terminal.read finalOffset");
      if (finalOffset !== null && (!packet?.processExited || finalOffset < endOffset || finalOffset < this._nextOffset)) {
        this._fail(new RynTerminalError("invalid_offset", "terminal.read finalOffset is inconsistent with stream progress"), true);
        return;
      }
      const encoded = String(packet?.dataBase64 ?? "");
      const readLimit = Math.min(this._maxReadBytes, this._creditBytes);
      if (endOffset - startOffset > BigInt(readLimit) || encoded.length > Math.ceil(readLimit / 3) * 4) {
        this._fail(new RynTerminalError("budget_exceeded", "terminal.read exceeded requested byte capacity"), true);
        return;
      }
      const bytes = decodeBase64(encoded);
      if (endOffset - startOffset !== BigInt(bytes.byteLength)) {
        this._fail(new RynTerminalError("invalid_offset", "terminal.read decoded byte count does not match its offset range"), true);
        return;
      }

      if (endOffset <= this._nextOffset) {
        // A retried response that is already consumed is harmless and must not be delivered twice.
      } else if (startOffset !== this._nextOffset) {
        this._fail(new RynTerminalError("resync_required", `Expected output at ${this._nextOffset}, received ${startOffset}`), false);
        return;
      } else if (bytes.byteLength > 0) {
        this._nextOffset = endOffset;
        try {
          await this._consumer.write(bytes, { startOffset, endOffset, client: this, lifecycle });
        } catch (error) {
          if (this._isCurrent(lifecycle)) this._fail(error, false);
          return;
        }
        if (!this._isCurrent(lifecycle)) return;
        this._consumedOffset = endOffset;
        this._scheduleAck();
      }
      await this._runBarriers(lifecycle);
      if (!this._isCurrent(lifecycle) || this._stopped) return;

      this._processExited = Boolean(packet?.processExited);
      this._exitCode = packet?.exitCode === null || packet?.exitCode === undefined ? null : Number(packet.exitCode);
      this._finalOffset = finalOffset;
      this._outputTruncated ||= Boolean(packet?.outputTruncated);
      if (this._processExited && this._finalOffset !== null && this._consumedOffset >= this._finalOffset) {
        await this._flushAck(true);
        this._setState("exited");
        return;
      }
    }
  }
  async _runBarriers(lifecycle) {
    while (this._barriers.length > 0 && this._isCurrent(lifecycle)) {
      const barrier = this._barriers.shift();
      try {
        barrier.resolve(await barrier.callback({
          client: this,
          offset: this._consumedOffset,
          lifecycle,
        }));
      } catch (error) {
        barrier.reject(error);
      }
    }
  }


  _scheduleAck() {
    if (this._consumedOffset <= this._acknowledgedOffset) return;
    if (this._consumedOffset - this._acknowledgedOffset >= BigInt(this._ackThresholdBytes)) {
      this._clearAckTimer();
      void this._flushAck();
      return;
    }
    if (this._ackTimer !== null) return;
    this._ackTimer = setTimeout(() => {
      this._ackTimer = null;
      void this._flushAck();
    }, this._ackDelayMilliseconds);
  }

  _clearAckTimer() {
    clearTimeout(this._ackTimer);
    this._ackTimer = null;
  }

  _flushAck(allowDisposed = false) {
    const offset = this._consumedOffset;
    if (offset <= this._acknowledgedOffset || this._attachmentId === null || this._generation === null) return this._ackChain;
    this._ackChain = this._ackChain.then(async () => {
      if ((!allowDisposed && (this._disposed || this._stopped)) || offset <= this._acknowledgedOffset) return;
      const result = await this._invoke("terminal.ack", {
        ...this._attachmentIdentity(),
        offset: offset.toString(),
      });
      assertOperation(result, "terminal.ack");
      this._acknowledgedOffset = offset;
      if (this._consumedOffset > offset && !this._disposed && !this._stopped) this._scheduleAck();
    }).catch((error) => {
      if (!this._disposed) this._fail(error, false);
    });
    return this._ackChain;
  }

  async _detachIdentity() {
    if (this._sessionId === null || this._attachmentId === null || this._generation === null) return;
    try {
      const result = await this._invoke("terminal.detach", this._attachmentIdentity());
      if (!result?.ok && result?.error !== "stale_generation" && result?.error !== "unknown_session") {
        assertOperation(result, "terminal.detach");
      }
    } catch (error) {
      if (!this._disposed) throw error;
    }
  }

  _fail(error, terminal) {
    if (this._stopped || this._disposed) return;
    this._stopped = true;
    this._clearAckTimer();
    const normalized = toTerminalError(error);
    this._setState(terminal ? "failed" : "paused");
    this._onError(normalized, { terminal, client: this });
  }

  _setState(state) {
    if (state === this._state) return;
    this._state = state;
    this._onStateChange(state, this);
  }

  _sessionIdentity() {
    return {
      sessionId: requiredString(this._sessionId, "sessionId"),
      accessToken: requiredString(this._accessToken, "accessToken"),
    };
  }

  _attachmentIdentity() {
    return {
      ...this._sessionIdentity(),
      attachmentId: requiredString(this._attachmentId, "attachmentId"),
      generation: decimalString(this._generation, "generation"),
    };
  }

  _assertUsable() {
    if (this._disposed) throw new RynTerminalError("cancelled", "Terminal client is disposed");
    if (this._stopped) throw new RynTerminalError("cancelled", "Terminal client is stopped");
  }

  _assertAttached() {
    this._assertUsable();
    this._attachmentIdentity();
  }

  _isCurrent(lifecycle) {
    return !this._disposed && !this._stopped && this._lifecycle === lifecycle;
  }

  async _invoke(command, args) {
    try {
      return await this._bridge.invoke(command, args);
    } catch (error) {
      throw toTerminalError(error);
    }
  }
}

/** @param {unknown} result @param {string} command */
function operationError(result, command) {
  const code = normalizeCode(result?.error);
  return new RynTerminalError(code, `${command}: ${String(result?.error ?? "operation failed")}`);
}

/** @param {unknown} result @param {string} command */
function assertOperation(result, command) {
  if (result?.ok) return;
  throw operationError(result, command);
}

/** @param {unknown} value */
function normalizeCode(value) {
  const text = String(value ?? "invalid_request");
  return TERMINAL_ERRORS.has(text) ? text : "invalid_request";
}

/** @param {unknown} error */
function toTerminalError(error) {
  if (error instanceof RynTerminalError) return error;
  const message = error instanceof Error ? error.message : String(error);
  for (const code of TERMINAL_ERRORS) {
    if (message.includes(code)) return new RynTerminalError(code, message, error);
  }
  return new RynTerminalError("invalid_request", message, error);
}

/** @param {unknown} value @param {number | undefined} fallback @param {string} name */
function positiveInteger(value, fallback, name) {
  if (value === undefined && fallback !== undefined) return fallback;
  if (!Number.isSafeInteger(value) || Number(value) <= 0) throw new TypeError(`${name} must be a positive safe integer`);
  return Number(value);
}

/** @param {unknown} value @param {number} fallback @param {string} name */
function nonNegativeInteger(value, fallback, name) {
  if (value === undefined) return fallback;
  if (!Number.isSafeInteger(value) || Number(value) < 0) throw new TypeError(`${name} must be a non-negative safe integer`);
  return Number(value);
}

/** @param {unknown} value @param {string} name */
function requiredString(value, name) {
  if (typeof value !== "string" || value.length === 0) throw new TypeError(`${name} must be a non-empty string`);
  return value;
}

/** @param {unknown} value @param {string} name */
function parseOffset(value, name) {
  if (typeof value === "bigint" && value >= 0n) return value;
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/.test(value)) throw new RynTerminalError("invalid_offset", `${name} must be a decimal string or non-negative bigint`);
  return BigInt(value);
}


/** @param {unknown} value @param {string} name */
function parseNullableOffset(value, name) {
  if (value === null) return null;
  if (typeof value === "bigint" && value >= 0n) return value;
  return parseOffset(value, name);
}
/** @param {unknown} value @param {string} name */
function decimalString(value, name) {
  if (typeof value !== "string" || !/^(0|[1-9][0-9]*)$/.test(value)) throw new TypeError(`${name} must be a decimal string`);
  return value;
}


/** @param {unknown} value */
function parseCheckpoint(value) {
  if (!value || typeof value !== "object") throw new RynTerminalError("incompatible_checkpoint", "Invalid checkpoint payload");
  if (typeof value.payloadBase64 !== "string") throw new RynTerminalError("incompatible_checkpoint", "Checkpoint payload must be a base64 string");
  return {
    offset: parseOffset(value.offset, "checkpoint offset"),
    format: requiredString(value.format, "checkpoint format"),
    payloadBase64: value.payloadBase64,
    cols: positiveInteger(value.cols, undefined, "checkpoint cols"),
    rows: positiveInteger(value.rows, undefined, "checkpoint rows"),
  };
}
