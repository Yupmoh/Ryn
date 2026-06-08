<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Yupmoh/Ryn/main/assets/logo.png">
    <img alt="ryn — Rich Yet Native" src="https://raw.githubusercontent.com/Yupmoh/Ryn/main/assets/logo-light.png" width="460">
  </picture>
</p>

<p align="center">
  <a href="https://github.com/Yupmoh/Ryn/actions/workflows/build.yml"><img alt="Build &amp; Test" src="https://github.com/Yupmoh/Ryn/actions/workflows/build.yml/badge.svg"></a>
  <a href="https://github.com/Yupmoh/Ryn/actions/workflows/aot.yml"><img alt="NativeAOT" src="https://github.com/Yupmoh/Ryn/actions/workflows/aot.yml/badge.svg"></a>
  <a href="https://www.nuget.org/packages/Ryn"><img alt="NuGet" src="https://img.shields.io/nuget/vpre/Ryn.svg"></a>
  <a href="LICENSE"><img alt="License: MIT" src="https://img.shields.io/badge/License-MIT-blue.svg"></a>
</p>

<p align="center">
  <strong>Rich Yet Native</strong> — a cross-platform, lightweight .NET framework for building desktop applications with web UIs.
</p>

Ryn gives .NET developers the Tauri experience without leaving C#. Native OS webviews, HTML/CSS/JS frontends, NativeAOT-ready, source-generated IPC.

## Why Ryn?

- **C# backend, web frontend** — HTML/CSS/JS frontend, C# backend with `[RynCommand]` source-generated IPC
- **Lightweight** — Uses native OS webviews (WebView2, WKWebView, WebKitGTK), not bundled Chromium
- **NativeAOT** — Small, fast, self-contained binaries (~4.3MB) with no runtime dependency
- **Cross-platform** — Windows, macOS, Linux
- **Plugin system** — FileSystem, Dialog (native pickers), Clipboard, Shell (spawn/PTY streaming), Notification, Audio, Tray, signed Auto-updater
- **Security model** — Unified `ryn.json` capability scopes with deny-all semantics
- **Branded by default** — every window (and bundled `.app`/installer) ships with the Ryn icon out of the box, overridable per app

## Status

**Alpha.** Ryn runs in production on **macOS** and **Windows** — Windows is verified by the Primal launcher, a real desktop app shipping on Ryn. **Linux is the one platform whose GUI paths are not yet verified end-to-end** (it builds and unit-tests in CI; the window/tray/dialog code is written but unproven on a real desktop). Treat Linux as experimental.

### Platform support matrix

Legend: ✅ verified on a real app · 🟡 implemented, not yet GUI-verified · ⚪ not implemented

| Capability | macOS | Windows | Linux |
|---|:---:|:---:|:---:|
| Window + WebView (saucer) | ✅ | ✅ | 🟡 |
| IPC (`ryn://` scheme + local server) | ✅ | ✅ | 🟡 |
| FileSystem plugin | ✅ | ✅ | 🟡 |
| Dialogs / file pickers | ✅ (osascript) | ✅ (PowerShell+WinForms) | 🟡 (zenity/kdialog) |
| Clipboard (text) | ✅ | ✅ | 🟡 (X11 via xclip, Wayland via wl-clipboard) |
| Clipboard (image) | ✅ | ✅ | 🟡 |
| Notifications | ✅ | ✅ | 🟡 (notify-send) |
| Audio playback | 🟡 | 🟡 | 🟡 |
| Shell / PTY | ✅ | ✅ | 🟡 |
| Tray icon | ✅ | ✅ | 🟡 (menu-only; no icon-click event) |
| Auto-updater (signed) | ✅ | ✅ | 🟡 |
| NativeAOT publish | ✅ | ✅ | 🟡 |

Native libraries are committed for `osx-arm64`; `win-x64`/`linux-x64` are built in CI. Help verifying the Linux GUI paths is very welcome.

## Installation

### Option 1: NuGet Packages (recommended)

```bash
dotnet new console -n MyApp
cd MyApp
dotnet add package Ryn
```

