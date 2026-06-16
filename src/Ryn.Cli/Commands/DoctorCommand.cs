using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ryn.Cli.Commands;

internal static class DoctorCommand
{
    internal static int Execute(ReadOnlySpan<string> args)
    {
        Console.WriteLine("Ryn Doctor");
        Console.WriteLine("==========");
        Console.WriteLine();

        var allGood = true;

        allGood &= Check(".NET SDK", CheckDotNetSdk);
        allGood &= Check("Native libraries", CheckNativeLibs);
        allGood &= Check("WebView runtime", CheckWebView);
        allGood &= Check("GitHub CLI (gh)", CheckGitHubCli);
        allGood &= Check("CMake", CheckCmake);
        allGood &= Check("Ninja", CheckNinja);

        if (args.Contains("--full"))
        {
            allGood &= Check("Solution build", CheckSolutionBuild);
        }

        Console.WriteLine();
        if (allGood)
            Console.WriteLine("All checks passed!");
        else
            Console.WriteLine("Some checks failed. See above for details.");

        return allGood ? 0 : 1;
    }

    private static bool Check(string label, Func<CheckResult> check)
    {
        var result = check();

        var (color, tag) = result.Status switch
        {
            CheckStatus.Ok => (ConsoleColor.Green, " OK "),
            CheckStatus.Warn => (ConsoleColor.Yellow, "WARN"),
            CheckStatus.Fail => (ConsoleColor.Red, "FAIL"),
            _ => (ConsoleColor.White, "????"),
        };

        var saved = Console.ForegroundColor;
        Console.Write("  [");
        Console.ForegroundColor = color;
        Console.Write(tag);
        Console.ForegroundColor = saved;
        Console.Write("] ");
        Console.Write(label);

        if (!string.IsNullOrEmpty(result.Detail))
        {
            Console.Write(" — ");
            Console.Write(result.Detail);
        }

        Console.WriteLine();

        return result.Status != CheckStatus.Fail;
    }

    private static CheckResult CheckDotNetSdk()
    {
        var dotnetPath = DotnetResolver.Resolve();
        if (dotnetPath is null)
            return CheckResult.Fail("dotnet not found (checked DOTNET_HOST_PATH, PATH, DOTNET_ROOT, and standard install locations)");

        var version = RunCommand(dotnetPath, "--version");
        if (version is null)
            return CheckResult.Fail($"dotnet found at {dotnetPath} but '--version' failed");

        version = version.Trim();
        if (version.StartsWith("10.", StringComparison.Ordinal))
            return CheckResult.Ok(version);

        return CheckResult.Fail($"found {version}, need 10.x");
    }

    // Native libraries Ryn loads at runtime. Mirrors NativeLibraryResolver._knownLibraries so the
    // doctor probes exactly the files the resolver will look for.
    private static readonly string[] KnownNativeLibs = ["saucer-bindings", "ryn-pty"];

    private static CheckResult CheckNativeLibs()
    {
        var rid = RuntimeInformation.RuntimeIdentifier;

        // Probe the locations NativeLibraryResolver actually resolves from, so an installed-tool
        // user (whose natives live next to the app, not in src/Ryn.Interop) is reported correctly.
        // The resolver searches AppContext.BaseDirectory/runtimes/{rid}/native and the base dir.
        var baseDir = AppContext.BaseDirectory;
        string[] runtimeNativeDirs =
        [
            Path.Combine(baseDir, "runtimes", rid, "native"),
            baseDir,
        ];

        foreach (var dir in runtimeNativeDirs)
        {
            if (FindNativeLib(dir, out var found))
                return CheckResult.Ok($"{found} for {rid}");
        }

        // In-repo fallback: a contributor running from a source checkout has the natives staged
        // under src/Ryn.Interop/runtimes rather than next to the CLI.
        var interopDir = FindInteropDir();
        if (interopDir is not null)
        {
            var nativeDir = Path.Combine(interopDir, "runtimes", rid, "native");
            if (FindNativeLib(nativeDir, out var found))
                return CheckResult.Ok($"{found} for {rid} (source tree)");

            return CheckResult.Warn($"no native libs for {rid} in {nativeDir} (run build/download-native.sh)");
        }

        // Installed-tool user with nothing resolvable: a Warn (not Fail) — the native libs ship
        // with the app being built/run, not with the global tool, so this is not fatal to doctor.
        return CheckResult.Warn($"none found next to the tool for {rid} (they ship with your app, not the global tool)");
    }

    // True if <paramref name="dir"/> contains at least one known native library (matching the
    // platform prefix/extension NativeLibraryResolver uses). Reports how many were found.
    private static bool FindNativeLib(string dir, out string detail)
    {
        detail = "";
        if (!Directory.Exists(dir))
            return false;

        var prefix = OperatingSystem.IsWindows() ? "" : "lib";
        string extension = OperatingSystem.IsWindows() ? ".dll" : OperatingSystem.IsMacOS() ? ".dylib" : ".so";

        var count = 0;
        foreach (var lib in KnownNativeLibs)
        {
            if (File.Exists(Path.Combine(dir, $"{prefix}{lib}{extension}")))
                count++;
        }

        if (count == 0)
            return false;

        detail = $"{count} of {KnownNativeLibs.Length} native lib(s)";
        return true;
    }

