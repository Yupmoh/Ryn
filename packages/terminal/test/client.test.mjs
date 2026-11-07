import assert from "node:assert/strict";
import test from "node:test";
import { RynTerminalClient } from "../src/client.js";

const tick = () => new Promise((resolve) => setTimeout(resolve, 0));
const base64 = (bytes) => Buffer.from(bytes).toString("base64");

function deferred() {
  let resolve;
  let reject;
  const promise = new Promise((yes, no) => { resolve = yes; reject = no; });
  return { promise, resolve, reject };
}

function harness(readPackets, consumer, overrides = {}) {
  const calls = [];
  let readIndex = 0;
  const bridge = {
    async invoke(command, args) {
      calls.push({ command, args });
      if (command === "terminal.attach") return {
        attachmentId: "attachment-a",
        generation: "7",
        state: "streaming",
        retainedStart: "0",
        retainedEnd: "0",
        nextOffset: "0",
        checkpoint: null,
      };
      if (command === "terminal.read") return readPackets[Math.min(readIndex++, readPackets.length - 1)];
      if (command === "terminal.ack" || command === "terminal.detach") return { ok: true, error: null, state: "streaming" };
      throw new Error(`Unexpected ${command}`);
    },
  };
  const client = new RynTerminalClient({
    bridge,
    consumer,
    sessionId: "session-a",
    accessToken: "secret",
    ackThresholdBytes: 1,
    readWaitMilliseconds: 1,
    ...overrides,
  });
  return { client, calls };
}

function packet(start, bytes, extra = {}) {
  const end = BigInt(start) + BigInt(bytes.length);
  return {
    state: extra.processExited ? "draining" : "streaming",
    startOffset: String(start),
    endOffset: String(end),
    dataBase64: base64(bytes),
    processExited: Boolean(extra.processExited),
    exitCode: extra.exitCode ?? null,
    finalOffset: extra.processExited ? String(end) : null,
    error: null,
  };
}

test("acknowledges decoded bytes only after consumer completion", async () => {
  const consumed = deferred();
  const bytes = Uint8Array.from([0xf0, 0x9f, 0x98, 0x80]);
  const { client, calls } = harness([packet(0, bytes), packet(4, new Uint8Array(), { processExited: true, exitCode: 0 })], {
    write: () => consumed.promise,
  });

  await client.attach();
  await tick();
  assert.equal(calls.some((call) => call.command === "terminal.ack"), false);
  consumed.resolve();
  await client.closed();

  const acknowledgements = calls.filter((call) => call.command === "terminal.ack");
  assert.equal(acknowledgements.length, 1);
  assert.equal(acknowledgements[0].args.offset, "4");
  assert.equal(client.exitCode, 0);
});

test("does not redeliver a duplicate packet or over-ack", async () => {
  const writes = [];
  const bytes = Uint8Array.from([1, 2, 3]);
  const { client, calls } = harness([
    packet(0, bytes),
    packet(0, bytes),
    packet(3, new Uint8Array(), { processExited: true, exitCode: 4 }),
  ], {
    write: async (value) => writes.push([...value]),
  });

  await client.attach();
  await client.closed();
  assert.deepEqual(writes, [[1, 2, 3]]);
  assert.deepEqual(calls.filter((call) => call.command === "terminal.ack").map((call) => call.args.offset), ["3"]);
});

test("pressure notifications track transitions without repeating every read", async () => {
  const states = [];
  const { client } = harness([
    { ...packet(0, []), state: "backpressured" },
    { ...packet(0, []), state: "backpressured" },
    packet(0, [1]),
    packet(1, [2]),
    packet(2, [], { processExited: true, exitCode: 0 }),
  ], { write: async () => {} }, {
    onStateChange: (state) => states.push(state),
  });
  await client.attach();
  await client.closed();
  assert.deepEqual(states, ["streaming", "backpressured", "streaming", "draining", "exited"]);
  await client.dispose();
});

