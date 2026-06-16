using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Ryn.Cli.Commands;

internal static class DevCommand
{
    // The scaffolded Vite config pins the dev server with `strictPort: true`, and the generated
    // Program.cs opens this exact URL when it sees `--vite`, so the host/port are fixed here.
    private const string ViteDevHost = "localhost";
    private const int ViteDevPort = 5173;
    private static readonly Uri ViteDevUrl = new($"http://{ViteDevHost}:{ViteDevPort}/");

    // How long the file watchers wait for quiescence before acting. A trailing-edge debounce: every
    // file event pushes the rebuild/sync this far into the future, so the action runs once, after the
    // last change in a burst — never dropping the change that arrives mid-window (see CLI-12).
    private static readonly TimeSpan DebounceQuiet = TimeSpan.FromMilliseconds(300);

    private static Process? _appProcess;
    private static Process? _viteProcess;
    private static readonly object _lock = new();
    private static string _dotnet = "dotnet";
    private static bool _viteMode;

    // Trailing-edge debounce timers. Each is armed/re-armed on every relevant file event and fires
    // once after DebounceQuiet of silence. Guarded by _lock for both the field and the timer state.
    private static Timer? _csTimer;
    private static Timer? _frontendTimer;

    // Set when teardown has started (Ctrl+C, SIGTERM/SIGHUP, or ProcessExit) so debounce timers that
    // fire during shutdown become no-ops instead of relaunching a process we are trying to kill.
    private static volatile bool _shuttingDown;

    internal static int Execute(ReadOnlySpan<string> args)
    {
        var (csproj, error) = ProjectResolver.Resolve(
            Directory.GetCurrentDirectory(), ProjectResolver.ReadExplicitProject(args));
        if (csproj is null)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var dotnet = DotnetResolver.ResolveOrReport();
        if (dotnet is null)
            return 1;
        _dotnet = dotnet;

        var projectDir = Path.GetDirectoryName(csproj)!;
        var projectName = Path.GetFileNameWithoutExtension(csproj);
        var wwwrootDir = Path.Combine(projectDir, "wwwroot");
        var frontendDir = Path.Combine(projectDir, "frontend");

        // Vite mode is on when the user passes `--vite`, or when the project looks like a Vite
        // project (a frontend/ with a vite.config.* or an npm "dev" script). In that mode the
        // webview points at the Vite dev server instead of static wwwroot files.
        _viteMode = HasFlag(args, "--vite") || IsViteProject(frontendDir);

        Console.WriteLine($"Ryn dev mode — {projectName}");
        Console.WriteLine();

        // Initial build
        if (!Build(projectDir))
        {
            Console.Error.WriteLine("Initial build failed.");
            return 1;
        }

        // Start the Vite dev server before launching the app so the webview has something to load.
        if (_viteMode)
            StartVite(frontendDir);

        // Launch app
        LaunchApp(projectDir, projectName);

        // Setup C# file watcher. Deleted/Renamed are included so removing or moving a source file
        // also triggers a rebuild (a stale build would otherwise keep the deleted type alive).
        using var csWatcher = new FileSystemWatcher(projectDir, "*.cs")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };
        csWatcher.Changed += (_, e) => OnSourceChanged(projectDir, projectName, e.FullPath);
        csWatcher.Created += (_, e) => OnSourceChanged(projectDir, projectName, e.FullPath);
        csWatcher.Deleted += (_, e) => OnSourceChanged(projectDir, projectName, e.FullPath);
        csWatcher.Renamed += (_, e) => OnSourceChanged(projectDir, projectName, e.FullPath);
        csWatcher.EnableRaisingEvents = true;

        // Setup frontend (wwwroot) file watcher. In Vite mode the UI is served by the Vite dev
        // server (which has its own HMR), so wwwroot is the production output and is not watched.
        FileSystemWatcher? frontendWatcher = null;
        if (!_viteMode && Directory.Exists(wwwrootDir))
        {
            frontendWatcher = new FileSystemWatcher(wwwrootDir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName,
            };
            frontendWatcher.Changed += (_, e) => OnFrontendChanged(projectDir, projectName, e.FullPath);
            frontendWatcher.Created += (_, e) => OnFrontendChanged(projectDir, projectName, e.FullPath);
            frontendWatcher.Deleted += (_, e) => OnFrontendChanged(projectDir, projectName, e.FullPath);
            frontendWatcher.Renamed += (_, e) => OnFrontendChanged(projectDir, projectName, e.FullPath);
            frontendWatcher.EnableRaisingEvents = true;
            Console.WriteLine("Watching wwwroot/ for frontend changes (no rebuild)");
        }

