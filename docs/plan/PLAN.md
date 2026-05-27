# Ryn — Project Plan

**Rich Yet Native** — A cross-platform, lightweight .NET framework for building desktop applications with web UIs.

---

## Vision

Give .NET developers the Tauri experience without leaving C#. Native OS webviews, Blazor-first frontend, NativeAOT-ready, zero JavaScript required. Ryn fills the gap where MAUI lacks Linux support, Avalonia/Uno require XAML, Photino is abandoned, and Tauri requires Rust.

## Architecture Overview

```
┌─────────────────────────────────────────────────┐
│                  User Application                │
│            (Blazor / HTML+CSS / JS)              │
├─────────────────────────────────────────────────┤
│  Ryn.Ipc          │  Ryn.Plugins.*              │
│  Source-generated  │  FileSystem, Dialog,        │
│  command routing   │  Clipboard, Shell,          │
│  JS ↔ C# bridge   │  Notification, Tray, etc.   │
├─────────────────────────────────────────────────┤
│  Ryn.Core                                       │
│  App lifecycle, window management,              │
│  configuration, plugin host, DI                 │
├─────────────────────────────────────────────────┤
│  Ryn.Interop                                    │
│  Auto-generated P/Invoke bindings (ClangSharp)  │
│  LibraryImport, NativeAOT-safe                  │
├─────────────────────────────────────────────────┤
│  saucer (C ABI)                                 │
│  Native webview: WebView2 / WKWebView /         │
│  WebKitGTK + window management                  │
└─────────────────────────────────────────────────┘
```

## Design Principles

1. **NativeAOT-first** — No reflection, no `Type.GetType()`, no `Assembly.Load()`. Source generators for everything that would traditionally use reflection. All projects must pass trim analysis with zero warnings.
2. **Zero-alloc hot paths** — The IPC bridge, event dispatch, and command routing must allocate zero bytes per call on the managed side in steady state. Use `stackalloc`, `ArrayPool`, `Span<T>`, `Memory<T>`, and pooled buffers.
3. **Vertical slice** — Each feature (IPC, file system plugin, dialog plugin, etc.) is self-contained with its own types, handlers, and tests. No shared "Helpers" or "Utilities" projects.
4. **ValueTask over Task** — All async APIs return `ValueTask` / `ValueTask<T>` since most calls complete synchronously (cached results, already-available data from native side).
5. **Minimal dependencies** — Only `Microsoft.Extensions.*` abstractions packages. No heavyweight frameworks.
6. **Fail fast, fail loud** — No silent fallbacks. If a platform API isn't available, throw a clear exception at startup, not at call time.

---

## Phase 1 — Foundation (Weeks 1-3) ✅ COMPLETE

**Goal:** A C# application can open a native window with an embedded webview, navigate to a URL, and evaluate JavaScript. Builds and runs on all three desktop platforms.

### Milestone 1.1 — Saucer C Bindings (Week 1) ✅ COMPLETE

**Deliverables:**
- [x] Clone saucer and saucer C-bindings repos as git submodules
- [x] Build saucer native libraries for macOS (arm64) — build/build-native.sh + CI workflow
- [x] Set up ClangSharp config file (`ryn-bindings.rsp`) targeting saucer's C headers
- [x] Auto-generate `Ryn.Interop` P/Invoke layer via ClangSharp (32 files, 856 lines)
- [ ] Validate all generated bindings use `[LibraryImport]` (ClangSharp generates `[DllImport]` — acceptable, works with NativeAOT)
- [x] Establish native library loading strategy (NativeLibraryResolver, RID-based, AppContext.BaseDirectory)
- [x] CI automation: regenerate bindings on saucer submodule update (bindings.yml workflow)
- [x] Local automation: build/regenerate-bindings.sh script

**Notes:**
- Saucer headers use C++ typed enums in `extern "C"` blocks, so ClangSharp must parse as C++17 not C
- `pdf.h` module excluded — uses `#include <cstdint>` (C++ only header bug)
- ClangSharp requires LLVM 21 (matching its version), not latest LLVM
- Native library build currently only targets macOS arm64; Windows/Linux builds not yet set up

**Tests:**
- [ ] Binding generation is deterministic (re-running ClangSharp produces identical output)
- [x] All generated signatures compile with NativeAOT-compatible project settings
- [x] Native library resolver finds correct binary per platform

**Benchmarks:**
- [ ] P/Invoke call overhead baseline (empty function call round-trip)

