/**
 * Tracks only public wire syntax needed to prove a conservative checkpoint
 * boundary. It does not emulate terminal state or replace xterm's parser.
 */
export class VtBoundaryTracker {
  constructor(offset = 0n) {
    this.reset(offset);
  }

  reset(offset = 0n) {
    if (typeof offset !== "bigint" || offset < 0n) throw new TypeError("offset must be a non-negative bigint");
    this._offset = offset;
    this._state = "ground";
    this._stringState = null;
    this._utf8Remaining = 0;
    this._utf8CodePoint = 0;
    this._utf8Minimum = 0;
    this._invalid = false;
    this._sequence = "";
    this._checkpointUnsafeReason = null;
  }

  get offset() { return this._offset; }
  get state() { return this._state; }
  get checkpointUnsafeReason() { return this._checkpointUnsafeReason; }
  get stable() {
    return !this._invalid && this._checkpointUnsafeReason === null
      && this._utf8Remaining === 0 && this._state === "ground";
  }

  /** Accept an exactly contiguous chunk after xterm's corresponding write callback. */
  accept(bytes, startOffset = this._offset, endOffset = startOffset + BigInt(bytes.byteLength)) {
    if (!(bytes instanceof Uint8Array)) throw new TypeError("bytes must be a Uint8Array");
    if (startOffset !== this._offset || endOffset - startOffset !== BigInt(bytes.byteLength)) {
      this._invalid = true;
      throw new RangeError("VT boundary tracker received a non-contiguous byte range");
    }
    for (const byte of bytes) this._acceptUtf8Byte(byte);
    this._offset = endOffset;
    return this.stable;
  }

  isStableAt(offset) {
    return typeof offset === "bigint" && offset === this._offset && this.stable;
  }

  _acceptUtf8Byte(byte) {
    if (this._utf8Remaining > 0) {
      if (byte < 0x80 || byte > 0xbf) {
        this._invalid = true;
        return;
      }
      this._utf8CodePoint = (this._utf8CodePoint << 6) | (byte & 0x3f);
      this._utf8Remaining -= 1;
      if (this._utf8Remaining === 0) {
        const codePoint = this._utf8CodePoint;
        if (codePoint < this._utf8Minimum || codePoint > 0x10ffff || (codePoint >= 0xd800 && codePoint <= 0xdfff)) {
          this._invalid = true;
          return;
        }
        this._acceptCodePoint(codePoint);
      }
      return;
    }

    if (byte <= 0x7f) {
      this._acceptCodePoint(byte);
    } else if (byte >= 0xc2 && byte <= 0xdf) {
      this._beginUtf8(byte & 0x1f, 1, 0x80);
    } else if (byte >= 0xe0 && byte <= 0xef) {
      this._beginUtf8(byte & 0x0f, 2, 0x800);
    } else if (byte >= 0xf0 && byte <= 0xf4) {
      this._beginUtf8(byte & 0x07, 3, 0x10000);
    } else {
      this._invalid = true;
    }
  }

  _beginUtf8(codePoint, remaining, minimum) {
    this._utf8CodePoint = codePoint;
    this._utf8Remaining = remaining;
    this._utf8Minimum = minimum;
  }

  _acceptCodePoint(codePoint) {
    if (codePoint === 0x0e || codePoint === 0x0f) this._markCheckpointUnsafe("shifted character set");
    if (codePoint === 0x18 || codePoint === 0x1a) {
      this._state = "ground";
      this._stringState = null;
      this._sequence = "";
      return;
    }
    if (codePoint === 0x1b) {
      if (this._state === "osc" || this._state === "string" || this._state === "string_escape") {
        this._stringState = this._state === "string_escape" ? this._stringState : this._state;
        this._state = "string_escape";
      } else {
        this._state = "escape";
        this._sequence = "\x1b";
      }
      return;
    }

    switch (this._state) {
      case "ground":
        if (codePoint === 0x9b) { this._state = "csi"; this._sequence = "\x1b["; }
        else if (codePoint === 0x9d) { this._state = "osc"; this._markCheckpointUnsafe("OSC state"); }
        else if (codePoint === 0x90 || codePoint === 0x98 || codePoint === 0x9e || codePoint === 0x9f) {
          this._state = "string";
          this._markCheckpointUnsafe("control string state");
        }
        break;
      case "escape":
        this._appendSequence(codePoint);
        if (codePoint === 0x5b) this._state = "csi";
        else if (codePoint === 0x5d) { this._state = "osc"; this._markCheckpointUnsafe("OSC state"); }
        else if (codePoint === 0x50 || codePoint === 0x58 || codePoint === 0x5e || codePoint === 0x5f) {
          this._state = "string";
          this._markCheckpointUnsafe("control string state");
        } else if (codePoint >= 0x30 && codePoint <= 0x7e) {
          this._inspectEscapeSequence();
          this._state = "ground";
        } else if (codePoint > 0x7e) this._invalid = true;
        break;
      case "csi":
        this._appendSequence(codePoint);
        if (codePoint >= 0x40 && codePoint <= 0x7e) {
          this._inspectCsiSequence();
          this._state = "ground";
        } else if (codePoint > 0x7e) this._invalid = true;
        break;
      case "osc":
        if (codePoint === 0x07 || codePoint === 0x9c) this._state = "ground";
        break;
      case "string":
        if (codePoint === 0x9c) this._state = "ground";
        break;
      case "string_escape":
        if (codePoint === 0x5c || codePoint === 0x9c) {
          this._state = "ground";
          this._stringState = null;
        } else {
          this._state = this._stringState ?? "string";
          this._stringState = null;
        }
        break;
      default:
        this._invalid = true;
    }
    if (this._state === "ground") this._sequence = "";
  }

  _appendSequence(codePoint) {
    if (this._sequence.length < 128) this._sequence += String.fromCodePoint(codePoint);
    else this._markCheckpointUnsafe("control sequence exceeds checkpoint inspection limit");
  }

  _inspectEscapeSequence() {
    if (/^\x1b[H78]$/.test(this._sequence)) this._markCheckpointUnsafe("tab stop or saved cursor state");
    if (/^\x1b[\x20-\x2f]+[\x30-\x7e]$/.test(this._sequence)) {
      this._markCheckpointUnsafe("designated character set or unrepresented ESC state");
    }
  }

  _inspectCsiSequence() {
    const final = this._sequence.at(-1);
    if (final === "r" || final === "g" || final === "s" || final === "u" || final === "q") {
      this._markCheckpointUnsafe("scroll margin, tab stop, saved cursor, or cursor style state");
      return;
    }
    if (final !== "h" && final !== "l") return;
    const body = this._sequence.slice(2, -1);
    const privateMode = body.startsWith("?");
    const parameters = (privateMode ? body.slice(1) : body).split(";").filter(Boolean);
    const supported = privateMode
      ? new Set(["1", "6", "7", "45", "66", "1000", "1002", "1003", "1004", "1049", "2004"])
      : new Set(["4"]);
    if (parameters.length === 0 || parameters.some((parameter) => !supported.has(parameter))) {
      this._markCheckpointUnsafe("terminal mode omitted by xterm serialization");
    }
  }

  _markCheckpointUnsafe(reason) {
    this._checkpointUnsafeReason ??= reason;
  }
}
