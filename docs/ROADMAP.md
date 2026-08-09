# Ryn Roadmap

Ryn is alpha software. The current focus is correctness and hardening: fixing
defects, tightening the security model, and making the existing single-window,
plugin-based feature set dependable across macOS, Windows, and Linux.

This document tracks larger capabilities and delivery status. Items that remain out of
scope are recorded here so they are planned rather than forgotten; delivered entries
remain as a short record of the capability and its implementation boundary.
Listing an item here is not a commitment to a date, and ordering within a section
is not a strict sequence. Each entry ends with a parenthetical referencing the
internal review finding it maps back to.

Status legend:

- **Planned** — intended for a future release; the shape of the work is understood.
- **Under consideration** — wanted, but not yet committed; design or trade-offs are still open.
- **Delivered** — implemented in the current public API; behavior and boundaries are described in the entry.

## Near-term

### Multi-window support — **delivered**

`RynApplication` is now a window manager: it owns one native event loop and tracks
multiple `RynWindow` instances, each with its own webview and per-window IPC
routing, exposed via `MainWindow`, `Windows`, `OpenWindow`/`OpenWindowAsync`, the
`IRynWindowManager` service, and the `window.open`/`list`/`current`/`close`/… JS
commands. Closing the last window (not the main one) quits the app. See
[multi-window.md](multi-window.md). One open item remains: on macOS a window opened
after launch may paint only its background (a WebKit/`saucer` first-paint limitation
documented in that doc); the API surface is complete and unaffected. (tracks
CMP-01, ARC-13)

### Application menu bar and global shortcuts — **delivered**

Shipped as three plugins: `Ryn.Plugins.MenuBar` (native menus with roles —
NSMenu on macOS, Win32 menus on Windows; Linux intentionally unsupported since
GTK apps use header bars), `Ryn.Plugins.GlobalShortcut` (RegisterHotKey /
Carbon RegisterEventHotKey; Wayland needs the portal API and is not yet
supported), and `Ryn.Plugins.Badge` (Dock badge / taskbar overlay). See the
plugin table in [getting-started.md](getting-started.md). (tracked CMP-03)

### Finished installers and code signing — **partially delivered**

macOS is done end-to-end: `ryn bundle --icon --sign --entitlements --notarize
--dmg` produces a signed (hardened runtime + timestamp), notarized `.app` and a
compressed `.dmg`. Windows still emits a folder plus a generated WiX `.wxs` with
a printed instruction to build the MSI yourself, and has no Authenticode
signing; Linux produces an AppDir and only builds an AppImage if `appimagetool`
is on the PATH. Remaining plan: a built MSI or NSIS installer with `signtool` /
Azure Trusted Signing on Windows, and a `.deb` via `dpkg-deb` on Linux,
auto-downloading missing tools the way it already does for `appimagetool`.
Status: **Planned** (Windows/Linux last mile). (tracks CMP-04)

### Linux GUI hardening

Primal Launcher's first broad run on Pop!_OS exercised Ryn's core Linux GUI and
plugin paths in a real third-party application. The only Ryn defect reported was
the notification backend acquiring GTK's default GLib context before
`g_application_run()`; v0.27.4 removes that competing loop and adds a Linux GTK
startup smoke under Xvfb and a D-Bus session. Linux is therefore near-full support,
with the notification fix awaiting downstream retest and focused validation still
needed for the capability-specific yellow entries in the README matrix, notably
the updater and NativeAOT publish path. Status: **In validation**. (tracks CMP-05)

## Mid-term

### Webview lifecycle, navigation, and permission events

