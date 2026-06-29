using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryn.Cli;

/// <summary>
/// Helpers for making Native AOT publishes succeed on Windows. AOT links with the MSVC toolchain, which the
/// .NET SDK locates by shelling out to <c>vswhere.exe</c> in the Visual Studio Installer directory. On
/// Build Tools installs that directory is frequently not on <c>PATH</c>, so the link step fails and
/// <c>dotnet publish</c> degrades to a framework-dependent output (hundreds of <c>System.*.dll</c>) instead
/// of a single native binary — usually without an obvious error. We prepend the standard installer directory
/// to the spawned publish process's <c>PATH</c> when it exists, and warn clearly when no <c>vswhere</c> can be
/// found at all so the failure is diagnosable rather than silent.
/// </summary>
internal static class NativeAotToolchain
{
    /// <summary>
    /// When publishing with AOT on Windows, ensures <c>vswhere.exe</c> is reachable from the given publish
    /// process by prepending the VS Installer directory to its <c>PATH</c>. No-op on non-Windows, or when AOT
    /// was not requested, or when <c>vswhere</c> is already on <c>PATH</c>.
    /// </summary>
    internal static void Configure(ProcessStartInfo psi, bool useAot)
    {
        ArgumentNullException.ThrowIfNull(psi);

        if (!useAot || !RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (IsOnPath("vswhere.exe"))
            return;

        var installerDir = VsInstallerDirectory();
        if (installerDir is not null)
        {
            var current = psi.Environment.TryGetValue("PATH", out var existing) && !string.IsNullOrEmpty(existing)
                ? existing
                : Environment.GetEnvironmentVariable("PATH") ?? "";
            psi.Environment["PATH"] = installerDir + Path.PathSeparator + current;
            Console.WriteLine($"  NativeAOT: added the Visual Studio Installer to PATH ({installerDir})");
        }
        else
        {
            Console.Error.WriteLine("  Warning: NativeAOT was requested but 'vswhere.exe' is not on PATH or in the");
            Console.Error.WriteLine("  Visual Studio Installer directory. AOT needs the \"Desktop development with C++\"");
            Console.Error.WriteLine("  workload (Visual Studio or Build Tools); without it the link step fails and the");
            Console.Error.WriteLine("  publish falls back to a framework-dependent build. Install that workload, or run");
            Console.Error.WriteLine("  from a Developer Command Prompt, then bundle again.");
        }
    }

    /// <summary>
    /// The directory that holds <c>vswhere.exe</c> for every VS edition including Build Tools, or null if it
    /// does not exist on this machine. vswhere ships at a fixed path under the x86 Program Files.
    /// </summary>
    private static string? VsInstallerDirectory()
    {
        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)")
            ?? Environment.GetEnvironmentVariable("ProgramFiles");
        if (string.IsNullOrEmpty(programFilesX86))
            return null;

        var dir = Path.Combine(programFilesX86, "Microsoft Visual Studio", "Installer");
        return File.Exists(Path.Combine(dir, "vswhere.exe")) ? dir : null;
    }

    /// <summary>True if <paramref name="exe"/> exists in any directory on the current <c>PATH</c>.</summary>
    private static bool IsOnPath(string exe)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return false;

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(dir, exe)))
                    return true;
            }
            catch (ArgumentException)
            {
                // A PATH entry with invalid path characters — skip it.
            }
        }

        return false;
    }
}
