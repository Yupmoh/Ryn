# Ryn Security Model

Ryn runs your C# backend in the same process as a web UI rendered by the OS webview. The trust
boundary is **between the frontend (HTML/JS) and your C# commands**. This document describes the
controls that enforce that boundary and how to configure them safely.

> Status: Ryn is early alpha. The controls below are implemented and tested, but the framework has
> not had an external audit. Treat it accordingly and report issues privately (see the bottom).

## Threat model

- **Untrusted/compromised frontend.** Even if you wrote the HTML/JS, a supply-chain compromise (a bad
  npm dependency) or an XSS can run arbitrary JavaScript in the webview. That JS can call any IPC
  command the capabilities allow. Capabilities, not "we trust our own code", are the boundary.
- **Other local processes.** When the optional local HTTP server is enabled, other processes/users on
  the machine could reach it. The per-launch IPC token + loopback/Origin checks defend against this.
- **The network / update channel.** Auto-updates are a code-execution channel and are protected by
  mandatory signature verification over HTTPS.

## Capabilities (`ryn.json`)

`ryn.json` sits next to the executable and is the allow-list for IPC commands.

```json
{
  "capabilities": {
    "fs":    { "allow": ["readTextFile", "readDir"], "scope": ["$APP_DATA/**"] },
    "shell": { "allow": ["execute"], "scopedCommands": [ { "name": "git", "args": ["status"] } ] },
    "clipboard": true,
    "updater": { "allow": ["check", "download", "apply"] }
  }
}
```

- **Deny-by-default & fail-closed.** A plugin is denied unless it appears here. **If `ryn.json` is
  missing or has no `capabilities` section, a Release build denies everything** (a Debug build of the
  app falls back to allow-all for convenience). When a Release build fails closed because `ryn.json` is
  absent, Ryn emits a one-time startup warning through the host logger so the misconfiguration is
  obvious rather than silently inert. Never ship without a `ryn.json`.
- `true` = allow all commands for that plugin; `false` = deny all; `{ "allow": [...], "deny": [...] }`
  for fine-grained control. `deny` always wins.
- Internal framework commands (`__ryn.*`) bypass capabilities by design.

## FileSystem scope

`"scope"` entries restrict which paths `fs.*` commands may touch.

- Paths are **canonicalized (symlinks resolved at every component, including parents and not-yet-existing
  write targets) before** the containment check, so a symlink that lexically sits inside an allowed
  directory but points outside it is rejected.
- Globs are supported: `*` (within a directory), `**` (across directories), `?` (single char).
- Case sensitivity matches the host filesystem (case-sensitive on Linux, case-insensitive on macOS/Windows).
- `FileSystemOptions.MaxReadBytes` (default 64 MiB) caps single-file reads to prevent memory exhaustion.

## Shell scope

The shell plugin is the highest-risk surface. Use **`scopedCommands`** (argv templates), not the legacy
binary-only `commands` list:

```json
"shell": {
  "allow": ["execute"],
  "scopedCommands": [
    { "name": "git", "args": ["status"] },
    { "name": "git", "args": [ { "validator": "^(log|show)$" }, { "validator": "^[\\w./-]+$" } ] }
  ],
  "open": { "schemes": ["https", "mailto"] }
}
```

- Each argument is matched either by an exact literal or a full-string regex `validator`. The argv must
  match a scope exactly (including length).
- **Never allowlist an interpreter** (`bash`, `sh`, `cmd.exe`, `powershell`) or a flexible tool
  (`env`, `xargs`, `find`, `cat`). Doing so turns `shell.execute` into arbitrary code/file access and
  defeats the sandbox. (The one legitimate exception is a deliberate terminal-emulator app.)
- `shell.open` only launches URLs whose scheme is allow-listed (default `http`/`https`/`mailto`); bare
  paths and `file://` are rejected.
- `ShellOptions` also offers `WorkingDirectory` and `ScrubEnvironmentVariables` (defaults strip common
  secret markers like `*TOKEN*`, `*SECRET*`, `AWS_*`) so spawned tools can't leak host secrets.

## IPC transport

- The default transport is the in-process `ryn://` scheme handler — not reachable by other processes.
- The optional local HTTP server binds to **loopback only**, requires a **per-launch random token**
  (`X-Ryn-Token`) on every request, validates the `Host`/`Origin`, and caps request body size.
- Command results are returned inline on the response body (no `eval`-based result injection).
- `EmitEvent(name, jsonData)` validates that `jsonData` is well-formed JSON; prefer the typed
  `EmitEvent<T>(name, payload, JsonTypeInfo<T>)` overload, which serializes safely via source-gen.
- IPC from a **remote** page (e.g. an `https://` site loaded into the webview) is intentionally **not**
  wired — that would grant a remote origin native access. Local content and loopback dev servers (Vite)
  get IPC; remote pages get a console warning.

## Auto-updater

Updates are only applied if a **detached signature verifies** against a public key embedded in your app.

1. Generate a keypair once (ECDSA P-256; base64 SPKI public / PKCS#8 private):

   ```csharp
   var (publicKey, privateKey) = Ryn.Plugins.Updater.UpdateSignature.GenerateKeyPair();
   // embed publicKey in UpdaterOptions.PublicKey; keep privateKey secret (CI secret).
   ```

2. At release time, sign each asset and publish the signature as `<asset-name>.sig` (base64):

   ```csharp
   var sig = UpdateSignature.Sign(File.ReadAllBytes(assetPath), privateKey);
   File.WriteAllText(assetPath + ".sig", Convert.ToBase64String(sig));
   ```

3. Configure the app:

   ```csharp
   services.AddRynUpdater(o => {
       o.GitHubOwner = "you"; o.GitHubRepo = "app";
       o.PublicKey = "<base64 public key>";
   });
   ```

Guarantees: the download is verified **before** anything executes; downloads/applies use an opaque
server-side handle (the frontend can never point `apply` at an arbitrary file); the download URL must be
HTTPS on a GitHub release host; a monotonic version floor prevents downgrade attacks; and apply never
builds shell strings from paths (managed file ops + positional argv only). **If `PublicKey` is unset, the
updater refuses to download** — there is no unverified-update path.

## Native-library integrity

`build/download-native.sh` / `.ps1` verify every downloaded native archive against a pinned SHA-256 in
`build/native-checksums.txt` before extraction; a mismatch aborts.

## Reporting a vulnerability

Please report security issues privately to the maintainer rather than opening a public issue, and allow
reasonable time for a fix before disclosure.
