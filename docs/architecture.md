# Ryn Architecture Deep Dive

Technical reference for the internal architecture of Ryn. Covers the layer stack, window lifecycle, IPC pipeline, source generator, plugin system, content serving, security model, NativeAOT design, and threading model.

## 1. Architecture Layers

```
+--------------------------------------------------+
|                User Application                   |
|           (Blazor / HTML+CSS / JS)                |
+-------------------+------------------------------+
| Ryn.Ipc           | Ryn.Plugins.*                |
| Source-generated   | FileSystem, Dialog,          |
| command routing    | Clipboard, Shell,            |
| JS <> C# bridge   | Notification                 |
+-------------------+------------------------------+
| Ryn.Core                                         |
| App lifecycle, window management,                |
| configuration, plugin host, DI                   |
+--------------------------------------------------+
| Ryn.Interop                                      |
| Auto-generated P/Invoke bindings (ClangSharp)    |
| LibraryImport, NativeAOT-safe                    |
+--------------------------------------------------+
| saucer (C ABI)                                   |
| Native webview: WebView2 / WKWebView /           |
| WebKitGTK + window management                    |
+--------------------------------------------------+
```

**Data flows downward.** The user application writes HTML/CSS/JS that runs inside the native webview. JavaScript calls `window.__ryn.invoke()` which sends an XHR to the `ryn://` scheme handler in Ryn.Core. Ryn.Core dispatches to Ryn.Ipc, which routes to the appropriate source-generated handler. Results flow back through JS execution on the webview.

**Dependency direction:**
- `Ryn.Core` depends on `Ryn.Interop` (P/Invoke bindings)
- `Ryn.Ipc` depends on nothing (pure .NET, no native calls)
- `Ryn.Plugins.*` depend on `Ryn.Core` and `Ryn.Ipc`
- User apps depend on `Ryn.Core` and `Ryn.Ipc`
- `Ryn.Ipc.Generator` targets `netstandard2.0` (Roslyn analyzer requirement) and has no runtime dependency

## 2. Window Lifecycle

```
CreateBuilder() --> Build() --> RunAsync() --> [window visible] --> [user closes] --> Dispose
                                   |
                    +--------------+--------------+
                    |              |              |
              Init plugins   Create window   Run event loop
                    |              |              |
              sync-over-async  OnReady cb     blocks thread
                    |              |              |
                    |         InitializeNative    |
                    |              |              |
                    |    +--------+--------+     |
                    |    |        |        |     |
                    |  Create   Create   Apply  |
                    |  window   webview  options |
                    |    |        |        |     |
                    |    +--------+--------+     |
                    |              |              |
                    |     Register schemes       |
                    |     Inject bridge JS        |
                    |     Navigate to content     |
                    |     Show window             |
                    |              |              |
                    +--------------+--------------+
                                   |
                              OnFinish cb
                                   |
                             DisposeNative
```

### Detailed sequence

**1. Builder phase** (`RynApplicationBuilder.Build()`):

```csharp
var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts => { ... })
    .ConfigureServices(services => { ... })
    .Build();
```

`Build()` constructs the DI container in this order:
1. Build `IConfiguration` from `appsettings.json`
2. Register logging services
3. Create `RynOptions`: defaults -> config file -> programmatic overrides -> callback overrides
4. Register `RynWindowAccessor` and interface factories for `IRynWindow` / `IRynWebView`
5. Execute user `ConfigureServices` callbacks
6. Build `ServiceProvider`
7. Resolve all `IRynPlugin` instances and attach to the application

**2. Plugin initialization** (`RynApplication.RunAsync()`):

Before the native event loop starts, each plugin's `InitializeAsync` is called synchronously (sync-over-async). This is intentional -- no event loop exists yet, so there is no deadlock risk:

```csharp
// From RynApplication.RunAsync():
foreach (var plugin in _plugins)
{
    plugin.InitializeAsync(cts.Token).AsTask().GetAwaiter().GetResult();
}
```

**3. Native window creation** (`RynWindow.Run()`):

