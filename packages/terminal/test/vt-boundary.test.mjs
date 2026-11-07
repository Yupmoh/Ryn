import assert from "node:assert/strict";
import test from "node:test";
import { VtBoundaryTracker } from "../src/vt-boundary.js";

const bytes = (...values) => Uint8Array.from(values);

test("defers boundary through split UTF-8 and accepts the completed code point", () => {
  const tracker = new VtBoundaryTracker();
  assert.equal(tracker.accept(bytes(0xf0, 0x9f), 0n, 2n), false);
  assert.equal(tracker.accept(bytes(0x98, 0x80), 2n, 4n), true);
  assert.equal(tracker.isStableAt(4n), true);
});

test("defers boundary through CSI, OSC and DCS strings", () => {
  const tracker = new VtBoundaryTracker();
  assert.equal(tracker.accept(new TextEncoder().encode("\u001b[31")), false);
  assert.equal(tracker.accept(new TextEncoder().encode("mred")), true);
  assert.equal(tracker.accept(new TextEncoder().encode("\u001b]0;title")), false);
  assert.equal(tracker.accept(bytes(0x07)), false);
  assert.equal(tracker.accept(new TextEncoder().encode("\u001bP1;2|payload\u001b")), false);
  assert.equal(tracker.accept(new TextEncoder().encode("\\")), false);
});

test("fails closed for malformed UTF-8 and non-contiguous offsets", () => {
  const invalid = new VtBoundaryTracker();
  assert.equal(invalid.accept(bytes(0xc0)), false);
  assert.equal(invalid.stable, false);

  const gap = new VtBoundaryTracker();
  assert.throws(() => gap.accept(bytes(1), 2n, 3n), /non-contiguous/);
  assert.equal(gap.stable, false);
});

test("latches unsupported serialized state across split sequences", () => {
  for (const sequence of [
    "\u001b[?1006h",
    "\u001b[3;8r",
    "\u001bH",
    "\u001b(B",
    "\u000e",
  ]) {
    const tracker = new VtBoundaryTracker();
    const encoded = new TextEncoder().encode(sequence);
    const split = Math.max(1, encoded.length - 2);
    tracker.accept(encoded.subarray(0, split));
    tracker.accept(encoded.subarray(split));
    assert.equal(tracker.stable, false, JSON.stringify(sequence));
    assert.notEqual(tracker.checkpointUnsafeReason, null);
  }
});

test("keeps serializer-supported modes checkpoint safe", () => {
  const tracker = new VtBoundaryTracker();
  const encoded = new TextEncoder().encode("\u001b[?2004h\u001b[?1h\u001b[?1000h");
  assert.equal(tracker.accept(encoded), true);
  assert.equal(tracker.checkpointUnsafeReason, null);
});

test("overlong mode sequence cannot bypass checkpoint safety inspection", () => {
  const tracker = new VtBoundaryTracker();
  const encoded = new TextEncoder().encode("\u001b[?" + "0;".repeat(80) + "1006h");
  for (const byte of encoded) tracker.accept(Uint8Array.of(byte));
  assert.equal(tracker.stable, false);
  assert.notEqual(tracker.checkpointUnsafeReason, null);
});
