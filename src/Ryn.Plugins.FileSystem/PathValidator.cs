using Ryn.Core;
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
    private readonly IFileAccessGrants _grants;

    /// <summary>The host filesystem's case semantics used by containment checks.</summary>
    internal static readonly StringComparison PathComparison = RynPath.HostComparison;

    private static readonly bool IgnoreCase = !OperatingSystem.IsLinux();

    public PathValidator(FileSystemOptions options, IFileAccessGrants? grants = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        _grants = grants ?? new FileAccessGrants();
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
    /// contained within an allowed scope. A leading <c>~</c> or <c>$HOME</c> is first expanded to the
    /// user's home directory (see <see cref="ExpandHome"/>). Symlinks are followed at every path component — including
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
    internal string Resolve(string path) => Resolve(path, FileAccessOperation.Read);

    internal string Resolve(string path, FileAccessOperation operation)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (_options.AccessDenied) throw new UnauthorizedAccessException("File system access is denied by capability policy");
        var granted = path.StartsWith("ryn-grant-", StringComparison.Ordinal) ? _grants.Resolve(path, operation) : null;
        var expanded = granted is null ? ExpandHome(path) : granted;
        var canonical = Canonicalize(Path.IsPathRooted(expanded) ? expanded : Path.Combine(AppContext.BaseDirectory, expanded));
        if (granted is not null) return canonical;
        if (_options.AllowedPaths.Count == 0)
        {
            if (!RynPath.IsContainedIn(canonical, Canonicalize(AppContext.BaseDirectory), PathComparison)) throw new UnauthorizedAccessException($"Access denied: path '{path}' is outside the application directory");
            return canonical;
        }
        foreach (var allowed in _options.AllowedPaths)
        {
            if (GlobMatcher.IsGlob(allowed) ? GlobMatcher.IsMatch(allowed, canonical.Replace('\\', '/'), IgnoreCase) : RynPath.IsContainedIn(canonical, Canonicalize(allowed), PathComparison)) return canonical;
        }
        throw new UnauthorizedAccessException($"Access denied: path '{path}' is not within any allowed directory");
    }
    internal static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        static bool IsSep(char c) => c == '/' || c == '\\';

        var home = HomeDirectory();
        if (string.IsNullOrEmpty(home))
            return path;

        // Leading ~ , but not ~username (which has no portable meaning here).
        if (path[0] == '~' && (path.Length == 1 || IsSep(path[1])))
            return home + path[1..];

        // Leading ${HOME} or $HOME, delimited by end-of-string or a separator so $HOMER is left alone.
        foreach (var token in HomeTokens)
        {
            if (path.StartsWith(token, StringComparison.Ordinal)
                && (path.Length == token.Length || IsSep(path[token.Length])))
                return home + path[token.Length..];
        }

        return path;
    }

    private static readonly string[] HomeTokens = ["${HOME}", "$HOME"];

    /// <summary>
    /// The user's home directory: the cross-platform user-profile folder (<c>$HOME</c> on Unix,
    /// <c>%USERPROFILE%</c> on Windows), falling back to the <c>HOME</c> environment variable. Returns
    /// an empty string when neither is available, in which case <see cref="ExpandHome"/> is a no-op.
    /// </summary>
    private static string HomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
            home = Environment.GetEnvironmentVariable("HOME") ?? string.Empty;
        return home;
    }

    /// <summary>
    /// Returns the fully canonical absolute form of <paramref name="path"/>, following symlinks at
    /// every component. For a target that does not yet exist, the longest existing ancestor is
    /// canonicalized and the remaining (lexical) segments are appended.
    /// </summary>
    internal static string Canonicalize(string path) => RynPath.Canonicalize(path);
}
