# Custom title bars and window dragging

A frameless or overlay window (set `RynWindowOptions.TitleBarStyle`) lets your HTML own the title-bar area.
To make a region of that HTML drag the window, resize it, or act as a window button, add a `data-webview-*`
attribute to the element. The webview hit-tests these attributes **inside the `mousedown` event** and
performs the native action immediately — no IPC round-trip, no lag.

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
| `data-webview-maximize` | toggle maximize on click; `data-webview-maximize="double"` toggles only on a double-click |
| `data-webview-ignore` | exclude an element from all of the above (keep it clickable) |

These fire on a left-button `mousedown` and are handled natively, so they work on macOS (WKWebView),
Windows (WebView2), and Linux (WebKitGTK) alike.

## Why not `-webkit-app-region: drag`?

`-webkit-app-region` is a Chromium/Electron CSS property — it is **not honored by WebKit**. It does nothing
on macOS (WKWebView) or Linux (WebKitGTK), and works only incidentally on Windows (WebView2 is Chromium).
Use `data-webview-drag` instead; it works on every backend.

## Why not `window.startDrag` / `IRynWindow.StartDrag` for title bars?

The `window.startDrag` IPC command (and `IRynWindow.StartDrag`) still exists as a programmatic escape
hatch, but **don't use it to drag a title bar.** It routes through the IPC pipeline (XHR → loopback
HTTP/scheme → UI-thread marshal), so the native drag begins only *after* the round-trip. On macOS that is
visibly broken: the native `performWindowDragWithEvent:` reads the *current* event, which by the time the
command runs is no longer the original mouse-down — so the window lags behind the cursor (drag desync).
`data-webview-drag` starts the drag synchronously inside the mouse-down, so there is no lag.

See `samples/VueApp` for a `data-webview-drag` title bar, and [multi-window.md](multi-window.md) for opening
and managing multiple windows.
