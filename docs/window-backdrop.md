# Window backdrop (vibrancy / acrylic / mica)

A **backdrop material** paints a translucent, blurred layer behind a transparent webview background —
macOS vibrancy, Windows 11 acrylic/mica. Give the page a (semi-)transparent background and the desktop
tints through.

```csharp
RynApplication.CreateBuilder()
    .ConfigureOptions(opts =>
    {
        opts.Backdrop = BackdropMaterial.Blur;   // None | Blur | Acrylic | Mica
        // Make the page background (semi-)transparent so the material shows:
        opts.Html = "<body style='background:rgba(255,255,255,0.12)'>…</body>";
    });
```

At runtime, from C#:

```csharp
window.SetBackdrop(BackdropMaterial.Acrylic);
var applied = window.GetBackdrop();   // may be None if it degraded on this OS
```

Or from JS:

```js
await window.__ryn.invoke('window.setBackdrop', { material: 'mica' }); // none|blur|acrylic|mica
const applied = await window.__ryn.invoke('window.getBackdrop');       // "none" if degraded
```

> The JS `window.setBackdrop` / `window.getBackdrop` commands (like all `window.*` commands) require the
> **`window`** capability in `ryn.json` (`{ "capabilities": { "window": true } }`). Without it the call is
> denied — the rejection surfaces only as a host-stdout warning and a rejected promise, so add the capability
> if a `window.*` invoke silently fails. The C# `IRynWindow.SetBackdrop`/`GetBackdrop` are not gated.

## Platform support

| Material | macOS | Windows 11 (22H2+) | Windows 10 | Linux |
|---|---|---|---|---|
| `Blur` | ✅ NSVisualEffect `.menu` | ✅ acrylic | ❌ → `None` | ❌ → `None` |
| `Acrylic` | ✅ NSVisualEffect `.hudWindow` | ✅ acrylic | ❌ → `None` | ❌ → `None` |
| `Mica` | ✅ NSVisualEffect `.underWindowBackground` | ✅ mica | ❌ → `None` | ❌ → `None` |

Where a material isn't available the window stays opaque and `getBackdrop` reports `None` — **always
check the effective value and design an opaque fallback** rather than assuming translucency. The
backdrop is per-window and composes with `Transparent`, a frameless `TitleBarStyle`, and always-on-top
for overlay/HUD chrome. On macOS the effect view sits behind the webview and tracks the window on resize
with no rendering artifacts.
