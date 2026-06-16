# Vite Integration

Ryn supports Vite as a frontend build tool. This guide covers setting up a Ryn
project with a Vite-powered frontend, using the same pattern as the
`samples/VueApp` reference implementation.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (v18+) and npm
- Ryn CLI installed (`dotnet tool install -g Ryn.Cli` or built from source)

## Quick Start

```bash
ryn new MyApp --vite
cd MyApp/frontend
npm install
cd ..
ryn dev --vite
```

This scaffolds a C# backend with a `frontend/` directory containing a vanilla
TypeScript + Vite project.

`ryn dev --vite` starts (and forwards the output of) the Vite dev server for you
and points the webview at it — you do not need a second terminal running
`npm run dev`. `ryn dev` also auto-detects a Vite frontend (a `frontend/`
directory with a `package.json`), so plain `ryn dev` in a scaffolded Vite
project behaves the same; pass `--vite` explicitly to force it.

## Project Structure

A Vite-enabled Ryn project has two halves:

```
MyApp/
  MyApp.csproj          # C# project (Ryn.Core, Ryn.Ipc references)
  Program.cs            # Entry point — switches between dev URL and wwwroot
  Commands.cs           # [RynCommand] handlers
  appsettings.json      # Window title, size, DevTools toggle
  ryn.json              # Capability allowlist
  wwwroot/              # Production build output (Vite writes here)
    index.html
    assets/
  frontend/             # Vite source project
    package.json
    vite.config.ts
    tsconfig.json
    index.html
    src/
      main.ts           # App entry point
      ryn.d.ts          # TypeScript declarations for window.__ryn
```

## Development Workflow

During development the Vite dev server and the Ryn app run together under one
command:

```bash
cd MyApp
ryn dev --vite
```

`ryn dev --vite` does three things:

1. **Starts the Vite dev server** in `frontend/` (and forwards its output to your
   terminal). Vite serves the UI at `http://localhost:5173` with hot module
   replacement.
2. **Launches the C# app with the `--vite` argument.** The generated `Program.cs`
   checks for that argument and opens the Vite dev server URL instead of loading
   static files:
   ```csharp
   if (args.Contains("--vite"))
       opts.Url = new Uri("http://localhost:5173");
   else
       opts.ContentDirectory = Path.Combine(AppContext.BaseDirectory, "wwwroot");
   ```
3. **Rebuilds and relaunches** the C# backend when `.cs` files change.

Plain `ryn dev` auto-detects a Vite frontend (a `frontend/` directory with a
`package.json`) and behaves the same way; use `--vite` to force Vite dev mode
regardless of detection.

To drive the backend in Vite dev mode by hand (with Vite started separately via
`npm run dev` in `frontend/`):

```bash
dotnet run -- --vite
```

Changes to `.ts`, `.html`, or `.css` files in `frontend/` are picked up instantly
by Vite. Changes to `.cs` files are picked up by `ryn dev`, which rebuilds and
relaunches the C# backend.

## Production Build

Build the frontend, then publish the C# app:

```bash
cd MyApp/frontend
npm run build          # Vite outputs to ../wwwroot
cd ..
ryn build              # or: dotnet publish -c Release
```

The Vite config sets `outDir: '../wwwroot'` so the built assets land where
Ryn's `ContentDirectory` expects them.

## IPC from the Vite App

Ryn injects the `window.__ryn` bridge into every page automatically. No extra
`<script>` tags or imports are needed.

Call C# commands from TypeScript:

```typescript
// Call a [RynCommand] named "app.greet" with a string parameter
const result = await window.__ryn.invoke('app.greet', { name: 'World' });
```

The bridge provides three methods:

| Method   | Signature                                                    | Description            |
|----------|--------------------------------------------------------------|------------------------|
| `invoke` | `(command: string, args?: Record<string, unknown>) => Promise<unknown>` | Call a C# command      |
| `on`     | `(event: string, callback: (data: unknown) => void) => void`            | Subscribe to an event  |
| `off`    | `(event: string, callback: (data: unknown) => void) => void`            | Unsubscribe            |

## TypeScript Declarations

The scaffolded project includes `frontend/src/ryn.d.ts` with type declarations
for the Ryn bridge:

```typescript
interface RynBridge {
  invoke(command: string, args?: Record<string, unknown>): Promise<unknown>
  on(event: string, callback: (data: unknown) => void): void
  off(event: string, callback: (data: unknown) => void): void
}

interface Window {
  __ryn: RynBridge
}
```

This gives full IntelliSense when calling `window.__ryn.invoke(...)` in any
`.ts` file.

For a more structured approach, create a typed wrapper module (see
`samples/VueApp/frontend/src/ryn.ts` for a Vue example):

```typescript
// src/ryn.ts
function ryn() {
  return window.__ryn;
}

export async function greet(name: string): Promise<string> {
  return (await ryn().invoke('app.greet', { name })) as string;
}
```

## Using a Framework (Vue, React, Svelte, etc.)

The `--vite` flag scaffolds a vanilla TypeScript project. To use a framework,
replace the `frontend/` directory with a framework-specific Vite template and
keep two things:

1. **`vite.config.ts`** must set `build.outDir` to `'../wwwroot'`:
   ```typescript
   export default defineConfig({
     plugins: [vue()],  // or react(), svelte(), etc.
     server: {
       port: 5173,
       strictPort: true,
     },
     build: {
       outDir: '../wwwroot',
       emptyOutDir: true,
     },
   });
   ```

2. **`window.__ryn`** is available globally. Add the type declarations from
   `ryn.d.ts` to your project's type roots or `env.d.ts`.

See `samples/VueApp/` for a complete Vue 3 example with typed IPC wrappers,
components, and source-generated JSON serialization.
