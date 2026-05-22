namespace Ryn.Plugins.FileSystem;

internal static class PathValidator
{
    private static FileSystemOptions? _options;

    internal static void Configure(FileSystemOptions options) => _options = options;

    internal static string Resolve(string path)
    {
        // Resolve relative paths against the app's base directory, not CWD
        var fullPath = Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, path));

        // Check for path traversal
        if (path.Contains("..", StringComparison.Ordinal))
        {
            // Re-resolve without the .. to see if it escapes
            var segments = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => s == ".."))
            {
                // Verify the resolved path is still within an allowed directory
                // (the check below handles this)
            }
        }

        var options = _options;
        if (options is null || options.AllowedPaths.Count == 0)
        {
            var appDir = AppContext.BaseDirectory;
            if (!fullPath.StartsWith(appDir, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Access denied: path '{path}' is outside the application directory");
            return fullPath;
        }

        foreach (var allowed in options.AllowedPaths)
        {
            var allowedFull = Path.GetFullPath(allowed);
            if (fullPath.StartsWith(allowedFull, StringComparison.OrdinalIgnoreCase))
                return fullPath;
        }

        throw new UnauthorizedAccessException($"Access denied: path '{path}' is not within any allowed directory");
    }
}