`Run()` calls `saucer_application_run()`, which starts the platform event loop and calls back into `OnReady`. Inside `OnReady`, `InitializeNative()` creates the native window and webview, applies options (title, size, decorations, icon), registers the `ryn://` scheme handler, injects the JS bridge script, navigates to the content source, and shows the window.

**4. Event loop**: `saucer_application_run()` blocks the calling thread until the application exits. On macOS, this is the AppKit run loop on the main thread.

**5. Shutdown**: When the window closes, `OnWindowClosed` fires, which calls `saucer_application_quit()`. The run loop returns, `DisposeNative()` frees all saucer handles, and `RunAsync()` completes.

### Window events

`RynWindow` subscribes to native saucer events via unmanaged function pointers:

| Saucer event | C# event | JS event |
|--------------|----------|----------|
| `SAUCER_WINDOW_EVENT_CLOSE` | `Closing` (cancelable) | `window.closeCancelled` |
| `SAUCER_WINDOW_EVENT_CLOSED` | `Closed` | -- |
| `SAUCER_WINDOW_EVENT_RESIZE` | `Resized` | `window.resized` |
| `SAUCER_WINDOW_EVENT_FOCUS` | `Focused` / `Blurred` | `window.focused` / `window.blurred` |
| `SAUCER_WINDOW_EVENT_MAXIMIZE` | `StateChanged` | `window.stateChanged` |
| `SAUCER_WINDOW_EVENT_MINIMIZE` | `StateChanged` | `window.stateChanged` |

Window position changes are detected by polling after resize/focus events via `CheckPositionChanged()`.

## 3. IPC Pipeline

The full round-trip for a command invocation:

```
JavaScript                    ryn:// scheme handler              C# backend
----------                    --------------------              ----------

invoke('greet', {name:'W'})
  |
  +-- new Promise(resolve, reject)
  |   store in pending[id]
  |   set 30s timeout
  |
  +-- XHR POST /ipc/cmd/{id}/greet
      body: {"name":"W"}
                              |
                    HandleAppSchemeRequest()
                    Parse URL: /ipc/cmd/1/greet
                    ReadRequestBody() -> {"name":"W"}
                    AcceptEmptyResponse() [immediate 200]
                              |
                    DispatchCommandAsync(1, "greet", body)
                              |                            Task.Run() -->
                              |                              |
                              |                        dispatcher.DispatchAsync()
                              |                              |
                              |                        capabilities.ThrowIfDenied()
                              |                              |
                              |                        router.CanRoute("greet") -> true
                              |                              |
                              |                        router.RouteAsync()
                              |                              |
                              |                        JsonDocument.Parse(args)
                              |                        root.GetProperty("name").GetString()
                              |                              |
                              |                        AppCommands.Greet("W")
                              |                        -> "Hello, W!"
                              |                              |
                              |                        return __ToJson(result)
                              |                              |
                    <-- result = "\"Hello, W!\""
                              |
                    EscapeForJs(result)
                    ExecuteOnUiThread(
                      "window.__ryn._resolve(1,true,'...')"
                    )
                              |
                    saucer_application_post() -> main thread
                    saucer_webview_execute()
                              |
_resolve(1, true, data)       |
  |
  +-- clearTimeout
  +-- delete pending[1]
  +-- JSON.parse(data)
  +-- resolve("Hello, W!")
```

### Key design decisions

**XHR, not fetch**: The bridge uses `XMLHttpRequest` because it works reliably with custom URL schemes across all webview implementations. Fetch has inconsistent behavior with non-HTTP schemes in some WebKit versions.

**Immediate 200 response**: The scheme handler returns an empty 200 immediately after receiving the request. The actual result is delivered later by calling `saucer_webview_execute()` to run `window.__ryn._resolve()`. This avoids blocking the webview's resource loading thread.

**Same-origin via ryn:// scheme**: Both content and IPC use the `ryn://app` origin. This eliminates CORS issues that arise when WebKit assigns a `null` origin to custom schemes.

**30-second timeout**: Each pending invocation has a `setTimeout` guard. If the C# handler takes longer than 30 seconds, the promise rejects with an `IPC timeout` error.

### JavaScript eval (C# to JS)