The `Ryn` package bundles the whole framework — `Ryn.Core`, `Ryn.Ipc` (with the `[RynCommand]` source generator), and `Ryn.Interop` (with the native webview libraries) — in a single reference. The individual packages (`Ryn.Core`, `Ryn.Ipc`, `Ryn.Interop`) are also published if you prefer to reference them à la carte.

Add plugins as needed:

```bash
dotnet add package Ryn.Plugins.FileSystem
dotnet add package Ryn.Plugins.Dialog
dotnet add package Ryn.Plugins.Clipboard
dotnet add package Ryn.Plugins.Shell
dotnet add package Ryn.Plugins.Notification
dotnet add package Ryn.Plugins.Audio
dotnet add package Ryn.Plugins.Tray
dotnet add package Ryn.Plugins.Updater
```

Install the CLI tool:

```bash
dotnet tool install -g Ryn.Cli
```

### Option 2: Build from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), Git, and [GitHub CLI](https://cli.github.com/) (`gh`).

**macOS:**
```bash
git clone --recursive https://github.com/Yupmoh/Ryn.git
cd Ryn
bash build/download-native.sh         # downloads prebuilt saucer libs
dotnet build Ryn.slnx
dotnet test Ryn.slnx
dotnet run --project samples/Showcase  # run the demo app
```

**Windows:**
```powershell
git clone --recursive https://github.com/Yupmoh/Ryn.git
cd Ryn
.\build\download-native.ps1           # downloads prebuilt saucer libs
dotnet build Ryn.slnx
dotnet test Ryn.slnx
dotnet run --project samples\Showcase
```

**Linux:**
```bash
git clone --recursive https://github.com/Yupmoh/Ryn.git
cd Ryn
sudo apt-get install libwebkitgtk-6.0-dev  # Ubuntu/Debian
bash build/download-native.sh
dotnet build Ryn.slnx
dotnet run --project samples/Showcase
```

To build native saucer libs from source (requires cmake + ninja):
```bash
bash build/build-native.sh
```

Check your environment with `ryn doctor` (or `dotnet run --project src/Ryn.Cli -- doctor` from source).

## Creating an App

### With the CLI

```bash
ryn new MyApp
cd MyApp
ryn dev
```

Or from source:
```bash
dotnet run --project src/Ryn.Cli -- new MyApp
cd MyApp
dotnet run
```

When run from within the Ryn source tree, `ryn new` generates project references. Otherwise it uses NuGet package references. The generated app includes a sample IPC command, a dark-themed HTML frontend, and a `ryn.json` capability file.

### Content serving

Ryn serves frontend files via the `ryn://` custom scheme, keeping everything same-origin with IPC (no CORS issues). There are three ways to provide content:

```csharp
// Option 1: ContentDirectory — serves files from disk (recommended for dev)
opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");

// Option 2: Html — inline HTML string
opts.Html = "<html>...</html>";

// Option 3: Url — external URL (e.g. Vite dev server)
opts.Url = new Uri("http://localhost:5173");
```

With `ContentDirectory`, files are read from disk on each request — changes are reflected on browser refresh without restarting the app.

### Windows requirements

On Windows, the entry point **must** use `[STAThread]` with a synchronous `Main` method. Without it, WebView2 initialization deadlocks silently. Ryn detects this at runtime and throws a clear error.

```csharp
public static class Program
{
    [System.STAThread]
    public static void Main()
    {
        var app = RynApplication.CreateBuilder()
            // ...
            .Build();

        app.Run(); // synchronous — blocks until window closes
    }
}
```

Do **not** use `async Task Main` or top-level statements on Windows — both default to MTA, which is incompatible with WebView2's COM requirements.

### How IPC works

Mark C# methods with `[RynCommand]` — the source generator creates dispatch tables at compile time:

```csharp
using Ryn.Ipc;

public static class MyCommands
{
    [RynCommand]
    public static string Greet(string name) => $"Hello, {name}!";

    [RynCommand]
    public static int Add(int a, int b) => a + b;
}
```

Call them from JavaScript:

```javascript
const greeting = await window.__ryn.invoke('greet', { name: 'World' });
const sum = await window.__ryn.invoke('add', { a: 2, b: 3 });
```

