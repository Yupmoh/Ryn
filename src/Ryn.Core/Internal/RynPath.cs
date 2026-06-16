namespace Ryn.Core.Internal;

/// <summary>
/// The one canonical path-containment helper for the whole framework (PAP-23). Every security-relevant
/// "is this path inside that directory?" check — the content-directory guard in <see cref="RynWebView"/>'s
/// <c>HandleAppSchemeRequest</c> and <see cref="LocalWebServer"/>'s <c>ResolveWithinContent</c>, plus the
/// FileSystem plugin's scope validation — funnels through <see cref="IsContainedIn"/> so the rule has a
/// single definition and cannot drift between call sites.
/// </summary>
internal static class RynPath
{
    /// <summary>
    /// The host filesystem's path-comparison policy: ordinal on Linux (case-sensitive volumes — a
    /// case-insensitive compare there would be over-permissive, treating <c>/Allowed</c> and
    /// <c>/allowed</c> as the same scope) and ordinal-ignore-case on macOS/Windows (default volumes are
    /// case-insensitive, where an ordinal compare would wrongly deny legitimate in-scope access).
    /// </summary>
    internal static readonly StringComparison HostComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>
    /// Returns <c>true</c> when <paramref name="candidateFullPath"/> is the same as, or lives strictly
    /// under, <paramref name="rootFullPath"/>. Both arguments must already be canonical, absolute paths
    /// (<see cref="System.IO.Path.GetFullPath(string)"/>'d, and symlink-resolved where the caller cares
    /// about symlinks) — this method performs no resolution of its own.
    /// </summary>
    /// <remarks>
    /// The containment test is the fuller "exact-root equality OR child-with-trailing-separator" form, not
    /// a bare <c>StartsWith(root)</c>. Both paths have trailing directory separators trimmed first, then the
    /// candidate must either equal the root exactly or start with <c>root + DirectorySeparatorChar</c>. The
    /// trailing-separator requirement is what stops a sibling-prefix false positive: with a bare
    /// <c>StartsWith(root)</c>, <c>/content-evil</c> would be (wrongly) treated as contained in
    /// <c>/content</c>; here it is not, because <c>/content-evil</c> neither equals <c>/content</c> nor
    /// starts with <c>/content/</c>.
    /// </remarks>
    /// <param name="candidateFullPath">The canonical absolute path being tested.</param>
    /// <param name="rootFullPath">The canonical absolute directory it must be inside (or equal to).</param>
    /// <param name="comparison">
    /// The string comparison to use. Pass <see cref="HostComparison"/> for the host default; callers that
    /// have already chosen a case policy pass it explicitly so the chosen policy is honored verbatim.
    /// </param>
    internal static bool IsContainedIn(string candidateFullPath, string rootFullPath, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(candidateFullPath);
        ArgumentNullException.ThrowIfNull(rootFullPath);

        var root = rootFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var candidate = candidateFullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return candidate.Equals(root, comparison)
            || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