The reverse direction -- evaluating JavaScript from C# -- uses a similar pattern:

```csharp
var result = await webView.EvaluateJavaScriptAsync("document.title");
```

1. C# base64-encodes the script
2. Calls `saucer_webview_execute()` with `window.__ryn.eval(id, base64)`
3. JS decodes, evals, and sends the result back via XHR POST to `/ipc/eval/{id}/{ok}`
4. C# resolves the `TaskCompletionSource` associated with that eval ID

## 4. Source Generator

The `Ryn.Ipc.Generator` is a Roslyn `IIncrementalGenerator` targeting `netstandard2.0`. It finds classes containing `[RynCommand]` methods and emits two things per class: a router and a DI extension method.

### What [RynCommand] produces

Given this user code:

```csharp
public static class AppCommands
{
    [RynCommand]
    public static string Greet(string name) => $"Hello, {name}!";

    [RynCommand]
    public static int Add(int a, int b) => a + b;
}
```

The generator emits:

```csharp
// <auto-generated />
file sealed class AppCommandsRouter : ICommandRouter
{
    public bool CanRoute(string command) => command is "greet" or "add";

    public async ValueTask<string> RouteAsync(
        string command, ReadOnlyMemory<byte> args,
        IServiceProvider services, CancellationToken cancellationToken)
    {
        switch (command)
        {
            case "greet":
            {
                using var __doc = JsonDocument.Parse(args);
                var __root = __doc.RootElement;
                var __p0 = __root.GetProperty("name").GetString()!;
                var __result = AppCommands.Greet(__p0);
                return __ToJson(__result);
            }
            case "add":
            {
                using var __doc = JsonDocument.Parse(args);
                var __root = __doc.RootElement;
                var __p0 = __root.GetProperty("a").GetInt32();
                var __p1 = __root.GetProperty("b").GetInt32();
                var __result = AppCommands.Add(__p0, __p1);
                return __result.ToString(CultureInfo.InvariantCulture);
            }
            default:
                throw new RynCommandNotFoundException(command);
        }
    }

    // JSON string escaper (handles \, ", \n, \r, \t, \b, \f, unicode)
    private static string __ToJson(string? value) { ... }
}

public static class AppCommandsRynExtensions
{
    public static IServiceCollection AddAppCommands(this IServiceCollection services)
    {
        services.AddSingleton<ICommandRouter, AppCommandsRouter>();
        return services;
    }
}
```

### Routing mechanics

The `ICommandRouter` interface has two methods:

```csharp
public interface ICommandRouter
{
    bool CanRoute(string command);
    ValueTask<string> RouteAsync(string command, ReadOnlyMemory<byte> args,
        IServiceProvider services, CancellationToken cancellationToken);
}
```

`RynCommandDispatcher` holds an array of all registered routers and iterates linearly to find the first one that can route a given command:

```csharp
for (var i = 0; i < _routers.Length; i++)
{
    if (_routers[i].CanRoute(command))
    {
        _capabilities.ThrowIfDenied(command);
        return await _routers[i].RouteAsync(command, args, services, cancellationToken);
    }
}
throw new RynCommandNotFoundException(command);
```

`CanRoute()` uses a pattern-match (`command is "greet" or "add"`), which the JIT compiles into an efficient comparison. `RouteAsync()` uses a `switch` statement for O(1) dispatch.

### Command naming

By default, method names are converted to camelCase with dots for plugin prefixes. The convention is:

- Class `AppCommands` with method `Greet` -> command name `greet`
- Class `FileSystemCommands` (in a plugin with prefix `fs`) with method `ReadTextFile` -> command name `fs.readTextFile`

You can override the name with `[RynCommand("customName")]`.

### Parameter type support

