// Ryn is a bundling meta-package: it ships Ryn.Core, Ryn.Ipc, Ryn.Interop and the IPC source
// generator as a single NuGet package. It intentionally contains no code of its own; this file
// only gives the compiler an input so it produces a (non-packed) assembly without warnings.
namespace Ryn;

internal static class PackageMarker;