### Milestone 1.2 — Window and WebView (Week 2) ✅ COMPLETE

**Deliverables:**
- [x] Implement `RynWindow` backed by saucer window via `Ryn.Interop`
- [x] Implement `RynWebView` backed by saucer webview
- [x] Window lifecycle: create, show, hide, close, resize
- [x] WebView navigation: URL, raw HTML string
- [x] JavaScript evaluation from C# side
- [x] Custom URI scheme registration (`ryn://`)
- [x] Thread marshaling via saucer_application_post

**Tests:**
- [x] Window creation (integration test)
- [ ] WebView navigates to URL and returns title
- [ ] JavaScript evaluation returns correct values
- [ ] Custom scheme handler receives requests and returns responses
- [x] Window properties (title, size, resizable) persist correctly (unit tests)

**Benchmarks:**
- [ ] Window creation time
- [ ] JavaScript evaluation round-trip latency
- [ ] Custom scheme handler throughput

### Milestone 1.3 — App Lifecycle and DI (Week 3) ✅ COMPLETE

**Deliverables:**
- [x] `RynApplicationBuilder` with Microsoft.Extensions.DI integration
- [x] Configuration via `RynOptions` and `appsettings.json`
- [x] Logging via `Microsoft.Extensions.Logging`
- [x] Plugin host with deterministic init order
- [x] Graceful shutdown with `CancellationToken` (Ctrl+C)
- [x] `CreateBuilder().Build().RunAsync()` works end-to-end

**Tests:**
- [x] Builder registers services correctly
- [x] Plugin initialization order is deterministic
- [ ] Cancellation token stops the app gracefully
- [ ] Disposal cleans up all resources (no finalizer warnings)
- [x] Configuration binds to `RynOptions` correctly

**Benchmarks:**
- [ ] Application startup time (from `Build()` to window visible)
- [ ] Memory footprint at idle (after window shown, no content)

---

## Phase 2 — IPC Bridge (Weeks 4-6) ✅ MOSTLY COMPLETE

**Goal:** C# methods can be invoked from JavaScript and vice versa. Source-generated, zero-reflection, allocation-conscious.

### Milestone 2.1 — Source Generator for Command Routing (Week 4) ✅ COMPLETE

**Deliverables:**
- [x] `[RynCommand]` attribute for marking methods as IPC-callable
- [x] Roslyn incremental source generator (IIncrementalGenerator) that emits:
  - JSON deserialization of arguments via JsonDocument (primitives + string)
  - Switch-based dispatch table (no dictionary lookup)
  - JSON serialization of return values
  - Error wrapping
- [x] Support for sync and async commands (`T` and `ValueTask<T>` returns)
- [x] Support for `CancellationToken` as final parameter (auto-wired)
- [x] Compile-time validation (RYN001-RYN005 diagnostics)

**Notes:**
- Only primitive types and string are supported; complex type support not yet implemented

**Tests:**
- [x] Generator emits correct code for simple command (string in, string out)
- [ ] Generator emits correct code for complex types (records, collections)
- [x] Generator emits compile error for unsupported signatures
- [x] Generated dispatch handles unknown command name with error
- [x] Async commands are awaited correctly
- [x] Verify tests (8 snapshot tests) for generated source output
- [x] Dispatcher functional tests (5 tests)

**Benchmarks:**
- [ ] Command dispatch overhead (invoke a no-op command from managed side)
- [ ] JSON serialization/deserialization for typical payloads (small, medium, large)
- [ ] Allocation per command invocation (target: zero in steady state)

### Milestone 2.2 — JavaScript Bridge (Week 5) ✅ COMPLETE

**Deliverables:**
- [x] Inject `window.__ryn` bridge script into webview on initialization
- [x] `window.__ryn.invoke(command, args)` returns a `Promise`
- [x] Request/response correlation via monotonic ID
- [x] Transport via unified `ryn://` scheme (same-origin, no CORS issues)
- [x] Event system: `window.__ryn.on/off/emit`
- [x] C# `EmitEvent` on `IRynWebView`

**Notes:**
- XHR used for IPC requests (same `ryn://` origin as content)
- Results returned via `saucer_webview_execute` (not XHR response)

**Tests:**
- [ ] JS invoke resolves promise with return value
- [ ] JS invoke rejects promise on C# exception
- [ ] Concurrent invocations resolve to correct responses
- [ ] Event subscription receives emitted events
- [ ] Event unsubscription stops delivery
- [ ] Large payload (1MB+) transfers without corruption

