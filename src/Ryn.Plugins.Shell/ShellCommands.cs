using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public static class ShellCommands
{
    private static ShellOptions? _options;

    internal static ShellOptions? Options => _options;


    internal static void Configure(ShellOptions options)
    {
        _options = options;
        (_resolvedBareCommands, _allowedFullPaths) = ResolveAllowlist(options.AllowedCommands);
    }

    [RynCommand("shell.execute")]
    public static string Execute(string command, string argsJson)
    {
        var resolvedCommand = ValidateAndResolveCommand(command);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedCommand,
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

    internal static string ValidateAndResolveCommand(string command)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (_options is null)
            return command;

        if (_options.AllowedCommands.Count == 0)
            throw new UnauthorizedAccessException("Shell execution is disabled (no commands in allowlist)");

        var hasPathSeparator = command.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || command.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

        if (hasPathSeparator)
        {
            var canonical = Path.GetFullPath(command);
            if (_allowedFullPaths is not null && _allowedFullPaths.Contains(canonical))
                return canonical;
            throw new UnauthorizedAccessException($"Command path '{command}' is not in the allowed list");
        }

        if (_resolvedBareCommands is not null
            && _resolvedBareCommands.TryGetValue(command, out var resolvedPath))
            return resolvedPath;

        throw new UnauthorizedAccessException($"Command '{command}' is not in the allowed list");
    }

    private static Dictionary<string, string>? _resolvedBareCommands;
    private static HashSet<string>? _allowedFullPaths;

    private static (Dictionary<string, string> bare, HashSet<string> full) ResolveAllowlist(List<string> commands)
    {
        var bare = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        var separator = OperatingSystem.IsWindows() ? ';' : ':';
        var dirs = pathEnv.Split(separator, StringSplitOptions.RemoveEmptyEntries);

        foreach (var cmd in commands)
        {
            var hasPath = cmd.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || cmd.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

            if (hasPath)
            {
                full.Add(Path.GetFullPath(cmd));
                continue;
            }

            var resolved = FindInPath(cmd, dirs);
            if (resolved is not null)
                bare[cmd] = resolved;
            // If bare command can't be found in PATH at config time, it is
            // silently excluded — fail closed. The command will be rejected
            // at invocation time since it won't be in either map.
        }
        return (bare, full);
    }

    private static string? FindInPath(string command, string[] dirs)
    {
        foreach (var dir in dirs)
        {
            var candidate = Path.Combine(dir, command);
            if (File.Exists(candidate))
                return Path.GetFullPath(candidate);

            if (OperatingSystem.IsWindows())
            {
                foreach (var ext in new[] { ".exe", ".cmd", ".bat" })
                {
                    var withExt = candidate + ext;
                    if (File.Exists(withExt))
                        return Path.GetFullPath(withExt);
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
