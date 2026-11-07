# Ryn terminal and managed-sidecar roadmap

Status: implementation authorized. Existing public behavior must remain compatible; no rewrite or breaking cutover.
Scope: all five approved directions, delivered in dependency-ordered work packets.
Decision 2026-11-07 (option B): Ryn adds no new saucer changes for this work. Document-scoped terminal authority (the native document broker) is deferred and parked on local branch `park/terminal-document-broker`; terminal attachments stay window-scoped on every platform. See implementation record entries 312-313.

## 1. Outcomes and boundaries

1. Renderer-consumption-aware, byte-bounded PTY flow control is the default for the new supported terminal API.
2. An optional session layer allows views to detach and reattach without killing the terminal, with correct replay/checkpoint semantics.
3. A first-party framework-neutral xterm.js adapter handles transport, acknowledgement, lifecycle and fitting. React/Vue wrappers are not required to use it.
4. Shared sessions have explicit resize ownership; background views cannot fight the active view.
5. A managed-sidecar service provides identity-safe lifecycle and readiness for local backends and helper processes.

Keep raw PTY transport available. Do not require xterm, a VT parser, Node, a daemon, or disk storage for basic PTY use. Do not replace xterm's renderer, fork its parser, rebuild Paseo's product, or change Cove in this project.

Reattach within a running host is in scope. Keeping PTYs alive after the whole Ryn process exits requires a broker process and is NOT promised by this session layer. Managed sidecars may deliberately survive UI exit; that does not make in-process PTYs survive. Document this distinction prominently.

## 2. Evidence and lessons

Baseline Ryn revision: 36588dce248cbcaf528c974e3f214c67be4b07e0. Paseo reference: d1a53255fcd559bccd95f3f9e0b6ba85dc11d420. Cove inspected locally, read-only.

- Ryn `src/Ryn.Plugins.Shell/PtyCommands.cs`: 4 KiB read buffer, base64 output, EventBatcher, monotonic session IDs, native Unix shim and ConPTY, transport-close ownership in ReadLoop. Preserve native lifecycle safety.
- Ryn `src/Ryn.Core/Internal/EventBatcher.cs`: timer/count batching and emitted-event metrics, not consumer acknowledgements. It has an overflow drop branch; inspection does not establish that branch is reached under current PTY usage. Do not claim reproduced byte loss without a reproduction.
- Ryn `src/Ryn.Plugins.Shell/SpawnCommands.cs`: spawning and output events exist, but not the proposed managed-sidecar state machine.
- Cove `src/Cove.Gui/frontend/src/terminal-session.ts`: distinct received/committed/acknowledged offsets, generations, control epochs, checkpoint restoration and xterm write callbacks. This directly supports the user's recollection that Cove implemented these concerns at application level.
- Cove `src/Cove.Engine/Pty/PtySessionReader.cs`: raw PTY reader, ring storage, terminal-mode tracking and handoff behavior. Study its tests and ring semantics before reusing design; do not copy app-specific nook, relay or persistence policy.
- Paseo `packages/server/src/terminal/terminal-session-controller.ts`: revision/snapshot and slow-transport catch-up; `terminal-size-ownership.ts`: owner-aware dimensions; `terminal.ts`: state/input-mode snapshots.
- Paseo `packages/app/src/terminal/runtime/terminal-emulator-runtime.ts`: existing xterm integration, WebGL, fit, search, clipboard, links and input behavior.
- Paseo daemon-instance and desktop lifecycle: readiness, attach-versus-own, PID replacement protection and quit policy.

Source review is not a throughput benchmark. Measure actual Ryn hosts before choosing numeric limits or claiming performance improvements.

## 3. Semantic decisions

### Consumption is not paint

A terminal write callback indicates xterm has processed the supplied bytes, not that the GPU has presented pixels. Acknowledge parser consumption, which bounds pending terminal work. Measure input-to-paint separately. Never use requestAnimationFrame as the sole credit-release mechanism: hidden tabs may stop receiving frames.

### Default policy

The new supported adapter starts with finite byte credit. Stop reading when total credit/storage capacity is exhausted and let native PTY/kernel buffering apply backpressure to the child. Do not silently drop bytes, send XOFF keystrokes, or SIGSTOP a process as generic flow control.

A paused producer can affect the application running in the terminal; this is the deliberate lossless/bounded-memory tradeoff. Cancellation, shutdown and control messages must remain actionable when output is stalled.