saucer exposes navigation (with a policy hook that can block), navigated,
dom-ready, load, title, favicon, and permission-request events. **Embedded panes
already surface the full set** (`webviewPane.navigated` / `titleChanged` /
`loadState` / `domReady` / `favicon` / `permissionRequested` /
`processTerminated` / download events — see
[webview-panes.md](webview-panes.md)), but the **main** `IRynWebView` still only
surfaces file drop. As a result an app cannot stop its top frame from navigating
to an arbitrary external site, reliably wait for page readiness, or apply policy
to camera/microphone/geolocation prompts on the primary webview. The plan is to
add `NavigationStarting` (cancellable), `Navigated`, `DomReady`/`Loaded`, and
`PermissionRequested` to `IRynWebView`, mapped from the saucer callbacks (the
pane plugin's interop is the template), before the interface is frozen. Status:
**Planned** (main webview only). (tracks ARC-14)

### Hot-reload dev loop

`ryn dev` currently kills and relaunches the whole app on every frontend save,
closing the window and losing all application and DOM state, which is far from the
"hot reload" the CLI advertises. The plan is to trigger an in-place webview reload
in the running app on a `wwwroot` change instead of a process restart, using a
dev-only reload channel into the webview, and to have `ryn dev` optionally
auto-start a configured frontend dev server (a `devCommand` / `devUrl` in
`ryn.json`) so Vite-style workflows do not require a second terminal. This touches
the CLI dev command and the webview's file-watching path. Status: **Planned**.
(tracks CMP-07)

### Off-thread static file serving with HTTP Range support — **delivered**

The `ryn://` scheme handler serves built-in files off the callback thread and supports custom and built-in file ranges. A range response contains only the selected bytes (`206`), bounding materialized memory for partial loads; malformed or unsatisfiable ranges return `416`. Saucer requires a contiguous stash, so the selected response or range is materialized before native acceptance rather than streamed zero-copy. (tracks ARC-21)

### Framework scaffold templates

`ryn new` today offers only plain HTML or a vanilla-TypeScript Vite setup; React,
Vue, and Svelte users have to swap the frontend directory and rewire the Vite
config by hand. The plan is to add `ryn new --template react|vue|svelte`, with an
interactive picker when no flag is given, generated from the proven VueApp wiring
and shipping typed `window.__ryn` declarations. The work lives in the CLI `new`
command and the `templates/` template pack.

```bash
ryn new MyApp --template react
```

Status: **Planned**. (tracks CMP-13)

### Documentation website and generated API reference

Documentation is currently a handful of markdown files plus the README. For
mainstream adoption the docs site is the storefront, and repo markdown does not
convert evaluators who arrive from a link. The plan is a static documentation site
on GitHub Pages (docfx or Starlight) covering getting started, guides, a generated
API reference built from the existing XML doc comments, a per-plugin permission
reference, a `window.__ryn` JS-API reference, and a published JSON schema for
`ryn.json` to enable editor autocomplete, wired up by a deploy workflow. This adds
a docs site and a `.github/workflows` job; it does not change framework code.
Status: **Planned**. (tracks CMP-06)

### Plugin ecosystem staples

The eight first-party plugins cover filesystem, dialog, clipboard, shell,
notification, audio, tray, and updater, but a few commonly needed building blocks
are missing: a store plugin for persistent key-value settings, an HTTP plugin for
remote API calls without CORS friction, file logging, and OS / process info.
Beyond first-party plugins, there is no published path for third parties to
participate. The plan is to ship `Ryn.Plugins.Store` and `Ryn.Plugins.Http` next
(both pure C# and AOT-friendly), then publish a `ryn-plugin` project template and
document the `Ryn.Plugins.*` naming and capability-prefix conventions so others can
build and share plugins. This adds new plugin projects and a template; the
third-party participation story is still being shaped, so that part is
**Under consideration** while the first-party staples are **Planned**.
(tracks CMP-12)

### Desktop integration set (single-instance, autostart, sidecars, runtime scope grants)

Ryn still has no single-instance mechanism, autostart support, or sidecar concept. Runtime filesystem grants are now available for native picker results through the `dialog.openFileSecure`, `dialog.openFilesSecure`, `dialog.openFolderSecure`, and `dialog.saveSecure` commands; grants are scoped to an operation and can be resolved by the host through `IFileAccessGrants`. Browser `FileDrop` remains names-only, so it cannot securely grant native filesystem paths. The remaining desktop integration work spans `Ryn.Core`, the dialog plugin, and the bundler. Status: **Planned**. (tracks CMP-11)

## Under consideration

### Blazor integration

Hosting a Blazor WebAssembly frontend as a first-class C# UI option would let
developers write both the backend and the UI in C# rather than JavaScript, which
is a capability that sits squarely in Ryn's "without leaving C#" goal. An
AOT-compatible Blazor WASM host served over the `ryn://` scheme, with a typed
interop service wrapping `window.__ryn`, would be a meaningful differentiator. This
is not committed. An earlier Blazor milestone was scrapped, no `Ryn.Blazor` project
exists, and the design (AOT compatibility, asset serving, interop surface, a
`ryn new --blazor` template, and a sample) is still open. Until it is committed,
Ryn's frontend story is HTML/CSS/JS with the framework templates above. Status:
**Under consideration**. (tracks CMP-02)
</content>
</invoke>