test("consumer rejection stops credit without acknowledging failed work", async () => {
  const errors = [];
  const { client, calls } = harness([packet(0, Uint8Array.from([9]))], {
    write: async () => { throw new Error("renderer unavailable"); },
  }, {
    onError: (error) => errors.push(error.message),
  });

  await client.attach();
  await client.closed();
  assert.equal(client.state, "paused");
  assert.deepEqual(errors, ["renderer unavailable"]);
  assert.equal(calls.some((call) => call.command === "terminal.ack"), false);
});

test("disposal invalidates delayed callbacks and flushes only consumed bytes", async () => {
  const consumed = deferred();
  const { client, calls } = harness([packet(0, Uint8Array.from([5, 6]))], {
    write: () => consumed.promise,
  });

  await client.attach();
  await tick();
  const disposal = client.dispose();
  consumed.resolve();
  await disposal;
  assert.equal(client.state, "disposed");
  assert.equal(calls.some((call) => call.command === "terminal.ack"), false);
  assert.equal(calls.filter((call) => call.command === "terminal.detach").length, 1);
});

test("rejects a gap rather than rendering corrupt replay", async () => {
  const writes = [];
  const errors = [];
  const { client, calls } = harness([packet(5, Uint8Array.from([1]))], {
    write: async (value) => writes.push(value),
  }, {
    onError: (error) => errors.push(error.code),
  });

  await client.attach();
  await client.closed();
  assert.deepEqual(writes, []);
  assert.deepEqual(errors, ["resync_required"]);
  assert.equal(calls.some((call) => call.command === "terminal.ack"), false);
});

test("lets a consumer reject a nonzero attachment without a restorable baseline", async () => {
  const errors = [];
  const { client, calls } = harness([], {
    prepareAttachment: async ({ nextOffset, checkpointAvailable }) => {
      if (nextOffset > 0n && !checkpointAvailable) throw new Error("history unavailable");
    },
    write: async () => assert.fail("output must not start"),
  }, {
    onError: (error) => errors.push(error.message),
  });
  const attach = calls;
  const originalInvoke = client._bridge.invoke;
  client._bridge.invoke = async (command, args) => {
    if (command === "terminal.attach") return {
      attachmentId: "attachment-a",
      generation: "7",
      state: "streaming",
      retainedStart: "10",
      retainedEnd: "20",
      nextOffset: "20",
      checkpoint: null,
    };
    return originalInvoke(command, args);
  };

  await assert.rejects(client.attach(), /history unavailable/);
  assert.equal(attach.some((call) => call.command === "terminal.read"), false);
  assert.equal(attach.filter((call) => call.command === "terminal.detach").length, 1);
});

test("flushes the consumed offset before a checkpoint barrier runs", async () => {
  const calls = [];
  let readCount = 0;
  const consumed = deferred();
  const bridge = {
    async invoke(command, args) {
      calls.push({ command, args });
      if (command === "terminal.attach") return {
        attachmentId: "attachment-a",
        generation: "7",
        state: "streaming",
        retainedStart: "0",
        retainedEnd: "1",
        nextOffset: "0",
        checkpoint: null,
      };
      if (command === "terminal.read") {
        if (readCount++ === 0) return packet(0, Uint8Array.from([65]));
        return packet(1, new Uint8Array(), { processExited: true, exitCode: 0 });
      }
      if (command === "terminal.ack" || command === "terminal.detach") return { ok: true, error: null, state: "streaming" };
      if (command === "terminal.checkpoint") return { ok: true, error: null, state: "streaming" };
      throw new Error(`Unexpected ${command}`);
    },
  };
  const client = new RynTerminalClient({
    bridge,
    consumer: { write: () => consumed.promise },
    sessionId: "session-a",
    accessToken: "secret",
    ackThresholdBytes: 100,
    ackDelayMilliseconds: 10_000,
  });

  await client.attach();
  await tick();
  const capture = client.withReadsPaused(async ({ offset }) => {
    assert.equal(offset, 1n);
    await client.saveCheckpoint({ offset, format: "test", payloadBase64: "QQ==", cols: 80, rows: 24 });
  });
  consumed.resolve();
  await capture;

  const relevant = calls.filter((call) => call.command === "terminal.ack" || call.command === "terminal.checkpoint");
  assert.deepEqual(relevant.map((call) => call.command), ["terminal.ack", "terminal.checkpoint"]);
  assert.equal(relevant[0].args.offset, "1");
  await client.closed();
  await client.dispose();
});

