# Ryn — Project Plan

> **SUPERSEDED / HISTORICAL (2026-06-30):** this is the original phased plan. Many "not yet / SKIPPED"
> notes are stale (Windows/Linux builds, MSI, AppImage, tray, updater all shipped; complex IPC types work).
> For current status see `docs/ROADMAP.md`; delivered features are documented under `docs/`.

**Rich Yet Native** — A cross-platform, lightweight .NET framework for building desktop applications with web UIs.

---

## Vision

Give .NET developers the Tauri experience without leaving C#. Native OS webviews, HTML/JS or Blazor frontends, NativeAOT-ready, source-generated IPC. Ryn fills the gap where MAUI lacks Linux support, Avalonia/Uno require XAML, Photino is deliberately minimal (no plugin system, capability model, IPC generator, or CLI), and Tauri requires Rust.

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
- [x] `fs.readFile(path)` → `string` (base64-encoded bytes)
- [x] `fs.readTextFile(path)` → `string`
- [x] `fs.writeFile(path, data)` → `string` (base64 input, returns resolved path)
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

### Milestone 3.2 — Dialog Plugin (Week 8) ✅ COMPLETE

**Commands:**
- [x] `dialog.openFile(options)` → `string` — via saucer_picker_* API
- [x] `dialog.openFiles(options)` → `string[]` — via saucer_picker_* API
- [x] `dialog.openFolder(options)` → `string` — via saucer_picker_* API
- [x] `dialog.save(options)` → `string` — via saucer_picker_* API
- [x] `dialog.message(title, message, kind)` → `void` — platform-native backends
- [x] `dialog.confirm(title, message)` → `bool` — platform-native backends