        // Tear the child app and Vite server down on every exit path, not just Ctrl+C: a closed
        // terminal (SIGHUP) or `kill` (SIGTERM) would otherwise orphan the launched app (CLI-19).
        using var exitEvent = new ManualResetEventSlim(false);

        void Shutdown(string reason)
        {
            _shuttingDown = true;
            Console.WriteLine($"\n{reason} — shutting down...");
            KillApp();
            KillVite();
            exitEvent.Set();
        }

        Console.CancelKeyPress += (_, e) =>
        {
            // Cancel the default terminate so our cleanup runs; the wait below then unblocks.
            e.Cancel = true;
            Shutdown("Ctrl+C");
        };

        // ProcessExit is the last-resort net for exits we don't otherwise intercept; it must run the
        // kill synchronously because the runtime tears down immediately after the handler returns.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!_shuttingDown)
            {
                _shuttingDown = true;
                KillApp();
                KillVite();
            }
        };

        // SIGTERM (`kill`, container stop) and SIGHUP (terminal closed) are not surfaced through
        // CancelKeyPress. Register them so the child app is always cleaned up. PosixSignalRegistration
        // is a no-op contract on Windows for these signals, so guard the registration to Unix.
        var signalRegistrations = new List<IDisposable>();
        if (!OperatingSystem.IsWindows())
        {
            foreach (var signal in (PosixSignal[])[PosixSignal.SIGTERM, PosixSignal.SIGHUP])
            {
                var captured = signal;
                signalRegistrations.Add(PosixSignalRegistration.Create(signal, ctx =>
                {
                    // Cancel the default action (process death) so our cleanup completes first, then
                    // unblock the main thread to exit normally with the cleanup already done.
                    ctx.Cancel = true;
                    Shutdown(captured.ToString());
                }));
            }
        }

        try
        {
            Console.WriteLine("Watching for changes... (Ctrl+C to stop)");
            Console.WriteLine();

            exitEvent.Wait();
        }
        finally
        {
            foreach (var registration in signalRegistrations)
                registration.Dispose();

            DisposeTimers();
            frontendWatcher?.Dispose();
            // Belt-and-suspenders: ensure nothing is left running even if we exited the wait for an
            // unexpected reason without going through Shutdown().
            KillApp();
            KillVite();
        }

        return 0;
    }

    private static bool HasFlag(ReadOnlySpan<string> args, string flag)
    {
        foreach (var arg in args)
        {
            if (string.Equals(arg, flag, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="frontendDir"/> looks like a Vite project: it has a
    /// <c>vite.config.*</c> file, or a <c>package.json</c> that declares a <c>dev</c> script.
    /// </summary>
    private static bool IsViteProject(string frontendDir)
    {
        if (!Directory.Exists(frontendDir))
            return false;

        foreach (var ext in (string[])["ts", "js", "mts", "mjs", "cts", "cjs"])
        {
            if (File.Exists(Path.Combine(frontendDir, "vite.config." + ext)))
                return true;
        }

        var packageJson = Path.Combine(frontendDir, "package.json");
        if (File.Exists(packageJson))
        {
            try
            {
                // A lightweight text probe (no JSON deserialization) keeps this NativeAOT-safe and
                // avoids pulling a serializer context into the CLI just to read one field.
                var text = File.ReadAllText(packageJson);
                if (text.Contains("\"dev\"", StringComparison.Ordinal))
                    return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable package.json — fall through to "not a vite project".
            }
        }

        return false;
    }

    private static bool Build(string projectDir)
    {
        Console.WriteLine("  Building...");
        var psi = new ProcessStartInfo
        {
            FileName = _dotnet,
            WorkingDirectory = projectDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add("--nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");

        var process = Process.Start(psi);
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }

    /// <summary>
    /// Starts <c>npm run dev</c> in the frontend directory and waits (up to a timeout) for the Vite
    /// dev server to start accepting connections. Failures are reported but non-fatal: the app still
    /// launches in Vite mode, where it will show a connection error until the server is reachable.
    /// </summary>
    private static void StartVite(string frontendDir)
    {
        if (!Directory.Exists(frontendDir))
        {
            Console.Error.WriteLine($"  --vite: no frontend/ directory at {frontendDir}; skipping Vite dev server.");
            return;
        }

        if (IsPortOpen(ViteDevHost, ViteDevPort))
        {
            // A dev server is already listening (e.g. the user ran `npm run dev` themselves).
            Console.WriteLine($"  Vite dev server already running at {ViteDevUrl}");
            return;
        }

        Console.WriteLine("  Starting Vite dev server (npm run dev)...");

        var npm = OperatingSystem.IsWindows() ? "npm.cmd" : "npm";
        var psi = new ProcessStartInfo
        {
            FileName = npm,
            WorkingDirectory = frontendDir,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("dev");

        try
        {
            lock (_lock)
                _viteProcess = Process.Start(psi);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine("  Could not start npm. Is Node.js installed and on PATH?");
            Console.Error.WriteLine($"  Run the dev server manually:  cd {frontendDir} && npm run dev");
            return;
        }

        if (WaitForPort(ViteDevHost, ViteDevPort, TimeSpan.FromSeconds(30)))
            Console.WriteLine($"  Vite dev server ready at {ViteDevUrl}");
        else
            Console.Error.WriteLine($"  Vite dev server did not become ready at {ViteDevUrl} within 30s.");
    }

    /// <summary>Blocks until the TCP port accepts a connection, or the timeout elapses.</summary>
    private static bool WaitForPort(string host, int port, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            // Give up early if the dev-server process died (e.g. a missing dependency).
            Process? vite;
            lock (_lock)
                vite = _viteProcess;
            if (vite is { HasExited: true })
                return false;

            if (IsPortOpen(host, port))
                return true;

            Thread.Sleep(250);
        }

        return false;
    }

    private static bool IsPortOpen(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connect = client.ConnectAsync(host, port);
            return connect.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch (Exception ex) when (ex is SocketException or AggregateException or ObjectDisposedException)
        {
            return false;
        }
    }

    private static void LaunchApp(string projectDir, string projectName)
    {
        var outputDir = Path.Combine(projectDir, "bin", "Debug", "net10.0");
        var exeName = OperatingSystem.IsWindows() ? projectName + ".exe" : projectName;
        var executable = Path.Combine(outputDir, exeName);

        if (!File.Exists(executable))
        {
            Console.Error.WriteLine($"  Executable not found: {executable}");
            return;
        }

        Console.WriteLine($"  Launching {projectName}...");

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            WorkingDirectory = outputDir,
            UseShellExecute = false,
        };

        // Forward --vite so the scaffolded Program.cs points the webview at the Vite dev server
        // instead of loading the static wwwroot files.
        if (_viteMode)
            psi.ArgumentList.Add("--vite");

        // Assign under the lock so a concurrent KillApp (from a signal handler racing a rebuild)
        // never observes a half-published _appProcess or leaks one it didn't see (CLI-19).
        lock (_lock)
        {
            // If shutdown began between the caller's decision to launch and here, don't start a
            // process that nobody will reap.
            if (_shuttingDown)
                return;
            _appProcess = Process.Start(psi);
        }
    }

    private static void KillApp()
    {
        lock (_lock)
        {
            if (_appProcess is { HasExited: false })
            {
                try { _appProcess.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
            }

            _appProcess = null;
        }
    }

    private static void KillVite()
    {
        lock (_lock)
        {
            if (_viteProcess is { HasExited: false })
            {
                try { _viteProcess.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { /* already exited */ }
            }

            _viteProcess = null;
        }
    }

    /// <summary>
    /// Mirrors <c>wwwroot/</c> into the build output: copies new/changed files and, crucially,
    /// deletes output files and directories whose source no longer exists. Without the delete pass a
    /// removed asset would linger in the running app's content directory (CLI-12).
    /// </summary>
    private static void SyncFrontendFiles(string projectDir)
    {
        var sourceDir = Path.Combine(projectDir, "wwwroot");
        var targetDir = Path.Combine(projectDir, "bin", "Debug", "net10.0", "wwwroot");

        if (!Directory.Exists(sourceDir))
        {
            // The whole wwwroot was removed; clear the mirrored output so nothing stale is served.
            if (Directory.Exists(targetDir))
            {
                try { Directory.Delete(targetDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
            }

            return;
        }

        Directory.CreateDirectory(targetDir);

        // Copy new/changed files.
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = sourceFile[sourceDir.Length..].TrimStart(Path.DirectorySeparatorChar);
            var targetFile = Path.Combine(targetDir, relativePath);

            var targetFileDir = Path.GetDirectoryName(targetFile);
            if (targetFileDir is not null)
                Directory.CreateDirectory(targetFileDir);

            try { File.Copy(sourceFile, targetFile, overwrite: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
        }

        // Delete output files whose source has been removed.
        foreach (var targetFile in Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = targetFile[targetDir.Length..].TrimStart(Path.DirectorySeparatorChar);
            var sourceFile = Path.Combine(sourceDir, relativePath);
            if (!File.Exists(sourceFile))
            {
                try { File.Delete(targetFile); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
            }
        }

        // Prune now-empty output directories left behind by deletions (deepest first).
        foreach (var targetSubDir in Directory.EnumerateDirectories(targetDir, "*", SearchOption.AllDirectories)
                     .OrderByDescending(p => p.Length))
        {
            var relativePath = targetSubDir[targetDir.Length..].TrimStart(Path.DirectorySeparatorChar);
            var sourceSubDir = Path.Combine(sourceDir, relativePath);
            if (!Directory.Exists(sourceSubDir))
            {
                try { Directory.Delete(targetSubDir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* best-effort */ }
            }
        }
    }

    private static void OnSourceChanged(string projectDir, string projectName, string filePath)
    {
        // Skip obj/bin directories.
        if (filePath.Contains(Path.Combine("", "obj", ""), StringComparison.OrdinalIgnoreCase) ||
            filePath.Contains(Path.Combine("", "bin", ""), StringComparison.OrdinalIgnoreCase))
            return;

        // Trailing-edge debounce: (re)arm the timer on every event so the rebuild runs once, after
        // the burst settles. Earlier code used a leading-edge guard that silently dropped any change
        // arriving inside the 300ms window — including the last one (CLI-12).
        lock (_lock)
        {
            if (_shuttingDown)
                return;

            _csTimer ??= new Timer(_ => RebuildAndRelaunch(projectDir, projectName));
            _csTimer.Change(DebounceQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    private static void OnFrontendChanged(string projectDir, string projectName, string filePath)
    {
        lock (_lock)
        {
            if (_shuttingDown)
                return;

            _frontendTimer ??= new Timer(_ => RefreshFrontend(projectDir, projectName));
            _frontendTimer.Change(DebounceQuiet, Timeout.InfiniteTimeSpan);
        }
    }

    private static void RebuildAndRelaunch(string projectDir, string projectName)
    {
        // The debounce timer can fire just as shutdown begins; bail before doing anything heavy.
        if (_shuttingDown)
            return;

        Console.WriteLine("  C# change detected.");
        Console.WriteLine("  Rebuilding...");

        lock (_lock)
        {
            if (_shuttingDown)
                return;

            KillApp();

            if (Build(projectDir))
            {
                LaunchApp(projectDir, projectName);
            }
            else
            {
                Console.Error.WriteLine("  Build failed. Waiting for next change...");
            }
        }
    }

    private static void RefreshFrontend(string projectDir, string projectName)
    {
        if (_shuttingDown)
            return;

        Console.WriteLine("  Frontend change detected.");
        Console.WriteLine("  Refreshing (no rebuild)...");

        lock (_lock)
        {
            if (_shuttingDown)
                return;

            KillApp();
            SyncFrontendFiles(projectDir);
            LaunchApp(projectDir, projectName);
        }
    }

    private static void DisposeTimers()
    {
        lock (_lock)
        {
            _csTimer?.Dispose();
            _csTimer = null;
            _frontendTimer?.Dispose();
            _frontendTimer = null;
        }
    }
}