| Type | Extraction method |
|------|-------------------|
| `int` | `.GetProperty("name").GetInt32()` |
| `long` | `.GetProperty("name").GetInt64()` |
| `float` | `.GetProperty("name").GetSingle()` |
| `double` | `.GetProperty("name").GetDouble()` |
| `bool` | `.GetProperty("name").GetBoolean()` |
| `string` | `.GetProperty("name").GetString()!` |
| `int[]`, `string[]` | `.GetProperty("name").EnumerateArray().Select(e => e.GetXxx()).ToArray()` |
| `int?`, `bool?` | Null-check + `.GetXxx()` |
| `JsonElement` | `.GetProperty("name")` (raw, for manual deserialization) |
| Custom DTOs | `JsonSerializer.Deserialize(raw, Context.Default.MyDto)` (requires `[RynJsonContext]`) |
| `CancellationToken` | Must be the last parameter, auto-wired from the dispatcher |

### Compile-time diagnostics

| Code | Description |
|------|-------------|
| RYN001 | Method must be accessible (public or internal) |
| RYN002 | Duplicate command name in the same class |
| RYN003 | Unsupported return type |
| RYN004 | `CancellationToken` must be the last parameter |
| RYN005 | Unsupported parameter type |

## 5. Plugin System

### IRynPlugin interface

```csharp
public interface IRynPlugin
{
    string Name { get; }
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);
}
```

Every plugin implements `IRynPlugin`. `InitializeAsync` is called once before the window opens -- use it for one-time setup (checking platform support, validating configuration, etc.).

### Plugin structure

Each plugin follows a consistent vertical slice:

```
Ryn.Plugins.Clipboard/
  ClipboardPlugin.cs                    -- IRynPlugin implementation
  ClipboardCommands.cs                  -- [RynCommand] methods
  ClipboardOptions.cs                   -- Configuration (if needed)
  ServiceCollectionExtensions.cs        -- .AddRynClipboard() extension
```

### DI registration

Plugin extension methods register both the plugin and its generated command router:

```csharp
public static IServiceCollection AddRynClipboard(this IServiceCollection services)
{
    services.AddSingleton<ClipboardPlugin>();
    services.AddSingleton<IRynPlugin>(sp => sp.GetRequiredService<ClipboardPlugin>());
    services.AddClipboardCommands();  // Source-generated router registration
    return services;
}
```

The dual registration pattern (`AddSingleton<ClipboardPlugin>()` + `AddSingleton<IRynPlugin>(...)`) ensures the plugin is both resolvable by concrete type (for configuration) and discoverable via `GetServices<IRynPlugin>()` during initialization.

### Init lifecycle

1. `Build()` resolves all `IRynPlugin` from DI and adds them to the application's plugin list
2. `RunAsync()` iterates the list and calls `InitializeAsync()` on each, synchronously, before the event loop starts
3. If a plugin throws during init, the exception is caught, logged, and the app continues (fail-open for non-critical plugins)

### Plugin commands and prefixes

Plugin commands use a dot-separated prefix: `fs.readTextFile`, `clipboard.writeText`, `shell.execute`. The prefix maps to a capability rule in `ryn.json`. The source generator derives command names from method names, and the plugin's command class conventionally uses `[RynCommand("prefix.methodName")]` to set the full qualified name.

## 6. Content Serving Modes

`RynOptions` supports four mutually exclusive content sources, evaluated in this priority order in `InitializeNative()`:

### 1. External URL (`opts.Url`)

```csharp
opts.Url = new Uri("http://localhost:5173");
```

Navigates the webview directly to the URL. Used for Vite/webpack dev server integration. The URL's origin is automatically added to the allowed origins list for CORS.

### 2. Local HTTP server (`opts.UseLocalServer + opts.ContentDirectory`)

```csharp
opts.ContentDirectory = "wwwroot";
opts.UseLocalServer = true;
```

Starts an embedded HTTP server that serves files from the content directory. The webview navigates to the server's local URL. Useful when the custom scheme causes compatibility issues with certain JavaScript frameworks.

### 3. Content directory via custom scheme (`opts.ContentDirectory`)

```csharp
opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
```

Files are served through the `ryn://app/` scheme handler. When a request arrives, `HandleAppSchemeRequest` resolves the URL path to a file:

```csharp
// From RynWebView.HandleAppSchemeRequest():
var relativePath = (path is "/" or "") ? "index.html" : path.TrimStart('/');
var filePath = Path.GetFullPath(Path.Combine(_contentDirectory, relativePath));
var canonicalBase = Path.GetFullPath(_contentDirectory + Path.DirectorySeparatorChar);

if (filePath.StartsWith(canonicalBase, comparison) && File.Exists(filePath))
{
    ServeFile(executor, filePath);
    return;
}
```

