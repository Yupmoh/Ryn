// INT-05 residual (defense-in-depth). The saucer P/Invokes live in the ClangSharp-generated
// Generated/Saucer.cs, which is overwritten on every regeneration and therefore cannot carry a
// per-import [DefaultDllImportSearchPaths]. Applying the attribute at assembly scope covers every
// P/Invoke in this assembly at once and survives regeneration.
//
// NativeLibraryResolver.SetDllImportResolver runs first and resolves saucer-bindings/ryn-pty from
// BaseDirectory-anchored absolute paths only. This attribute hardens the runtime's *fallback* default
// probe (used when the resolver returns nint.Zero, and for any non-allow-listed import) so a bare-name
// native load cannot be satisfied from an attacker-controlled current working directory or PATH:
//   - AssemblyDirectory: load the native lib from the deployed application/assembly directory (where
//     runtimes/{rid}/native and side-by-side natives ship). This is the directory we trust.
//   - SafeDirectories  : LOAD_LIBRARY_SEARCH_DEFAULT_DIRS on Windows (System32 + app dir + AddDllDirectory
//     entries), which deliberately excludes the current working directory.
// Together these exclude the CWD and the unsafe PATH lookup that the OS loader would otherwise consult.
// On macOS/Linux the non-AssemblyDirectory flags are no-ops and do not affect the existing native-load
// path (resolver + runtimes/{rid}/native still apply).
//
// CA5393 flags AssemblyDirectory conservatively as a "may load from a less-trusted directory" value, but
// here the assembly directory is the deployed app directory we control and the combination is strictly
// narrower than the default probing order (which includes CWD/PATH, the actual hijack surface). This is
// the documented safe-to-suppress case, and it mirrors the per-import suppression on the ryn-pty
// DllImport in Ryn.Plugins.Shell/PtyCommands.cs.
//
// Because the attribute is assembly-wide, the analyzer re-reports CA5393 at every individual P/Invoke
// site in the generated Generated/Saucer.cs (those lines inherit this search-path config). The generated
// file cannot carry pragmas — that is the very reason this is assembly-level — so the suppression must
// also be assembly-wide. A scope-less [SuppressMessage] is the documented way to silence an analyzer
// diagnostic in generated code from a separate file. Both attributes are compile-time only and are not
// emitted into IL, so this is fully NativeAOT-safe (no reflection, no runtime codegen).
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5393:Do not use unsafe DllImportSearchPath value",
    Justification = "INT-05 hardening: AssemblyDirectory is intentional and narrower than the default CWD/PATH probe; the assembly directory is the trusted deployed app directory.")]
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory
    | System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
