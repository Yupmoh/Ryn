# Ryn Roadmap

Ryn is alpha software. The current focus is correctness and hardening: fixing
defects, tightening the security model, and making the existing single-window,
plugin-based feature set dependable across macOS, Windows, and Linux.

This document tracks the larger capabilities that are deliberately out of scope
for that pass. They are recorded here so they are planned rather than forgotten.
Listing an item here is not a commitment to a date, and ordering within a section
is not a strict sequence. Each entry ends with a parenthetical referencing the
internal review finding it maps back to.

Status legend:

- **Planned** — intended for a future release; the shape of the work is understood.
- **Under consideration** — wanted, but not yet committed; design or trade-offs are still open.

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

### Application menu bar and global shortcuts

Ryn currently has no native application menu and no global hotkey registration. On
macOS in particular, an app without the standard App/Edit/Window menus and the
usual Cmd-Q / Cmd-C conventions sits below the platform baseline. The plan is an
app-menu API backed by per-platform native menus (NSMenu via the ObjC runtime on
macOS first, then Win32 and GTK) and a global-shortcut API (RegisterHotKey on
Windows, Carbon RegisterEventHotKey on macOS, the GTK equivalent on Linux). The
Tray plugin already proves the per-platform native-backend approach, so the same
structure applies. This adds a new menu surface to `Ryn.Core` and a new
global-shortcut plugin. Status: **Planned**. (tracks CMP-03)

### Finished installers and code signing

The bundler stops one manual step short of shippable artifacts. macOS produces an
`.app` with codesign and notarization but no disk image; Windows emits a folder
plus a generated WiX `.wxs` and a printed instruction to build the MSI yourself,
with no Authenticode signing; Linux produces an AppDir and only builds an AppImage
if `appimagetool` happens to be on the PATH. The last mile is exactly where
evaluators decide, so the plan is to make `ryn bundle` produce final artifacts in
one command: a `.dmg` via `hdiutil` plus notarization on macOS, a built MSI or
NSIS installer with `signtool` / Azure Trusted Signing on Windows, and a `.deb`
via `dpkg-deb` on Linux, auto-downloading missing tools the way it already does
for `appimagetool`. This work lives in `Ryn.Cli`'s bundle command. Status:
**Planned**. (tracks CMP-04)

### Linux GUI end-to-end verification

Linux is listed as cross-platform but its GUI paths (window, tray, dialog, file
pickers) are written and unit-tested in CI yet never run on a real desktop, so the
platform-support matrix marks them experimental. That unproven third leg
undercuts the cross-platform headline. The plan is a CI job that opens a real
window and runs an IPC round-trip smoke test under xvfb or weston with WebKitGTK
on Ubuntu, followed by a manual pass over tray, dialogs, and pickers on one
distribution, after which the README matrix can move Linux from experimental to
verified. This adds a workflow under `.github/workflows` and a small smoke-test
harness; it changes no public API. Status: **Planned**. (tracks CMP-05)

## Mid-term

### Webview lifecycle, navigation, and permission events

saucer exposes navigation (with a policy hook that can block), navigated,
dom-ready, load, title, favicon, and permission-request events, but `IRynWebView`
surfaces none of them today (only file drop). As a result an app cannot stop the
top frame from navigating to an arbitrary external site, cannot reliably wait for
the page to be ready before emitting events, and cannot apply its own policy to
camera, microphone, or geolocation permission requests. The plan is to add
`NavigationStarting` (cancellable), `Navigated`, `DomReady`/`Loaded`, and
`PermissionRequested` to `IRynWebView`, mapped from the saucer callbacks, before
the interface is frozen. This is scoped to `Ryn.Core`'s webview surface. Status:
**Planned**. (tracks ARC-14)

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

### Off-thread static file serving with HTTP Range support

The `ryn://` scheme handler reads whole files synchronously on the UI thread with
`File.ReadAllBytes`, which blocks the UI for large assets and holds entire files
in memory, and it has no Range / 206 handling. WebKit needs Range responses for
media seeking, so even though the MIME table advertises mp4, webm, and mp3, audio
and video scrubbing over `ryn://` does not work correctly. The IPC command path
already hops off-thread via the copied-executor pattern; static file serving
should do the same and add Range request handling, with streaming or chunked
writes for large files. This is scoped to the webview's scheme handler in
`Ryn.Core`. Status: **Planned**. (tracks ARC-21)

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

Ryn has no single-instance mechanism, no autostart support, no sidecar concept for
bundling and resolving companion binaries, and no runtime capability grants, so a
file the user picks through a native dialog is still denied by the filesystem
plugin unless its path was statically allowlisted. That gap pushes apps toward
over-broad static scopes, the exact anti-pattern the capability system exists to
prevent. The plan is a single-instance facility (with argument and deep-link
forwarding to the running instance), an autostart plugin (LaunchAgent on macOS,
the Run key on Windows, XDG autostart on Linux), a sidecar convention in the
`ryn.json` bundle config, and an option for the dialog plugin to grant a picked
path into the filesystem scope for the session, with opt-in persistence. This
spans `Ryn.Core`, the dialog and filesystem plugins, and the bundler. Status:
**Planned**. (tracks CMP-11)

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
