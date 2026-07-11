# Getting Started with Ryn

This guide walks you through creating your first Ryn desktop application -- from installing the CLI to bundling for distribution.

## Prerequisites

**Required:**

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview or later)

**Platform-specific:**

| Platform | Requirement |
|----------|-------------|
| macOS | Xcode Command Line Tools (WKWebView is built-in) |
| Windows | [WebView2 Runtime](https://developer.microsoft.com/en-us/microsoft-edge/webview2/) (bundled with Windows 11, separate install on Windows 10) |
| Linux | `libwebkitgtk-6.0-dev` -- install with `sudo apt-get install libwebkitgtk-6.0-dev` on Ubuntu/Debian |

Verify your .NET SDK version:

```bash
dotnet --version
# Should output 10.0.x or higher
```

## 1. Install

### From NuGet (recommended)

Install the CLI:

```bash
dotnet tool install -g Ryn.Cli
```

Verify:

```bash
ryn --help
ryn doctor
```

### From Source

If you want to build Ryn itself or contribute:

```bash
git clone --recursive https://github.com/Yupmoh/Ryn.git
cd Ryn
bash build/download-native.sh   # macOS/Linux (or .\build\download-native.ps1 on Windows)
dotnet build Ryn.slnx
dotnet test Ryn.slnx
```

When working from source, the CLI is available via `dotnet run --project src/Ryn.Cli --`. For example: `dotnet run --project src/Ryn.Cli -- new MyApp`. Projects created from within the source tree automatically use project references instead of NuGet packages.

## 2. Create Your First App

```bash
ryn new MyApp
```

This scaffolds a complete Ryn project with IPC commands, a dark-themed HTML frontend, and a capability file. You will see output like:

```
Creating Ryn project 'MyApp'...
  Using NuGet package references
  Created project files
  Restoring packages...

  Project 'MyApp' created successfully!

  cd MyApp
  ryn dev
```

Project names must start with a letter and contain only letters, digits, and underscores.

### Vite frontend (optional)

If you prefer a Vite + TypeScript frontend instead of plain HTML:

```bash
ryn new MyApp --vite
```

This creates an additional `frontend/` directory with a Vite project, TypeScript config, and typed `window.__ryn` declarations.

## 3. Project Structure

After scaffolding, your project looks like this:

```
MyApp/
  MyApp.csproj        -- Project file with Ryn package references
  Program.cs          -- Entry point: creates the app builder, configures options, runs the app
  Commands.cs         -- Your [RynCommand] methods (C# backend logic callable from JS)
  appsettings.json    -- Window title, size, and logging config
  ryn.json            -- Capability-based security (controls what JS can access)
  wwwroot/
    index.html        -- Your frontend (HTML/CSS/JS)
```

### What each file does

**Program.cs** -- Configures and launches the application:

```csharp
using Ryn.Core;
using Ryn.Ipc;
using MyApp;

public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            .ConfigureOptions(opts =>
            {
                opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            })
            .ConfigureServices(services =>
            {
                services.AddRynCommands();    // Registers the IPC dispatcher
                services.AddAppCommands();    // Registers your [RynCommand] methods (source-generated)
            })
            .Build();

        app.Run();
    }
}
```

**Commands.cs** -- Your backend logic, exposed to JavaScript via `[RynCommand]`:

```csharp
using System.Globalization;
using Ryn.Ipc;

namespace MyApp;

public static class AppCommands
{
    [RynCommand("app.greet")]
    public static string Greet(string name) => $"Hello, {name}!";

    [RynCommand("app.add")]
    public static int Add(int a, int b) => a + b;

    [RynCommand("app.getTime")]
    public static string GetTime() => DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
}
```

Command names are plugin-prefixed: your own commands live under `app.*`, and each plugin owns its own prefix (`fs.*`, `clipboard.*`, ...). The prefix is what `ryn.json` capabilities grant or deny.

**appsettings.json** -- Window and logging configuration:

```json
{
  "Ryn": {
    "Title": "MyApp",
    "Width": 900,
    "Height": 700,
    "DevTools": true
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information"
    }
  }
}
```

**ryn.json** -- Security: controls which plugins and commands the frontend can invoke. The scaffolded file grants only your own `app.*` commands:

```json
{
  "capabilities": {
    "app": true
  }
}
```

Each plugin is granted by its prefix. A fuller example that also enables a couple of plugins:

```json
{
  "capabilities": {
    "app": true,
    "fs": {
      "allow": ["readTextFile", "readDir", "exists", "stat"]
    },
    "clipboard": true,
    "notification": true
  }
}
```

When `ryn.json` is present, all commands are denied by default unless explicitly allowed. When it is **absent**, the fallback depends on the build: a **Debug** build allows everything (dev convenience), while a **Release** build **fails closed and denies every command** (logging a one-time startup warning). Always ship a `ryn.json` with your app — see [SECURITY.md](../SECURITY.md) for the full capability model.

## 4. Run in Dev Mode

```bash
cd MyApp
ryn dev
```

Dev mode does three things:

1. **Builds** the project
2. **Launches** the app window
3. **Watches** for file changes:
   - C# file changes (`.cs`) trigger a full rebuild and relaunch
   - Frontend file changes (`wwwroot/`) sync to the output directory and relaunch without rebuilding

For a Vite project, `ryn dev` also auto-starts the Vite dev server (`npm run dev`) and points the webview at it — running `npm run dev` yourself in a second terminal is optional now (if a server is already listening on the Vite port, `ryn dev` reuses it). See the [Vite Integration Guide](vite-integration.md).

Press `Ctrl+C` to stop.

During development, you can also use standard `dotnet run`:

```bash
dotnet run
```

## 5. Add an IPC Command

IPC (inter-process communication) lets your JavaScript frontend call C# methods and get results back.

### C# side

Add a method to `Commands.cs` with the `[RynCommand]` attribute. Give it an `app.*` name so it falls under the `"app"` capability:

```csharp
[RynCommand("app.getFruits")]
public static string[] GetFruits() => ["Apple", "Banana", "Cherry"];

[RynCommand("app.fetchData")]
public static async ValueTask<string> FetchData(string url, CancellationToken cancellationToken)
{
    using var client = new HttpClient();
    return await client.GetStringAsync(url, cancellationToken);
}
```

The source generator automatically creates a router and DI registration at compile time. Supported parameter and return types:

- Primitives: `int`, `long`, `float`, `double`, `bool`, `string`
- Arrays: `int[]`, `string[]`, etc.
- Nullable: `int?`, `bool?`, etc.
- `JsonElement` (for manual deserialization of complex types)
- `CancellationToken` (must be the last parameter, auto-wired)
- `void` and `ValueTask` returns (for fire-and-forget commands)
- Custom DTOs via `[RynJsonContext]` and STJ source generation

### JavaScript side

Call the command from your frontend using `window.__ryn.invoke()`:

```javascript
// Simple call
const greeting = await window.__ryn.invoke('app.greet', { name: 'World' });

// Array return
const fruits = await window.__ryn.invoke('app.getFruits', {});

// Async call
const data = await window.__ryn.invoke('app.fetchData', { url: 'https://api.example.com/data' });
```

The name you pass to `[RynCommand(...)]` is exactly the name JavaScript invokes. The prefix (`app` here) is what `ryn.json` capabilities grant or deny, so keep your own commands under `app.*`. Parameters are passed as a JSON object whose keys match the C# parameter names (camelCase).

You can use any prefixed name you like:

```csharp
[RynCommand("app.doSomething")]
public static string DoSomething() => "done";
```

```javascript
const result = await window.__ryn.invoke('app.doSomething', {});
```

### Events

Ryn also supports events from C# to JavaScript:

```csharp
// C# side: emit an event.
// Preferred — strongly typed, serialized via source-generated JsonTypeInfo (AOT- and injection-safe):
webView.EmitEvent("dataUpdated", new Update(42), AppJsonContext.Default.Update);

// String overload — the payload MUST be valid JSON; it is validated/canonicalized to prevent
// script injection. An invalid JSON string throws ArgumentException.
webView.EmitEvent("dataUpdated", "{\"count\": 42}");
```

```javascript
// JS side: listen for events
window.__ryn.on('dataUpdated', (data) => {
    console.log('Data updated:', data.count);
});

// Unsubscribe
window.__ryn.off('dataUpdated', handler);
```

## 6. Use a Plugin

Ryn includes built-in plugins for common native operations. Here is how to add the Clipboard plugin.

### Install the package

```bash
dotnet add package Ryn.Plugins.Clipboard
```

### Register in Program.cs

```csharp
using Ryn.Plugins.Clipboard;

var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    })
    .ConfigureServices(services =>
    {
        services.AddRynCommands();
        services.AddAppCommands();
        services.AddRynClipboard();       // Add the clipboard plugin
    })
    .Build();

app.Run();
```

### Allow in ryn.json

Add clipboard to your capabilities:

```json
{
  "capabilities": {
    "clipboard": true
  }
}
```

### Use from JavaScript

```javascript
// Write to clipboard
await window.__ryn.invoke('clipboard.writeText', { text: 'Hello from Ryn!' });

// Read from clipboard
const text = await window.__ryn.invoke('clipboard.readText', {});

// Check if clipboard has text
const hasText = await window.__ryn.invoke('clipboard.hasText', {});
```

### Available plugins

| Plugin | Package | Registration | Commands |
|--------|---------|-------------|----------|
| FileSystem | `Ryn.Plugins.FileSystem` | `AddRynFileSystem(opts => ...)` | `fs.readTextFile`, `fs.writeTextFile`, `fs.readDir`, `fs.stat`, `fs.exists`, `fs.mkdir`, `fs.remove` |
| Dialog | `Ryn.Plugins.Dialog` | `AddRynDialog()` | `dialog.message`, `dialog.confirm`, `dialog.openFile`, `dialog.openFiles`, `dialog.openFolder`, `dialog.save` |
| Clipboard | `Ryn.Plugins.Clipboard` | `AddRynClipboard()` | `clipboard.readText`, `clipboard.writeText`, `clipboard.hasText`, `clipboard.clear` |
| Shell | `Ryn.Plugins.Shell` | `AddRynShell(opts => ...)` | `shell.execute`, `shell.open`, `shell.spawn`, `shell.kill`, `shell.pty`, `shell.ptyWrite`, `shell.ptyResize`, `shell.ptyMetrics`, `shell.ptyKill` |
| Notification | `Ryn.Plugins.Notification` | `AddRynNotification()` | `notification.send`, `notification.isSupported`, `notification.requestPermission` |
| Audio | `Ryn.Plugins.Audio` | `AddRynAudio()` | `audio.play`, `audio.playSystem`, `audio.stop`, `audio.setVolume`, `audio.isPlaying` |
| Tray | `Ryn.Plugins.Tray` | `AddRynTray(opts => ...)` | `tray.show`, `tray.hide`, `tray.setTooltip`, `tray.setMenu`, `tray.notify` |
| MenuBar | `Ryn.Plugins.MenuBar` | `AddRynMenuBar(opts => ...)` | `menubar.setMenu`, `menubar.reset` |
| Badge | `Ryn.Plugins.Badge` | `AddRynBadge()` | `badge.set`, `badge.setCount`, `badge.clear` |
| GlobalShortcut | `Ryn.Plugins.GlobalShortcut` | `AddRynGlobalShortcut()` | `globalShortcut.register`, `globalShortcut.unregister`, `globalShortcut.isRegistered`, `globalShortcut.unregisterAll` |
| WebViewPane | `Ryn.Plugins.WebViewPane` | `AddRynWebViewPane()` | `webviewPane.open`, `webviewPane.close`, `webviewPane.setBounds`, `webviewPane.navigate`, `webviewPane.back`, `webviewPane.forward`, `webviewPane.reload`, `webviewPane.setZoom`, `webviewPane.setDevTools`, `webviewPane.execute`, `webviewPane.eval`, `webviewPane.url`, `webviewPane.list` |
| Updater | `Ryn.Plugins.Updater` | `AddRynUpdater(opts => ...)` | `updater.check`, `updater.download`, `updater.apply` |

The file pickers (`dialog.openFile`, `dialog.openFiles`, `dialog.openFolder`, `dialog.save`) resolve to
the picked path (`openFiles`: a JSON array of paths), `null` when the user cancels, and reject with an
error when the dialog itself fails. The initial path is best-effort: a leading `~` expands, a file path
means its directory, and unusable paths (relative or nonexistent) fall back to the platform's default
location instead of failing.

> **`shell.pty` platform support.** The PTY commands use ConPTY on Windows (Windows 10 1809+) and a native `ryn-pty` shim on macOS and Linux. If that native shim is not present next to the application, `shell.pty` throws a clear `PlatformNotSupportedException` rather than falling back to an unsafe path. The non-PTY `shell.execute`/`shell.open`/`shell.spawn` commands work on all three platforms.

Plugins that access the filesystem or shell require configuration for safety:

```csharp
services.AddRynFileSystem(fs =>
    fs.AllowedPaths.Add(AppContext.BaseDirectory));

services.AddRynShell(shell =>
    shell.AllowedCommands.AddRange(["echo", "git", "ls"]));
```

## 7. Build for Production

### Standard release build

```bash
ryn build
```

This runs `dotnet publish -c Release` and outputs the result to `bin/Release/net10.0/publish/`.

### NativeAOT build

```bash
ryn build --aot
```

NativeAOT produces a single native binary with no .NET runtime dependency. A hello-world app is around 5.0 MB on macOS arm64; a full app pulling in every plugin is around 5.6 MB. Ryn is designed NativeAOT-first -- no reflection is used anywhere. JSON serialization uses source-generated `JsonSerializerContext`, and IPC routing uses a source-generated switch-based dispatch table.

### Embedded content

```bash
ryn build --embed
```

Bundles your `wwwroot/` directory into the binary for single-file distribution.

## 8. Bundle for Distribution

```bash
ryn bundle
```

This builds a release and creates a platform-appropriate distributable:

| Platform | Output | Location |
|----------|--------|----------|
| macOS | `.app` bundle with `Info.plist` | `bin/bundle/MyApp.app` |
| Windows | Folder with executable + WiX `.wxs` for MSI | `bin/bundle/MyApp/` |
| Linux | AppDir structure (ready for `appimagetool`) | `bin/bundle/MyApp.AppDir/` |

### Bundle options

```bash
ryn bundle --aot                      # NativeAOT publish
ryn bundle --self-contained           # Include .NET runtime
ryn bundle --icon path/to/icon.png    # Override the app icon (PNG auto-converted to .icns/.ico)
ryn bundle --sign "Developer ID"      # Code sign (macOS)
ryn bundle --notarize                 # Submit for Apple notarization (macOS)
ryn bundle --version 1.0.0            # Set bundle version
```

If you don't pass `--icon` (or set `bundle.icon` in `ryn.json`), the bundle is branded with the Ryn default icon — a real `AppIcon.icns` on macOS, an `.ico` on Windows, and a hicolor PNG on Linux. At runtime every window also uses the Ryn icon by default; override it with `RynOptions.IconPath`.

You can also configure bundle metadata in `ryn.json`:

```json
{
  "capabilities": { ... },
  "bundle": {
    "identifier": "com.example.myapp",
    "version": "1.0.0",
    "icon": "assets/icon.png"
  }
}
```

### Creating an MSI (Windows)

After bundling, the output directory contains a generated WiX `.wxs` file:

```powershell
dotnet tool install --global wix
cd bin\bundle\MyApp
wix build MyApp.wxs -o MyApp.msi
```

### Creating an AppImage (Linux)

If `appimagetool` is in your `PATH`, `ryn bundle` builds the AppImage automatically. Otherwise:

```bash
bash bin/bundle/build-appimage.sh
```

## 9. Content Serving Modes

Ryn supports three ways to provide frontend content:

```csharp
// Option 1: ContentDirectory -- serve files from disk (recommended for most apps)
opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");

// Option 2: Html -- inline HTML string (good for simple tools or generated UI)
opts.Html = "<html><body><h1>Hello</h1></body></html>";

// Option 3: Url -- external URL (for Vite/webpack dev server integration)
opts.Url = new Uri("http://localhost:5173");
```

With `ContentDirectory`, files are served through the `ryn://` custom scheme, keeping IPC same-origin. Changes to files on disk are reflected on browser refresh without restarting the app.

## 10. Windows Compatibility Note

On Windows, the entry point **must** use `[STAThread]` with a synchronous `Main` method. Without it, WebView2 initialization deadlocks silently.

```csharp
public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            // ...
            .Build();

        app.Run();  // Synchronous -- blocks until window closes
    }
}
```

Do **not** use `async Task Main` or top-level statements on Windows -- both default to MTA, which is incompatible with WebView2's COM requirements. On macOS and Linux, `await app.RunAsync()` and top-level statements work fine.

## Next Steps

- [Plugin Authoring Guide](plugin-authoring.md) -- Create your own Ryn plugins with commands, options, DI, and capability scopes
- [Vite Integration Guide](vite-integration.md) -- Use Vite or other JS bundlers as your frontend toolchain
