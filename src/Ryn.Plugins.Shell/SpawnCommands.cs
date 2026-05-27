using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ryn.Core;
using Ryn.Core.Internal;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public sealed class SpawnCommands : IDisposable
{
    private readonly IRynWebView _webView;
    private readonly ConcurrentDictionary<int, SpawnedProcess> _processes = new();

    public SpawnCommands(IRynWebView webView)
    {
        _webView = webView;
    }

    [RynCommand("shell.spawn")]
    public int Spawn(string command, string argsJson)
    {
        var resolvedCommand = ShellCommands.ValidateAndResolveCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        ShellCommands.PopulateArguments(psi, argsJson);

        var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        var pid = process.Id;
        var pidStr = pid.ToString(CultureInfo.InvariantCulture);

        // Batchers are owned by SpawnedProcess which is stored in _processes and disposed on exit/kill/Dispose.
#pragma warning disable CA2000
        var stdoutBatcher = new EventBatcher(_webView, $"shell.stdout.{pidStr}");
        var stderrBatcher = new EventBatcher(_webView, $"shell.stderr.{pidStr}");
#pragma warning restore CA2000

        var spawned = new SpawnedProcess(process, stdoutBatcher, stderrBatcher);
        _processes[pid] = spawned;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stdoutBatcher.Add(JsonSerializer.Serialize(e.Data, ShellJsonContext.Default.String));
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
                stderrBatcher.Add(JsonSerializer.Serialize(e.Data, ShellJsonContext.Default.String));
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        _ = Task.Run(() =>
        {
            process.WaitForExit();

            // TryRemove claims ownership — if Kill already removed it, skip cleanup
            if (!_processes.TryRemove(pid, out var owned))
                return;

            owned.StdoutBatcher.FlushNow();
            owned.StderrBatcher.FlushNow();

            var exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            _webView.EmitEvent($"shell.exit.{pidStr}", exitCode);

            owned.Dispose();
        });

        return pid;
    }

    [RynCommand("shell.metrics")]
    public string Metrics()
    {
        var entries = new List<ProcessMetrics>();
        foreach (var kvp in _processes)
        {
            var pid = kvp.Key;
            var sp = kvp.Value;
            entries.Add(new ProcessMetrics(
                pid,
                sp.StdoutBatcher.AddedCount + sp.StderrBatcher.AddedCount,
                sp.StdoutBatcher.FlushedCount + sp.StderrBatcher.FlushedCount,
                sp.StdoutBatcher.DroppedCount + sp.StderrBatcher.DroppedCount));
        }
        return JsonSerializer.Serialize(entries, ShellJsonContext.Default.ListProcessMetrics);
    }

    [RynCommand("shell.kill")]
    public bool Kill(int pid)
    {
        if (!_processes.TryRemove(pid, out var spawned))
            return false;

        try
        {
            if (!spawned.Process.HasExited)
                spawned.Process.Kill(entireProcessTree: true);

            // Wait for the process to fully exit so WaitForExit in the background
            // task unblocks and all output callbacks complete before we dispose
            spawned.Process.WaitForExit();
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }

        spawned.StdoutBatcher.FlushNow();
        spawned.StderrBatcher.FlushNow();

        var pidStr = pid.ToString(CultureInfo.InvariantCulture);
        var exitCode = spawned.Process.HasExited
            ? spawned.Process.ExitCode.ToString(CultureInfo.InvariantCulture)
            : "-1";
        _webView.EmitEvent($"shell.exit.{pidStr}", exitCode);

        spawned.Dispose();
        return true;
    }

    public void Dispose()
    {
        foreach (var kvp in _processes)
        {
            if (_processes.TryRemove(kvp.Key, out var spawned))
            {
                try
                {
                    if (!spawned.Process.HasExited)
                        spawned.Process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Process already exited
                }

                spawned.Dispose();
            }
        }
    }
}

internal sealed record SpawnedProcess(Process Process, EventBatcher StdoutBatcher, EventBatcher StderrBatcher) : IDisposable
{
    public void Dispose()
    {
        StdoutBatcher.Dispose();
        StderrBatcher.Dispose();
        Process.Dispose();
    }
}
