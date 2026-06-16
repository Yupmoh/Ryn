using Ryn.Core.Internal;
using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

internal static class CapabilityScopeMerger
{
    /// <summary>
    /// Merges capability scopes from ryn.json into FileSystemOptions.
    /// If ryn.json declares path scopes, they become the maximum allowed set.
    /// Programmatic paths that fall outside the declared scopes are removed.
    /// If ryn.json doesn't declare scopes, programmatic options apply as-is.
    /// </summary>
    internal static void MergeFileSystemScope(RynCapabilities capabilities, FileSystemOptions options)
    {
        var scope = capabilities.GetScope("fs");
        if (scope is null || !scope.HasPathPolicy)
            return;

        // scope: [] means explicit deny-all
        if (scope.AllowedPaths!.Count == 0)
        {
            options.AllowedPaths.Clear();
            options.AccessDenied = true;
            return;
        }

        if (options.AllowedPaths.Count == 0)
        {
            options.AllowedPaths.AddRange(scope.AllowedPaths);
            return;
        }

        var ignoreCase = !OperatingSystem.IsLinux();

        var clamped = new List<string>();
        foreach (var programmatic in options.AllowedPaths)
        {
            var resolved = PathValidator.Canonicalize(programmatic);
            foreach (var allowed in scope.AllowedPaths)
            {
                // Containment uses the one canonical helper (PAP-23); the host case policy it carries
                // (RynPath.HostComparison) is the same ordinal/ordinal-ignore-case split as the glob path.
                var within = GlobMatcher.IsGlob(allowed)
                    ? GlobMatcher.IsMatch(allowed, resolved.Replace('\\', '/'), ignoreCase)
                    : RynPath.IsContainedIn(resolved, PathValidator.Canonicalize(allowed), RynPath.HostComparison);
                if (within)
                {
                    clamped.Add(programmatic);
                    break;
                }
            }
        }

        options.AllowedPaths.Clear();
        if (clamped.Count == 0)
            options.AccessDenied = true;
        else
            options.AllowedPaths.AddRange(clamped);
    }
}
