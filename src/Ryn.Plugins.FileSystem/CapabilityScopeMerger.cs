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
        if (scope is null || !scope.HasPathRestrictions)
            return;

        if (options.AllowedPaths.Count == 0)
        {
            // No programmatic paths — use capability paths directly
            options.AllowedPaths.AddRange(scope.AllowedPaths);
            return;
        }

        // Clamp: keep only programmatic paths that are within a capability scope path
        var clamped = new List<string>();
        foreach (var programmatic in options.AllowedPaths)
        {
            var resolved = Path.GetFullPath(programmatic);
            foreach (var allowed in scope.AllowedPaths)
            {
                if (IsWithin(resolved, allowed))
                {
                    clamped.Add(programmatic);
                    break;
                }
            }
        }

        options.AllowedPaths.Clear();
        options.AllowedPaths.AddRange(clamped.Count > 0 ? clamped : scope.AllowedPaths);
    }

    private static bool IsWithin(string fullPath, string directory)
    {
        var normalizedDir = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var normalizedPath = fullPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return normalizedPath.Equals(normalizedDir, StringComparison.OrdinalIgnoreCase)
            || normalizedPath.StartsWith(normalizedDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
