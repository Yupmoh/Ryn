# Accessibility and Internationalization

This document records where Ryn stands today on accessibility (a11y) and
internationalization (i18n), and what is and is not the framework's responsibility.
Ryn is alpha software; this is a statement of current state and intent, not a finished
feature set.

## Accessibility (a11y)

### What the platform gives you

Ryn renders your UI in the operating system's own webview (WebView2, WKWebView,
WebKitGTK). That means your frontend inherits the **full accessibility stack of the host
browser engine**: the accessibility tree, screen-reader support (Narrator, VoiceOver,
Orca), keyboard navigation, focus management, and high-contrast/forced-colors handling
are all provided by the webview, exactly as they would be in a browser. Standard web a11y
practices apply directly:

- Set `<html lang="...">` so assistive technology announces content in the right language.
- Use semantic HTML and ARIA roles/attributes where semantics are not implicit.
- Ensure focus order is logical and all interactive elements are keyboard-reachable.
- Meet contrast guidelines and respect `prefers-reduced-motion` / `prefers-color-scheme`.

Because the UI is web content, the existing web a11y tooling (axe, Lighthouse, browser
devtools) works against a Ryn app the same way it works against a website.

### Current state of the first-party surfaces

- **Native chrome** (the window frame, title bar, and tray menus) is provided by the OS
  and carries the platform's own accessibility behavior.
- **Scaffold and sample HTML** are intentionally minimal and are being brought up to
  baseline (a `lang` attribute on the root element, semantic structure). They are starting
  points, not accessibility references — audit your own UI.

### Intent

Accessibility is a table-stakes axis, not a differentiator we are claiming. The framework's
job is to not get in the way of the webview's accessibility stack and to make the
first-party scaffold/samples a reasonable starting point. A dedicated accessibility audit
of the samples and the native chrome is planned but not yet done. The responsibility for an
accessible application UI sits with the app author, as it does for any web frontend.

## Internationalization (i18n)

### Your frontend

Localizing the **application UI** is a frontend concern and is fully under your control:
use any JavaScript i18n library, ship translated strings, and switch locale in the webview
like any web app. Ryn does not constrain this.

### Framework-emitted strings

A small number of strings are emitted by Ryn itself rather than by your frontend:

- **CLI output** (`ryn new`, `ryn dev`, `ryn build`, `ryn bundle`, `ryn doctor`).
- **Error messages** surfaced from C# to the webview (for example IPC dispatch errors).
- **Native plugin text** routed through the OS (dialog and notification bodies are the
  strings *you* pass in, so those are already yours to localize).

These framework-emitted strings are currently **hardcoded in English** with no `.resx` or
culture mechanism.

### Intent / non-goal for now

For the alpha, localizing framework-emitted strings is an explicit **non-goal**: the CLI and
internal error text remain English. This is a deliberate scope decision, not an oversight.
If demand warrants it, a `.resx`/`IStringLocalizer`-based mechanism for the runtime-facing
strings (IPC errors, plugin messages) may be added later; the CLI is likely to stay English.
Until then, do not rely on framework strings being translatable, and localize your own UI in
the frontend.
