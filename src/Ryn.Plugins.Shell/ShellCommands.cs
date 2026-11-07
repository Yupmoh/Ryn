using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public sealed class ShellCommands
{
    private readonly ShellExecutionPolicy _policy;

    public ShellCommands(ShellExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _policy = policy;
    }

    [RynCommand("shell.execute")]
    public string Execute(string command, string argsJson)
    {
        var args = ShellExecutionPolicy.ParseArgs(argsJson);
        var resolvedCommand = _policy.ValidateInvocation(command, args);

        var psi = new ProcessStartInfo
        {
            FileName = resolvedCommand,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        _policy.ApplyProcessPolicy(psi);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Failed to start process: {command}");

        // Stream both pipes with a per-stream character cap so a chatty child cannot exhaust memory, while
        // still draining past the cap so it can't deadlock on a full pipe.
        var maxChars = _policy.MaxExecuteOutputChars;
        var stdoutTask = ReadBoundedAsync(process.StandardOutput, maxChars);
        var stderrTask = ReadBoundedAsync(process.StandardError, maxChars);

        // Bound the wall-clock time; a hung child is killed (whole tree) rather than blocking IPC forever.
        var timeout = _policy.ExecuteTimeout;
        var timeoutMs = timeout > TimeSpan.Zero
            ? (int)Math.Min(timeout.TotalMilliseconds, int.MaxValue)
            : Timeout.Infinite;

        var exited = process.WaitForExit(timeoutMs);
        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { /* already exited */ }
            catch (System.ComponentModel.Win32Exception) { /* lost the race to kill it */ }
            process.WaitForExit();
        }

        // Streams reach EOF once the process (and its tree, on timeout) exits, so the readers complete here.
        var (stdout, stdoutTruncated) = stdoutTask.GetAwaiter().GetResult();
        var (stderr, stderrTruncated) = stderrTask.GetAwaiter().GetResult();

        if (!exited)
            throw new TimeoutException(
                $"Command '{command}' did not complete within {timeout.TotalSeconds:0}s and was terminated.");

        var output = new ProcessOutput(stdout, stderr, process.ExitCode, stdoutTruncated || stderrTruncated);
        return JsonSerializer.Serialize(output, ShellJsonContext.Default.ProcessOutput);
    }

    [RynCommand("shell.open")]
    public void Open(string url)
    {
        ArgumentNullException.ThrowIfNull(url);
        _policy.ValidateOpenTarget(url);

        if (OperatingSystem.IsMacOS())
            Process.Start(new ProcessStartInfo { FileName = "open", ArgumentList = { url }, UseShellExecute = false });
        else if (OperatingSystem.IsLinux())
            Process.Start(new ProcessStartInfo { FileName = "xdg-open", ArgumentList = { url }, UseShellExecute = false });
        else if (OperatingSystem.IsWindows())
            // UseShellExecute is required to launch the default handler, but only after we have
            // verified the target is a permitted URL scheme (never a bare path or executable).
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
    }

    /// <summary>
    /// Reads <paramref name="reader"/> to EOF, keeping at most <paramref name="maxChars"/> characters and
    /// discarding (but still draining) the rest. A non-positive cap means unbounded. Returns the captured
    /// text and whether any output was dropped.
    /// </summary>
    private static async Task<(string Text, bool Truncated)> ReadBoundedAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buffer = new char[8192];
        var truncated = false;

        int n;
        while ((n = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
        {
            if (maxChars <= 0)
            {
                sb.Append(buffer, 0, n);
                continue;
            }

            if (truncated)
                continue; // past the cap: drain and discard so the child isn't blocked on a full pipe

            var remaining = maxChars - sb.Length;
            if (n <= remaining)
            {
                sb.Append(buffer, 0, n);
            }
            else
            {
                sb.Append(buffer, 0, remaining);
                truncated = true;
            }
        }

        return (sb.ToString(), truncated);
    }
}

internal record ProcessOutput(string Stdout, string Stderr, int ExitCode, bool Truncated);

internal record KillResult(bool Success, string? Error);

internal record ProcessMetrics(int Pid, long Added, long Flushed, long Dropped);

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
[System.Text.Json.Serialization.JsonSerializable(typeof(ProcessOutput))]
[System.Text.Json.Serialization.JsonSerializable(typeof(KillResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(List<ProcessMetrics>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalCreateResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalCheckpoint))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalAttachResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalReadResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalOperationResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalLeaseResult))]
[System.Text.Json.Serialization.JsonSerializable(typeof(TerminalStatusResult))]
[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
internal partial class ShellJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
