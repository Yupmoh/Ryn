# GPU-accelerated rendering in Ryn

How to build a smooth, GPU-accelerated rendering surface (a map editor, a node
graph, a sprite-heavy canvas, a 2D game) inside a Ryn webview — and how to make
sure you are actually on the GPU and not silently falling back to software.

> **Short version.** The GPU is *already on* in every Ryn backend. There is no
> magic toggle that makes a slow renderer fast. Lag in a canvas-heavy app is
> almost always the **rendering approach**, not a disabled flag: drawing thousands
> of objects with the DOM or the Canvas 2D API runs on the CPU and hits a wall.
> The fix is to draw through **WebGL** — in practice, via a batching library like
> **PixiJS** — so the work goes to the GPU as a handful of draw calls instead of
> thousands. That is what closes the gap with a native engine like Godot.

---

## 1. The mental model: who does the drawing?

A webview can paint pixels several different ways, and they do *not* cost the same:

| How you draw | Where the work runs | Scales to… |
|---|---|---|
| **DOM + CSS** (divs, absolute positioning) | Layout + paint on the **CPU** main thread | Hundreds of static nodes. Moving many nodes per frame triggers reflow and stalls everything. |
| **Canvas 2D** (`ctx.drawImage`, `fillRect`) | CPU-orchestrated; GPU-backed only under browser heuristics | A few hundred to low-thousands of objects before jank. |
| **WebGL / WebGL2** (raw, or via PixiJS / three.js) | **GPU**, talking to Direct3D / Metal / OpenGL under the hood | Tens to hundreds of thousands of sprites if batched. |
| **WebGPU** | **GPU**, lower CPU overhead + compute | Highest — but not available on every platform yet (see §4). |

**This is what "switch the backend to OpenGL / three.js" meant.** WebGL is
essentially OpenGL ES exposed to JavaScript — it talks straight to the GPU. You
almost never write raw WebGL; you use a library that wraps it. So "switching the
backend" does not mean changing anything in Ryn or C# — it means changing **which
drawing API your front-end JavaScript uses for the viewport**: from DOM/Canvas 2D
(CPU) to WebGL-via-PixiJS (GPU).

The GPU was available the whole time. The lag came from feeding it work through a
CPU-bound API.

---

## 2. The Ryn knobs (and what they do *not* do)

Ryn exposes two relevant options on both `RynOptions` (app-wide) and
`RynWindowOptions` (per secondary window). Shipped in PR #32.

### `HardwareAcceleration` — `bool`, default `true`

GPU compositing for the whole webview. **It is on by default**, which is what you
want for canvas/WebGL/WebGPU. The only reason to set it `false` is as a
compatibility escape hatch for flaky GPU drivers or headless/virtualized
environments — turning it off makes everything slower. It is applied once, before
the webview is created, so it can't change for a live window.

```csharp
var app = RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Title = "Map Editor";
        opts.Width = 1280;
        opts.Height = 800;
        // opts.HardwareAcceleration is already true — leave it on.
    })
    .Build();
```

> **There is no "make it fast" toggle beyond this.** If your renderer is slow with
> `HardwareAcceleration = true`, the problem is in your JS rendering code, not Ryn.

### `BrowserFlags` — `IList<string>`, the per-engine escape hatch

Engine-specific flags applied before the webview is created — the lever for
opting into experimental rendering features. **The syntax is not portable**,
because each OS runs a different engine. Always guard a flag behind an OS check.

```csharp
.ConfigureOptions(opts =>
{
    if (OperatingSystem.IsWindows())
    {
        // WebView2 / Chromium command-line switches:
        opts.BrowserFlags.Add("--ignore-gpu-blocklist");   // force GPU on blocklisted hardware
        // opts.BrowserFlags.Add("--enable-unsafe-webgpu"); // only needed on ARM64; not on Win x64
        // opts.BrowserFlags.Add("--use-angle=d3d11");      // pick the ANGLE backend
    }
    // macOS (WKWebView): key=value pairs applied to WKWebViewConfiguration (value parsed as JSON).
    // Linux (WebKitGTK): WebKit feature/setting flags. For GPU issues on Linux you usually want
    //                    environment variables instead — see §3.
})
```

You can also set both from `ryn.json` / configuration: a `HardwareAcceleration`
boolean and a `BrowserFlags` array. They project onto secondary windows too.

