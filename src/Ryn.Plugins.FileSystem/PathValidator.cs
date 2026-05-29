using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

internal static class PathValidator
{
    private static FileSystemOptions? _options;

    // Match the host filesystem's case semantics. Linux is case-sensitive; using a
    // case-insensitive compare there would be *over-permissive* (treat /Allowed and /allowed as the
    // same scope when they are genuinely different directories). Windows/macOS default volumes are
    // case-insensitive, where an ordinal compare would wrongly deny legitimate access.
    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private static readonly bool IgnoreCase = !OperatingSystem.IsLinux();

    internal static void Configure(FileSystemOptions options) => _options = options;

    /// <summary>
    /// Resolves a path for reading and enforces the configured maximum read size, so a hostile
    /// in-scope target cannot OOM the process. Returns the canonical path to read.
    /// </summary>
    internal static string ResolveForRead(string path)
    {
        var resolved = Resolve(path);
        var max = _options?.MaxReadBytes ?? 0;
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
    /// scope but whose real target escapes is rejected.
    /// </summary>
    internal static string Resolve(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var options = _options;
        if (options is not null && options.AccessDenied)
            throw new UnauthorizedAccessException("File system access is denied by capability policy");

        // Canonical real path (resolves '..' AND symlinks). This is the value we both authorize and
        // operate on, closing the validate-vs-open gap for the planted-symlink case.
        var canonical = Canonicalize(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(AppContext.BaseDirectory, path));

        if (options is null || options.AllowedPaths.Count == 0)
        {
            // Default: restrict to the app directory (also canonicalized so macOS /var->/private etc. match).
            if (!IsWithin(canonical, Canonicalize(AppContext.BaseDirectory)))
                throw new UnauthorizedAccessException($"Access denied: path '{path}' is outside the application directory");
            return canonical;
        }

        foreach (var allowed in options.AllowedPaths)
        {
            if (GlobMatcher.IsGlob(allowed))
            {
                if (GlobMatcher.IsMatch(allowed, canonical.Replace('\\', '/'), IgnoreCase))
                    return canonical;
            }
            else if (IsWithin(canonical, Canonicalize(allowed)))
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

    private static bool IsWithin(string canonicalPath, string canonicalDirectory)
    {
        var dir = canonicalDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var p = canonicalPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return p.Equals(dir, PathComparison)
            || p.StartsWith(dir + Path.DirectorySeparatorChar, PathComparison);
    }
}