Path traversal is prevented by checking that the resolved path starts with the canonical content directory base. MIME types are determined by file extension.

### 4. Inline HTML (`opts.Html`)

```csharp
opts.Html = "<html><body>Hello</body></html>";
```

The HTML string is stored in memory and served via the scheme handler when `/` or `/index.html` is requested. The webview navigates to `ryn://app/index.html`.

## 7. Security Model

### Capabilities (ryn.json)

The capability system controls which IPC commands the frontend can invoke. It operates at the `RynCommandDispatcher` level, before any handler code runs.

**No ryn.json** = allow all (dev mode). **ryn.json present** = deny by default.

```json
{
  "capabilities": {
    "fs": {
      "allow": ["readTextFile", "readDir"],
      "deny": ["remove"],
      "scope": ["$APP_DATA"]
    },
    "shell": {
      "allow": ["execute"],
      "commands": ["echo", "git"]
    },
    "clipboard": true,
    "notification": true
  }
}
```

### Capability resolution

`RynCapabilities` is loaded at startup by `RynCapabilitiesLoader` and registered as a singleton. Each command dispatch goes through `ThrowIfDenied()`:

```csharp
public void ThrowIfDenied(string command)
{
    if (!_enforced) return;   // No ryn.json -- allow all

    // Internal framework commands bypass checks
    if (command.StartsWith("__ryn.", StringComparison.Ordinal))
        return;

    // Split "fs.readTextFile" into prefix "fs" and suffix "readTextFile"
    var dotIndex = command.IndexOf('.', StringComparison.Ordinal);
    var prefix = command[..dotIndex];
    var suffix = command[(dotIndex + 1)..];

    // Plugin not in capabilities -> denied
    if (!_rules.TryGetValue(prefix, out var rule))
        throw new RynCommandDeniedException(command, ...);

    // Rule check: AllowAll, Allow set, Deny set
    if (!rule.IsAllowed(suffix))
        throw new RynCommandDeniedException(command, ...);
}
```

`CapabilityRule.IsAllowed()` logic:

- `"clipboard": true` -> `AllowAll = true`, all commands pass (unless in `Deny`)
- `"fs": { "allow": ["readTextFile"] }` -> only `readTextFile` passes
- `"fs": { "allow": ["readTextFile"], "deny": ["readTextFile"] }` -> deny wins over allow

### Scoped capabilities

`scope` and `commands` in `ryn.json` restrict plugin-level configuration:

- `scope` clamps the plugin's allowed filesystem paths to the listed directories
- `commands` clamps the shell plugin's allowed command list

These restrictions are applied at the plugin options level -- `RynCapabilities.GetScope()` returns the scope constraints, and plugin registration code intersects them with programmatic options.

### Path validation

The FileSystem plugin uses `PathValidator` with multiple defenses:

1. **Allowed paths list**: only paths under configured directories are accessible
2. **Path traversal prevention**: rejects paths containing `..` that escape the allowed directory
3. **Canonical path comparison**: `Path.GetFullPath()` resolves symlinks and relative segments before comparison

### CORS origin validation

The scheme handler validates the `Origin` header on IPC requests:

```csharp
var requestOrigin = ParseRequestOrigin(request);
var matchedOrigin = ResolveAllowedOrigin(requestOrigin);

if (path.StartsWith("/ipc/", ...) && matchedOrigin is null && requestOrigin is not null)
{
    Saucer.saucer_scheme_executor_reject(executor, SAUCER_SCHEME_ERROR_FAILED);
    return;
}
```

Allowed origins default to `ryn://app`. When `opts.Url` is set, the URL's origin is added automatically. Additional origins can be configured via `opts.AllowedOrigins`.

## 8. NativeAOT Considerations

Ryn is designed NativeAOT-first. Every design decision accounts for the constraints of ahead-of-time compilation.

### No reflection anywhere

