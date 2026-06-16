# VueApp sample

A Ryn app with a Vue 3 + Vite + TypeScript frontend and typed IPC. It demonstrates the
recommended structure for a framework frontend: a `frontend/` Vite project that builds into
`wwwroot/`, which Ryn serves over the `ryn://` scheme.

See [docs/vite-integration.md](../../docs/vite-integration.md) for the full Vite guide.

## Run it

The repository ships a **prebuilt** `wwwroot/` snapshot so the sample runs without a Node
toolchain:

```bash
dotnet run --project samples/VueApp
```

## Develop the frontend

The Vite source lives in `frontend/`. To work on the UI:

```bash
cd samples/VueApp/frontend
npm install
npm run dev        # Vite dev server with hot module replacement
```

Then run the backend pointed at the dev server:

```bash
cd samples/VueApp
dotnet run -- --dev
```

## Rebuild the production bundle

`npm run build` writes the production assets into `wwwroot/` (the Vite config sets
`outDir: '../wwwroot'`):

```bash
cd samples/VueApp/frontend
npm install        # first time only
npm run build      # emits wwwroot/index.html + wwwroot/assets/index-<hash>.{js,css}
```

> **Note on the committed bundle.** The files under `wwwroot/` are a prebuilt snapshot kept
> in the repo only so the sample runs out-of-box. Each `npm run build` produces new
> content-hashed asset filenames, and those fresh outputs are **gitignored** (see
> `.gitignore`) so the bundle does not silently drift from `frontend/src` or clutter diffs.
> If you change the frontend, rebuild locally to see your changes; the committed snapshot is
> not expected to track every source edit.
