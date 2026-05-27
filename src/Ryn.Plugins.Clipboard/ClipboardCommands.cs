using System.Diagnostics;
using Ryn.Ipc;

namespace Ryn.Plugins.Clipboard;

public static class ClipboardCommands
{
    [RynCommand("clipboard.readText")]
    public static string ReadText()
    {
        if (OperatingSystem.IsMacOS())
            return RunProcess("pbpaste", "");

        if (OperatingSystem.IsLinux())
            return RunProcess("xclip", "-selection clipboard -o");

        if (OperatingSystem.IsWindows())
            return RunProcess("powershell", "-command Get-Clipboard");

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.writeText")]
    public static void WriteText(string text)
    {
        if (OperatingSystem.IsMacOS())
            RunProcessWithInput("pbcopy", "", text);
        else if (OperatingSystem.IsLinux())
            RunProcessWithInput("xclip", "-selection clipboard", text);
        else if (OperatingSystem.IsWindows())
            RunProcess("powershell", $"-command Set-Clipboard -Value '{text.Replace("'", "''", StringComparison.Ordinal)}'");
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.hasText")]
    public static bool HasText()
    {
        var text = ReadText();
        return !string.IsNullOrEmpty(text);
    }

    [RynCommand("clipboard.clear")]
    public static void Clear()
    {
        if (OperatingSystem.IsMacOS())
            RunProcessWithInput("pbcopy", "", "");
        else if (OperatingSystem.IsLinux())
            RunProcessWithInput("xclip", "-selection clipboard", "");
        else if (OperatingSystem.IsWindows())
            RunProcess("powershell", "-command Set-Clipboard -Value $null");
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    private static string RunProcess(string fileName, string arguments)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName}' exited with code {process.ExitCode}: {error}".TrimEnd());

        return output;
    }

    private static void RunProcessWithInput(string fileName, string arguments, string input)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.StandardInput.Write(input);
        process.StandardInput.Close();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Process '{fileName}' exited with code {process.ExitCode}: {error}".TrimEnd());
    }

    private static void EnsureToolExists(string tool)
    {
        // powershell is always available on Windows; skip the 'which' check
        if (string.Equals(tool, "powershell", StringComparison.OrdinalIgnoreCase))
            return;

        var whichCommand = OperatingSystem.IsWindows() ? "where" : "which";

        var psi = new ProcessStartInfo
        {
            FileName = whichCommand,
            Arguments = tool,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
                return; // can't verify, proceed anyway

            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Required tool '{tool}' is not installed or not found in PATH.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 'which'/'where' itself not found — can't verify, proceed anyway
        }
    }
}
