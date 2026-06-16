// INT-05 residual (defense-in-depth). Applies [DefaultDllImportSearchPaths] at assembly scope so every
// P/Invoke in this assembly (notably the native ryn-pty shim, plus the libc imports) hardens the
// runtime's default native-library probe: a bare-name load cannot be satisfied from an attacker-
// controlled current working directory or PATH.
//   - AssemblyDirectory: load the native lib from the deployed application/assembly directory (where the
//     ryn-pty shim ships next to the app). This is the directory we trust.
//   - SafeDirectories  : LOAD_LIBRARY_SEARCH_DEFAULT_DIRS on Windows (System32 + app dir + AddDllDirectory
//     entries), which deliberately excludes the current working directory.
// The ryn-pty DllImport in PtyCommands.cs already carries an equivalent per-import attribute; this
// assembly-level attribute is the catch-all for the assembly's remaining P/Invokes. On macOS/Linux the
// non-AssemblyDirectory flags are no-ops and do not change the existing native-load path.
//
// CA5393 flags AssemblyDirectory conservatively; here the assembly directory is the deployed app
// directory we control and the combination is strictly narrower than the default probe (which includes
// CWD/PATH). Documented safe-to-suppress case; mirrors the per-import suppression in PtyCommands.cs.
//
// The attribute is assembly-wide, so the analyzer re-reports CA5393 at every P/Invoke site that inherits
// it (the libc imports here, plus ryn-pty which also has its own per-import attribute). A scope-less
// [SuppressMessage] silences it assembly-wide. Both attributes are compile-time only (not emitted into
// IL), so this is NativeAOT-safe (no reflection, no runtime codegen).
[assembly: System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Security",
    "CA5393:Do not use unsafe DllImportSearchPath value",
    Justification = "INT-05 hardening: AssemblyDirectory is intentional and narrower than the default CWD/PATH probe; the assembly directory is the trusted deployed app directory.")]
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(
    System.Runtime.InteropServices.DllImportSearchPath.AssemblyDirectory
    | System.Runtime.InteropServices.DllImportSearchPath.SafeDirectories)]
