# @ryn/terminal

Framework-neutral ESM client for Ryn's acknowledged terminal transport, plus an
optional xterm.js adapter. A PTY runs the process and transports bytes; xterm is
the terminal emulator that parses VT sequences, renders the screen, and owns
keyboard/IME behavior. This package does not implement another renderer or VT
parser.

## Compatibility and release status

This package has not yet been published. The checked-in candidate version is
`0.1.0-alpha.0`; it requires the `terminal.*` protocol in the matching
`Ryn.Plugins.Shell` release candidate. Older Shell packages exposing only
`shell.pty*` cannot serve this client. Stage the JS tarball and matching NuGet
packages from the same commit, complete the non-Windows native/package checks,
then publish those staged artifacts together. Do not mix this candidate with
same-named packages from another build or commit.

The xterm adapter targets xterm `6.0.0`, FitAddon `0.11.0`, and optional
SerializeAddon `0.14.0`. Checkpoint format compatibility is separate from
transport compatibility; do not restore snapshots produced by other versions.
Only the package rehearsals described below are established. They do not prove
the complete supported-platform, native UI, signing, or clean-install matrix.

## Install

After the matched JS/Shell candidate is published, install the generic client
from the registry with `npm install @ryn/terminal`. Before publication, use the
local tarball procedure below.

The generic client has no xterm or framework dependency. Install the pinned
optional peers for the first-party xterm integration:

```bash
npm install @ryn/terminal @xterm/xterm@6.0.0 @xterm/addon-fit@0.11.0
```

Checkpoint support additionally requires the pinned serializer:

```bash
npm install @xterm/addon-serialize@0.14.0
```

Import `@xterm/xterm/css/xterm.css` in the application bundle. No CDN or Node
runtime is required after the frontend has been built. CSP can remain
`default-src 'self'`; WebGL uses local code/canvases and no remote asset.

## Generic Consumer

A consumer resolves `write` only after it has processed the supplied bytes.
Offsets are decoded-byte offsets represented as decimal strings on IPC and
`bigint` in JavaScript.

```js
import { RynTerminalClient } from "@ryn/terminal";

const sink = new WritableStream();
const writer = sink.getWriter();
const client = new RynTerminalClient({
  bridge: window.__ryn,
  consumer: { write: (bytes) => writer.write(bytes) },
  onError: (error) => console.error(error.code, error.message),
});
await client.create("/bin/bash", []);
await client.attach();

// Later: flushes consumed credit, detaches the view, and invalidates old callbacks.
await client.dispose();
```

Use the same class from React/Vue/Svelte lifecycle hooks: construct after mount,
call `attach(container)`, and await `dispose()` during unmount. Do not add another
transport store or ACK on event receipt.

## xterm.js

```js
import { Terminal } from "@xterm/xterm";
import { FitAddon } from "@xterm/addon-fit";
import { RynXtermAdapter } from "@ryn/terminal/xterm";
import "@xterm/xterm/css/xterm.css";

const terminal = new Terminal({ scrollback: 10_000, windowOptions: {} });
const fitAddon = new FitAddon();
terminal.loadAddon(fitAddon);
const adapter = new RynXtermAdapter({
  bridge: window.__ryn,
  terminal,
  fitAddon,
  onError: (error) => console.error(error),
});
await adapter.create("/bin/bash", []);
await adapter.attach(document.querySelector("#terminal"));
```

Framework adapters do not need a wrapper package. React, for example, creates
the same `RynXtermAdapter` inside an effect after the container ref exists and
returns `() => { void adapter.dispose(); }`; Vue uses `onMounted` and
`onBeforeUnmount` around those same two calls. Keep one adapter per mounted
xterm instance so stale write callbacks cannot mutate its replacement.

The adapter sends xterm `onData` as UTF-8 and preserves `onBinary` byte strings
for non-UTF-8 mouse reports. It acknowledges output from each `Terminal.write`
callback: parser consumption, not GPU paint. xterm retains keyboard, Unicode,
composition/IME, modifier and bracketed-paste behavior.

Input `writeText`/`writeBytes` completion means the host accepted bytes into its
bounded input queue, not that the child consumed them. Queue saturation is
reported as `budget_exceeded`; callers must not treat input admission as a
process-level acknowledgement.

