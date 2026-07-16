# Multi-window

Ryn applications can open and manage more than one window. The application owns a
single native event loop and application object; each window has its own webview,
IPC token, scheme handler, and events, so windows are fully isolated from one
another while sharing one process and one DI container.

> **macOS limitation:** a window opened *after* the app has started may render only
> its background, not its content. This is a WebKit issue, not a Ryn API gap — see
> [Known limitation](#known-limitation-macos-secondary-window-rendering) below
> before relying on multi-window on macOS.

## Concepts

- **Main window** — created when the app starts. `RynApplication.MainWindow` (and,
  for backward compatibility, `RynApplication.Window`) always points at it. Closing
  the main window no longer quits the app on its own; the app quits when the *last*
  window closes.
- **Secondary windows** — opened at runtime from C# or JavaScript. Each gets its own
  integer `Id`, assigned in creation order (the main window is `1`).
- **Per-window IPC** — the `window.*` commands act on the window that *originated*
  the call. A button in a secondary window that invokes `window.close` closes *that*
  window, not the main one.

## C# API

```csharp
// Open a window and keep the handle.
IRynWindow settings = app.OpenWindow(new RynWindowOptions
{
    Title  = "Settings",
    Width  = 480,
    Height = 360,
    Html   = "<h1>Settings</h1>",
});

// Non-blocking variant.
IRynWindow w = await app.OpenWindowAsync(new RynWindowOptions { Url = new Uri("http://localhost:5173") });

IRynWindow main          = app.MainWindow;   // the first window
IReadOnlyList<IRynWindow> all = app.Windows; // all open windows, main first
```

`OpenWindow` is safe to call from any thread: native window creation is marshalled
onto the UI thread and the call blocks until the window exists. `OpenWindowAsync`
returns once the window has been created without blocking the caller.

To open windows from a service or command class without holding a `RynApplication`
reference, inject `IRynWindowManager`:

```csharp
public sealed class MyCommands(IRynWindowManager windows)
{
    [RynCommand("app.openPanel")]
    public int OpenPanel() => windows.OpenWindow(new RynWindowOptions { Title = "Panel" }).Id;
}
```

### `RynWindowOptions`

A per-window subset of `RynOptions` (the app-global fields — `ApplicationId`,
`DeepLinkSchemes`, exception capture, default logging — are not per-window):

| Property | Default | Notes |
|---|---|---|
| `Title` | `"Ryn Window"` | |
| `Width` / `Height` | `800` / `600` | |
| `Resizable` | `true` | |
| `TitleBarStyle` | `Native` | |
| `Transparent` | `false` | |
| `Url` | `null` | loopback dev URL, or remote URL (IPC only wired for loopback/local) |
| `Html` | `null` | inline HTML over the `ryn://` scheme |
| `ContentDirectory` | `null` | a directory of static content |
| `UseLocalServer` | `false` | serve `ContentDirectory` over loopback HTTP instead of `ryn://` |
| `LocalServerPort` | `7421` | |
| `IconPath` | `null` | |
| `DevTools` | `false` | |
| `PersistWindowState` | `false` | secondary windows persist under a per-`Id` key so they don't collide with the main window |
| `AllowedOrigins` | `[]` | |
| `CustomSchemes` | `[]` | |

Provide exactly one of `Url` / `Html` / `ContentDirectory` for the window's content.

## JavaScript API

All commands are invoked through the bridge and routed to the originating window:

```js
// Open a window; returns its id (as a string).
const id = await window.__ryn.invoke('window.open', {
  title: 'JS child', width: 420, height: 320, html: '<h1>Hi</h1>',
});

await window.__ryn.invoke('window.current'); // this window's id (number)
await window.__ryn.invoke('window.list');    // ids of all open windows (number[])

// These act on the calling window:
await window.__ryn.invoke('window.close');
await window.__ryn.invoke('window.minimize');
await window.__ryn.invoke('window.toggleMaximize');
await window.__ryn.invoke('window.setTitle', { title: 'Renamed' });
await window.__ryn.invoke('window.setSize', { width: 600, height: 400 });
await window.__ryn.invoke('window.setPageZoom', { factor: 1.25 });
await window.__ryn.invoke('window.getPageZoom'); // 1.25
```

## Page zoom

`IRynWindow.SetPageZoom(factor)` and `window.setPageZoom` zoom the calling window's primary page;
`GetPageZoom()` / `window.getPageZoom` return the effective factor. Values are clamped to `0.25–5.0`,
with `1.0` as 100%. The implementation is native page zoom on every supported engine: `WKWebView.pageZoom`
on macOS, WebView2 controller `ZoomFactor` on Windows, and `webkit_web_view_set_zoom_level` on Linux.
If a native handle or API is unavailable, Ryn falls back to document-element CSS zoom and re-applies it
after navigation.

Page zoom is separate from `webviewPane.setZoom`, which zooms content inside a child pane. Pane bounds
remain expressed in the host page's CSS pixels; Ryn scales them into native coordinates. macOS custom
title-bar drag/ignore rectangles follow the same rule.

`window.open` accepts the optional named arguments `title`, `width`, `height`,
`resizable`, `devTools`, and one of `url` / `html` / `contentDirectory`. Omitted
fields fall back to the window defaults.

## Capabilities

The `window` commands are governed by the capability system like any other. Grant
them in `ryn.json` (a missing file allows everything, for development):

```json
{
  "capabilities": {
    "window": {
      "allow": ["open", "list", "current", "close", "minimize", "toggleMaximize", "setTitle", "setSize", "setPageZoom", "getPageZoom"]
    }
  }
}
```

`window.open` is the most powerful grant (it creates windows), so keep it explicit.
See [capabilities.md](capabilities.md) for the full model.

## Lifecycle

- Closing a window removes it from `app.Windows` and fires its `Closed` event.
- The app's event loop ends — and `RunAsync` returns — when the **last** window
  closes, not when the main window closes.
- `RequestShutdown()` closes all windows and ends the loop.

## Known limitation: macOS secondary-window rendering

On macOS, a window created **after** the application has started (i.e. any window
other than the main one) may display only its page background — the content (text,
controls, laid-out DOM) does not appear, even though the page loaded and its
JavaScript ran.

**Cause.** WebKit renders a `WKWebView`'s content only while its window is in the
`NSWindowOcclusionStateVisible` state. The main window reaches that state during
AppKit's launch display cycle; a window created after the run loop is already
spinning does not reliably get that transition, so WebKit keeps its WebContent
process throttled and only the background layer composites. This is a property of
the underlying `saucer`/WebKit layer (saucer's own examples only ever create
windows during launch), not of Ryn's window manager — the window, its IPC, its
events, and `window.current`/`window.list` all work correctly; only the first paint
is affected.

**Status.** The multi-window **API is stable and complete**; the rendering gap is
tracked against the native layer and does not affect the C#/JS surface. To reduce
the visual impact, Ryn shows a secondary window before loading its content so it at
least paints its themed background rather than a blank white view, and foregrounds
the app on launch. Windows, Linux (WebKitGTK), and the main window on every platform
are unaffected.

If you hit this, prefer composing UI inside the main window (panels, routes,
overlays) until the upstream fix lands.

## Sample

`samples/MultiWindow` opens a second window from JavaScript and a third from C#,
tiles all three, and demonstrates per-window `window.current` / `window.list` and
close-one-keeps-the-app-alive / close-last-quits.
