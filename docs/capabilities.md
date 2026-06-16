# Capabilities (`ryn.json`) Reference

`ryn.json` is the per-app allow-list for IPC commands. It sits next to the executable
(it is copied to the output directory at build time) and is loaded once at startup.
This document is the canonical schema reference; [SECURITY.md](../SECURITY.md) explains
the threat model and the reasoning behind the defaults.

## Top-level shape

```json
{
  "capabilities": {
    "app": true,
    "fs": { "allow": ["readTextFile", "readDir"], "scope": ["$APP_DATA/**"] },
    "shell": {
      "allow": ["execute"],
      "scopedCommands": [ { "name": "git", "args": ["status"] } ],
      "open": { "schemes": ["https", "mailto"] }
    },
    "clipboard": true,
    "notification": true
  },
  "bundle": {
    "identifier": "com.example.myapp",
    "version": "1.0.0",
    "icon": "assets/icon.png"
  }
}
```

The file has two independent top-level objects:

- **`capabilities`** — the IPC allow-list, read at runtime by `RynCapabilitiesLoader`.
- **`bundle`** — packaging metadata, read by `ryn bundle` only. It does not affect runtime security.

## The `capabilities` object

Keys are **plugin prefixes** (the part before the dot in a command name). `app` is the
prefix for your own `[RynCommand("app.*")]` methods; each plugin owns its own prefix
(`fs`, `shell`, `clipboard`, `dialog`, `notification`, `audio`, `tray`, `updater`).

Each value is one of three forms:

| Value | Meaning |
|-------|---------|
| `true` | Allow every command in this plugin. |
| `false` | Deny every command in this plugin. |
| `{ ... }` | Fine-grained object form (below). |

### Object form

```json
"fs": {
  "allow": ["readTextFile", "writeTextFile"],
  "deny": ["remove"],
  "scope": ["$APP_DATA/data", "$APP_DATA/**"]
}
```

| Key | Type | Meaning |
|-----|------|---------|
| `allow` | string array | Command names (without the plugin prefix) that are permitted. |
| `deny` | string array | Command names that are blocked. **`deny` always wins** over `allow`. |
| `scope` | string array | Filesystem path scope for `fs.*` (see below). |
| `scopedCommands` | array | Argv templates for `shell.execute` (see below). |
| `commands` | string array | Legacy binary-name list for shell. Prefer `scopedCommands`. |
| `open` | object | `shell.open` URL-scheme allow-list (see below). |

Notes:

- If you supply only `deny` (no `allow`), every other command in that plugin is allowed
  and the listed ones are blocked. Supply `allow` to switch to a strict allow-list.
- Command-name matching is case-insensitive.
- Internal framework commands (`__ryn.*`) bypass capabilities by design.

## Filesystem `scope`

`scope` restricts which paths `fs.*` commands may touch. Paths are canonicalized
(symlinks resolved at every component) before the containment check.

- **Variable:** `$APP_DATA` expands to the application's base directory
  (`AppContext.BaseDirectory`). It is the only supported variable.
- **Globs:** `*` matches within a directory, `**` matches across directories, `?` matches
  a single character. Example: `"$APP_DATA/**"` allows everything under the app directory.
- **Relative paths** are resolved against the app base directory; absolute paths are used as-is.
- An **empty** `"scope": []` is an explicit deny-all: no path is in scope.

```json
"fs": { "allow": ["readTextFile"], "scope": ["$APP_DATA/content/**"] }
```

## Shell `scopedCommands`

The shell plugin is the highest-risk surface. Prefer **`scopedCommands`** (argv templates)
over the legacy binary-only `commands` list.

```json
"shell": {
  "allow": ["execute"],
  "scopedCommands": [
    { "name": "git", "args": ["status"] },
    { "name": "git", "args": [ { "validator": "^(log|show)$" }, { "validator": "^[\\w./-]+$" } ] }
  ]
}
```

- `name` is the command binary (resolved on `PATH`).
- `args` is an ordered list of argument rules. Each entry is either a **string literal**
  (matched exactly) or an object `{ "validator": "<regex>" }` matched as a full-string regex.
- The invocation's argv must match a scope **exactly**, including length.
- **Never allowlist an interpreter** (`bash`, `sh`, `cmd.exe`, `powershell`) or a flexible
  tool (`env`, `xargs`, `find`, `cat`) — that turns `shell.execute` into arbitrary code
  execution and defeats the sandbox.

## Shell `open`

`shell.open` only launches URLs whose scheme is allow-listed (default `http`, `https`, `mailto`).

```json
"shell": { "allow": ["open"], "open": { "schemes": ["https", "mailto"] } }
```

Bare paths and `file://` URLs are always rejected.

## Defaults when `ryn.json` is missing

The behavior of a **missing** `ryn.json` (or a present file with no `capabilities` object)
depends on the build configuration:

- **Debug** build: falls back to **allow-all** for local convenience.
- **Release** build: **fails closed and denies everything**, and logs a one-time startup
  warning so the misconfiguration is obvious rather than silently inert.

Always ship a `ryn.json` with your app. An empty `"scope": []` or `"commands": []` is an
explicit deny-all.

## The `bundle` object

`bundle` is read by `ryn bundle` to populate packaging metadata. It has no effect at runtime.

| Key | Type | Used by | Meaning |
|-----|------|---------|---------|
| `identifier` | string | macOS, Windows | Reverse-DNS app id (e.g. `com.example.myapp`). |
| `version` | string | all | Bundle version; overridden by `--version`. |
| `icon` | string | all | Path to a PNG/`.icns`/`.ico`; overridden by `--icon`. |
| `sign` | string | macOS | Signing identity; overridden by `--sign`. |
| `manufacturer` | string | Windows | Vendor name written into the MSI metadata. |
| `categories` | string | Linux | Desktop-entry categories for the AppImage. |
| `deepLinkSchemes` | string array | all | Custom URL schemes to register for deep linking. |

```json
"bundle": {
  "identifier": "com.example.myapp",
  "version": "1.0.0",
  "icon": "assets/icon.png",
  "deepLinkSchemes": ["myapp"]
}
```

See [Getting Started](getting-started.md) and [SECURITY.md](../SECURITY.md) for fuller
walkthroughs of configuring and shipping a secure app.
