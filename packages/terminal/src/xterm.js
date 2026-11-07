import { binaryStringToBytes, decodeBase64, encodeBase64 } from "./base64.js";
import { RynTerminalClient, RynTerminalError } from "./client.js";
import { VtBoundaryTracker } from "./vt-boundary.js";

export const XTERM_CHECKPOINT_FORMAT = "xterm-addon-serialize@0.14.0:text-v1";

/** Resolve once xterm's write callback says its parser consumed the bytes. */
export function writeXterm(terminal, bytes) {
  return new Promise((resolve, reject) => {
    let settled = false;
    try {
      terminal.write(bytes, () => {
        if (settled) return;
        settled = true;
        resolve();
      });
    } catch (error) {
      settled = true;
      reject(error);
    }
  });
}

/**
 * Conservative xterm checkpoint provider. xterm 6 exposes buffer serialization,
 * but not its partial UTF-8/control parser state; callers must explicitly attest
 * a protocol-level stable boundary before capture.
 */
export function createXtermSerializeCheckpointProvider(options) {
  const { terminal, serializeAddon } = options;
  if (!terminal || !serializeAddon || typeof serializeAddon.serialize !== "function") {
    throw new TypeError("terminal and an activated SerializeAddon are required");
  }
  return {
    format: XTERM_CHECKPOINT_FORMAT,
    async capture(context) {
      if (typeof context?.attestStableBoundary !== "function") {
        throw new RynTerminalError("incompatible_checkpoint", "xterm cannot prove a stable parser boundary; provide attestStableBoundary(offset)");
      }
      const stable = await context.attestStableBoundary(context.offset);
      if (stable !== true) {
        throw new RynTerminalError("incompatible_checkpoint", "Checkpoint capture was not attested at a stable stream boundary");
      }
      const payload = serializeAddon.serialize();
      return {
        offset: context.offset,
        format: XTERM_CHECKPOINT_FORMAT,
        payloadBase64: encodeBase64(new TextEncoder().encode(payload)),
        cols: terminal.cols,
        rows: terminal.rows,
      };
    },
    async restore(checkpoint) {
      if (checkpoint.format !== XTERM_CHECKPOINT_FORMAT) {
        throw new RynTerminalError("incompatible_checkpoint", `Unsupported xterm checkpoint format: ${checkpoint.format}`);
      }
      terminal.reset();
      terminal.resize(checkpoint.cols, checkpoint.rows);
      await writeXterm(terminal, decodeBase64(checkpoint.payloadBase64));
    },
  };
}

/** Framework-neutral binding for a caller-owned xterm Terminal. */
export class RynXtermAdapter {
  constructor(options) {
    if (!options?.terminal) throw new TypeError("An xterm Terminal instance is required");
    this.terminal = options.terminal;
    this.element = options.element ?? null;
    this.fitAddon = options.fitAddon ?? null;
    this.checkpointProvider = options.checkpointProvider ?? null;
    this.onError = options.onError ?? (() => {});
    this.onStateChange = options.onStateChange ?? (() => {});
    this.onRendererChange = options.onRendererChange ?? (() => {});
    this.inputEnabled = options.inputEnabled ?? true;
    this.requestResizeLease = options.requestResizeLease ?? true;
    this.resizeDebounceMilliseconds = options.resizeDebounceMilliseconds ?? 50;
    this.linkPolicy = options.linkPolicy ?? null;
    this.clipboardPolicy = options.clipboardPolicy ?? null;

    this._callerOwnsClient = options.client !== undefined;
    this.client = options.client ?? new RynTerminalClient({
      bridge: options.bridge,
      sessionId: options.sessionId,
      accessToken: options.accessToken,
      cursor: options.cursor,
      mode: options.mode,
      creditBytes: options.creditBytes,
      maxReadBytes: options.maxReadBytes,
      readWaitMilliseconds: options.readWaitMilliseconds,
      ackThresholdBytes: options.ackThresholdBytes,
      ackDelayMilliseconds: options.ackDelayMilliseconds,
      consumer: {
        prepareAttachment: ({ nextOffset, checkpointAvailable }) => {
          if (nextOffset > 0n && !checkpointAvailable) {
            throw new RynTerminalError("history_unavailable", "xterm cannot restore a nonzero stream without a compatible checkpoint baseline");
          }
        },
        write: async (bytes, context) => {
          await writeXterm(this.terminal, bytes);
          this.boundaryTracker.accept(bytes, context.startOffset, context.endOffset);
        },
        restoreCheckpoint: (checkpoint) => this._restoreCheckpoint(checkpoint),
        attachmentReady: async () => {
          if (this.requestResizeLease && this._isActiveEligibleView()) await this.claimResize();
          if (this._lease) await this.fit();
        },
      },
      onStateChange: (state) => this.onStateChange(state, this),
      onError: (error, context) => this.onError(error, context),
    });

    this._disposables = [];
    this._resizeObserver = null;
    this._resizeTimer = null;
    this._resizeRunning = false;
    this._resizeAgain = false;
    this._visibilityHandler = null;
    this._windowResizeHandler = null;
    this._focusHandler = null;
    this._windowFocusHandler = null;
    this.boundaryTracker = options.boundaryTracker ?? new VtBoundaryTracker(options.cursor === undefined || options.cursor === null ? 0n : BigInt(options.cursor));
    this._fontReady = null;
    this._lease = null;
    this._resizeClaimGeneration = 0;
    this._inputSuppressionDepth = 0;
    this._rendererAddon = null;
    this._rendererContextDisposable = null;
    this._disposed = false;
    this._opened = false;
    this._attachBoundHandlers();
  }