test("rejects checkpoint work when its consumed-byte ACK fails", async () => {
  const consumed = deferred();
  const { client } = harness([packet(0, Uint8Array.of(65))], {
    write: () => consumed.promise,
  }, { ackThresholdBytes: 1024, ackDelayMilliseconds: 10000, onError: () => {} });
  const invoke = client._bridge.invoke;
  client._bridge.invoke = async (command, args) => command === "terminal.ack"
    ? { ok: false, error: "stale_generation", state: "streaming" }
    : invoke(command, args);
  await client.attach();
  await tick();
  let captured = false;
  const barrier = client.withReadsPaused(() => { captured = true; });
  const rejected = assert.rejects(barrier, /stopped/);
  consumed.resolve();
  await rejected;
  assert.equal(captured, false);
  await client.dispose();
});

test("live output waits for restore-dependent attachment setup", async () => {
  const ready = deferred();
  const entered = deferred();
  const seen = [];
  const { client, calls } = harness([packet(0, Uint8Array.of(65), { processExited: true })], {
    attachmentReady: async () => { entered.resolve(); await ready.promise; },
    write: (bytes) => { seen.push(...bytes); },
  });
  const attaching = client.attach();
  await entered.promise;
  assert.equal(calls.some((call) => call.command === "terminal.read"), false);
  ready.resolve();
  await attaching;
  await client.closed();
  assert.deepEqual(seen, [65]);
  await client.dispose();
});

test("disposal releases an attachment returned after teardown", async () => {
  const response = deferred();
  const detached = [];
  const client = new RynTerminalClient({
    sessionId: "session", accessToken: "token", consumer: { write() {} },
    bridge: { invoke: async (command, args) => {
      if (command === "terminal.attach") return response.promise;
      if (command === "terminal.detach") { detached.push(args); return { ok: true }; }
      assert.fail(`Unexpected ${command}`);
    } },
  });
  const attaching = client.attach();
  const rejected = assert.rejects(attaching, (error) => error.code === "cancelled");
  await client.dispose();
  response.resolve({ attachmentId: "late", generation: "4" });
  await rejected;
  assert.deepEqual(detached, [{ sessionId: "session", attachmentId: "late", generation: "4" }]);
});

test("empty serialized checkpoint restores before live output", async () => {
  const restored = [];
  const client = new RynTerminalClient({ sessionId: "s", accessToken: "t",
    consumer: { write() {}, restoreCheckpoint: (checkpoint) => restored.push(checkpoint.payloadBase64) },
    bridge: { invoke: async (command) => {
      if (command === "terminal.attach") return { attachmentId: "a", generation: "1", nextOffset: "0", retainedStart: "0", retainedEnd: "0", checkpoint: { offset: "0", format: "empty", payloadBase64: "", cols: 80, rows: 24 } };
      if (command === "terminal.read") return packet(0, new Uint8Array(), { processExited: true });
      return { ok: true };
    } },
  });
  await client.attach();
  await client.closed();
  assert.deepEqual(restored, [""]);
  await client.dispose();
});