`client.status()` reports `ackLatencyMilliseconds`: the latest sampled host
delivery-to-cumulative-ACK interval, or `null` until a sample completes. One
delivery endpoint is sampled at a time, with constant memory per attachment;
partial ACKs do not complete that sample, and duplicate/backward ACKs do not
change it. The interval includes transport, consumer processing and ACK batching,
not GPU paint. It is a sampled duration, not an average or percentile; older hosts
may omit the field. `onStateChange` reports observed pressure/lifecycle transitions
without repeating unchanged state on every read.

TerminalApp defaults to `streaming`: acknowledged history is released so normal
shell use does not stop at the retention ceiling. Set `retentionMode: "reattachable"`
explicitly when creating a session that must retain history between views. Such
sessions pause at the retention ceiling unless a compatible checkpoint permits
pruning; this is optional, not the default flow-control behavior.

Fit waits for fonts, ignores hidden/zero-size containers, and observes container,
window and visibility changes for controllers. Every successful lease claim
sends the controller's current fitted dimensions, even when local columns/rows
did not change. Mirrors preserve checkpoint/host dimensions and letterbox or
scroll inside their container; they never locally fit or resize the PTY. Restore
ordering is checkpoint dimensions, serialized VT payload, controller lease,
then final fit/host resize, before the first live read. Generic consumers can
await restore-dependent work in `consumer.attachmentReady`; rejection detaches
without starting delivery. Caller-supplied clients own this lifecycle ordering.

Host resize dispatch pauses while the document is unfocused. When the window
regains focus, only the adapter containing the active element requests ownership
and resends its fitted size; sibling terminals do not all claim the lease.

Client input admission is bounded to 8 MiB of decoded pending bytes and 64
pending writes, including the in-flight write. Excess input rejects with
`budget_exceeded` before base64 encoding and is never retried automatically.
Host input policy may impose tighter limits. Capacity is released on both
successful and failed writes.

## Addon Policy

All addons are opt-in and application-owned:

- Fit: pass an activated `FitAddon` to enable measured resizing.
- WebGL: call `adapter.useWebgl(() => new WebglAddon())`. Unsupported WebGL,
  initialization failure, or context loss disposes it and returns to xterm's
  supported built-in renderer. Never make WebGL required.
- Serialization: use `createXtermSerializeCheckpointProvider`; exact format is
  `xterm-addon-serialize@0.14.0:text-v1`.
- Search: safe to load explicitly; its UI and shortcuts remain app policy.
- Links: output is untrusted. The adapter accepts only `http:`/`https:` and calls
  an explicit `linkPolicy`; it never invokes `window.open` itself.
- Clipboard: OSC clipboard requests do nothing without an explicit
  `clipboardPolicy`. Loading a clipboard addon must follow the same rule.

`allowProposedApi` and xterm `windowOptions` stay disabled unless the application
explicitly opts into and secures each feature. Themes, fonts and scrollback are
caller options.

## Checkpoints And Replay

`@xterm/addon-serialize` serializes screen/buffer state, cursor, attributes,
normal/alternate buffers, wrapping and documented terminal modes. It does not
expose xterm's private incremental UTF-8 state, ESC/CSI/OSC/DCS parser state or
async parser stack. A write callback alone therefore cannot prove that an
arbitrary byte split is a checkpoint boundary.

The adapter includes `VtBoundaryTracker`, a conservative stream-syntax guard.
It runs after each xterm write callback and only attests complete UTF-8 and
complete control sequences that the pinned serializer can represent. It rejects
checkpoint capture after observing omitted state, including mouse encodings
1005/1006/1015, scroll margins, tab-stop changes, character-set designation or
shift, saved cursor state, cursor style, OSC/control-string state, and unknown
SM/DECSET modes. Detection works across transport chunk boundaries and remains
latched because the public xterm API cannot prove hidden state returned to its
default. The guard intentionally does not treat RIS as a reset: xterm options
and addon-owned state can survive it. Applications needing broader coverage must
provide a different explicit checkpoint provider; the built-in xterm adapter
does not allow custom attestation to bypass its guard.