### Checkpoints are not raw rings

Recent output bytes alone cannot reconstruct arbitrary terminal state after earlier data is evicted. A usable checkpoint includes a consumed output offset, serialization format/version, terminal dimensions and verified mode state. It must align with a stable parser boundary. Some parser states, modes or addons may not serialize completely; inventory these before claiming lossless restoration.

Default detached behavior is bounded retention followed by backpressure, not unbounded disk growth or silent eviction. A valid retained checkpoint plus contiguous subsequent bytes can support reconstruction. Optional bounded durable output storage may be selected explicitly; full transcript retention is distinct from screen restoration.

## 4. Package and ownership design

Provisional package names; confirm naming with repository conventions in packet A1.

| Layer | Responsibility | Dependencies |
| --- | --- | --- |
| Existing Shell/raw PTY | native creation/read/write/resize/signal/exit | Ryn native shim / ConPTY |
| Terminal transport core | byte budgets, credit, offsets, attachment identity, lifecycle | no xterm or VT parser |
| Optional Terminal sessions | attach/detach, retention, checkpoints and lease ownership | transport core |
| First-party JS terminal client | protocol framing and generic consumer interface | Ryn JS bridge |
| First-party xterm adapter | xterm write callbacks, fit, renderer lifecycle and input | JS client + peer xterm dependency |
| Managed sidecars | host-defined executable descriptors, readiness, ownership and stop | process APIs + Ryn application lifetime |

Prefer typed C# interfaces usable without JS. IPC routers adapt those interfaces; they do not own session state. Runtime services own processes independently of individual webviews. Framework-specific visual styling stays in the application.

Do not route raw PTY output through the generic drop-capable EventBatcher. A dedicated byte-budgeted stream path must bound native reads, managed pending delivery and renderer in-flight data together. Retain current base64 transport initially if it passes budgets; a new binary transport requires a separate measured justification and security review.

## 5. Protocol contract to freeze before implementation

Names are design labels, not final APIs.

### Identity and output

- Session ID is opaque and non-reusable; never an OS PID.
- Each attachment has a host-authorized identity and generation/epoch. Old callbacks/messages cannot acknowledge or control a replacement attachment.
- Output has an absolute decoded-byte offset and payload; contiguous byte ranges, not JavaScript character counts or base64 length.
- Use lossless offset encoding across C#/JS, such as decimal strings mapped to bigint. Do not silently exceed JavaScript's safe integer range.
- Cumulative acknowledgement advances the highest contiguous consumed byte offset. Duplicate ACK is idempotent; backward ACK cannot release credit; beyond-delivered ACK is rejected; stale epoch cannot mutate state.
- Configure per-attachment, per-session and global decoded-byte ceilings. Count queued/in-flight buffers and checkpoint/replay reservations, not just queue items. Account separately for encoding overhead.
- Control/exit/error/resize traffic has a bounded independent path so full output credit cannot deadlock shutdown.
- Attach handshake installs listeners before streaming. No output between native spawn and renderer subscription may disappear.
- Exit has an exit status and final output offset. Delivery complete and process exited are distinct; stalled consumers cannot hold native handles forever. Retain final bytes under defined session retention.

### Input and resizing

- Bound input by bytes and queue depth, preserve ordering, handle partial writes, avoid blocking the UI thread and avoid holding a lifecycle lock across an indefinitely blocked write.
- Resize dimensions validated against safe limits. Sequence resize relative to checkpoint/replay dimensions; coalesce intermediate sizes, not final committed size.
- Input permission, view permission and resize lease are separate capabilities. A readonly viewer cannot write or claim control.

### State and errors

- Transport states: created, waiting-for-attachment/credit, streaming, backpressured, draining, exited, disposed (names may be refined; transitions mandatory).
- Explicit errors: unknown session, unauthorized attachment, stale generation/lease, invalid offset, budget exceeded, history unavailable, incompatible checkpoint, terminal exited, cancelled, platform unsupported.
- Public metrics: bytes read/sent/consumed/retained, outstanding credit, queue high-water, pause duration, ACK latency, exit/drain state. No output contents in metrics.

## 6. Work packets

Every packet produces code where authorized later, focused behavioral proof, relevant API/user documentation, and an explicit acceptance record. No packet is complete merely because it compiles. New tests defend actual invariants, not method names or source text.

### Milestone A: baseline and frozen contracts