    private static CheckResult CheckWebView()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return CheckResult.Ok("WebKit (built into macOS)");

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return CheckWebView2();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return CheckWebKitGtk();

        return CheckResult.Warn("unknown platform");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static CheckResult CheckWebView2()
    {
        // The Evergreen WebView2 runtime registers its version under an EdgeUpdate Clients key.
        // A machine-wide install lands under HKLM (32-bit view via WOW6432Node on x64); a per-user
        // install lands under HKCU. Probe both hives so per-user installs aren't missed.
        var regPaths = new[]
        {
            @"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
            @"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        };

        var hives = new[]
        {
            (Root: Microsoft.Win32.Registry.CurrentUser, Scope: "per-user"),
            (Root: Microsoft.Win32.Registry.LocalMachine, Scope: "machine-wide"),
        };

        foreach (var (root, scope) in hives)
        {
            foreach (var path in regPaths)
            {
                try
                {
                    using var key = root.OpenSubKey(path);
                    var version = key?.GetValue("pv") as string;
                    if (!string.IsNullOrEmpty(version) && version != "0.0.0.0")
                        return CheckResult.Ok($"WebView2 {version} ({scope})");
                }
                catch (System.Security.SecurityException)
                {
                    // Registry access denied; continue checking other paths
                }
                catch (IOException)
                {
                    // Registry I/O error; continue checking other paths
                }
            }
        }

        return CheckResult.Fail("WebView2 runtime not found (checked per-user and machine-wide installs)");
    }

    private static CheckResult CheckWebKitGtk()
    {
        var result = RunCommand("pkg-config", "--exists webkitgtk-6.0");
        if (result is not null)
            return CheckResult.Ok("libwebkitgtk-6.0");

        // Fallback: check ldconfig
        var ldconfig = RunCommand("ldconfig", "-p");
        if (ldconfig is not null && ldconfig.Contains("libwebkitgtk-6.0", StringComparison.Ordinal))
            return CheckResult.Ok("libwebkitgtk-6.0 (via ldconfig)");

        return CheckResult.Fail("libwebkitgtk-6.0 not found (install libwebkitgtk-6.0-dev)");
    }

    private static CheckResult CheckGitHubCli()
    {
        var version = RunCommand("gh", "--version");
        if (version is null)
            return CheckResult.Warn("not found (needed for native lib downloads)");

        var firstLine = version.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return CheckResult.Ok(firstLine);
    }

    private static CheckResult CheckCmake()
    {
        var version = RunCommand("cmake", "--version");
        if (version is null)
            return CheckResult.Warn("not found (needed for building native libs from source)");

        var firstLine = version.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return CheckResult.Ok(firstLine);
    }

    private static CheckResult CheckNinja()
    {
        var version = RunCommand("ninja", "--version");
        if (version is null)
            return CheckResult.Warn("not found (needed for building native libs from source)");

        return CheckResult.Ok(version.Trim());
    }

    private static CheckResult CheckSolutionBuild()
    {
        var slnx = FindSolutionFile();
        if (slnx is null)
            return CheckResult.Fail("cannot locate Ryn.slnx");

        var dotnet = DotnetResolver.Resolve();
        if (dotnet is null)
            return CheckResult.Fail("dotnet not found");

        Console.WriteLine();
        Console.WriteLine("  Running dotnet build (this may take a moment)...");

        var exitCode = RunCommandPassthrough(dotnet, $"build \"{slnx}\" --nologo -v quiet");
        if (exitCode == 0)
            return CheckResult.Ok("solution builds successfully");

        return CheckResult.Fail("solution build failed");
    }

    private static string? RunCommand(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return null;

            // Drain both pipes before waiting so a tool that writes a lot to stderr (or fills the
            // stdout buffer) can't deadlock us, and bound the wait so a hung tool can't hang doctor.
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(milliseconds: 15_000))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
                return null;
            }

            var output = stdout.GetAwaiter().GetResult();
            _ = stderr.GetAwaiter().GetResult();

            return process.ExitCode == 0 ? output : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static int RunCommandPassthrough(string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
                return 1;

            process.WaitForExit();
            return process.ExitCode;
        }
        catch (Win32Exception)
        {
            return 1;
        }
    }

    private static string? FindInteropDir()
    {
        // Walk up from current directory looking for src/Ryn.Interop
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "src", "Ryn.Interop");
            if (Directory.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return null;
    }

    private static string? FindSolutionFile()
    {
        var dir = Directory.GetCurrentDirectory();
        for (var i = 0; i < 10; i++)
        {
            var candidate = Path.Combine(dir, "Ryn.slnx");
            if (File.Exists(candidate))
                return candidate;

            var parent = Directory.GetParent(dir);
            if (parent is null)
                break;
            dir = parent.FullName;
        }

        return null;
    }

    private enum CheckStatus { Ok, Warn, Fail }

    private readonly record struct CheckResult(CheckStatus Status, string? Detail)
    {
        internal static CheckResult Ok(string? detail = null) => new(CheckStatus.Ok, detail);
        internal static CheckResult Warn(string? detail = null) => new(CheckStatus.Warn, detail);
        internal static CheckResult Fail(string? detail = null) => new(CheckStatus.Fail, detail);
    }
}
