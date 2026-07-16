# Custom title bars and window dragging

A frameless or overlay window (set `RynWindowOptions.TitleBarStyle`) lets your HTML own the title-bar area.
Ryn injects a small script (for every non-`Native` title-bar style) that decides, **at mousedown time, from
the live DOM**, whether a click should drag the window — no geometry is ever published ahead of time, so
there is nothing to go stale: popovers, modals and dynamic layout are correct by construction.

> **Capability.** The drag/control attributes call `window.*` commands, so an app with a `ryn.json` must
> grant the **`window`** capability (`{ "capabilities": { "window": true } }`). Without it the calls are
> denied (a host-stdout warning + rejected promise) and the title bar appears inert. Dev mode with no
> `ryn.json` allows all commands.

**How it works, per platform:**
- **macOS** — Ryn observes the window's event stream (`sendEvent:`) and retains the latest left-mousedown
  `NSEvent`. When the page rules the point draggable, it posts `window.beginNativeDrag` and the host starts
  the drag with `performWindowDragWithEvent:` **using the retained original event** — so the IPC delay
  costs nothing: AppKit anchors the drag to the true mousedown and the window can never desync from the
  cursor. Traffic lights stay fully native. (This is deliberately *not* the Tauri model, which drags from
  `NSApp.currentEvent` after IPC and lags, nor the Electron model, which pre-publishes region rectangles
  that go stale.)
- **Windows / Linux** — the same verdict logic runs, and a draggable mousedown starts the drag via
  `window.startDrag`; there is no click-eating overlay, so interactive elements work without extra markup.

## Which `TitleBarStyle` for a custom title bar?

**Drawing your own title bar in HTML? Use `Overlay`.** On macOS `Overlay` gives the webview the full window
height (`fullSizeContentView`) with a transparent title bar and native traffic lights floating on top, so
your page reaches the very top edge. `Hidden` instead leaves an **empty native title-bar strip** above the
webview — your content renders *below* it, and with a transparent/`Backdrop` window that strip reads as a
see-through band around the traffic lights. `Frameless` removes all chrome (no traffic lights). Use `Hidden`
only when you want the native strip but no title text; use `Overlay` for edge-to-edge custom chrome.

## The invisible top bar (zero markup)

Set `RynOptions.TitleBarAutoDragHeight` to the height of your top bar in CSS pixels and the whole strip
becomes a natural, native-feeling title bar with **no markup at all**:

```csharp
opts.TitleBarStyle = TitleBarStyle.Overlay;
opts.TitleBarAutoDragHeight = 44; // top 44px drag the window
```

Inside the strip, every point drags the window **except**:

- interactive elements — `button`, `a[href]`, `input`, `select`, `textarea`, `[contenteditable]`, media
  with controls, ARIA widget roles (`button`, `tab`, `menuitem`, `combobox`, …), `[onclick]`,
  `[draggable="true"]` — and anything inside them;
- anything marked `data-webview-ignore` (the escape hatch for non-semantic clickable elements, e.g. a
  `div` with a JS click handler);
- overlays: an element that covers the strip but extends well below it (a modal, dropdown, or backdrop)
  is treated as content, not chrome, so clicking it never drags the window.

Empty space between controls drags; the search box, buttons and menus in the bar just work. Double-click
on any draggable point zooms (maximize/restore), like a native title bar.

## Explicit drag regions

`data-webview-drag` still works — and is checked against the live element under the cursor, so interactive
descendants are excluded **automatically**:

```html
<header data-webview-drag>
  <span>My App</span>
  <nav>
    <button>One</button><button>Two</button>  <!-- buttons auto-detected: they click, they don't drag -->
  </nav>
</header>
```

- **`data-webview-drag`** — click-and-drag anywhere on the element moves the window, except on interactive
  descendants (detected from the DOM at click time — no `data-webview-ignore` needed for standard controls).
- **`data-webview-ignore`** — excludes an element (and its subtree) from dragging. Only needed for
  clickable elements the interactive heuristic can't see: plain `div`/`span` with JS-attached listeners
  and no `role`/`onclick` attribute.

> **Do NOT mark layout wrappers `data-webview-ignore`.** It excludes the wrapper's whole rectangle —
> including its empty space — from dragging. Mark the individual controls (or nothing, if they're
> semantic elements).

## Window controls and resizing

| Attribute | Effect |
|---|---|
| `data-webview-drag` | drag-move the window |
| `data-webview-resize="<edge>"` | drag-resize from an edge/corner (`top`, `bottom`, `left`, `right`, `top-left`, `top-right`, `bottom-left`, `bottom-right`) |
| `data-webview-close` | close the window on click |
| `data-webview-minimize` | minimize on click |
| `data-webview-maximize` | toggle maximize on click (double-clicking any draggable point also zooms) |
| `data-webview-ignore` | exclude an element from dragging (keep it clickable) |

Window controls fire on `click`; drag and resize fire on left-button `mousedown`. All work on macOS
(WKWebView), Windows (WebView2), and Linux (WebKitGTK).

### Page zoom

Verdict coordinates are raw page CSS pixels; Ryn scales them to AppKit points internally, so
`window.setPageZoom` needs no special handling.

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
Use `TitleBarAutoDragHeight` or `data-webview-drag` instead; they work on every backend.

## Why not `window.startDrag` / `IRynWindow.StartDrag` directly?

`window.startDrag` still exists as a programmatic escape hatch (and is what the injected script uses on
Windows/Linux), but on macOS prefer the attribute/auto-strip path: it routes through
`window.beginNativeDrag`, which starts the OS drag from the **retained original mousedown event** — no
cursor desync regardless of IPC latency. Calling `startDrag` from JS on macOS drags from whatever event is
current when the IPC arrives, which lags.

See `samples/VueApp` for a `data-webview-drag` title bar, and [multi-window.md](multi-window.md) for opening
and managing multiple windows.