**A1 — repository/caller inventory**
- Enumerate raw PTY C# and JS callers, samples, docs, capability policies, generator constraints and native packaging targets.
- Inspect Cove ACK/checkpoint tests and Paseo flow/session/resize tests as behavioral references.
- Determine existing JS package build/distribution conventions; settle package names and dependencies.
- Deliver a caller migration matrix and exact supported OS/architecture matrix. No native ABI changes assumed.

**A2 — executable baseline harness**
- Build a throwaway workload producer plus real Ryn/xterm host in the implementation phase.
- Exercise fast output, stalled consumers, large paste, child ignoring stdin, exit while blocked, repeated mount/unmount and concurrent sessions.
- Measure managed/native/webview process memory, throughput, parser backlog, input latency, UI-thread stalls and exit cleanup.
- Include byte-for-byte capture sink separate from visual screen tests. Screen state cannot prove complete byte delivery.
- Gate: archived baseline results on actual WKWebView/WebView2/WebKitGTK or an explicitly recorded unavailable platform; release cannot waive unsupported evidence.

**A3 — public contract and defaults**
- Freeze protocol semantics from section 5, state diagrams, security ownership and capacity configuration.
- Choose numeric defaults from A2, with hysteresis and fair scheduling documented. Benchmark larger chunk coalescing instead of blindly preserving 4 KiB event granularity.
- Set checkpoint provider contract and compatibility/migration policy, including release versioning.
- Gate: reviewed invariant table and exact API/packet fixtures, no unspecified timeout or overload behavior.

### Milestone B: lossless bounded terminal transport

**B1 — host transport service extraction** (depends A3)
- Move PTY lifetime out of webview-owned routing without changing Unix fork safety, descriptor ownership, Windows handle safety or one-shot exit.
- Introduce typed host services and opaque session handles; generated IPC delegates to them.
- Keep terminal security scope validation and per-app environment/working-directory policy.
- Gate: actual shell spawn/input/resize/exit on all supported platforms, lifetime regression cases pass.

**B2 — credit and byte budgets** (B1)
- Implement generation-scoped cumulative ACK, finite delivery credit, bounded queue accounting and read gating.
- Reserve capacity before reads, release ownership exactly once, pool buffers only with clear ownership.
- ACK receipt releases downstream capacity only; it never means UI paint or durable storage.
- Fair service between sessions; slow terminal must not starve others or monopolize UI dispatch.
- Gate: exact stream delivered once/in order under slow renderer; bounded memory; invalid ACK cannot increase credit.

**B3 — deadlock-safe native I/O and shutdown** (B2)
- Validate stopping read flow on Unix and ConPTY, interrupting/cancelling blocked reads and writes, child-exit notification independent of EOF, and drain deadlines.
- Serialize input via bounded worker path; address partial writes, interrupted syscalls and saturated stdin.
- Preserve final output ordering; distinguish forced termination/truncated retention explicitly.
- Gate: kill/dispose completes while renderer never ACKs and child never reads; no descriptor reuse or double-close race.

**B4 — generic JS consumer client** (B2/B3)
- Subscribe/attach before delivery; expose a consumer write-and-complete interface with explicit disposal.
- Count decoded bytes, batch cumulative ACKs with timer/threshold, flush final ACK, discard stale callbacks after remount.
- Async consumption failure stops credit and reports a recoverable/terminal error; never ACK work that failed.
- Gate: delayed callbacks, duplicate events, reconnect and disposal cannot over-ACK or duplicate delivery.

**B5 — diagnostics and default cutover** (B4)
- Provide metrics described in section 5; pressure events on transitions rather than per byte.
- Migrate all in-repo PTY callers to supported acknowledged stream; publish raw-consumer integration guidance.
- Preserve existing public PTY commands, event payloads and behavior. New acknowledged transport is explicitly negotiated by the first-party adapter; existing consumers require no ACKs. Share native transport and lifecycle implementation rather than maintaining duplicate PTY backends. Breaking changes are not authorized.
- Gate: existing documented terminal examples run with bounded acknowledged delivery by default; raw use is explicit and documented.

### Milestone C: first-party xterm integration

**C1 — minimal adapter package** (B4)
- Framework-neutral create/attach/dispose API supporting caller-supplied xterm instance as well as documented convenience construction.
- Feed Uint8Array directly; ACK from xterm write completion, not socket arrival or DOM frame callback.
- No mandatory React, Node runtime, VT parser or app-level stores.
- Document ESM/package typings, peer versions, CSP/asset/CSS integration and bundler usage.
- Gate: same adapter used unchanged from plain HTML and a framework sample.