Supported parameter/return types: `int`, `long`, `float`, `double`, `bool`, `string`, `JsonElement`, primitive arrays (`int[]`, `string[]`), nullable types (`int?`), and complex DTOs via `[RynJsonContext]`.

### Security with ryn.json

Control what the frontend can access:

```json
{
  "capabilities": {
    "fs": {
      "allow": ["readTextFile", "readDir"],
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

Missing `ryn.json` = allow all (dev mode). Present = deny by default. Empty `scope: []` or `commands: []` = explicit deny-all.

## Bundling for Distribution

```bash
dotnet run --project src/Ryn.Cli -- bundle
```

Options:
- `--aot` — Enable NativeAOT publishing
- `--self-contained` — Include .NET runtime
- `--icon path/to/icon.png` — Override the app icon (a PNG is auto-converted to `.icns` on macOS / `.ico` on Windows; an `.icns`/`.ico` is used as-is)
- `--sign "Developer ID"` — Code sign (macOS)
- `--notarize` — Submit for Apple notarization (macOS)
- `--version 1.0.0` — Set bundle version

When no icon is supplied (via `--icon` or `ryn.json` → `bundle.icon`), the bundle is branded with the Ryn default icon: a real dock icon (`AppIcon.icns`) on macOS, an `.ico` on Windows, and a hicolor PNG on Linux.

Output:
- **macOS**: `.app` bundle with Info.plist and `AppIcon.icns`
- **Windows**: Self-contained folder with executable + WiX `.wxs`
- **Linux**: AppDir structure (use [appimagetool](https://appimage.github.io/) to create an AppImage)

## Sample Apps

| Sample | Description | Run |
|--------|-------------|-----|
| [HelloWindow](samples/HelloWindow) | Minimal IPC demo | `dotnet run --project samples/HelloWindow` |
| [Showcase](samples/Showcase) | Full-featured demo with all plugins | `dotnet run --project samples/Showcase` |
| [ViteApp](samples/ViteApp) | URL-backed frontend for Vite dev servers | `dotnet run --project samples/ViteApp` |
| [TerminalApp](samples/TerminalApp) | Terminal with shell.execute and streaming metrics | `dotnet run --project samples/TerminalApp` |
| [FileManager](samples/FileManager) | File browser with breadcrumb nav and preview | `dotnet run --project samples/FileManager` |
| [MarkdownEditor](samples/MarkdownEditor) | Split-pane editor with live preview and native dialogs | `dotnet run --project samples/MarkdownEditor` |
| [VueApp](samples/VueApp) | Vue 3 + Vite frontend with typed IPC | `dotnet run --project samples/VueApp` |
| [DevKit](samples/DevKit) | Developer toolkit exercising every Ryn capability | `dotnet run --project samples/DevKit` |

## Project Structure

```
src/
  Ryn                  — Bundle package: Core + Ipc + Interop in one PackageReference
  Ryn.Core             — Window management, app lifecycle, configuration, events
  Ryn.Interop          — Auto-generated saucer C bindings via ClangSharp
  Ryn.Ipc              — JS <> C# IPC bridge, source generator, capabilities, observability
  Ryn.Plugins.*        — FileSystem, Dialog, Clipboard, Shell, Notification, Audio, Tray, Updater
  Ryn.Cli              — CLI: new, dev, build, bundle, doctor
samples/               — 8 example applications
templates/             — dotnet new template pack
tests/                 — 200+ xUnit tests across 7 test projects
benchmarks/            — BenchmarkDotNet suites (IPC, marshaling, JSON, escaping)
docs/
  plan/PLAN.md         — Full project plan with milestone tracking
  plugin-authoring.md  — Guide for writing Ryn plugins
```

## Documentation

- [Getting Started](docs/getting-started.md) — Full walkthrough from install to bundle
- [Architecture](docs/architecture.md) — IPC pipeline, threading model, security internals
- [Plugin Authoring](docs/plugin-authoring.md) — Creating Ryn plugins with commands, options, DI, events
- [Vite Integration](docs/vite-integration.md) — Using Vite + TypeScript with Ryn

## License

[MIT](LICENSE)
