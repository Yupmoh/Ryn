namespace Ryn.Core;

/// <summary>Provides stable, absolute paths for common application directories.</summary>
public interface IRynPaths
{
    /// <summary>Per-user local application data directory.</summary>
    public string LocalAppData { get; }

    /// <summary>Per-user roaming application data directory.</summary>
    public string RoamingAppData { get; }

    /// <summary>Per-user documents directory.</summary>
    public string Documents { get; }

    /// <summary>Per-user cache directory.</summary>
    public string Cache { get; }

    /// <summary>Directory for temporary files.</summary>
    public string Temp { get; }

    /// <summary>Directory containing application resources.</summary>
    public string ResourceDirectory { get; }

    /// <summary>Directory where the application is installed.</summary>
    public string InstallDirectory { get; }
}

/// <summary>Default NativeAOT-safe implementation of <see cref="IRynPaths"/>.</summary>
public sealed class RynPaths : IRynPaths
{
    /// <summary>Initializes paths using the host platform's standard directory conventions.</summary>
    public RynPaths()
    {
        var home = GetHomeDirectory();
        var local = OperatingSystem.IsLinux()
            ? MakeAbsolute(GetXdgDirectory("XDG_DATA_HOME", Path.Combine(home, ".local", "share")))
            : GetPlatformDirectory(
                Environment.SpecialFolder.LocalApplicationData,
                Path.Combine(home, "AppData", "Local"),
                Path.Combine(home, "Library", "Application Support"));
        var roaming = OperatingSystem.IsLinux()
            ? MakeAbsolute(GetXdgDirectory("XDG_DATA_HOME", Path.Combine(home, ".local", "share")))
            : GetPlatformDirectory(
                Environment.SpecialFolder.ApplicationData,
                Path.Combine(home, "AppData", "Roaming"),
                Path.Combine(home, "Library", "Application Support"));
        LocalAppData = local;
        RoamingAppData = roaming;
        Documents = GetPlatformDirectory(
            Environment.SpecialFolder.MyDocuments,
            Path.Combine(home, "Documents"),
            Path.Combine(home, "Documents"));
        Cache = OperatingSystem.IsMacOS()
            ? MakeAbsolute(Path.Combine(home, "Library", "Caches"))
            : OperatingSystem.IsWindows()
                ? MakeAbsolute(Path.Combine(local, "Cache"))
                : MakeAbsolute(GetXdgDirectory("XDG_CACHE_HOME", Path.Combine(home, ".cache")));
        Temp = MakeAbsolute(Path.GetTempPath());
        InstallDirectory = MakeAbsolute(AppContext.BaseDirectory);
        ResourceDirectory = InstallDirectory;
    }

    /// <inheritdoc />
    public string LocalAppData { get; }
    /// <inheritdoc />
    public string RoamingAppData { get; }
    /// <inheritdoc />
    public string Documents { get; }
    /// <inheritdoc />
    public string Cache { get; }
    /// <inheritdoc />
    public string Temp { get; }
    /// <inheritdoc />
    public string ResourceDirectory { get; }
    /// <inheritdoc />
    public string InstallDirectory { get; }

    internal static string GetPlatformDirectory(
        Environment.SpecialFolder specialFolder,
        string fallback,
        string macFallback,
        bool isWindows,
        bool isMacOS,
        Func<Environment.SpecialFolder, string> getFolderPath)
    {
        if (isWindows)
        {
            var path = getFolderPath(specialFolder);
            if (!string.IsNullOrWhiteSpace(path))
                return MakeAbsolute(path);
        }

        return MakeAbsolute(isMacOS ? macFallback : fallback);
    }

    private static string GetPlatformDirectory(Environment.SpecialFolder specialFolder, string fallback, string macFallback)
        => GetPlatformDirectory(
            specialFolder,
            fallback,
            macFallback,
            OperatingSystem.IsWindows(),
            OperatingSystem.IsMacOS(),
            Environment.GetFolderPath);


    private static string GetXdgDirectory(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return !string.IsNullOrWhiteSpace(value) && Path.IsPathRooted(value) ? value : fallback;
    }

    private static string GetHomeDirectory()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrWhiteSpace(home) ? MakeAbsolute(home) : MakeAbsolute(AppContext.BaseDirectory);
    }

    private static string MakeAbsolute(string path) => Path.GetFullPath(path);
}