Invalid/unknown input also fails closed. The host retains contiguous raw bytes
after every accepted checkpoint, because a renderer snapshot cannot represent
later detached output. Checkpoint payloads are renderer-authored display state,
not trusted authority. They must remain bounded and access-token authorized. If
no compatible baseline exists, attach fails with `history_unavailable` or
`incompatible_checkpoint` rather than showing a corrupt terminal. This is
in-process reattachment only: PTYs do not survive complete Ryn host exit.

## Host Contract

Ryn hosts expose pull-based `terminal.*` commands through
`window.__ryn.invoke`; there are no terminal output events:

- `create`, `attach`, `read`, cumulative `ack`, `write`, `detach`, `terminate`
- `checkpoint`, `claimResize`, `resize`, `releaseResize`, `status`

`attach` returns opaque session/attachment identity and generation. `read` has
one outstanding call per attachment and returns contiguous base64 bytes with
absolute decimal-string offsets. Output uses finite decoded-byte credit; failed
consumer work receives no ACK. All control commands carry session, access token,
attachment and generation, while resize additionally carries the host-issued
lease id/generation.

The C# host registers this API with `services.AddRynTerminal()` and must grant
only the needed `terminal` commands in `ryn.json`. Existing `shell.pty*` remains
a separate compatible raw API for custom integrations; it is not used by this
adapter.

## Local Package-Only Adoption

Use temporary directories outside this repository for package output, the NuGet
global-packages cache, and consumer workspaces. Override one shared package
version explicitly so every locally packed dependency is guaranteed to match:

```bash
repo=/absolute/path/to/Ryn
feed="$(mktemp -d)/ryn-feed"
mkdir -p "$feed"
version=99.0.0-rc-audit

dotnet pack "$repo/src/Ryn.Interop/Ryn.Interop.csproj" -c Release -o "$feed" -p:MinVerVersionOverride="$version"
dotnet pack "$repo/src/Ryn.Core/Ryn.Core.csproj" -c Release -o "$feed" -p:MinVerVersionOverride="$version"
dotnet pack "$repo/src/Ryn.Ipc.Generator/Ryn.Ipc.Generator.csproj" -c Release -o "$feed" -p:MinVerVersionOverride="$version"
dotnet pack "$repo/src/Ryn.Ipc/Ryn.Ipc.csproj" -c Release -o "$feed" -p:MinVerVersionOverride="$version"
dotnet pack "$repo/src/Ryn.Plugins.Shell/Ryn.Plugins.Shell.csproj" -c Release -o "$feed" -p:MinVerVersionOverride="$version"

consumer="$(mktemp -d)/ryn-terminal-consumer"
dotnet new console -n Host -o "$consumer/Host" -f net10.0
cat > "$consumer/Host/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$consumer/packages" />
  </config>
  <packageSources>
    <clear />
    <add key="ryn-local" value="$feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="ryn-local"><package pattern="Ryn*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="Microsoft.*" /></packageSource>
  </packageSourceMapping>
</configuration>
EOF
(cd "$consumer/Host" && dotnet add package Ryn.Plugins.Shell --version "$version")
```

The source mapping keeps every `Ryn*` dependency on the isolated feed while
allowing third-party dependencies from NuGet.org. The shared version override
and isolated package cache prevent a stale global-cache package from satisfying
the restore; the consumer itself has only package references, never Ryn
`ProjectReference` items.

Pack the frontend from the same checkout, then install the generated tarball by
its actual filename:

```bash
frontend="$(mktemp -d)/ryn-terminal-frontend"
terminal_tgz="$(cd "$repo/packages/terminal" && npm pack --pack-destination "$feed" --json | node -e 'let s="";process.stdin.on("data",d=>s+=d).on("end",()=>process.stdout.write(JSON.parse(s)[0].filename))')"
cd "$frontend"
npm init -y
npm install --save-exact "$feed/$terminal_tgz" @xterm/xterm@6.0.0 @xterm/addon-fit@0.11.0
```

The core `@ryn/terminal` export needs no xterm peer. The `@ryn/terminal/xterm`
export requires `@xterm/xterm`; install Fit only when passing `fitAddon`, and
install `@xterm/addon-serialize@0.14.0` only when opting into checkpoints. A
fixed-size consumer can omit Fit, and an application without checkpoints can
omit Serialize. This is the required isolated frontend-consumer rehearsal; its
success does not establish publication or cross-platform native UI acceptance.

