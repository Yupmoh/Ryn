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
        var options = ShellCommands.Options;
        if (options is not null && options.AllowedCommands.Count > 0)
        {
            var cmdName = Path.GetFileName(command);
            if (!options.AllowedCommands.Contains(cmdName, StringComparer.OrdinalIgnoreCase)
                && !options.AllowedCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
            {
                throw new UnauthorizedAccessException($"Command '{command}' is not in the allowed list");
            }
        }
        else if (options is not null && options.AllowedCommands.Count == 0)
        {
            throw new UnauthorizedAccessException("Shell execution is disabled (no commands in allowlist)");
        }

        var args = string.Empty;
        if (!string.IsNullOrEmpty(argsJson) && argsJson != "{}")
        {
            var argsArray = JsonSerializer.Deserialize(argsJson, ShellJsonContext.Default.StringArray);
            if (argsArray is not null)
                args = string.Join(' ', argsArray.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
        }

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

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

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            stdoutBatcher.FlushNow();
            stderrBatcher.FlushNow();

            var exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            _webView.EmitEvent($"shell.exit.{pidStr}", exitCode);

            if (_processes.TryRemove(pid, out var removed))
                removed.Dispose();
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return pid;
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
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }

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