---

## 3. Are you actually on the GPU? (verify the fallback)

The single most common cause of "why is this so slow" is a **silent software
fallback** — the webview couldn't use the GPU and quietly switched to a CPU
rasterizer (Chromium's *SwiftShader*, Mesa's *llvmpipe*). Everything still renders,
just at a fraction of the speed.

Log the real renderer string on startup:

```js
function gpuRenderer() {
  const c = document.createElement('canvas');
  const gl = c.getContext('webgl2') || c.getContext('webgl');
  if (!gl) return 'NO WEBGL';
  const ext = gl.getExtension('WEBGL_debug_renderer_info');
  return ext ? gl.getParameter(ext.UNMASKED_RENDERER_WEBGL) : 'unknown';
}
const r = gpuRenderer();
console.log('GPU renderer:', r);
if (/swiftshader|llvmpipe|software|basic render/i.test(r)) {
  console.warn('SOFTWARE FALLBACK — not using the GPU!');
}
```

What you want to see:
- **Windows / WebView2:** `ANGLE (..., Direct3D11 ...)` — good. `SwiftShader` or
  `Microsoft Basic Render Driver` — software fallback (bad).
- **Linux / WebKitGTK:** a real GPU name — good. `llvmpipe` or `Software` — software (bad).
- **macOS / WKWebView:** WebKit **masks** the renderer to a generic `"Apple GPU"`,
  so this check can't distinguish hardware from software on macOS. Judge by frame
  timing instead. In practice macOS WKWebView is hardware-accelerated (ANGLE → Metal)
  and "just works".

If you see a software fallback on **Windows**, try `--ignore-gpu-blocklist` (and,
if needed, `--use-angle=d3d11`) via `BrowserFlags`. Newer WebView2 runtimes are
moving to *fail* WebGL rather than fall back silently, so also handle the
context-creation-fails case.

If you see `llvmpipe` on **Linux**, it's an environment/driver issue, not a Ryn
setting. The relevant levers are **environment variables set before launch**, not
`BrowserFlags`:
- `WEBKIT_DISABLE_COMPOSITING_MODE=1` — fixes blank/garbled windows under software GL.
- `WEBKIT_DISABLE_DMABUF_RENDERER=1` — common NVIDIA / VM workaround.
- `LIBGL_ALWAYS_SOFTWARE=1` / `GALLIUM_DRIVER=llvmpipe` — these *force* software; make
  sure they aren't set in your environment.

---

## 4. WebGL vs WebGPU — what's available where (as of mid-2026)

**WebGL2 is hardware-accelerated by default on all three backends.** It is the
correct default target for a 2D map editor and works everywhere Ryn runs.

**WebGPU is faster but not yet a cross-platform default:**

| Backend | WebGL2 | WebGPU |
|---|---|---|
| **WebView2 (Windows)** | ✅ default (ANGLE → D3D11) | ✅ default (Chromium 113+, via D3D12) |
| **WKWebView (macOS)** | ✅ default (ANGLE → Metal) | ⚠️ only on **macOS Tahoe 26+** (`navigator.gpu` is `undefined` on Sonoma/Sequoia) |
| **WebKitGTK (Linux)** | ✅ default | ❌ **not available at all** |

**Conclusion: build on WebGL2, feature-detect WebGPU, never assume it.** A Ryn app
that requires WebGPU would be broken on Linux and on pre-Tahoe Macs. The clean way
to get "WebGPU where available, WebGL2 everywhere else" is to use a library that
auto-selects the backend (PixiJS does this) and to guard any direct WebGPU code
with `if ('gpu' in navigator)`.

---

## 5. Library recommendation

**Use PixiJS v8 for the rendering viewport.** Reasons:

- It auto-detects the backend: **WebGPU where available, automatic WebGL2 fallback
  otherwise** — exactly the cross-platform story above, for free.
- **Automatic sprite batching:** sprites sharing a texture atlas / blend mode
  collapse into one or two draw calls. Draw-call count is *the* binding constraint;
  batching is what makes thousands of objects cheap.
- It is the benchmarked top performer for 2D.

Plan for these gaps (Pixi is a renderer, not an editor framework):
- **Pan/zoom** isn't in core — add the [`pixi-viewport`](https://github.com/pixijs-userland/pixi-viewport)
  plugin (drag / pinch / wheel-zoom / clamp).