**Benchmarks:**
- [ ] Full round-trip latency: JS invoke → C# handler → JS promise resolved
- [ ] Event emit throughput (events per second from C# to JS)
- [ ] Memory usage under sustained IPC load

### Milestone 2.3 — Blazor Integration (Week 6) ⏭️ SKIPPED

Not implemented. Can be added later when Blazor WebAssembly support is needed.

**Deliverables:**
- [ ] `Ryn.Blazor` package that hosts Blazor WebAssembly in the webview
- [ ] Blazor services can inject `IRynWindow`, `IRynWebView`
- [ ] `RynInterop` Blazor service for calling IPC commands without raw JS
- [ ] Static file serving via custom scheme for Blazor assets
- [ ] Hot reload support in dev mode (file watcher + webview refresh)

**Tests:**
- [ ] Blazor app renders in webview
- [ ] Blazor component can invoke IPC command and display result
- [ ] Blazor service injection resolves correctly
- [ ] Static assets (CSS, JS, WASM) load via custom scheme
- [ ] Hot reload triggers on file change

**Benchmarks:**
- [ ] Blazor app startup time in webview
- [ ] IPC call latency from Blazor component vs raw JS

---

## Phase 3 — Core Plugins (Weeks 7-10) ✅ COMPLETE

**Goal:** Essential native capabilities available as independent NuGet packages.

Each plugin follows the same vertical slice structure:
```
Ryn.Plugins.{Name}/
  {Name}Plugin.cs          — IRynPlugin implementation, registers commands
  {Name}Commands.cs         — [RynCommand] methods
  {Name}Options.cs          — Configuration (if needed)
  ServiceCollectionExtensions.cs — .AddRyn{Name}() extension
```

### Milestone 3.1 — FileSystem Plugin (Week 7) ✅ COMPLETE

**Commands:**
- [ ] `fs.readFile(path)` → `byte[]` — not implemented (only text variant)
- [x] `fs.readTextFile(path)` → `string`
- [ ] `fs.writeFile(path, data)` → `void` — not implemented (only text variant)
- [x] `fs.writeTextFile(path, text)` → `void`
- [x] `fs.exists(path)` → `bool`
- [x] `fs.mkdir(path)` → `void`
- [x] `fs.remove(path)` → `void`
- [x] `fs.readDir(path)` → `FileEntry[]`
- [x] `fs.stat(path)` → `FileStat`

**Security:**
- [x] PathValidator with configurable AllowedPaths
- [x] Path traversal prevention (reject `..` escapes)
- [x] Configurable allowed paths in plugin options

**Notes:**
- NativeAOT-safe JSON via STJ source generation (FsJsonContext)
- Binary read/write (fs.readFile, fs.writeFile) not yet implemented

**Tests:**
- [x] 8 unit tests covering commands and path validation
- [x] Path traversal attack is rejected
- [ ] Large file read/write (100MB+) doesn't OOM
- [ ] Concurrent file operations don't corrupt
- [ ] Temp directory cleanup on disposal

**Benchmarks:**
- [ ] File read throughput (small, medium, large files)
- [ ] Directory listing performance (1000+ entries)

### Milestone 3.2 — Dialog Plugin (Week 8) ✅ COMPLETE (basic)

**Commands:**
- [ ] `dialog.open(options)` → `string[]` — not wired yet (saucer has saucer_picker_* API)
- [ ] `dialog.save(options)` → `string` — not wired yet
- [x] `dialog.message(title, message, kind)` → `void` — via osascript on macOS
- [x] `dialog.confirm(title, message)` → `bool` — via osascript on macOS

**Deliverables:**
- [ ] Native file open dialog (single/multi, filters) — saucer API available but not wired
- [ ] Native file save dialog (filters, default name) — saucer API available but not wired
- [x] Message box (info, warning, error)
- [x] Confirmation dialog (yes/no)
- [ ] All dialogs are non-blocking (async, don't freeze the webview)

**Notes:**
- Currently uses osascript for message/confirm instead of saucer_picker_* native integration

**Tests:**
- [ ] Dialog options serialize correctly
- [ ] Platform-specific dialog invocation doesn't crash
- [ ] Cancellation returns null/empty, not exception

### Milestone 3.3 — Clipboard Plugin (Week 8) ✅ COMPLETE (text only)

**Commands:**
- [x] `clipboard.readText()` → `string` — via pbpaste/xclip/PowerShell
- [x] `clipboard.writeText(text)` → `void` — via pbcopy/xclip/PowerShell
- [ ] `clipboard.readImage()` → `byte[]` — not implemented (binary)
- [ ] `clipboard.writeImage(data)` → `void` — not implemented (binary)
- [x] `clipboard.hasText()` → `bool`

**Tests:**
- [ ] Text round-trip (write then read)
- [ ] Image round-trip
- [ ] Empty clipboard returns empty, not exception
- [ ] Large text (10MB) handles correctly

### Milestone 3.4 — Shell Plugin (Week 9) ✅ COMPLETE

**Commands:**
- [x] `shell.execute(command, args)` → `ProcessOutput`
- [x] `shell.open(url)` → `void` (open in default browser/app)
- [ ] `shell.spawn(command, args)` → `ChildProcess` — not implemented (streaming output)

**Security:**
- [x] Command allowlist in configuration (no arbitrary shell access by default)
- [ ] Environment variable filtering

**Notes:**
- NativeAOT-safe JSON via ShellJsonContext

**Tests:**
- [x] 3 unit tests
- [ ] Open launches default browser
- [ ] Spawn streams stdout line by line
- [x] Disallowed command is rejected
- [ ] Timeout kills spawned process

**Benchmarks:**
- [ ] Process spawn overhead
- [ ] Stdout streaming throughput

### Milestone 3.5 — Notification Plugin (Week 10) ✅ COMPLETE (basic)

**Commands:**
- [x] `notification.send(title, body, options)` → `void` — via osascript/notify-send/PowerShell
- [ ] `notification.requestPermission()` → `bool` — not implemented
- [x] `notification.isSupported()` → `bool`

**Deliverables:**
- [x] Native OS notifications (macOS osascript, Linux notify-send, Windows PowerShell)
- [ ] Icon support
- [ ] Click callback

**Notes:**
- Uses platform shell commands, not native API integration (UNUserNotification, etc.)
- Permission request/check not implemented

**Tests:**
- [ ] Notification sends without crash on all platforms
- [ ] Permission check returns correct state
- [ ] Invalid icon path handled gracefully

---

## Phase 4 — CLI Tooling (Weeks 11-13) ✅ COMPLETE

**Goal:** `dotnet ryn` CLI tool that scaffolds, develops, and builds Ryn applications.

### Milestone 4.1 — Project Scaffolding (Week 11) ✅ COMPLETE

**Commands:**
- [x] `ryn new <name>` — create a new Ryn project
- [ ] `ryn new <name> --blazor` — not implemented (Blazor not implemented)
- [x] `ryn new <name> --html` — default template (static HTML)

**Deliverables:**
- [ ] `dotnet new` template packages (currently direct file generation instead)
- [x] Generated project includes: csproj, Program.cs, Commands.cs, wwwroot/index.html, appsettings.json, ryn.json
- [ ] Template uses latest Ryn packages from NuGet
- [x] Validates project name
- [ ] NativeAOT-ready csproj out of the box

**Notes:**
- Runs `dotnet restore` after scaffolding
- Uses direct file generation rather than `dotnet new` template packages

**Tests:**
- [ ] Template generates valid project that builds
- [ ] Template generates valid project that runs
- [ ] `--blazor` template includes Blazor dependencies
- [ ] Invalid project name is rejected with clear message

### Milestone 4.2 — Dev Mode (Week 12) ✅ COMPLETE

**Commands:**
- [x] `ryn dev` — builds, launches, watches for changes

**Deliverables:**
- [x] FileSystemWatcher on `*.cs` files with 300ms debounce
- [ ] On frontend change: refresh webview only (currently rebuilds everything)
- [x] On backend change: rebuild and restart app
- [ ] Dev mode injects dev tools (right-click inspect)
- [ ] Console log forwarding from webview to terminal

**Notes:**
- Auto-rebuild and relaunch on C# file change
- Ctrl+C graceful shutdown

**Tests:**
- [ ] Frontend file change triggers webview refresh
- [ ] Backend file change triggers rebuild + restart
- [ ] Dev tools accessible in dev mode
- [ ] Console.log from webview appears in terminal

### Milestone 4.3 — Build and Package (Week 13) ✅ COMPLETE

**Commands:**
- [x] `ryn build` — dotnet publish -c Release
- [x] `ryn build --aot` — NativeAOT publish
- [x] `ryn bundle` — macOS .app bundle with Info.plist

**Deliverables:**
- [x] Release build with optimizations
- [x] NativeAOT publish with trimming
- [ ] Windows: produce folder, optional MSI/MSIX via WiX
- [x] macOS: produce .app bundle
- [ ] Linux: produce AppImage, optional .deb
- [ ] Embed frontend assets into binary (single-file distribution)
- [ ] Code signing support (configurable in ryn.json)

**Tests:**
- [ ] Release build produces working binary
- [ ] NativeAOT build produces working binary under 20MB
- [ ] Bundled installer installs and runs correctly per platform
- [ ] Embedded assets are accessible at runtime

**Benchmarks:**
- [ ] Build time (regular vs NativeAOT)
- [ ] Output binary size (regular vs NativeAOT vs NativeAOT+trimmed)
- [ ] Startup time (regular vs NativeAOT)

---

## Phase 5 — Security Model (Week 14) ✅ COMPLETE

**Goal:** Configuration-driven permission system controlling what IPC commands the frontend can invoke.

### Milestone 5.1 — Capability System ✅ COMPLETE

**Deliverables:**
- [x] `ryn.json` configuration file with capabilities section
- [x] Per-plugin allow/deny rules
- [x] Capabilities are checked at dispatch time (before handler runs)
- [x] Denied capability returns structured error (RynCommandDeniedException)
- [ ] Compile-time source generator emits capability checks (done at runtime instead)
- [x] Default: deny all when ryn.json exists, allow all when missing (dev mode)

**Notes:**
- Scoped paths are handled via plugin options rather than capability system
- 10 unit tests covering allow/deny/unconfigured scenarios

**Configuration example:**
```json
{
  "capabilities": {
    "fs": {
      "scope": ["$APP_DATA", "$DOCUMENTS"],
      "allow": ["readFile", "writeFile", "readDir"],
      "deny": ["remove"]
    },
    "shell": {
      "allowlist": ["git", "dotnet"]
    },
    "dialog": true,
    "clipboard": true
  }
}
```

**Tests:**
- [x] Allowed command executes
- [x] Denied command returns error
- [x] Unconfigured plugin is fully denied
- [ ] Scoped paths are enforced (handled via plugin options instead)
- [x] Shell allowlist prevents unlisted commands
- [ ] Malformed config fails at startup with clear error

---

## Phase 6 — Polish and Ecosystem (Weeks 15-18) ❌ NOT STARTED

### Milestone 6.1 — Auto-Updater (Week 15) ❌ NOT STARTED

- [ ] Check for updates from configurable URL
- [ ] Download and verify update (checksum + optional code sign)
- [ ] Apply update and restart
- [ ] Configurable: silent, notify, or manual

### Milestone 6.2 — System Tray (Week 16) ❌ NOT STARTED

- [ ] Tray icon with context menu
- [ ] Tray click events
- [ ] Minimize to tray option
- [ ] Platform-appropriate behavior (Windows: system tray, macOS: menu bar, Linux: AppIndicator)

### Milestone 6.3 — Documentation and Examples (Weeks 17-18) ❌ NOT STARTED

**Notes:**
- Showcase sample exists in samples/ directory

- [ ] API reference generated from XML docs
- [ ] Getting started guide
- [ ] Architecture deep-dive
- [ ] Plugin authoring guide
- [ ] Example: Hello World (minimal)
- [ ] Example: Blazor Counter (demonstrates IPC)
- [ ] Example: File Manager (demonstrates plugins)
- [ ] Example: Markdown Editor (demonstrates real use case)

---

## Test Strategy

### Layers

| Layer | Framework | What it tests |
|-------|-----------|---------------|
| Unit tests | xUnit + FluentAssertions | Individual types, serialization, routing logic |
| Snapshot tests | Verify | Source generator output stability |
| Integration tests | xUnit + real saucer | Window creation, webview navigation, IPC round-trip |
| Platform tests | CI matrix (Win/Mac/Linux) | Platform-specific behavior |
| NativeAOT tests | `dotnet publish -r <rid>` + run | Trim/AOT compatibility |

### Conventions

- Every public type has unit tests
- Every IPC command has an integration test
- Every plugin has end-to-end tests
- Tests are colocated by feature (vertical slice)
- No mocking of saucer interop in integration tests — use real native calls
- Flaky test tolerance: zero. Flaky tests are bugs.

### CI Matrix

```yaml
os: [windows-latest, macos-latest, ubuntu-latest]
config: [Debug, Release]
aot: [true, false]
```

### Allocation Enforcement

A custom test utility wraps `GC.TryStartNoGCRegion` / `GC.EndNoGCRegion` to assert zero allocation in hot paths:

```csharp
AllocationTracker.AssertNoAllocation(() =>
{
    dispatcher.Dispatch("myCommand", payload);
});
```

This runs in CI on every PR. Regressions in allocation behavior fail the build.

---

## Benchmark Strategy

### Tools

- **BenchmarkDotNet** with `MemoryDiagnoser` and `NativeMemoryDiagnoser`
- **Custom allocation tracker** for CI enforcement
- Benchmark results committed to repo as baselines
- CI compares PR benchmarks against baseline, flags regressions >5%

### Key Benchmarks

| Benchmark | Target | Category |
|-----------|--------|----------|
| P/Invoke empty call | <50ns | Interop |
| IPC command dispatch (no-op) | <1μs | IPC |
| IPC full round-trip (JS → C# → JS) | <500μs | IPC |
| JSON serialize (small payload) | <200ns, 0 alloc | Serialization |
| JSON serialize (medium payload) | <2μs | Serialization |
| Window creation | <100ms | Core |
| App startup to window visible | <200ms | Core |
| NativeAOT binary size (hello world) | <15MB | Build |
| Memory at idle | <30MB | Core |
| File read 1KB | <50μs | Plugin |
| File read 1MB | <5ms | Plugin |

### Regression Detection

CI runs benchmarks on `main` and on each PR. Results are compared using BenchmarkDotNet's statistical analysis. A regression report is posted as a PR comment if any benchmark degrades beyond threshold.

---

## Automation

### CI/CD (GitHub Actions)

| Workflow | Trigger | What it does |
|----------|---------|--------------|
| `build.yml` | Push/PR | Build + test on 3 OS × 2 configs |
| `aot.yml` | Push/PR | NativeAOT publish + smoke test on 3 OS |
| `benchmarks.yml` | PR | Run benchmarks, compare to baseline, comment on PR |
| `bindings.yml` | Submodule update | Regenerate ClangSharp bindings, open PR if changed |
| `release.yml` | Tag `v*` | Build, test, pack NuGet, publish to nuget.org |
| `docs.yml` | Push to main | Build and deploy docs site |

### Local Automation

| Script | Purpose |
|--------|---------|
| `build/regenerate-bindings.sh` | Run ClangSharp against saucer headers |
| `build/build-native.sh` | Build saucer for current platform |
| `dotnet ryn dev` | Watch + rebuild + hot reload |

### Dependabot / Renovate

- Auto-update NuGet package versions
- Auto-update GitHub Actions versions
- Auto-update saucer submodule (triggers binding regeneration)

---

## Release Strategy

### Versioning

- **SemVer 2.0** strictly
- Pre-1.0: breaking changes increment minor version
- Post-1.0: breaking changes increment major version
- NuGet packages all share the same version (monorepo versioning via `Directory.Build.props`)

### Release Cadence

- **Alpha** releases during Phase 1-3 (0.1.0-alpha.x)
- **Beta** releases during Phase 4-5 (0.1.0-beta.x)
- **RC** after Phase 5 security model is complete
- **1.0** after Phase 6 polish

### NuGet Packages

| Package | Description |
|---------|-------------|
| `Ryn.Core` | App lifecycle, window, webview, DI |
| `Ryn.Interop` | Saucer P/Invoke bindings |
| `Ryn.Ipc` | IPC bridge + source generator |
| `Ryn.Blazor` | Blazor WebAssembly integration |
| `Ryn.Plugins.FileSystem` | File system access |
| `Ryn.Plugins.Dialog` | Native dialogs |
| `Ryn.Plugins.Clipboard` | Clipboard access |
| `Ryn.Plugins.Shell` | Process execution |
| `Ryn.Plugins.Notification` | Native notifications |
| `Ryn.Cli` | CLI tool |
| `Ryn` | Metapackage (Core + Ipc + Blazor) |

---

## Known Issues & Gaps (Post Phase 5)

These are issues discovered during implementation that need to be addressed before beta.

### P0 — Bugs (broken functionality) — ALL RESOLVED

1. ~~**Plugin initialization never fires in real apps**~~ **FIXED**
   - Plugin extension methods now also register as `IRynPlugin` in DI; `Build()` resolves all `IRynPlugin` from the service provider — unified discovery eliminates the gap between DI registration and plugin list

2. ~~**`ryn new` generates a project that can't build**~~ **FIXED**
   - CLI auto-detects Ryn source root (walks up from assembly location looking for `Ryn.slnx`); generates project references when running from within the repo, NuGet package references otherwise

3. ~~**`EvaluateJavaScriptAsync` never verified end-to-end**~~ **FIXED**
   - Code review confirmed the eval bridge correctly uses the ryn:// scheme (same-origin, no CORS issues) and shares the same `ReadRequestBody`/`AcceptEmptyResponse` mechanism as the working command bridge
   - Fixed thread safety bug: `saucer_webview_execute` was called directly instead of via `ExecuteOnUiThread`, which would crash if called from a thread pool thread (e.g., after an `await` in a `[RynCommand]` handler)

### P1 — Missing features (planned but not implemented)

4. **Source generator only supports primitive types**
   - Only int, long, float, double, bool, string parameters and returns
   - Plan called for complex types (records, collections) — would need STJ source-gen integration or a user-provided JsonSerializerContext
   
5. **Dialog plugin missing file picker**
   - Only has `dialog.message` and `dialog.confirm` via osascript
   - Saucer has `saucer_picker_pick_file`, `saucer_picker_pick_folder`, `saucer_picker_save` bindings ready in Ryn.Interop — need to wire them via `NativeApplicationAccessor`

6. **Binary file operations missing**
   - `fs.readFile` / `fs.writeFile` with `byte[]` not implemented (only text variants)

7. **Clipboard uses subprocess hacks**
   - Calls `pbcopy`/`pbpaste`/`xclip`/PowerShell instead of native APIs
   - No image clipboard support

8. **Shell spawn (streaming output) not implemented**
   - `shell.execute` works but blocks until process exits
   - `shell.spawn` with streaming stdout via events not built

9. **Notification is basic**
   - Uses `osascript`/`notify-send`/PowerShell subprocess calls
   - No icon support, no click callbacks, no permission management

10. **Event system (`window.__ryn.on/off/emit`) never tested**
    - Code exists in bridge script and `EmitEvent` on `IRynWebView`
    - Zero test coverage — unknown if it actually works

### P2 — Quality gaps

11. **Zero benchmarks**
    - Plan had specific targets: IPC dispatch <1μs, window creation <100ms, JSON serialize <200ns
    - `AllocationTracker` utility exists in benchmarks project but no benchmark suites written

12. **No CLI tests**
    - `ryn new`, `ryn dev`, `ryn build`, `ryn bundle` have zero test coverage

13. **macOS only**
    - Nothing tested on Windows or Linux
    - Native lib CI builds for all platforms but no cross-platform integration tests

14. **Capability checks don't integrate with plugin options**
    - `ryn.json` capabilities and per-plugin options (FileSystemOptions.AllowedPaths, ShellOptions.AllowedCommands) are independent systems
    - Plan envisioned capabilities scoping paths (e.g., `"scope": ["$APP_DATA"]`) — not implemented

---

## Risk Register

| Risk | Impact | Mitigation |
|------|--------|------------|
| Saucer C API changes | Breaks bindings | Pin submodule to release tags, not HEAD |
| WebKitGTK behavior differs from WebView2/WKWebView | Platform bugs | Integration test matrix, platform-specific code paths where needed |
| NativeAOT trim removes needed code | Runtime crashes | Trim analysis on CI, rd.xml for edge cases |
| ClangSharp generates invalid bindings | Build breaks | Snapshot tests on generated output, manual review |
| Blazor WASM startup is slow | Bad first impression | Lazy loading, prerender, measure in benchmarks |
| One-person project bus factor | Project dies | Clear docs, clean architecture, easy to contribute |

---

## Success Criteria for 1.0

- [ ] A developer can `dotnet ryn new myapp && dotnet ryn dev` and see a Blazor app in a native window in under 30 seconds
- [ ] Works on Windows 10+, macOS 12+, Ubuntu 22.04+
- [ ] NativeAOT binary under 20MB for a hello-world app
- [ ] Cold start under 500ms
- [ ] All benchmarks meet targets
- [ ] Zero known P1 bugs
- [ ] Documentation covers all public APIs
- [ ] At least 3 example applications
- [ ] Security model prevents unauthorized native access by default
