# Custom title bars and window dragging

A frameless or overlay window (set `RynWindowOptions.TitleBarStyle`) lets your HTML own the title-bar area.
To make a region of that HTML drag the window, resize it, or act as a window button, add a `data-webview-*`
attribute to the element. Ryn injects a small script (for every non-`Native` title-bar style) that wires
these up.

> **Capability.** The drag/control attributes call `window.*` commands, so an app with a `ryn.json` must
> grant the **`window`** capability (`{ "capabilities": { "window": true } }`). Without it the calls are
> denied (a host-stdout warning + rejected promise) and the title bar appears inert. Dev mode with no
> `ryn.json` allows all commands.

**How it works, per platform:**
- **macOS** — an invisible native drag view sits over the webview and hit-tests the `data-webview-drag`
  rectangles the script publishes. A mouse-down inside one starts a **native, lag-free** window drag
  (`performWindowDragWithEvent:`); every other point falls straight through to the DOM, so buttons and
  inputs in the title bar are clickable. Set `RynOptions.TitleBarDragView = false` to remove the native
  view and self-manage dragging.
- **Windows / Linux** — the script starts the drag from the `mousedown` via `window.startDrag`; there is no
  click-eating overlay, so interactive elements work without extra markup.

## Which `TitleBarStyle` for a custom title bar?

**Drawing your own title bar in HTML? Use `Overlay`.** On macOS `Overlay` gives the webview the full window
height (`fullSizeContentView`) with a transparent title bar and native traffic lights floating on top, so
your page reaches the very top edge. `Hidden` instead leaves an **empty native title-bar strip** above the
webview — your content renders *below* it, and with a transparent/`Backdrop` window that strip reads as a
see-through band around the traffic lights. `Frameless` removes all chrome (no traffic lights). Use `Hidden`
only when you want the native strip but no title text; use `Overlay` for edge-to-edge custom chrome.

## Dragging

```html
<header data-webview-drag>
  <span>My App</span>
  <!-- interactive children must opt out, or clicking them would drag the window -->
  <nav data-webview-ignore>
    <button>One</button><button>Two</button>
  </nav>
</header>
```

- **`data-webview-drag`** — click-and-drag anywhere on the element moves the window.
- **`data-webview-ignore`** — marks an interactive descendant (buttons, inputs, links) as *not* a drag
  handle, so clicks reach it normally. Put it on anything clickable inside a drag region.

## Window controls and resizing

| Attribute | Effect |
|---|---|
| `data-webview-drag` | drag-move the window |
| `data-webview-resize="<edge>"` | drag-resize from an edge/corner (`top`, `bottom`, `left`, `right`, `top-left`, `top-right`, `bottom-left`, `bottom-right`) |
| `data-webview-close` | close the window on click |
| `data-webview-minimize` | minimize on click |
| `data-webview-maximize` | toggle maximize on click (double-clicking any `data-webview-drag` region also zooms) |
| `data-webview-ignore` | exclude an element from dragging (keep it clickable) |

Window controls fire on `click`; drag and resize fire on left-button `mousedown`. All work on macOS
(WKWebView), Windows (WebView2), and Linux (WebKitGTK).

**Interactive children inside a drag region.** On macOS the native drag view grabs the whole
`data-webview-drag` rectangle, so a button *inside* the drag bar must be marked `data-webview-ignore`
(the window-control attributes above are treated as ignore automatically). Place non-draggable controls
outside the drag rectangle, or tag them `data-webview-ignore`.

## Traffic-light position (macOS)

By default the macOS traffic lights hug the top edge, which looks off in a taller custom title bar. Set
`RynOptions.TrafficLightPosition` (or call `IRynWindow.SetTrafficLightPosition` / `window.setTrafficLightPosition`
at runtime) to place the close button's top-left, in points from the window's top-left; the miniaturize and
zoom buttons follow at their native spacing. Ryn re-applies it on resize.

```csharp
// Vertically center the (~14pt) lights in a 48pt title bar: (48 - 14) / 2 ≈ 17.
opts.TitleBarStyle = TitleBarStyle.Overlay;
opts.TrafficLightPosition = new TrafficLightPosition(X: 20, Y: 17);
```

## Why not `-webkit-app-region: drag`?

`-webkit-app-region` is a Chromium/Electron CSS property — it is **not honored by WebKit**. It does nothing
on macOS (WKWebView) or Linux (WebKitGTK), and works only incidentally on Windows (WebView2 is Chromium).
Use `data-webview-drag` instead; it works on every backend.

## Why not `window.startDrag` / `IRynWindow.StartDrag` directly?

`window.startDrag` still exists as a programmatic escape hatch, but prefer `data-webview-drag`. On macOS
the attribute path drags natively with no IPC (the drag view starts the OS drag inside the real
mouse-down), avoiding the cursor desync you get calling `startDrag` from JS after an IPC round-trip. On
Windows/Linux `data-webview-drag` uses `startDrag` under the hood, which is fine there.

See `samples/VueApp` for a `data-webview-drag` title bar, and [multi-window.md](multi-window.md) for opening
and managing multiple windows.
