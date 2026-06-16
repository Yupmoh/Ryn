# Contributing to Ryn

Thanks for your interest in Ryn — **Rich Yet Native**, a cross-platform .NET
framework for building desktop apps with web UIs. Contributions are welcome:
bug reports, fixes, plugins, docs, and platform support (Windows/Linux especially).

## Ground rules

- `main` is protected. **Nobody pushes to it directly** (except the maintainer for
  hotfixes) — all changes land through a pull request that passes CI and review.
- Be excellent to each other. Assume good faith, keep discussion technical.
- By contributing you agree your work is licensed under the project's
  [MIT License](LICENSE).

## Getting set up

Prerequisites:

- **.NET 10 SDK** (preview) — every project targets `net10.0`.
- macOS, Windows, or Linux. Native webview support today is best on macOS;
  Windows (WebView2) and Linux (WebKitGTK) are works in progress.
- For building the native saucer libraries from source: `cmake` + `ninja`.

```bash
# Build everything
dotnet build Ryn.slnx

# Run the test suite
dotnet test Ryn.slnx

# Run a sample
dotnet run --project samples/Showcase/Showcase.csproj
```

## The workflow (fork → branch → PR)

1. **Fork** the repo and clone your fork.
2. Create a topic branch: `git checkout -b fix/short-description`.
3. Make your change. Add or update tests — the suite must stay green.
4. Run `dotnet build Ryn.slnx` and `dotnet test Ryn.slnx` locally.
5. Commit using the style below.
6. Push to your fork and open a **pull request** against `Yupmoh/Ryn:main`.
7. CI runs and the maintainer reviews. Address feedback by pushing more commits
   to the same branch.

## Commit style

Conventional, lowercase, with a bulleted body explaining the change:

```
type: lowercase description

- what changed and why
- one bullet per distinct point
```

Types: `feat`, `fix`, `chore`, `test`, `docs`, `refactor`, `perf`.
No AI attribution / `Co-Authored-By` lines.

## What makes a PR easy to merge

- Focused — one logical change per PR.
- Tested — new behavior has tests; bug fixes have a regression test.
- AOT-safe — no reflection; serialization goes through the source-generated
  `JsonSerializerContext`. The codebase is NativeAOT-first.
- Documented — update the README / docs if you change user-facing behavior.

## Test dependencies

The test suite uses [FluentAssertions](https://www.nuget.org/packages/FluentAssertions),
**pinned to `7.2.0`** in `Directory.Packages.props`. This is deliberate: the FluentAssertions
8.x line moved to Xceed's community/commercial license, which is a gray zone for contributors
who run the suite in a commercial context. The 7.x line is **Apache-2.0** and free for any use.
7.2.0 keeps the same `FluentAssertions` namespace and assertion API, so no test code changes
are needed when building against it.

If you prefer a fully MIT-licensed, drop-in alternative, the
[AwesomeAssertions](https://www.nuget.org/packages/AwesomeAssertions) fork (Apache-2.0, keeps the
`FluentAssertions` namespace) is API-compatible; swapping it in would mean changing the
`PackageReference Include="FluentAssertions"` IDs in the seven test `.csproj` files. Either way,
do not bump FluentAssertions back to 8.x without revisiting this licensing note.

## Reporting bugs

Open an issue with: what you did, what you expected, what happened, your OS, and
the .NET version. A minimal repro is worth a thousand words.