test("rejected input does not poison later writes or retry rejected bytes", async () => {
  const sent = [];
  const client = new RynTerminalClient({sessionId:"s",accessToken:"t",consumer:{write(){}},
    bridge:{invoke:async(command,args)=>{
      if(command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"0",retainedStart:"0",retainedEnd:"0"};
      if(command === "terminal.read") return packet(0,new Uint8Array(),{processExited:true});
      if(command === "terminal.write") {sent.push(Buffer.from(args.dataBase64,"base64").toString());return sent.length===1?{ok:false,error:"budget_exceeded"}:{ok:true};}
      return {ok:true};
    }}
  });
  await client.attach();
  await assert.rejects(client.writeText("rejected"),error=>error.code==="budget_exceeded");
  await client.writeText("accepted");
  assert.deepEqual(sent,["rejected","accepted"]);
  await client.dispose();
});

test("concurrent attach callers share one host attachment", async () => {
  const response = deferred();
  let attaches = 0;
  const client = new RynTerminalClient({sessionId:"s",accessToken:"t",consumer:{write(){}},
    bridge:{invoke:async(command)=>{
      if(command === "terminal.attach") {attaches++;return response.promise;}
      if(command === "terminal.read") return packet(0,new Uint8Array(),{processExited:true});
      return {ok:true};
    }}
  });
  const first = client.attach();
  const second = client.attach();
  assert.equal(attaches,1);
  response.resolve({attachmentId:"a",generation:"1",nextOffset:"0",retainedStart:"0",retainedEnd:"0"});
  await Promise.all([first,second]);
  assert.equal(client.attachmentId,"a");
  await client.dispose();
});

test("malformed read offsets report failure without delivering or acknowledging bytes", async () => {
  const errors = [];
  const { client, calls } = harness([
    { ...packet(0, new Uint8Array([1])), startOffset: "not-an-offset" },
  ], { write() { assert.fail("malformed output delivered"); } }, {
    onError: (error) => errors.push(error),
  });
  await client.attach();
  await client.closed();
  assert.equal(client.state, "failed");
  assert.equal(errors.length, 1);
  assert.equal(calls.some(call => call.command === "terminal.ack"), false);
  await client.dispose();
});

test("final offset behind delivered bytes fails before consumption", async () => {
  const errors = [];
  const { client, calls } = harness([
    { ...packet(0, new Uint8Array([1]), { processExited: true }), finalOffset: "0" },
  ], { write() { assert.fail("inconsistent output delivered"); } }, {
    onError: error => errors.push(error),
  });
  await client.attach();
  await client.closed();
  assert.equal(client.state, "failed");
  assert.equal(errors[0]?.code, "invalid_offset");
  assert.equal(calls.some(call => call.command === "terminal.ack"), false);
  await client.dispose();
});

test("mismatched attachment checkpoint cannot mutate the consumer", async () => {
  let restored = false;
  let detached = false;
  const client = new RynTerminalClient({sessionId:"s",accessToken:"t",
    consumer:{write(){},restoreCheckpoint(){restored=true;}},
    bridge:{async invoke(command){
      if(command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"2",retainedStart:"0",retainedEnd:"2",checkpoint:{offset:"1",format:"test",payloadBase64:"",cols:80,rows:24}};
      if(command === "terminal.detach") {detached=true;return {ok:true};}
      throw new Error(command);
    }}
  });
  await assert.rejects(client.attach(),error=>error.code==="invalid_offset");
  assert.equal(restored,false);
  assert.equal(detached,true);
  assert.equal(client.attachmentId,null);
  assert.equal(client.generation,null);
  assert.throws(()=>client.writeText("must not reach detached host"));
  await client.dispose();
});

test("invalid attachment cursor releases allocated host attachment", async () => {
  let detached = 0;
  const client = new RynTerminalClient({sessionId:"s",accessToken:"t",consumer:{write(){}},
    bridge:{async invoke(command){
      if(command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"bad"};
      if(command === "terminal.detach") {detached++;return {ok:true};}
      throw new Error(command);
    }}
  });
  await assert.rejects(client.attach());
  assert.equal(detached,1);
  assert.equal(client.attachmentId,null);
  await client.dispose();
});