In the host, register `AddRynShell(...)` before `AddRynTerminal()` exactly as in
the Host Contract, and copy both `ryn.json` and the built frontend to output. The
minimal project additions are:

```xml
<ItemGroup>
  <Content Include="ryn.json" CopyToOutputDirectory="PreserveNewest" />
  <Content Include="wwwroot/**" CopyToOutputDirectory="PreserveNewest" />
</ItemGroup>
```

```json
{
  "capabilities": {
    "shell": { "commands": ["/bin/bash"] },
    "terminal": {
      "allow": ["create", "attach", "read", "ack", "write", "detach",
        "terminate", "checkpoint", "claimResize", "resize", "releaseResize", "status"]
    }
  }
}
```

An interactive shell allowlist permits arbitrary commands; use it only for a
terminal product. A normal app must instead use a fixed executable and
`ShellOptions.CommandScopes`. Build the xterm example above into `Host/wwwroot`;
it is the minimal frontend entrypoint. Then publish one RID at a time:

```bash
(cd "$consumer/Host" && dotnet publish -c Release -r osx-arm64 \
  --self-contained -p:PublishAot=true)
```

Replace `osx-arm64` with each non-Windows candidate RID in turn. Inspect every
publish directory for the matching Ryn native libraries and exercise a real
terminal before distribution; compile or restore success alone is insufficient.
Windows is deferred. Do not claim macOS or Linux readiness until the corresponding
fresh package-only runtime rehearsal completes.


### Native artifact release blockers

Do not publish the managed or JS candidate merely because local native files
work. On macOS and Linux, the Unix `ryn-pty` shim must be rebuilt for
`osx-arm64`, `osx-x64`, `linux-x64`, and `linux-arm64` from the current ABI,
included in the matching native archives, and exercised through fresh
package-only installs. Sidecar publication additionally requires its matching
Unix crash-ownership helper in those archives. Release the authoritative
archives, then repin their real SHA-256 values in `build/native-checksums.txt`;
local replacement binaries are not release assets. Stage the JS tarball and
NuGet packages from that pinned commit, verify their package-only consumers, and
publish the matched set; do not publish `@ryn/terminal` independently of its
required Shell protocol. Windows remains deferred, but its deferral does not
remove any macOS or Linux gate above.

### Candidate staging and publication

1. Finish and pin authoritative `osx-arm64`, `osx-x64`, `linux-x64`, and
   `linux-arm64` native archives from the candidate commit.
2. From that same commit, stage the NuGet package set with one version and stage
   `@ryn/terminal` at its checked-in version; do not rewrite either version in
   the working tree merely to rehearse consumption.
3. Install the staged NuGet packages through the isolated feed above and install
   the staged npm tarball into an unrelated frontend; run real terminal sessions
   on every non-Windows RID and confirm the PTY and sidecar native payloads come
   from the matching package.
4. Publish only the exact verified artifacts. Publish the Shell/NuGet candidate
   and `@ryn/terminal` as one coordinated release, then verify fresh registry
   installs resolve those exact versions. Windows validation/publication remains
   deferred.

For day-to-day work inside this repository, `packages/terminal` still provides
`npm install`, `npm run typecheck`, and `npm test`; those source-tree commands do
not rehearse package consumption.

## Cove Adoption Map (Planning Only)

No Cove migration is included in this change. Its application-level terminal
session and PTY reader remain authoritative until a separately authorized
cutover has passed Cove's own regression checks. This is not a wire-compatible
replacement for Cove's existing protocol or stored checkpoints.

