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

## Phase 1 — Foundation (Weeks 1-3)

**Goal:** A C# application can open a native window with an embedded webview, navigate to a URL, and evaluate JavaScript. Builds and runs on all three desktop platforms.

### Milestone 1.1 — Saucer C Bindings (Week 1)

**Deliverables:**
- [ ] Clone saucer and saucer C-bindings repos as git submodules
- [ ] Build saucer native libraries for Windows (x64), macOS (arm64/x64), Linux (x64)
- [ ] Set up ClangSharp config file (`ryn-bindings.rsp`) targeting saucer's C headers
- [ ] Auto-generate `Ryn.Interop` P/Invoke layer via ClangSharp
- [ ] Validate all generated bindings use `[LibraryImport]` (source-gen, not `[DllImport]`)
- [ ] Establish native library loading strategy (runtime identifier-based: `runtimes/{rid}/native/`)
- [ ] CI automation: regenerate bindings on saucer submodule update

**Tests:**
- [ ] Binding generation is deterministic (re-running ClangSharp produces identical output)
- [ ] All generated signatures compile with NativeAOT publish
- [ ] Native library resolver finds correct binary per platform

**Benchmarks:**
- [ ] P/Invoke call overhead baseline (empty function call round-trip)

### Milestone 1.2 — Window and WebView (Week 2)

**Deliverables:**
- [ ] Implement `RynWindow` backed by saucer window via `Ryn.Interop`
- [ ] Implement `RynWebView` backed by saucer webview
- [ ] Window lifecycle: create, show, hide, close, resize
- [ ] WebView navigation: URL, raw HTML string
- [ ] JavaScript evaluation from C# side
- [ ] Custom URI scheme registration (`ryn://`)
- [ ] Thread marshaling: ensure native calls happen on the correct thread

**Tests:**
- [ ] Window creation and disposal (no leaked handles)
- [ ] WebView navigates to URL and returns title
- [ ] JavaScript evaluation returns correct values
- [ ] Custom scheme handler receives requests and returns responses
- [ ] Window properties (title, size, resizable) persist correctly

**Benchmarks:**
- [ ] Window creation time
- [ ] JavaScript evaluation round-trip latency
- [ ] Custom scheme handler throughput

### Milestone 1.3 — App Lifecycle and DI (Week 3)

**Deliverables:**
- [ ] `RynApplication` / `RynApplicationBuilder` with Microsoft.Extensions.DI integration
- [ ] Configuration via `RynOptions` and `appsettings.json`
- [ ] Logging via `Microsoft.Extensions.Logging`
- [ ] Plugin host: `IRynPlugin` registration and initialization
- [ ] Graceful shutdown with `CancellationToken` propagation
- [ ] `RynApplication.CreateBuilder().Build().RunAsync()` works end-to-end

**Tests:**
- [ ] Builder registers services correctly
- [ ] Plugin initialization order is deterministic
- [ ] Cancellation token stops the app gracefully
- [ ] Disposal cleans up all resources (no finalizer warnings)
- [ ] Configuration binds to `RynOptions` correctly

**Benchmarks:**
- [ ] Application startup time (from `Build()` to window visible)
- [ ] Memory footprint at idle (after window shown, no content)

---

## Phase 2 — IPC Bridge (Weeks 4-6)

**Goal:** C# methods can be invoked from JavaScript and vice versa. Source-generated, zero-reflection, allocation-conscious.

### Milestone 2.1 — Source Generator for Command Routing (Week 4)

**Deliverables:**
- [ ] `[RynCommand]` attribute for marking methods as IPC-callable
- [ ] Roslyn source generator that emits:
  - JSON deserialization of arguments (System.Text.Json source-gen)
  - Method dispatch table (switch on command name, no dictionary lookup)
  - JSON serialization of return values
  - Error wrapping
- [ ] Support for sync and async commands (`T` and `ValueTask<T>` returns)
- [ ] Support for `CancellationToken` as final parameter (auto-wired)
- [ ] Compile-time validation: commands must be `static` or on a registered service

**Tests:**
- [ ] Generator emits correct code for simple command (string in, string out)
- [ ] Generator emits correct code for complex types (records, collections)
- [ ] Generator emits compile error for unsupported signatures
- [ ] Generated dispatch handles unknown command name with error
- [ ] Async commands are awaited correctly
- [ ] Verify tests (snapshot testing) for generated source output