  get resizeLease() { return this._lease; }
  get disposed() { return this._disposed; }

  async create(command, args = [], dimensions) {
    return this.client.create(command, args, dimensions);
  }

  /** Open the terminal before create when initial fitted dimensions are needed. */
  async open(element = this.element) {
    this._assertUsable();
    if (!element) throw new TypeError("A terminal container element is required");
    this.element = element;
    if (!this._opened) {
      this.terminal.open(element);
      this._opened = true;
      this._observeLayout();
    }
    return this.fit();
  }

  /** Open xterm, attach/restore at checkpoint size, then fit as resize owner. */
  async attach(element = this.element) {
    await this.open(element);
    await this.client.attach();
    // Caller-owned clients control their own delivery lifecycle.
    if (this._callerOwnsClient) {
      if (this.requestResizeLease && this._isActiveEligibleView()) await this.claimResize();
      if (this._lease) await this.fit();
    }
  }

  async fit() {
    return this._fit(false);
  }

  async _fit(forceHostResize) {
    if (this._disposed || this.client.mode !== "controller" || !this.fitAddon || !this._isFittable()) return false;
    if (document.fonts?.ready) {
      this._fontReady ??= document.fonts.ready.catch(() => undefined);
      await this._fontReady;
      if (this._disposed || this.client.mode !== "controller" || !this._isFittable()) return false;
    }
    const beforeCols = this.terminal.cols;
    const beforeRows = this.terminal.rows;
    this.fitAddon.fit();
    if (forceHostResize || this.terminal.cols !== beforeCols || this.terminal.rows !== beforeRows) await this._sendResize();
    return true;
  }

  scheduleFit() {
    if (this._disposed || this.client.mode !== "controller" || this._resizeTimer !== null) return;
    this._resizeTimer = setTimeout(() => {
      this._resizeTimer = null;
      void this.fit().catch((error) => this.onError(error, { terminal: false, adapter: this }));
    }, this.resizeDebounceMilliseconds);
  }

  async claimResize() {
    this._assertUsable();
    if (this.client.attachmentId === null || !this._isActiveEligibleView()) return false;
    const claimGeneration = ++this._resizeClaimGeneration;
    let lease;
    try {
      lease = await this.client.claimResize();
    } catch (error) {
      if (error instanceof RynTerminalError && error.code === "unauthorized_attachment") return false;
      throw error;
    }
    if (this._disposed || claimGeneration !== this._resizeClaimGeneration || this.client.attachmentId === null || !this._isActiveEligibleView()) {
      try { await this.client.releaseResize(lease); } catch { /* The stale host lease may already be revoked. */ }
      return false;
    }
    this._lease = lease;
    if (!(await this._fit(true))) await this._sendResize();
    return true;
  }