test("stalled input is byte bounded and rejected work is not replayed", async () => {
  const gate = deferred();
  const sent = [];
  const client = new RynTerminalClient({
    sessionId: "s", accessToken: "t", consumer: { async write() {} },
    bridge: { async invoke(command, args) {
      if (command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"0",state:"streaming"};
      if (command === "terminal.read") return new Promise(() => {});
      if (command === "terminal.write") { sent.push(args.dataBase64); await gate.promise; }
      return {ok:true};
    } },
  });
  await client.attach();
  const accepted = client.writeBytes(new Uint8Array(8 * 1024 * 1024));
  await assert.rejects(client.writeBytes(Uint8Array.of(1)), error => error.code === "budget_exceeded");
  gate.resolve();
  await accepted;
  await client.writeBytes(Uint8Array.of(2));
  assert.equal(sent.length, 2);
  assert.equal(sent[1], "Ag==");
  await client.dispose();
});

test("input item ceiling recovers after bridge rejection without reordering", async () => {
  const gate = deferred();
  const sent = [];
  const client = new RynTerminalClient({
    sessionId: "s", accessToken: "t", consumer: { async write() {} },
    bridge: { async invoke(command, args) {
      if (command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"0",state:"streaming"};
      if (command === "terminal.read") return new Promise(() => {});
      if (command === "terminal.write") {
        sent.push(Buffer.from(args.dataBase64, "base64")[0]);
        if (sent.length === 1) { await gate.promise; throw new Error("bridge failure"); }
      }
      return {ok:true};
    } },
  });
  await client.attach();
  const pending = Array.from({length:64}, (_, index) => client.writeBytes(Uint8Array.of(index)));
  const failed = assert.rejects(pending[0], /bridge failure/);
  await assert.rejects(client.writeBytes(Uint8Array.of(200)), error => error.code === "budget_exceeded");
  gate.resolve();
  await failed;
  await Promise.all(pending.slice(1));
  await client.writeBytes(Uint8Array.of(201));
  assert.deepEqual(sent, [...Array.from({length:64}, (_, index) => index), 201]);
  await client.dispose();
});

test("dispose detaches during an in-flight write and cancels queued input", async () => {
  const gate = deferred();
  const entered = deferred();
  const sent = [];
  let detached = false;
  const client = new RynTerminalClient({
    sessionId: "s", accessToken: "t", consumer: { async write() {} },
    bridge: { async invoke(command, args) {
      if (command === "terminal.attach") return {attachmentId:"a",generation:"1",nextOffset:"0",state:"streaming"};
      if (command === "terminal.read") return new Promise(() => {});
      if (command === "terminal.write") { sent.push(args.dataBase64); entered.resolve(); await gate.promise; }
      if (command === "terminal.detach") detached = true;
      return {ok:true};
    } },
  });
  await client.attach();
  const first = client.writeBytes(Uint8Array.of(1));
  const queued = client.writeBytes(Uint8Array.of(2));
  const cancelled = assert.rejects(queued, error => error.code === "cancelled");
  await entered.promise;
  await client.dispose();
  assert.equal(detached, true);
  gate.resolve();
  await first;
  await cancelled;
  assert.deepEqual(sent, ["AQ=="]);
});

test("oversized read payload never reaches the consumer or ACK path", async () => {
  const errors = [];
  const {client, calls} = harness([packet(0, [1,2,3], {processExited:true})],
    {async write() { assert.fail("oversized packet reached consumer"); }},
    {maxReadBytes:2, onError:error=>errors.push(error)});
  await client.attach();
  await client.closed();
  assert.equal(errors[0]?.code, "budget_exceeded");
  assert.equal(calls.some(call=>call.command === "terminal.ack"), false);
  await client.dispose();
});
