using Ryn.Core.Internal;
using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

/// <summary>
/// Validates and canonicalizes filesystem paths against a single application's <see cref="FileSystemOptions"/>.
/// Resolved from DI as a per-application singleton rather than process-global static state, so two windows
/// or hosts in the same process can run with different filesystem policies without clobbering each other.
/// The path-canonicalization helpers are stateless and remain <c>static</c> (also used by
/// <see cref="CapabilityScopeMerger"/>); the containment rule itself lives in one place for the whole
/// framework, <see cref="RynPath.IsContainedIn"/> (PAP-23), which both this validator and the scope merger
/// call.
/// </summary>
public sealed class PathValidator
{
    private readonly FileSystemOptions _options;

    /// <summary>
    /// The host filesystem's case semantics, the single comparison used by every containment and
    /// glob check in this assembly. Linux is case-sensitive; using a case-insensitive compare there
    /// would be *over-permissive* (treat <c>/Allowed</c> and <c>/allowed</c> as the same scope when
    /// they are genuinely different directories). Windows/macOS default volumes are case-insensitive,
    /// where an ordinal compare would wrongly deny legitimate access. This is the same host case policy
    /// the framework-wide containment helper uses (<see cref="RynPath.HostComparison"/>); it is aliased
    /// here so this assembly's call sites read naturally without re-deriving the rule.
    /// </summary>
    internal static readonly StringComparison PathComparison = RynPath.HostComparison;

    private static readonly bool IgnoreCase = !OperatingSystem.IsLinux();

    public PathValidator(FileSystemOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>
    /// Resolves a path for reading and enforces the configured maximum read size, so a hostile
    /// in-scope target cannot OOM the process. Returns the canonical path to read.
    /// </summary>
    internal string ResolveForRead(string path)
    {
        var resolved = Resolve(path);
        var max = _options.MaxReadBytes;
        if (max > 0)
        {
            var info = new FileInfo(resolved);
            if (info.Exists && info.Length > max)
                throw new UnauthorizedAccessException(
                    $"Access denied: file '{path}' is {info.Length} bytes, exceeding the {max}-byte read limit");
        }
        return resolved;
    }

    /// <summary>
    /// Resolves <paramref name="path"/> to a canonical, symlink-free absolute path and verifies it is
    /// contained within an allowed scope. Symlinks are followed at every path component — including
    /// parent directories and not-yet-existing write targets — so a link whose lexical path is in
    /// scope but whose real target escapes is rejected. Legitimate symlinks that resolve to an in-scope
    /// real target are *not* rejected (per the medium-risk register, blanket-rejecting in-scope links
    /// would break valid setups).
    /// </summary>
    /// <remarks>
    /// The value returned is the canonical (symlink-resolved) path, which is what the caller should both
    /// authorize against and operate on. Note that returning a *string* still leaves a narrow
    /// time-of-check/time-of-use race: a caller that re-opens the returned path by name follows symlinks
    /// afresh, so an attacker who can swap a path component for an escaping symlink in the window between
    /// this call and the caller's open could still escape. Closing that window fully requires the caller
    /// to open atomically and re-verify the opened handle's real path against the scope (see PLG-03);
    /// that is a caller-side change and is tracked separately.
    /// </remarks>
    internal string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_options.AccessDenied)
            throw new UnauthorizedAccessException("File system access is denied by capability policy");

        // Canonical real path (resolves '..' AND symlinks). This is the value we both authorize and
        // hand back, so authorization is performed on the post-symlink-resolution path, not the lexical
        // one. (A re-opened *string* cannot pin an inode, so a racing caller-side re-open is still a
        // residual TOCTOU — see the <remarks> above.)
        var canonical = Canonicalize(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));

        if (_options.AllowedPaths.Count == 0)
        {
            // Default: restrict to the app directory (also canonicalized so macOS /var->/private etc. match).
            if (!RynPath.IsContainedIn(canonical, Canonicalize(AppContext.BaseDirectory), PathComparison))
                throw new UnauthorizedAccessException($"Access denied: path '{path}' is outside the application directory");
            return canonical;
        }

        foreach (var allowed in _options.AllowedPaths)
        {
            if (GlobMatcher.IsGlob(allowed))
            {
                if (GlobMatcher.IsMatch(allowed, canonical.Replace('\\', '/'), IgnoreCase))
                    return canonical;
            }
            else if (RynPath.IsContainedIn(canonical, Canonicalize(allowed), PathComparison))
            {
                return canonical;
            }
        }

        throw new UnauthorizedAccessException($"Access denied: path '{path}' is not within any allowed directory");
    }

    /// <summary>
    /// Returns the fully canonical absolute form of <paramref name="path"/>, following symlinks at
    /// every component. For a target that does not yet exist, the longest existing ancestor is
    /// canonicalized and the remaining (lexical) segments are appended.
    /// </summary>
    internal static string Canonicalize(string path)
    {
        var full = Path.GetFullPath(path);

        // Split into the longest existing prefix + non-existing remainder.
        var existing = full;
        var remainder = new Stack<string>();
        while (!File.Exists(existing) && !Directory.Exists(existing))
        {
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, existing, StringComparison.Ordinal))
            {
                existing = string.Empty; // nothing of the path exists
                break;
            }
            remainder.Push(Path.GetFileName(existing));
            existing = parent;
        }

        var real = string.IsNullOrEmpty(existing) ? full : ResolveAllLinks(existing);
        while (remainder.Count > 0)
            real = Path.Combine(real, remainder.Pop());

        return Path.GetFullPath(real);
    }

    /// <summary>Walks each component from the root, following symlinks (including chains and relative targets).</summary>
    private static string ResolveAllLinks(string existingAbsolutePath)
    {
        var root = Path.GetPathRoot(existingAbsolutePath);
        if (string.IsNullOrEmpty(root))
            return existingAbsolutePath;

        var rest = existingAbsolutePath[root.Length..]
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);

        var current = root;
        foreach (var component in rest)
        {
            current = Path.Combine(current, component);

            // Follow a symlink chain at this component (guarded against cycles).
            for (var hop = 0; hop < 40; hop++)
            {
                string? target;
                try
                {
                    FileSystemInfo info = Directory.Exists(current)
                        ? new DirectoryInfo(current)
                        : new FileInfo(current);
                    target = info.LinkTarget;
                }
                catch (IOException)
                {
                    break;
                }

                if (target is null)
                    break;

                var resolved = Path.IsPathRooted(target)
                    ? target
                    : Path.Combine(Path.GetDirectoryName(current) ?? root, target);
                current = Path.GetFullPath(resolved);
            }
        }

        return current;
    }
}