  async releaseResize() {
    ++this._resizeClaimGeneration;
    const lease = this._lease;
    this._lease = null;
    if (!lease || this.client.attachmentId === null) return;
    try {
      await this.client.releaseResize(lease);
    } catch (error) {
      if (!(error instanceof RynTerminalError) || (error.code !== "stale_generation" && error.code !== "unknown_session")) throw error;
    }
  }

  /** Capture after the built-in tracker proves no omitted stream state was observed. */
  async captureCheckpoint() {
    if (!this.checkpointProvider) {
      throw new RynTerminalError("incompatible_checkpoint", "No xterm checkpoint provider is configured");
    }
    return this.client.withReadsPaused(async ({ offset }) => {
      const checkpoint = await this.checkpointProvider.capture({
        offset,
        attestStableBoundary: (candidate) => this.boundaryTracker.isStableAt(candidate),
      });
      await this.client.saveCheckpoint(checkpoint);
      return checkpoint;
    });
  }

  /** Install WebGL only when explicitly requested; xterm's built-in renderer remains fallback. */
  useWebgl(createAddon) {
    this._assertUsable();
    if (typeof createAddon !== "function") throw new TypeError("useWebgl expects a WebglAddon factory");
    this.disableWebgl("replaced");
    try {
      const addon = createAddon();
      this._rendererAddon = addon;
      this.terminal.loadAddon(addon);
      if (typeof addon.onContextLoss === "function") {
        this._rendererContextDisposable = addon.onContextLoss(() => this.disableWebgl("context_lost"));
      }
      this.onRendererChange("webgl", "active");
      return true;
    } catch (error) {
      this.disableWebgl("initialization_failed");
      this.onError(error, { terminal: false, adapter: this });
      return false;
    }
  }

  disableWebgl(reason = "disabled") {
    const contextDisposable = this._rendererContextDisposable;
    const addon = this._rendererAddon;
    this._rendererContextDisposable = null;
    this._rendererAddon = null;
    try {
      contextDisposable?.dispose();
    } finally {
      addon?.dispose();
    }
    if (this._opened && this.terminal.rows > 0) this.terminal.refresh(0, this.terminal.rows - 1);
    this.onRendererChange("dom", reason);
  }

  /** Link activation remains an application decision; only http(s) is accepted here. */
  async openLink(uri, event) {
    let url;
    try {
      url = new URL(uri);
    } catch {
      return false;
    }
    if ((url.protocol !== "https:" && url.protocol !== "http:") || typeof this.linkPolicy !== "function") return false;
    return (await this.linkPolicy(url, event)) === true;
  }

  /** No terminal output can read/write the system clipboard without an explicit app policy. */
  async clipboard(request) {
    if (typeof this.clipboardPolicy !== "function") return null;
    return this.clipboardPolicy(request);
  }

  async dispose() {
    if (this._disposed) return;
    this._disposed = true;
    try {
    if (this._resizeTimer !== null) clearTimeout(this._resizeTimer);
    this._resizeTimer = null;
    this._resizeObserver?.disconnect();
    this._resizeObserver = null;
    if (this._visibilityHandler) document.removeEventListener("visibilitychange", this._visibilityHandler);
    if (this._windowResizeHandler) window.removeEventListener("resize", this._windowResizeHandler);
    if (this._windowFocusHandler) window.removeEventListener("focus", this._windowFocusHandler);
    if (this._focusHandler && this.element) {
      this.element.removeEventListener("focusin", this._focusHandler);
      this.element.removeEventListener("pointerdown", this._focusHandler);
    }
    for (const disposable of this._disposables.splice(0)) disposable.dispose();
    this.disableWebgl("disposed");
    } finally {
      try {
        await this.releaseResize();
      } finally {
        try {
          await this.client.dispose();
        } finally {
          this.terminal.dispose();
        }
      }
    }
  }

  async _restoreCheckpoint(checkpoint) {
    if (!this.checkpointProvider) throw new RynTerminalError("incompatible_checkpoint", "No xterm checkpoint provider is configured");
    // Serialized VT may contain DA/DSR queries. Never forward their synthetic
    // replies into the live PTY while replaying renderer-authored state.
    this._inputSuppressionDepth += 1;
    try {
      await this.checkpointProvider.restore(checkpoint);
      this.boundaryTracker.reset(checkpoint.offset);
    } finally {
      this._inputSuppressionDepth -= 1;
    }
  }

