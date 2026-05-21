using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryn.Interop;

public static class NativeLibraryResolver
{
    private const string LibraryName = "saucer-bindings";
    private static bool _registered;

    public static void Register()
    {
        if (_registered)
        {
            return;
        }

        _registered = true;
        NativeLibrary.SetDllImportResolver(typeof(NativeLibraryResolver).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName != LibraryName)
        {
            return nint.Zero;
        }

        var rid = RuntimeInformation.RuntimeIdentifier;
        var extension = GetPlatformExtension();
        var prefix = GetPlatformPrefix();
        var baseDir = AppContext.BaseDirectory;

        string[] searchPaths =
        [
            Path.Combine(baseDir, "runtimes", rid, "native", $"{prefix}{LibraryName}{extension}"),
            Path.Combine(baseDir, $"{prefix}{LibraryName}{extension}"),
            $"{prefix}{LibraryName}{extension}",
        ];

        foreach (var path in searchPaths)
        {
            if (NativeLibrary.TryLoad(path, out var handle))
            {
                return handle;
            }
        }

        return nint.Zero;
    }

    private static string GetPlatformExtension()
    {
        if (OperatingSystem.IsWindows())
        {
            return ".dll";
        }

        if (OperatingSystem.IsMacOS())
        {
            return ".dylib";
        }

        return ".so";
    }

    private static string GetPlatformPrefix()
    {
        if (OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        return "lib";
    }
}
