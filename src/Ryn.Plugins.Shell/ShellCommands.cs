using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public sealed class ShellCommands
{
    private static ShellOptions? _options;

    private readonly IRynWebView _webView;
    private readonly ConcurrentDictionary<int, Process> _processes = new();
    private static int _nextPid;

    public ShellCommands(IRynWebView webView)
    {
        _webView = webView;
    }

    internal static void Configure(ShellOptions options) => _options = options;

    [RynCommand("shell.execute")]
    public static string Execute(string command, string argsJson)
    {
        ValidateCommand(command);

        var args = ParseArgs(argsJson);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();

        var output = new ProcessOutput(stdout, stderr, process.ExitCode);
        return JsonSerializer.Serialize(output, ShellJsonContext.Default.ProcessOutput);
    }

    [RynCommand("shell.spawn")]
    public string Spawn(string command, string argsJson)
    {
        ValidateCommand(command);

        var args = ParseArgs(argsJson);
        var pid = Interlocked.Increment(ref _nextPid);

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

        _processes[pid] = process;

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                var json = JsonSerializer.Serialize(e.Data, ShellJsonContext.Default.String);
                _webView.EmitEvent($"shell.stdout.{pid.ToString(CultureInfo.InvariantCulture)}", json);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                var json = JsonSerializer.Serialize(e.Data, ShellJsonContext.Default.String);
                _webView.EmitEvent($"shell.stderr.{pid.ToString(CultureInfo.InvariantCulture)}", json);
            }
        };

        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            var exitCode = process.ExitCode.ToString(CultureInfo.InvariantCulture);
            _webView.EmitEvent($"shell.exit.{pid.ToString(CultureInfo.InvariantCulture)}", exitCode);
            _processes.TryRemove(pid, out _);
            process.Dispose();
        };

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return pid.ToString(CultureInfo.InvariantCulture);
    }

    [RynCommand("shell.kill")]
    public string Kill(int pid)
    {
        if (!_processes.TryRemove(pid, out var process))
        {
            return JsonSerializer.Serialize(
                new KillResult(false, $"No spawned process with pid {pid.ToString(CultureInfo.InvariantCulture)}"),
                ShellJsonContext.Default.KillResult);
        }

        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Process already exited
        }
        finally
        {
            process.Dispose();
        }

        return JsonSerializer.Serialize(new KillResult(true, null), ShellJsonContext.Default.KillResult);
    }

    [RynCommand("shell.open")]
    public static void Open(string url)
    {
        if (OperatingSystem.IsMacOS())
            Process.Start("open", url);
        else if (OperatingSystem.IsLinux())
            Process.Start("xdg-open", url);
        else if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    internal static void ValidateCommand(string command)
    {
        var options = _options;
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
    }

    private static string ParseArgs(string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson) || argsJson == "{}")
            return string.Empty;

        var argsArray = JsonSerializer.Deserialize(argsJson, ShellJsonContext.Default.StringArray);
        if (argsArray is null)
            return string.Empty;

        return string.Join(' ', argsArray.Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a));
    }
}

internal record ProcessOutput(string Stdout, string Stderr, int ExitCode);

internal record KillResult(bool Success, string? Error);

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProcessOutput))]
[System.Text.Json.Serialization.JsonSerializable(typeof(KillResult))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class ShellJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