  _attachBoundHandlers() {
    if (this.inputEnabled) {
      this._disposables.push(this.terminal.onData(async (data) => {
        if (this._disposed || this._inputSuppressionDepth > 0 || this.client.mode !== "controller") return;
        try {
          await this.client.writeText(data);
        } catch (error) {
          this.onError(error, { terminal: false, adapter: this });
        }
      }));
      this._disposables.push(this.terminal.onBinary(async (data) => {
        if (this._disposed || this._inputSuppressionDepth > 0 || this.client.mode !== "controller") return;
        try {
          await this.client.writeBytes(binaryStringToBytes(data));
        } catch (error) {
          this.onError(error, { terminal: false, adapter: this });
        }
      }));
    }
  }

  _observeLayout() {
    if (!this.element || this.client.mode !== "controller") return;
    if (typeof ResizeObserver === "function") {
      this._resizeObserver = new ResizeObserver(() => this.scheduleFit());
      this._resizeObserver.observe(this.element);
    }
    this._visibilityHandler = () => {
      if (document.visibilityState === "visible") this.scheduleFit();
    };
    document.addEventListener("visibilitychange", this._visibilityHandler);
    this._windowResizeHandler = () => this.scheduleFit();
    window.addEventListener("resize", this._windowResizeHandler);
    this._focusHandler = () => {
      if (this.requestResizeLease && this._isActiveEligibleView()) {
        void this.claimResize().catch((error) => this.onError(error, { terminal: false, adapter: this }));
      }
    };
    this.element.addEventListener("focusin", this._focusHandler);
    this.element.addEventListener("pointerdown", this._focusHandler);
    this._windowFocusHandler = () => {
      if (this.element?.contains(document.activeElement)) this._focusHandler();
    };
    window.addEventListener("focus", this._windowFocusHandler);
  }

  async _sendResize() {
    if (!this._lease || this.client.attachmentId === null || !this._isActiveEligibleView()) return;
    if (this._resizeRunning) {
      this._resizeAgain = true;
      return;
    }
    this._resizeRunning = true;
    try {
      do {
        this._resizeAgain = false;
        const lease = this._lease;
        if (!lease || this._disposed || !this._isActiveEligibleView()) return;
        try {
          await this.client.resize(lease, this.terminal.cols, this.terminal.rows);
        } catch (error) {
          if (error instanceof RynTerminalError && (error.code === "stale_generation" || error.code === "unauthorized_attachment")) {
            if (this._lease === lease) {
              this._lease = null;
              return;
            }
            this._resizeAgain = true;
            continue;
          }
          throw error;
        }
      } while (this._resizeAgain && !this._disposed);
    } finally {
      this._resizeRunning = false;
    }
  }

  _isFittable() {
    if (!this.element?.isConnected || document.visibilityState === "hidden") return false;
    const rect = this.element.getBoundingClientRect();
    const style = getComputedStyle(this.element);
    return rect.width > 0 && rect.height > 0 && style.display !== "none" && style.visibility !== "hidden";
  }

  _isActiveEligibleView() {
    return this.client.mode === "controller" && this._isFittable() && document.hasFocus();
  }

  _assertUsable() {
    if (this._disposed) throw new RynTerminalError("cancelled", "xterm adapter is disposed");
  }
}

/** Convenience construction without making xterm a hard dependency of the core entry. */
export function createXtermAdapter(options) {
  const terminal = options.terminal ?? new options.Terminal(options.terminalOptions);
  if (options.fitAddon) terminal.loadAddon(options.fitAddon);
  return new RynXtermAdapter({ ...options, terminal });
}

function requiredString(value, name) {
  if (typeof value !== "string" || value.length === 0) throw new TypeError(`${name} must be a non-empty string`);
  return value;
}

function positiveInteger(value, name) {
  if (!Number.isSafeInteger(value) || value <= 0) throw new TypeError(`${name} must be a positive safe integer`);
  return value;
}