| Existing responsibility | Ryn public API | Migration rule / application responsibility |
| --- | --- | --- |
| Received output offset | `RynTerminalClient.nextOffset`; `TerminalWriteContext.startOffset` / `endOffset` | Treat delivery as provisional. Do not release capacity or advance Cove's committed state when the bridge response merely arrives. |
| Committed/consumed output | `TerminalConsumer.write(bytes, context)` and `RynTerminalClient.consumedOffset` | Resolve `write` only after the parser/sink accepts the bytes. `RynXtermAdapter` does this from the `Terminal.write` callback. |
| Cumulative ACK | `RynTerminalClient.acknowledgedOffset` and `status().acknowledgedOffset` | The client owns ACK batching and final flush. Remove Cove's second ACK sender; ACK means decoded bytes processed, not painted or persisted. |
| Attachment generation | `sessionId`, `accessToken`, `attachmentId`, `generation` on `RynTerminalClient` | Let `attach()` obtain fresh authority. Never translate Cove generation numbers or persist attachment authority for a new document. |
| Control epoch / fit owner | `claimResize()`, `resize()`, `releaseResize()`; or `RynXtermAdapter.claimResize()` / `fit()` / `releaseResize()` | Ryn's attachment-scoped lease replaces the control epoch. Cove still decides which focused view is eligible; mirrors must not fit or resize the PTY. |
| Checkpoint write barrier | `RynTerminalClient.withReadsPaused(callback)` followed by `saveCheckpoint(checkpoint)` | Capture only inside this barrier, after delivered bytes are consumed and ACKed and before the next read. Do not snapshot concurrently with output delivery. |
| xterm checkpoint capture | `createXtermSerializeCheckpointProvider(...)` and `RynXtermAdapter.captureCheckpoint()` | Use `retentionMode: "reattachable"`; install the pinned serializer. Rejected capture means keep raw history/backpressure, never discard bytes. |
| Restore and live tail | `TerminalConsumer.restoreCheckpoint`, `attachmentReady`; adapter-managed attach ordering | Restore checkpoint dimensions/state, acquire resize ownership, apply final fit, then begin live reads. Do not send xterm's restore-time query responses to the PTY. |
| PTY reader/ring storage | Host `AddRynTerminal()` plus `RynTerminalClient` / `RynXtermAdapter` | Remove Cove's reader and delivery loop only for the migrated view. Keep Cove-specific relay, discovery, durability, and product policy until separately replaced. |

The built-in checkpoint is deliberately narrower than Cove's terminal mode
tracking. `XTERM_CHECKPOINT_FORMAT` is
`xterm-addon-serialize@0.14.0:text-v1`; existing Cove snapshots cannot be
relabelled. `VtBoundaryTracker` permits capture only when UTF-8 and control
sequence syntax is currently complete. It permanently fails closed after it
observes serializer-omitted state such as mouse encodings 1005/1006/1015,
changed scroll margins or tab stops, character-set designation/shift, saved
cursor state, cursor style, OSC/control-string state, or unknown SM/DECSET
modes. Applications needing those states must retain and replay contiguous raw
bytes or use the generic client with a separately designed provider; the
built-in adapter does not allow custom attestation to bypass its guard.

Ryn retention and checkpoints are memory-only. `reattachable` preserves bounded
history only while the same Ryn host process lives and can pause at its ceiling;
the default `streaming` mode releases acknowledged history and is not
reattachable. Neither mode survives host exit, and a managed sidecar surviving UI
exit does not keep an in-process PTY alive. Ryn supplies no durable checkpoint or
transcript migration for Cove.

Cut over one terminal view end to end:

1. Build the host and frontend from the same local package set described above;
   register `AddRynShell()` then `AddRynTerminal()` and grant only required
   `terminal.*` commands.
2. Create a fresh Ryn session and choose `streaming` or `reattachable`
   explicitly; do not import a live Cove session or old checkpoint.
3. Mount one `RynXtermAdapter`, then remove Cove's delivery, ACK, checkpoint and
   resize subscriptions for that view so there is exactly one owner of each.
4. Await `dispose()` on unmount. Decide explicitly whether product close means
   detach/retain or `terminate()`; do not move sessions between host registries.
5. Repeat Cove's split-Unicode/control, alternate-buffer, paste/IME,
   focus/resize, remount, stale-callback, checkpoint rejection and live-tail
   regression scenarios before migrating another view.

The non-Windows release remains blocked until authoritative native
archives/checksum pins (including current Unix PTY ABI and sidecar helper
payloads) and matched staged JS/NuGet package consumption are verified.
Windows acceptance is explicitly deferred. Local
package rehearsals are evidence only for the exact environments exercised, not
proof for another platform or engine.