- **Culling** is off by default — set `cullable = true` or cull to the viewport yourself.
- **Selection handles** you build yourself.
- **Test the WebGPU→WebGL fallback on real target hardware** — it has had reported bugs.

**Alternative — Konva.** If your editor is more "a few hundred to low-thousands of
selectable shapes" than "a sprite-heavy tilemap," Konva gives you a built-in
selection `Transformer` (drag/resize/rotate handles), layers, hit-detection, JSON
serialization, and React/Vue/Svelte bindings out of the box. The trade-off: it's
**Canvas 2D**, so it won't match WebGL once object counts climb into the thousands.

**three.js** only if you also need real 3D (its WebGPU renderer is still officially
"experimental"). **regl / raw WebGL** only for a fully bespoke renderer.

> Typical map-editor architecture: **PixiJS for the canvas viewport** (tiles +
> sprites, batched, culled) and ordinary HTML/CSS (or your framework) for the
> surrounding panels, toolbars, and inspectors.

---

## 6. Architecture patterns that kill lag

These matter more than any flag. A map editor that follows them will feel native.

1. **Render on demand, not on a permanent loop.** Don't run a `requestAnimationFrame`
   loop that redraws every frame forever. Mark the scene "dirty" on change, coalesce
   bursty input (mousemove, wheel) into **at most one redraw per animation frame**,
   and only run a continuous loop while something is actually animating (inertial pan,
   animated tiles). An idle editor should be drawing **zero** frames.
2. **Batch + texture atlases.** Pack all your tile/sprite art into a few atlas
   textures. A texture switch flushes the batch, so interleaving many separate images
   or blend modes destroys batching. One atlas → one draw call for the whole layer.
3. **Cull to the viewport.** Only draw tiles/objects that are on or near screen. For
   very large maps, chunk the world and load/unload regions around the camera. A
   10,000×10,000 tile map should still only draw the ~hundreds of tiles you can see.
4. **Render the tile layer as a tilemap, not N sprites.** The efficient WebGL
   technique draws the visible map as a single quad and looks up each tile in a
   fragment shader (PixiJS has tilemap helpers), instead of one sprite object per tile.
5. **No per-frame allocations.** Creating objects/arrays inside the render loop feeds
   the garbage collector, and GC pauses are the #1 cause of stutter. Reuse objects;
   pool them.
6. **Create the WebGL context once.** Contexts are heavyweight and limited in number —
   never recreate the canvas/context per frame.
7. **OffscreenCanvas + a Web Worker** moves rendering off the main thread so UI input
   stays responsive (feature-detect `'OffscreenCanvas' in window`; keep a main-thread
   fallback for older engine versions). Note this does **not** require
   `SharedArrayBuffer` — see the Ryn caveat below.
8. **Keep per-frame data inside the webview.** Never push image/vertex/tile buffers
   across the C#↔JS IPC boundary every frame — the bridge serializes payloads. Do all
   rendering inside JS/WebGL; use Ryn's IPC layer only for **load / save / commands**,
   passing file paths or handles, not pixel data.
9. **Handle `devicePixelRatio`** for crisp HiDPI, but remember it multiplies pixel
   cost (dpr 2 → 4× the pixels). Capping the backing store at ~1.5–2× is common even
   when the OS reports more.
10. **Don't let the surrounding DOM fight the canvas.** Heavy CSS effects
    (`backdrop-blur`, animated shadows) and layout thrash on the page compete with the
    renderer for the main thread.

---

## 7. `SharedArrayBuffer`, multithreaded WASM, and Blazor — the `CrossOriginIsolation` option

Some of the heaviest paths — **multithreaded WebAssembly** (a threaded
Emscripten/Godot-web build, a Rust/WASM renderer using threads, or **multithreaded
Blazor WASM**) and cross-thread pixel-buffer sharing — require `SharedArrayBuffer`,
which the browser only enables when the page is **cross-origin isolated**. That
requires `Cross-Origin-Opener-Policy: same-origin` +
`Cross-Origin-Embedder-Policy: require-corp` on the document (and
`Cross-Origin-Resource-Policy` on same-origin subresources).

**Ryn ships this as a one-line opt-in:**