**C2 — input and layout fidelity** (C1)
- Fit after fonts/layout become available; ignore hidden/zero-sized elements; observe DPI/zoom and visibility changes; batch resizes.
- Keep xterm responsible for keyboard/terminal sequences. Verify composition/IME, Unicode, bracketed paste, modifiers, alternate screen and platform shortcuts.
- Prevent duplicate input handlers on remount; preserve selection/viewport on resize where xterm supports it.
- Gate: shell plus interactive TUI workflow works through resize/hide/show and composition on actual engines.

**C3 — renderer and addon lifecycle** (C2)
- WebGL is optional/feature-detected; handle initialization failure/context loss and dispose GPU resources. Verify the pinned xterm version's supported fallback rather than inventing one.
- Optional fit/search/links/clipboard/serialization addons; leave visual theme/fonts/scrollback policy configurable.
- External links use validated application callbacks; clipboard escape requests require explicit policy, not arbitrary terminal-controlled system clipboard access.
- Gate: repeated renderer swaps and tab churn do not leak; text remains correct on fallback.

**C4 — reference terminal application and consumer guide** (C3)
- A real Ryn terminal sample demonstrating flow metrics, large-output stress, optional sessions and resize ownership as later milestones land.
- Explain PTY versus terminal emulator; show minimal setup and ownership/disposal responsibilities.
- Keep raw API and custom renderer example documented so xterm is not a lock-in.
- Gate: clean consumer installation/build works without copying transport boilerplate.

### Milestone D: optional session retention and reattachment

**D1 — host session/attachment registry** (B5)
- Session lifetime independent of a window; attach/detach authorized and idempotent; per-window cleanup removes attachments, not arbitrary sessions.
- Explicit close-view, terminate-session, retain-exited-session and host-shutdown behavior.
- Bound detached session count/retention; default to pause at limit and expose status; no silent termination/eviction policy.
- Gate: destroy/recreate webview while shell remains alive; unauthorized window cannot attach.

**D2 — replay storage and attach barrier** (D1)
- Bounded byte-offset ring with explicit retained-start/end; reserve replay bytes and atomically capture replay-to-live boundary.
- Handle reconnect cursor ahead/behind/outside retention, session exited during attach, and output arriving during replay.
- If no valid baseline exists, return history-unavailable; do not display a corrupt terminal as a successful restore.
- Gate: concurrent replay/live stream delivers contiguous bytes once, across repeated disconnects.

**D3 — checkpoint provider and xterm implementation** (D2/C3)
- Optional provider API, no mandatory C# VT parser. Pin serialization format to supported terminal/addon versions.
- Capture only at a stable consumed offset while writes are gated; include size and required mode supplements; avoid checkpoint bytes accidentally being sent as terminal input.
- Inventory parser partial sequences, active/normal buffers, scrollback wrap flags, cursor/attributes, title and input modes, including unsupported features. Preserve raw replay where checkpoint cannot represent state.
- Bound checkpoint bytes/frequency; checksums are integrity checks, not proof an untrusted renderer's state is authentic. Authorize checkpoint writer.
- A renderer-originated checkpoint cannot track later detached output by itself: retain all subsequent raw bytes or apply backpressure. Do not pretend otherwise.
- Gate: restore from checkpoint plus live tail matches reference terminal state for supported escape/input modes, including chunk-split sequences and resizes. Unsupported snapshots fail explicitly.

**D4 — retention, multi-view and cleanup policy** (D3)
- Default: controller consumer drives producer credit; mirrors have bounded independent buffers and resnapshot/detach explicitly if behind. A passive hidden mirror must not globally stall the session unless strict-all-consumers mode is explicitly selected.
- Define what happens when controller detaches: session retention budget owns credit until a controller claims or storage fills.
- No checkpoint may evict bytes needed by an attachment without a resync transition.
- Keep memory-only retention default. If durable spool is selected in A3, implement bounded quota, restrictive permissions, crash cleanup and declared secret-retention policy; otherwise explicitly document no disk persistence.
- Gate: two viewers with different speeds, controller loss and exited-session retention remain bounded and truthful.

### Milestone E: resize ownership

