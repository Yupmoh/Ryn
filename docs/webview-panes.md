# WebView Panes — embedded browser panes

`Ryn.Plugins.WebViewPane` embeds **real browsers inside your window** as positioned panes:
secondary native webviews (WKWebView on macOS, WebView2 on Windows, WebKitGTK on Linux)
created against the same window that hosts your app's UI. Panes render the full web —
including sites that refuse to load in iframes (`X-Frame-Options: DENY`,
`CSP: frame-ancestors`) — with **no embedded browser engine**: the ~5 MB binary stays a
~5 MB binary.

```csharp
services.AddRynWebViewPane();
```

```json
{ "capabilities": { "webviewPane": true } }
```

## The layering model

A pane is a **native view floating above your HTML UI** (the same model as Electron's
`WebContentsView` and Tauri's child webviews). Your frontend owns the layout: it reserves
a rectangle, opens a pane with those bounds, and keeps the bounds synced when the layout
changes (resize observers work well). Two consequences to design around:

- Your HTML cannot draw **on top of** a pane — position popovers, context menus, and drag
  overlays outside the pane rect, or hide/shrink the pane while they're open.
- Panes ignore the page scroll; bounds are window client-area pixels.

## JS API

### Commands

```js
// Open — returns the pane id. All fields optional except bounds you care about.
const id = await window.__ryn.invoke('webviewPane.open', { options: {
  x: 300, y: 0, width: 500, height: 560,
  url: 'https://github.com',
  storagePath: '/path/to/session',   // per-pane cookie/storage dir (panes sharing a path share a session)
  devTools: false,
  zoom: 1.0,                         // 0.25–5.0
  userAgent: 'MyApp/1.0'             // custom UA for this pane (omit for the engine default)
}});

await window.__ryn.invoke('webviewPane.navigate',    { id, url: 'https://example.com' });
await window.__ryn.invoke('webviewPane.back',        { id });
await window.__ryn.invoke('webviewPane.forward',     { id });
await window.__ryn.invoke('webviewPane.reload',      { id });
await window.__ryn.invoke('webviewPane.setBounds',   { id, x: 0, y: 0, width: 800, height: 400 });
await window.__ryn.invoke('webviewPane.setZoom',     { id, factor: 1.5 });
await window.__ryn.invoke('webviewPane.setDevTools', { id, enabled: true });
await window.__ryn.invoke('webviewPane.setUserAgent', { id, userAgent: 'MyApp/1.0' });
await window.__ryn.invoke('webviewPane.setSuspended', { id, suspended: true }); // hide + throttle
await window.__ryn.invoke('webviewPane.reloadFromCrash', { id });               // after processTerminated
await window.__ryn.invoke('webviewPane.execute',     { id, code: "window.scrollTo(0, 0)" }); // fire-and-forget
const result = await window.__ryn.invoke('webviewPane.eval', { id, code: "document.title" }); // JSON result

// Screenshot — base64 PNG of the visible pane rect at device-pixel scale
const b64 = await window.__ryn.invoke('webviewPane.screenshot', { id });
// e.g. img.src = 'data:image/png;base64,' + b64;

// Find in page — all three return { matches, activeIndex } (activeIndex is 0-based, -1 = none)
const hit = await window.__ryn.invoke('webviewPane.find',     { id, text: 'saucer', matchCase: false });
await window.__ryn.invoke('webviewPane.findNext', { id, forward: true });   // wraps at the ends
await window.__ryn.invoke('webviewPane.findStop', { id, clearHighlights: true });
const url    = await window.__ryn.invoke('webviewPane.url',  { id });
const ids    = await window.__ryn.invoke('webviewPane.list');
await window.__ryn.invoke('webviewPane.close', { id });
```

### Events

```js
window.__ryn.on('webviewPane.navigated',        e => { /* { id, url } */ });
window.__ryn.on('webviewPane.titleChanged',     e => { /* { id, title } */ });
window.__ryn.on('webviewPane.loadStateChanged', e => { /* { id, state: 'started' | 'finished' } */ });
window.__ryn.on('webviewPane.domReady',         e => { /* { id } */ });
window.__ryn.on('webviewPane.faviconChanged',   e => { /* { id, dataUrl } — base64 data: URL for <img src> */ });
window.__ryn.on('webviewPane.closed',           e => { /* { id } */ });
window.__ryn.on('webviewPane.processTerminated', e => {
  // { id, reason } — the pane's web process died (crash/OOM). Recover with reloadFromCrash.
});
window.__ryn.on('webviewPane.downloadRequested', async e => {
  // { id, downloadId, url, suggestedName } — the app is the "save as" dialog
  const path = await pickSavePath(e.suggestedName);
  await window.__ryn.invoke('webviewPane.resolveDownload',
    { downloadId: e.downloadId, action: path ? 'allow' : 'deny', path });
});
window.__ryn.on('webviewPane.downloadProgress',  e => { /* { id, downloadId, receivedBytes, totalBytes } */ });
window.__ryn.on('webviewPane.downloadCompleted', e => { /* { id, downloadId, path } */ });
window.__ryn.on('webviewPane.downloadFailed',    e => { /* { id, downloadId, error } */ });
window.__ryn.on('webviewPane.permissionRequested', async e => {
  // { id, requestId, kinds, url } — kinds ⊂ ['microphone','camera','screenShare','mouseLock',
  //                                          'deviceInfo','geolocation','clipboard','notifications','unknown']
  const grant = await myPermissionPrompt(e.kinds, e.url);
  await window.__ryn.invoke('webviewPane.resolvePermission', { requestId: e.requestId, grant });
});
```

Everything a browser pane's chrome needs — URL bar, back/forward, spinner, tab title,
favicon — comes from these events.

## `eval` semantics

`webviewPane.eval` runs code in the pane and returns the JSON-serialized result;
promises are awaited. The code is injected natively (exempt from the page's CSP) but is
**inlined as a single expression** — it is never passed through JavaScript `eval()`,
which strict-CSP sites (GitHub, banks) block. To run statements, wrap them in an IIFE:

```js
const stats = await window.__ryn.invoke('webviewPane.eval', { id, code: `
  (() => {
    const links = document.querySelectorAll('a').length;
    return { title: document.title, links };
  })()
`});
// -> '{"title":"...","links":146}'
```

Script errors reject the promise with the page-side error message; a pane that never
responds (e.g. a syntax error in the expression) rejects after 10 seconds.

## Zoom

`setZoom` is native page zoom on macOS (`WKWebView.pageZoom` — crisp, survives
navigation). On Windows and Linux it applies CSS zoom, re-applied automatically after
each navigation; layout-affecting but universally supported.

## Downloads

A download in a pane raises `webviewPane.downloadRequested { id, downloadId, url, suggestedName }`
instead of a native save dialog — your UI decides where it goes. Answer with
`webviewPane.resolveDownload { downloadId, action: 'allow' | 'deny', path? }`; `allow` writes to
`path`, `deny` cancels. Completion arrives as `downloadCompleted { downloadId, path }` or
`downloadFailed { downloadId, error }`. **Progress** (`downloadProgress`) is real on Windows
(WebView2 reports received/total bytes); macOS and Linux emit request and completion only, so drive
your progress UI off those two on those platforms. On macOS a download is any navigation the engine
cannot render inline (attachments, unknown MIME types).

## Crash recovery & suspension

A dying web process (crash, OOM-kill) raises `webviewPane.processTerminated { id, reason }` —
the pane object survives, showing a blank view. `reloadFromCrash` renavigates to the last known
URL, which respawns the process. Reasons: `webContentProcessTerminated` (macOS),
`renderProcessExited`/`browserProcessExited`/… (Windows), `crashed`/`exceededMemoryLimit`/
`terminatedByApi` (Linux).

`setSuspended` **hides the pane and throttles it** — on WebView2 it is a real process freeze
(`TrySuspend`; the engine may decline while DevTools is open or a download runs), elsewhere the
hidden view gets the engine's background throttling. Treat it as "this pane is in the background";
resume makes the pane visible again. Pane visibility/occlusion tracking stays app-side: your
frontend owns every pane rect, so it already knows what is covered.

## Screenshots

`screenshot` captures the pane's **visible viewport** as a PNG via the engine's native
snapshot API (`WKWebView.takeSnapshot`, `CapturePreview`, `webkit_web_view_get_snapshot`)
at device-pixel scale, returned as a base64 string. It works while the pane is partially
occluded by sibling panes, and errors (rather than hangs) on a crashed pane — the call
also times out after 15 seconds as a backstop.

## Find in page

`find` starts a session and scrolls the first match into view; `findNext` cycles (wrapping);
`findStop` clears. The engine is injected JavaScript shared by all three platforms: matches are
counted per text node and painted with the **CSS Custom Highlight API** (no DOM mutation). On
engines without `CSS.highlights` (WebKit before Safari 17.2) counting, cycling, and scrolling
still work — only the visual highlight is missing. Sessions are page-scoped: a navigation clears
them, and matches spanning element boundaries (`ab<b>cd</b>`) are not found. Searching an empty
string returns `{ matches: 0, activeIndex: -1 }` without error.

## Permission prompts

When a page in a pane asks for a sensitive capability (getUserMedia, geolocation, …) the
request surfaces as `webviewPane.permissionRequested` instead of a native prompt. Resolve
it with `webviewPane.resolvePermission` — your HTML UI is the prompt. Unresolved requests
are **denied automatically after 30 seconds** (and when their pane closes); resolving an
expired request returns `false`. A camera+microphone getUserMedia call arrives as one
request with both kinds. Which kinds actually surface varies by engine (macOS reports
media capture; WebView2 and WebKitGTK cover the wider set).

## Per-pane user agent

`userAgent` at open time applies before the first navigation (`navigator.userAgent` in the
pane reports it immediately). `setUserAgent` at runtime applies to subsequent navigations —
immediately on macOS/Linux, on the next navigation on Windows — so reload after changing it.

## Per-pane sessions

`storagePath` gives a pane its own cookie jar and storage, persisted across runs. Use one
path per workspace/profile to keep logins isolated, or share a path across panes that
should share a session. Omitting it uses the engine's default (shared) session.

## C# API

`WebViewPaneService` (singleton, resolvable from DI) exposes the same surface:
`OpenAsync(PaneOpenRequest)`, `CloseAsync`, `SetBounds`, `Navigate`, `Back`, `Forward`,
`Reload`, `SetZoom`, `SetDevTools`, `SetUserAgentAsync`, `ScreenshotAsync`, `FindAsync`,
`FindNextAsync`, `FindStopAsync`, `ResolvePermissionAsync`, `ResolveDownloadAsync`,
`SetSuspendedAsync`, `ReloadFromCrash`, `Execute`, `EvalAsync`, `GetUrl`, `List`, `CloseAll`.

## Platform notes

| | macOS | Windows | Linux |
|---|---|---|---|
| Rendering | ✅ WKWebView | ✅ WebView2 | 🟡 WebKitGTK (untested interactively) |
| Zoom | ✅ native page zoom | ✅ CSS zoom | ✅ CSS zoom |
| DevTools | ✅ | ✅ | ✅ |
| Per-pane session | ✅ | ✅ | ✅ |

Panes attach to the main window. They are torn down automatically when the window closes
or the service is disposed.