**Deliverables:**
- [x] Native file open dialog (single/multi, filters) via saucer_picker_*
- [x] Native file save dialog (filters, default name) via saucer_picker_*
- [x] Message box (info, warning, error)
- [x] Confirmation dialog (yes/no)
- [ ] All dialogs are non-blocking (async, don't freeze the webview)

**Notes:**
- Platform backends: macOS (osascript), Windows (MessageBox), Linux (zenity/kdialog)
- File picker uses NativeApplicationAccessor for saucer_application handle

**Tests:**
- [ ] Dialog options serialize correctly
- [ ] Platform-specific dialog invocation doesn't crash
- [ ] Cancellation returns null/empty, not exception

### Milestone 3.3 — Clipboard Plugin (Week 8) ✅ COMPLETE

**Commands:**
- [x] `clipboard.readText()` → `string` — via pbpaste/xclip/PowerShell
- [x] `clipboard.writeText(text)` → `void` — via pbcopy/xclip/PowerShell
- [x] `clipboard.readImage()` → `byte[]` — base64-encoded image data
- [x] `clipboard.writeImage(data)` → `void` — base64-encoded image data
- [x] `clipboard.hasText()` → `bool`
- [x] `clipboard.hasImage()` → `bool`

**Tests:**
- [ ] Text round-trip (write then read)
- [ ] Image round-trip
- [ ] Empty clipboard returns empty, not exception
- [ ] Large text (10MB) handles correctly

### Milestone 3.4 — Shell Plugin (Week 9) ✅ COMPLETE

**Commands:**
- [x] `shell.execute(command, args)` → `ProcessOutput`
- [x] `shell.open(url)` → `void` (open in default browser/app)
- [x] `shell.spawn(command, args)` → `int` (pid, streams stdout/stderr via batched events)

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

### Milestone 3.5 — Notification Plugin (Week 10) ✅ COMPLETE

**Commands:**
- [x] `notification.send(title, body, options)` → `void` — via osascript/notify-send/PowerShell
- [x] `notification.sendWithIcon(title, body, iconPath)` → `void`
- [x] `notification.sendWithSound(title, body)` → `void`
- [x] `notification.requestPermission()` → `string` ("granted"/"denied")
- [x] `notification.isSupported()` → `bool`

**Deliverables:**
- [x] Native OS notifications (macOS osascript, Linux notify-send, Windows PowerShell)
- [x] Icon support (sendWithIcon)
- [x] Urgency levels
- [ ] Click callback

**Notes:**
- Uses platform shell commands, not native API integration (UNUserNotification, etc.)
- ArgumentList safety for shell argument escaping
- [x] Permission request/check via `notification.requestPermission()`

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
- [x] `ryn new <name> --vite` — Vite + TypeScript scaffolding

**Deliverables:**
- [x] `dotnet new` template packages (templates/Ryn.Templates.csproj + templates/ryn-app/.template.config; `ryn new` also generates files directly)
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
- [x] On frontend change: sync wwwroot to output and relaunch the app without a C# rebuild (in-place webview reload is a roadmap item, see docs/ROADMAP.md)
- [x] On backend change: rebuild and restart app
- [ ] Dev mode injects dev tools (right-click inspect)
- [x] Console log forwarding from webview to C# ILogger in dev mode

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
- [x] Windows: WiX MSI generation
- [x] macOS: produce .app bundle
- [x] Linux: AppImage support
- [x] Embed frontend assets into binary (single-file distribution via `ryn build --embed`)
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

## Phase 6 — Polish and Ecosystem (Weeks 15-18) 🟡 MOSTLY COMPLETE

### Milestone 6.1 — Auto-Updater (Week 15) ✅ COMPLETE

- [x] Check for updates from GitHub Releases
- [x] Download update
- [x] Apply update (platform-specific binary replacement)
- [ ] Verify update (checksum + optional code sign)
- [ ] Configurable: silent, notify, or manual

**Notes:**
- Implemented as Ryn.Plugins.Updater with GitHub Releases as the update source

### Milestone 6.2 — System Tray (Week 16) ✅ COMPLETE

- [x] Tray icon with context menu (full menu support)
- [x] Tray click events
- [ ] Minimize to tray option
- [x] Platform-appropriate behavior (Windows: Win32 Shell_NotifyIcon, macOS: NSStatusItem via ObjC runtime, Linux: libappindicator)
- [x] Balloon notifications

**Notes:**
- Implemented as Ryn.Plugins.Tray with native backends for all three platforms

### Milestone 6.3 — Documentation and Examples (Weeks 17-18) 🟡 PARTIALLY COMPLETE

- [ ] API reference generated from XML docs (roadmap: docs site, see docs/ROADMAP.md)
- [x] Getting started guide (docs/getting-started.md)
- [x] Architecture deep-dive (docs/architecture.md)
- [x] Plugin authoring guide (docs/plugin-authoring.md)
- [x] Vite integration guide (docs/vite-integration.md)
- [x] XML doc comments on all public APIs
- [x] Example: HelloWindow (minimal IPC demo)
- [x] Example: Showcase (full-featured demo with all plugins)
- [x] Example: VueApp (Vite + Vue integration)
- [x] Example: TerminalApp
- [x] Example: FileManager (demonstrates plugins)
- [x] Example: MarkdownEditor (demonstrates real use case)
- [x] Example: DevKit

---

## Additional Features (Beyond Original Plan)

The following features were implemented outside the original phased plan:

### Window Events & State
- [x] Window events: Closing (with Cancel), Closed, Resized, Focused, Blurred, Moved, StateChanged
- [x] Window state persistence (auto save/restore position and size)

### Platform Integration
- [x] System theme detection (dark/light mode, macOS/Windows/Linux)
- [x] File drag-and-drop (HTML5 API with FileDrop event)
- [~] Deep linking (custom URL schemes with OS protocol registration) — partial: macOS Apple Event delivery and Windows/Linux single-instance forwarding are implemented but not yet verified per-OS; treat as unverified until each platform is exercised end-to-end
- [x] Console log forwarding (webview console to C# ILogger in dev mode)

### Build & Distribution
- [x] Embedded content / single-file distribution (`ryn build --embed`)
- [x] Windows WiX MSI generation
- [x] Linux AppImage support

### CLI Enhancements
- [x] `ryn new --vite` (Vite + TypeScript scaffolding)
- [x] Dev mode frontend hot reload (wwwroot changes skip C# rebuild)

### Plugin Improvements
- [x] Dialog backends for Windows (MessageBox) and Linux (zenity/kdialog)
- [x] Notification improvements: sendWithIcon, urgency levels, ArgumentList safety
- [x] Clipboard image support: readImage, writeImage, hasImage

### Security Hardening
- [x] Path traversal fix (case-insensitive comparison + canonical base path)
- [x] EscapeForJs backtick/$ injection prevention
- [x] JS pending map 30s timeout (DoS prevention)
- [x] Shell argument validation (deny prefixes)
- [x] Thread safety (volatile _disposed, _menuLock, volatile cached properties)
- [x] Plugin init error handling (try-catch, continue on failure)

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
| `native-libs.yml` | Native tag/dispatch | Build and publish prebuilt saucer native libs |
| `codeql.yml` | Push/PR/schedule | CodeQL security scanning |
| `release.yml` | Tag `v*` | Build, test, pack NuGet, publish to nuget.org |
| `docs.yml` | Push to main | Build and deploy docs site — _not yet implemented; see docs/ROADMAP.md_ |

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
| `Ryn.Plugins.Tray` | System tray icon, menu, notifications |
| `Ryn.Plugins.Updater` | Auto-updater via GitHub Releases |
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

### P1 — Missing features — ALL RESOLVED

4. ~~**Source generator only supports primitive types**~~ **FIXED**
   - Added JsonElement parameter support for manual deserialization
   - Added primitive array support (int[], string[], etc.) for both parameters and returns
   - Added nullable primitive support (int?, bool?, etc.) with proper null-check codegen

5. ~~**Dialog plugin missing file picker**~~ **FIXED**
   - Wired saucer picker bindings: dialog.openFile, dialog.openFolder, dialog.openFiles, dialog.save
   - Uses NativeApplicationAccessor for saucer_application handle, creates saucer_desktop per call

6. ~~**Binary file operations missing**~~ **FIXED**
   - Added fs.readFile (returns base64) and fs.writeFile (accepts base64)

7. ~~**Clipboard uses subprocess hacks**~~ **IMPROVED**
   - Still subprocess-based (no native clipboard bindings in saucer), but added:
   - clipboard.clear command, tool existence checks, proper error handling, PlatformNotSupportedException

8. ~~**Shell spawn (streaming output) not implemented**~~ **FIXED**
   - Added shell.spawn with streaming stdout/stderr via events (shell.stdout.{pid}, shell.stderr.{pid}, shell.exit.{pid})
   - Added shell.kill to terminate spawned processes
   - Process tracking via ConcurrentDictionary with atomic PID assignment

9. ~~**Notification is basic**~~ **IMPROVED**
   - Added notification.requestPermission, notification.sendWithSound
   - Proper escaping helpers for osascript, shell args, and PowerShell

10. ~~**Event system (`window.__ryn.on/off/emit`) never tested**~~ **FIXED**
    - 27 tests added covering EscapeForJs, EmitEvent contract, and JS bridge structure
    - Fixed EscapeForJs bug: missing \0,  ,   escapes

### P2 — Quality gaps

11. ~~**Zero benchmarks**~~ **FIXED** — 4 BenchmarkDotNet suites: IPC dispatch, Utf8String marshaling, JSON serialization, EscapeForJs

12. ~~**No CLI tests**~~ **FIXED** — 21 CLI tests now exist

13. **Cross-platform: macOS + Windows verified; Linux pending** (see README support matrix)
    - macOS (arm64): verified, primary development platform
    - Windows: verified in production by the Primal launcher (a real Ryn app); not yet covered by automated GUI tests
    - Linux: builds + unit-tests in CI, but GUI paths (window/tray/dialogs/pickers) are **not yet verified end-to-end** — the one remaining gap
    - Integration tests now hard-fail in CI if native libs are missing (no more silent skip)

14. ~~**Capability checks don't integrate with plugin options**~~ **FIXED**
    - ryn.json now supports `scope` (paths) and `commands` (shell allowlist) per plugin
    - Capabilities RESTRICT, code CONFIGURES — programmatic options clamped to ryn.json scopes

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

- [x] A developer can `ryn new myapp && ryn dev` and see an app in a native window
- [x] Works on Windows (confirmed by user), macOS (confirmed), Linux (CI builds pass)
- [x] NativeAOT binary under 20MB for a hello-world app (5.0MB achieved)
- [ ] Cold start under 500ms
- [ ] All benchmarks meet targets
- [x] Zero known P1 bugs
- [x] XML doc comments on all public APIs
- [x] 7 example applications (HelloWindow, Showcase, VueApp, TerminalApp, FileManager, MarkdownEditor, DevKit)
- [x] Security model with capability system + argument validation prevents unauthorized native access by default
- [x] Getting started guide (docs/getting-started.md)
- [ ] Generated API reference site (roadmap, see docs/ROADMAP.md)