**E1 — lease protocol** (D1, can proceed before D3)
- Host-issued generation-scoped resize lease; claim/update/release and explicit control transfer.
- Focus is a claim request, not automatic trust. Validate attachment permissions; input and resize authority remain distinct.
- Revoke on detach/window disposal; reject late updates with old lease; no queued stale resize after transfer.
- Gate: a hidden or stale viewer cannot resize a terminal controlled by another window.

**E2 — xterm integration and restore ordering** (E1/C2/D3)
- Active eligible view requests lease, inactive views render without altering PTY size.
- Restore checkpoint at its dimensions, apply valid owner resize in a defined order, then continue live delivery.
- Local readonly view layout must not silently claim host dimensions. Define letterboxing/reflow behavior through xterm's supported behavior.
- Gate: rapid focus transfers, multi-DPI windows, fullscreen and detach/reconnect produce one coherent final size.

### Milestone F: managed sidecars (independent of terminal restoration)

**F1 — typed host-only definition and policy** (A3)
- Define logical sidecar ID, executable/args/environment/cwd, readiness probe, graceful stop protocol/deadline, startup timeout, keep-running policy and explicit restart policy.
- Executable definitions are host-controlled; JS may request approved IDs, not arbitrary launch strings or arbitrary probes.
- No mandatory Node or Paseo-specific protocol. Readiness may be application callback, authenticated socket/HTTP probe or bounded log predicate; merely opening a port does not prove identity.
- Gate: policy and state-machine fixtures cover start/attach/ready/fail/stop/exited/dispose.

**F2 — ownership and concurrent startup** (F1)
- Separate owned child from external attachment; retain process handles/identity metadata, avoid PID-only kill decisions.
- Coordinate competing launches with restrictive lock/IPC metadata; handle stale locks, PID reuse, replaced binary/instance and endpoint spoofing.
- Generic external service must supply verifiable identity and shutdown handshake; if absent, do not claim safe ownership/stop support.
- Gate: concurrent starts produce one owned instance; attaching never grants stop-on-close rights.

**F3 — asynchronous startup and diagnostics** (F2)
- Launch without shell interpolation, drain stdout/stderr independently with bounded logs, cancellation and readiness deadline.
- Login-shell environment is optional host policy, time-bounded and cached; preserve explicit env overrides and never evaluate agent-controlled text.
- Redact credentials, distinguish startup failure versus running unhealthy service; limits for extremely long log lines and failed probes.
- Gate: nonready/crashing/hung process produces bounded actionable diagnostics without blocking Ryn's main thread.

**F4 — graceful stop and restart policy** (F3)
- Request service-specific graceful stop, wait, escalate only for owned process identity within deadline. Windows job/process-tree and Unix process-group semantics require explicit OS implementation checks.
- Keep-running must survive disposal without accidentally killing descendants; dispose handles safely and document reassociation on next launch.
- Optional restart policy is disabled by default; bounded backoff/attempts, no restart after explicit stop/disposal, no rapid crash loop.
- Gate: owned versus external services, descendants, replaced identity and UI crashes exercise correct cleanup without collateral kills.

**F5 — application/update integration** (F4)
- Coordinate close/update/relaunch through Ryn lifetime. Stop owned sidecars when required; wait before replacing their payload files.
- External services are not forcibly upgraded; version/readiness mismatches are surfaced to host policy.
- Package sidecar executables per RID with license/signature and executable-permission requirements. Existing updater owns installation; do not add a competing updater.
- Gate: real small helper app survives supported keep-running/reconnect and stops reliably during owned update flow.

### Milestone G: hardening, platform proofs and release

**G1 — security and resource audit** (B5/C4/D4/E2/F5)
- Per-window/session/attachment capabilities, origin/navigation revocation, arbitrary input rejection and no PID-based authority.
- Fuzz chunk boundaries and malformed/stale ACK/control packets with finite resource ceilings.
- Treat terminal output as untrusted: OSC links/clipboard, file URLs, escape payloads and checkpoint contents must not gain host permissions.
- Verify global budgets and fairness under many sessions/sidecars; no unbounded diagnostics.