**Benchmarks:**
- [ ] Command dispatch overhead (invoke a no-op command from managed side)
- [ ] JSON serialization/deserialization for typical payloads (small, medium, large)
- [ ] Allocation per command invocation (target: zero in steady state)

### Milestone 2.2 — JavaScript Bridge (Week 5)

**Deliverables:**
- [ ] Inject `window.__ryn` bridge script into webview on initialization
- [ ] `window.__ryn.invoke(command, args)` returns a `Promise`
- [ ] Request/response correlation via monotonic ID
- [ ] Binary message transport via custom scheme (`ryn://ipc/{id}`)
- [ ] Event system: C# can emit events, JS can subscribe
- [ ] `window.__ryn.on(event, callback)` / `window.__ryn.off(event, callback)`

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

### Milestone 2.3 — Blazor Integration (Week 6)

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

## Phase 3 — Core Plugins (Weeks 7-10)

**Goal:** Essential native capabilities available as independent NuGet packages.

Each plugin follows the same vertical slice structure:
```
Ryn.Plugins.{Name}/
  {Name}Plugin.cs          — IRynPlugin implementation, registers commands
  {Name}Commands.cs         — [RynCommand] methods
  {Name}Options.cs          — Configuration (if needed)
  ServiceCollectionExtensions.cs — .AddRyn{Name}() extension
```

### Milestone 3.1 — FileSystem Plugin (Week 7)

**Commands:**
- `fs.readFile(path)` → `byte[]`
- `fs.readTextFile(path)` → `string`
- `fs.writeFile(path, data)` → `void`
- `fs.writeTextFile(path, text)` → `void`
- `fs.exists(path)` → `bool`
- `fs.mkdir(path)` → `void`
- `fs.remove(path)` → `void`
- `fs.readDir(path)` → `FileEntry[]`
- `fs.stat(path)` → `FileStat`

**Security:**
- Scoped to configured base directories (no arbitrary filesystem access)
- Path traversal prevention (reject `..` escapes)
- Configurable allowed paths in `RynOptions`

**Tests:**
- [ ] Each command works on all platforms
- [ ] Path traversal attack is rejected
- [ ] Large file read/write (100MB+) doesn't OOM
- [ ] Concurrent file operations don't corrupt
- [ ] Temp directory cleanup on disposal

**Benchmarks:**
- [ ] File read throughput (small, medium, large files)
- [ ] Directory listing performance (1000+ entries)

### Milestone 3.2 — Dialog Plugin (Week 8)

**Commands:**
- `dialog.open(options)` → `string[]` (file paths)
- `dialog.save(options)` → `string`
- `dialog.message(title, message, kind)` → `void`
- `dialog.confirm(title, message)` → `bool`

