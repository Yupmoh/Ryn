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