- **IPC routing**: Source-generated `switch` statements, not `MethodInfo.Invoke()`
- **JSON serialization**: All plugins use STJ source-generated `JsonSerializerContext` (e.g., `FsJsonContext`, `ShellJsonContext`). User apps with complex DTOs provide their own context via `[RynJsonContext]`
- **DI registration**: Explicit `AddSingleton<T>()` calls, not assembly scanning
- **Configuration binding**: Manual property assignment in `BindOptionsFromConfiguration()`, not `configuration.Bind(options)` (which uses reflection)

### Source generator targeting

`Ryn.Ipc.Generator` targets `netstandard2.0` as required by the Roslyn analyzer infrastructure. It has no runtime dependency -- it only produces source code at compile time.

### Trim safety

All projects pass trim analysis with zero warnings. The generator avoids emitting any code that would trigger trim warnings:

- No `Type.GetType()` or `Assembly.Load()`
- No `Activator.CreateInstance()`
- No `Expression.Compile()`
- `DynamicallyAccessedMembers` annotations where DI requires constructor access

### Binary size

A hello-world Ryn app with NativeAOT produces a ~4.3 MB binary on macOS arm64. This includes the .NET runtime, the app, and the saucer native library.

## 9. Threading Model

### Main thread (saucer event loop)

`saucer_application_run()` blocks the calling thread and runs the platform event loop:
- **macOS**: AppKit `NSApplication` run loop on the main thread (required by AppKit)
- **Windows**: Win32 message pump (requires STA thread for COM/WebView2)
- **Linux**: GLib main loop

All saucer API calls that modify the window or webview must happen on this thread.

### Command execution (thread pool)

IPC commands are dispatched to the thread pool via `Task.Run()`:

```csharp
// From RynWebView.DispatchCommandAsync():
var result = await Task.Run(async () =>
    await _commandHandler(command, args, CancellationToken.None)
        .ConfigureAwait(false))
    .ConfigureAwait(false);
```

This prevents long-running commands from blocking the UI thread. The scheme handler returns an empty response immediately, and the result is delivered asynchronously.

### UI thread marshaling

When command results need to update the webview (calling `saucer_webview_execute()`), they must be marshaled back to the main thread:

```csharp
private void ExecuteOnUiThread(string js)
{
    if (_disposed || _app == 0 || _webview == 0) return;

    var callbackData = NativeCallbackHelper.Alloc((Action)(() =>
    {
        var str = Utf8String.Create(js, buf);
        Saucer.saucer_webview_execute((saucer_webview*)webview, str.Pointer);
        str.Dispose();
    }));

    Saucer.saucer_application_post(
        (saucer_application*)app,
        &ExecutePostedAction,
        callbackData);
}
```

`saucer_application_post()` enqueues the callback on the main thread's event loop, similar to `Control.BeginInvoke()` in WinForms or `DispatchQueue.main.async()` in Swift.

### Shared state synchronization

State shared between the UI thread and worker threads uses lock-free primitives:

- **`volatile` fields**: `_disposed`, `_cachedTitle`, `_cachedResizable` -- reads/writes are always fresh
- **`Interlocked` operations**: `_nextEvalId` uses `Interlocked.Increment()` for atomic ID generation
- **`ConcurrentDictionary`**: `_pendingEvals` maps eval IDs to `TaskCompletionSource` instances, accessed from both the UI thread (when JS sends eval results) and worker threads (when C# initiates an eval)
- **`TaskCompletionSource` with `RunContinuationsAsynchronously`**: prevents continuations from running inline on the completing thread, avoiding deadlocks when the completing thread is the UI thread

### Thread safety summary

| Component | Thread | Synchronization |
|-----------|--------|-----------------|
| Saucer window/webview API | Main thread only | Enforced by `ExecuteOnUiThread` |
| IPC command handlers | Thread pool | `Task.Run()` |
| Eval ID generation | Any thread | `Interlocked.Increment` |
| Pending evals map | Main + worker | `ConcurrentDictionary` |
| Window property cache | Main + worker | `volatile` fields |
| Plugin init | Main thread | Sequential, before event loop |
| Event handlers (C#) | Main thread | Invoked from saucer callbacks |
