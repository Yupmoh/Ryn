using System.Reflection;
using System.Runtime.InteropServices;

namespace Ryn.Interop;

public static class NativeLibraryResolver
{
    private static readonly HashSet<string> _registeredAssemblies = new(StringComparer.Ordinal);
    private static readonly HashSet<string> _knownLibraries = new(StringComparer.Ordinal)
    {
        "saucer-bindings",
        "ryn-pty",
    };

    public static void Register()
    {
        RegisterForAssembly(typeof(NativeLibraryResolver).Assembly);
    }

    public static void RegisterForAssembly(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var name = assembly.FullName ?? assembly.GetName().Name ?? "";
        lock (_registeredAssemblies)
        {
            if (!_registeredAssemblies.Add(name))
                return;
        }
        NativeLibrary.SetDllImportResolver(assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        // Only resolve the handful of native libraries Ryn ships. Everything else falls through to the
        // default runtime resolver. This keeps the allow-list authoritative for what we will load.
        if (!_knownLibraries.Contains(libraryName))
            return nint.Zero;

        var rid = RuntimeInformation.RuntimeIdentifier;
        var extension = GetPlatformExtension();
        var prefix = GetPlatformPrefix();
        var baseDir = AppContext.BaseDirectory;
        var fileName = $"{prefix}{libraryName}{extension}";

        // Probe only trusted, BaseDirectory-anchored absolute paths. We deliberately do NOT fall back to a
        // bare, separator-free name: passing one to NativeLibrary.TryLoad delegates to the OS loader, which
        // searches the working directory / PATH / @rpath and would happily load an attacker-planted DLL of
        // the same name (DLL planting / search-order hijack). Anchoring to AppContext.BaseDirectory ties the
        // load to the deployed application directory instead. (INT-05)
        string[] searchPaths =
        [
            Path.Combine(baseDir, "runtimes", rid, "native", fileName),
            Path.Combine(baseDir, fileName),
        ];

        foreach (var path in searchPaths)
        {
            // Require the candidate to be a rooted path that actually exists before handing it to the OS
            // loader. This avoids any relative-name resolution and gives a clean miss when the lib is absent.
            if (!Path.IsPathRooted(path) || !File.Exists(path))
                continue;

            if (NativeLibrary.TryLoad(path, out var handle))
                return handle;
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