**G2 — performance and OS acceptance** (G1)
- Repeat A2 with identical data, hardware, engine version and cache state.
- Publish exact throughput, parser/input-to-paint percentiles, memory high-water, idle CPU/timer wakeups, reattach latency and cancellation deadlines.
- Targets fixed after A2: bounded queues, zero unexplained byte loss/duplication, no unbounded memory slope; interactive latency must not regress under sustained background output. Numeric speed claims require measured improvement.
- Actual macOS ARM64/x64, Windows and Linux supported artifacts tested; add Windows ARM64 only if native support is implemented and validated. Use project's actual advertised matrix, not wishful support.
- Validate UTF-8 splits, ANSI/OSC splits, TUI alternate screen, bracketed paste/IME, terminal resize, blocked child, crash/reload, sleep/wake, many sessions and GPU context loss.

**G3 — release/migration and native artifacts** (G2)
- Update all affected callers, examples, API docs, security/capability docs, changelog and package metadata; remove obsolete in-repo transport paths after cutover.
- If Unix shim implementation/ABI changes, follow ryn-pty-native-abi-release: rebuild all native archives, repin checksums, verify packaged shim and runtime behavior before tagging. Do not publish managed calls against old native assets.
- Publish matched JS/NuGet versions and a compatibility table; verify fresh consumer installs and NativeAOT publish on clean environments.
- No Node dependency introduced through terminal or sidecar packages unless consumer explicitly bundles Node as its sidecar.

**G4 — consumer adoption rehearsal** (G3)
- Exercise the reference app and a separate minimal consumer using only public packages.
- Prepare a Cove migration guide mapping its ACK/checkpoint/fit logic to Ryn; actual Cove migration requires separate authorization and its own regression checks.
- Release acceptance record lists each packet, proof links, supported limits and intentionally optional behavior. No unresolved release-blocking packet marked done.

## 7. Milestone dependency graph

```text
A1 -> A2 -> A3
             |-> B1 -> B2 -> B3 -> B4 -> B5
             |                    |      |-> D1 -> D2 -> D3 -> D4
             |                    |-> C1 -> C2 -> C3 -> C4
             |                              D1 -> E1 -> E2 (also C2/D3)
             |-> F1 -> F2 -> F3 -> F4 -> F5
B5 + C4 + D4 + E2 + F5 -> G1 -> G2 -> G3 -> G4
```

Parallel work is safe after A3 when interfaces are frozen: sidecar service versus terminal transport, later xterm versus session registry. Do not have concurrent agents redesign shared IPC/state contracts. One integration owner merges and validates each milestone.

## 8. Acceptance matrix

| Scenario | Required result |
| --- | --- |
| Renderer consumes slower than PTY produces | finite memory; producer backpressure; no silent output loss |
| Renderer fully stops ACKing | finite resources; explicit pressure state; kill/dispose still completes |
| ACK duplicate/backward/future/stale/wrong owner | no incorrect credit release or cross-session mutation |
| Fast child exits before attachment | retained bounded output and truthful exit/final offset |
| Input pipe blocked / huge paste | bounded non-UI-thread queue; cancellation and shutdown work |
| UTF-8/control sequence split across chunks | same terminal state as contiguous reference |
| Destroy/recreate renderer | old callbacks ignored; correct retained restore or explicit unavailable error |
| Replay concurrent with live output | contiguous offsets, no gap or duplicate |
| Checkpoint mid-partial sequence / incompatible addon version | reject/defer/use valid replay; no false restored state |
| Two views of unequal speed | declared controller/mirror policy, bounded resources |
| Hidden stale view resizes | rejected by lease without disturbing active terminal |
| Child crash / native handle close race | one exit, no recycled descriptor/PID operation |
| Sidecar concurrent starts / replacement | identity-safe single ownership |
| External sidecar attached | never killed by ordinary UI close/update |
| Sidecar never ready / never exits | bounded deadline, diagnostic error, safe owned escalation |
| WebGL unavailable/context lost | supported renderer path remains functional, resources disposed |
| Unauthorized page/session/file/link | explicit denial, no native privilege leakage |
| Native archive mismatch | packaging fails closed before shipping |

## 9. Execution and progress discipline

Each work packet gets a small implementation issue/checklist containing: owner, prerequisites, exact target files/APIs, invariants, measurable exit proof, documentation/migration effect and state (pending/in progress/blocked/done). A blocker names a missing decision or runtime capability, never hides a partial implementation behind a success flag.

Prove core flow control before expanding into snapshot complexity. Release terminal transport/xterm as a coherent usable milestone if desired, but all five approved areas remain on this roadmap; intermediate release is not completion of the entire request.

Implementation is authorized. Begin with A1 and executable A2 baseline; preserve all existing public behavior and reuse existing native backends. No speed or size claims before measurements.
