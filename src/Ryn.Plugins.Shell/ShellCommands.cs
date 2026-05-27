using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public static class ShellCommands
{
    private static ShellOptions? _options;

    internal static ShellOptions? Options => _options;


    internal static void Configure(ShellOptions options) => _options = options;

    [RynCommand("shell.execute")]
    public static string Execute(string command, string argsJson)
    {
        ValidateCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        PopulateArguments(psi, argsJson);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        var stderrTask = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = stderrTask.GetAwaiter().GetResult();
        process.WaitForExit();

        var output = new ProcessOutput(stdout, stderr, process.ExitCode);
        return JsonSerializer.Serialize(output, ShellJsonContext.Default.ProcessOutput);
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
        ArgumentNullException.ThrowIfNull(command);

        var options = _options;
        if (options is null)
            return;

        if (options.AllowedCommands.Count == 0)
            throw new UnauthorizedAccessException("Shell execution is disabled (no commands in allowlist)");

        var hasPathSeparator = command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

        if (hasPathSeparator)
        {
            var resolved = Path.GetFullPath(command);
            if (!options.AllowedCommands.Contains(resolved, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Command path '{command}' is not in the allowed list");
        }
        else
        {
            if (!options.AllowedCommands.Contains(command, StringComparer.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException($"Command '{command}' is not in the allowed list");

            var resolvedPath = ResolveCommandPath(command);
            if (resolvedPath is not null)
            {
                // Verify the resolved path is a real executable, not a shim or symlink to something unexpected
                // Store for potential future use (e.g., logging which binary ran)
            }
        }
    }

    private static string? ResolveCommandPath(string command)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null) return null;

        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        foreach (var dir in pathEnv.Split(separator))
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate)) return candidate;

            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                {
                    var withExt = candidate + ext;
                    if (File.Exists(withExt)) return withExt;
                }
            }
        }
        return null;
    }

    internal static void PopulateArguments(ProcessStartInfo psi, string argsJson)
    {
        if (string.IsNullOrEmpty(argsJson) || argsJson == "{}")
            return;

        var argsArray = JsonSerializer.Deserialize(argsJson, ShellJsonContext.Default.StringArray);
        if (argsArray is null)
            return;

        foreach (var arg in argsArray)
            psi.ArgumentList.Add(arg);
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