**Deliverables:**
- [ ] Native file open dialog (single/multi, filters)
- [ ] Native file save dialog (filters, default name)
- [ ] Message box (info, warning, error)
- [ ] Confirmation dialog (yes/no)
- [ ] All dialogs are non-blocking (async, don't freeze the webview)

**Tests:**
- [ ] Dialog options serialize correctly
- [ ] Platform-specific dialog invocation doesn't crash
- [ ] Cancellation returns null/empty, not exception

### Milestone 3.3 — Clipboard Plugin (Week 8)

**Commands:**
- `clipboard.readText()` → `string`
- `clipboard.writeText(text)` → `void`
- `clipboard.readImage()` → `byte[]`
- `clipboard.writeImage(data)` → `void`
- `clipboard.has(kind)` → `bool`

**Tests:**
- [ ] Text round-trip (write then read)
- [ ] Image round-trip
- [ ] Empty clipboard returns empty, not exception
- [ ] Large text (10MB) handles correctly

### Milestone 3.4 — Shell Plugin (Week 9)

**Commands:**
- `shell.execute(command, args)` → `ProcessOutput`
- `shell.open(url)` → `void` (open in default browser/app)
- `shell.spawn(command, args)` → `ChildProcess` (long-running, streamed output)

**Security:**
- Command allowlist in configuration (no arbitrary shell access by default)
- Environment variable filtering

**Tests:**
- [ ] Execute returns stdout, stderr, exit code
- [ ] Open launches default browser
- [ ] Spawn streams stdout line by line
- [ ] Disallowed command is rejected
- [ ] Timeout kills spawned process

**Benchmarks:**
- [ ] Process spawn overhead
- [ ] Stdout streaming throughput

### Milestone 3.5 — Notification Plugin (Week 10)

**Commands:**
- `notification.send(title, body, options)` → `void`
- `notification.requestPermission()` → `bool`
- `notification.isPermissionGranted()` → `bool`

**Deliverables:**
- [ ] Native OS notifications (Windows toast, macOS UNUserNotification, Linux libnotify)
- [ ] Icon support
- [ ] Click callback

**Tests:**
- [ ] Notification sends without crash on all platforms
- [ ] Permission check returns correct state
- [ ] Invalid icon path handled gracefully

---

## Phase 4 — CLI Tooling (Weeks 11-13)

**Goal:** `dotnet ryn` CLI tool that scaffolds, develops, and builds Ryn applications.

### Milestone 4.1 — Project Scaffolding (Week 11)

**Commands:**
- `dotnet ryn new <name>` — create a new Ryn project
- `dotnet ryn new <name> --blazor` — create with Blazor template
- `dotnet ryn new <name> --html` — create with static HTML template

**Deliverables:**
- [ ] `dotnet new` template packages for both flavors
- [ ] Generated project includes: csproj, Program.cs, wwwroot/, appsettings.json
- [ ] Template uses latest Ryn packages from NuGet
- [ ] Validates project name and target directory
- [ ] NativeAOT-ready csproj out of the box

**Tests:**
- [ ] Template generates valid project that builds
- [ ] Template generates valid project that runs
- [ ] `--blazor` template includes Blazor dependencies
- [ ] Invalid project name is rejected with clear message

### Milestone 4.2 — Dev Mode (Week 12)

**Commands:**
- `dotnet ryn dev` — build, run, and watch for changes

**Deliverables:**
- [ ] File watcher on `wwwroot/` and `*.cs` files
- [ ] On frontend change: refresh webview (no app restart)
- [ ] On backend change: rebuild and restart app
- [ ] Dev mode injects dev tools (right-click inspect)
- [ ] Console log forwarding from webview to terminal

**Tests:**
- [ ] Frontend file change triggers webview refresh
- [ ] Backend file change triggers rebuild + restart
- [ ] Dev tools accessible in dev mode
- [ ] Console.log from webview appears in terminal

### Milestone 4.3 — Build and Package (Week 13)

**Commands:**
- `dotnet ryn build` — produce a release build
- `dotnet ryn build --aot` — produce NativeAOT build
- `dotnet ryn bundle` — package into platform installer

**Deliverables:**
- [ ] Release build with optimizations
- [ ] NativeAOT publish with trimming
- [ ] Windows: produce folder, optional MSI/MSIX via WiX
- [ ] macOS: produce .app bundle, optional DMG
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

## Phase 5 — Security Model (Week 14)

**Goal:** Configuration-driven permission system controlling what IPC commands the frontend can invoke.

### Milestone 5.1 — Capability System

**Deliverables:**
- [ ] `ryn.json` configuration file with capabilities section
- [ ] Each plugin declares required capabilities
- [ ] Capabilities are checked at command dispatch time (before handler runs)
- [ ] Denied capability returns structured error to JS
- [ ] Compile-time source generator emits capability checks
- [ ] Default: deny all. Explicit opt-in per plugin.

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
- [ ] Allowed command executes
- [ ] Denied command returns error
- [ ] Unconfigured plugin is fully denied
- [ ] Scoped paths are enforced
- [ ] Shell allowlist prevents unlisted commands
- [ ] Malformed config fails at startup with clear error

---

## Phase 6 — Polish and Ecosystem (Weeks 15-18)

### Milestone 6.1 — Auto-Updater (Week 15)

- [ ] Check for updates from configurable URL
- [ ] Download and verify update (checksum + optional code sign)
- [ ] Apply update and restart
- [ ] Configurable: silent, notify, or manual

### Milestone 6.2 — System Tray (Week 16)

- [ ] Tray icon with context menu
- [ ] Tray click events
- [ ] Minimize to tray option
- [ ] Platform-appropriate behavior (Windows: system tray, macOS: menu bar, Linux: AppIndicator)

### Milestone 6.3 — Documentation and Examples (Weeks 17-18)

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