```csharp
.ConfigureOptions(opts =>
{
    opts.CrossOriginIsolation = true;   // emits COOP/COEP/CORP on ryn:// and the local server
})
```

(or `"CrossOriginIsolation": true` in `ryn.json`). It applies to the `ryn://` scheme
handler *and* the local HTTP server, and projects onto secondary windows. With it on:

- `window.crossOriginIsolated === true`, so `SharedArrayBuffer` is available.
- **Caveat 1 (subresources):** under COEP `require-corp`, cross-origin subresources (a
  CDN script, a remote font) must send their own CORP/CORS headers or the browser
  blocks them. Keep assets same-origin (bundled) or ensure the remote sends them.
- **Caveat 2 (secure context — important):** `crossOriginIsolated` also requires a
  *secure context*. `http://localhost` (the local server) qualifies, but the `ryn://`
  custom scheme may **not** be treated as a secure context on WebKit (macOS/Linux) — so
  the headers can be correct yet `crossOriginIsolated` stays `false`. **If you need
  `SharedArrayBuffer`, serve over the local server** (`UseLocalServer = true`) alongside
  `CrossOriginIsolation = true`, rather than the default `ryn://` content path. Verify
  with `console.log(window.crossOriginIsolated)` after load.

What needs it vs. what doesn't:

- **Plain WebGL / WebGPU / PixiJS** — does **not** need it. Leave it off.
- **OffscreenCanvas + Worker rendering** — does **not** need it (`transferControlToOffscreen`
  works without `SharedArrayBuffer`).
- **Multithreaded WASM / threaded Godot-web / Rust+wasm-threads** — **needs it on**.
- **Blazor WebAssembly** — single-threaded Blazor works without it; **multithreaded
  Blazor WASM needs it on**. (Dev-server note: if you serve Blazor from an external
  dev server via `RynOptions.Url`, that server must send COOP/COEP itself — Ryn only
  controls the `ryn://` scheme and its own local server, not your external dev server.)

So for the **map editor** with PixiJS: leave `CrossOriginIsolation` off. Flip it on
only if you move to a threaded WASM renderer or multithreaded Blazor.

---

## 8. Diagnosing a slow renderer — checklist

1. **Log the renderer string** (§3). Software fallback? Fix that first.
2. **Open DevTools → Performance**, record while panning/editing. Look for:
   - red bar over the FPS track (framerate hurting UX),
   - long tasks (>50 ms) on the main thread (red-triangle marks),
   - purple Layout events with red triangles (forced reflow / layout thrash).
   - Throttle the CPU 4–20× to surface jank your dev machine hides.
3. **Check the usual culprits:**
   - `getImageData()` / `toDataURL()` readback, or `willReadFrequently: true` →
     flips the canvas to **software CPU**. Avoid in the hot path.
   - Per-frame `new` / array allocations → GC stutter.
   - Recreating contexts/canvases per frame.
   - Canvas 2D or DOM for thousands of objects → the headline cause. Move to PixiJS.
   - A permanent rAF loop redrawing an idle scene → switch to render-on-demand.

---

## 9. Starter recipe for the map editor

1. Front-end: render the viewport with **PixiJS v8**; keep panels/toolbars in
   HTML/CSS (or your framework).
2. Add **`pixi-viewport`** for pan/zoom; turn on **culling**; draw the tile layer as
   a **tilemap / atlas-batched** sprites, not one object per tile.
3. **Render on demand** — redraw only when the scene changes; coalesce input to one
   frame.
4. On startup, **log `UNMASKED_RENDERER_WEBGL`** and warn on `swiftshader`/`llvmpipe`
   (skip the assertion on macOS, where it's masked).
5. **Feature-detect `navigator.gpu`**; let PixiJS pick WebGPU vs WebGL2. Never require
   WebGPU (Linux has none; macOS needs Tahoe 26+).
6. Keep `HardwareAcceleration = true` (default). Add Windows-only
   `--ignore-gpu-blocklist` via `BrowserFlags` *only* if §3 shows a software fallback.
7. Do **all** rendering in JS/WebGL; use Ryn IPC only for open/save/commands — never
   per-frame pixel data.

Done right, a webview-based 2D editor matches a native one for this workload —
Godot's own web export is itself a WebAssembly + WebGL2 canvas renderer, so "web
rendering" is not inherently second-class. The lag you were fighting was
architecture, not the platform.
