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
            return IsWayland()
                ? RunProcess("wl-paste", "--no-newline")
                : RunProcess("xclip", "-selection clipboard -o");

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
        {
            if (IsWayland())
                RunProcessWithInput("wl-copy", "", text);
            else
                RunProcessWithInput("xclip", "-selection clipboard", text);
        }
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
        {
            if (IsWayland())
                RunProcess("wl-copy", "--clear");
            else
                RunProcessWithInput("xclip", "-selection clipboard", "");
        }
        else if (OperatingSystem.IsWindows())
            RunProcess("powershell", "-command Set-Clipboard -Value $null");
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.readImage")]
    public static string ReadImage()
    {
        if (OperatingSystem.IsMacOS())
            return ReadImageMacOS();

        if (OperatingSystem.IsLinux())
            return ReadImageLinux();

        if (OperatingSystem.IsWindows())
            return ReadImageWindows();

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.writeImage")]
    public static void WriteImage(string base64Png)
    {
        if (string.IsNullOrEmpty(base64Png))
            throw new ArgumentException("base64Png must not be null or empty.", nameof(base64Png));

        byte[] pngBytes = Convert.FromBase64String(base64Png);

        if (OperatingSystem.IsMacOS())
            WriteImageMacOS(pngBytes);
        else if (OperatingSystem.IsLinux())
            WriteImageLinux(pngBytes);
        else if (OperatingSystem.IsWindows())
            WriteImageWindows(pngBytes);
        else
            throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    [RynCommand("clipboard.hasImage")]
    public static bool HasImage()
    {
        if (OperatingSystem.IsMacOS())
            return HasImageMacOS();

        if (OperatingSystem.IsLinux())
            return HasImageLinux();

        if (OperatingSystem.IsWindows())
            return HasImageWindows();

        throw new PlatformNotSupportedException("Clipboard is not supported on this platform.");
    }

    // ── macOS image helpers ──────────────────────────────────────────────

    private static string ReadImageMacOS()
    {
        EnsureToolExists("osascript");

        const string script =
            "ObjC.import('AppKit');" +
            "var pb = $.NSPasteboard.generalPasteboard;" +
            "var data = pb.dataForType($.NSPasteboardTypePNG);" +
            "if (!data.isNil()) { data.base64EncodedStringWithOptions(0).js; } else { ''; }";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("JavaScript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start process 'osascript'.");

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        // osascript may exit non-zero if no image — return empty
        return process.ExitCode != 0 ? "" : output;
    }

    private static void WriteImageMacOS(byte[] pngBytes)
    {
        EnsureToolExists("osascript");

        var tempPath = Path.Combine(Path.GetTempPath(), $"ryn_clip_{Environment.ProcessId}.png");
        try
        {
            File.WriteAllBytes(tempPath, pngBytes);

            var script = $"set the clipboard to (read (POSIX file \"{tempPath}\") as «class PNGf»)";

            var psi = new ProcessStartInfo
            {
                FileName = "osascript",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);

            using var process = Process.Start(psi);
            if (process is null)
                throw new InvalidOperationException("Failed to start process 'osascript'.");

            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Process 'osascript' exited with code {process.ExitCode}: {error}".TrimEnd());
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool HasImageMacOS()
    {
        EnsureToolExists("osascript");

        const string script =
            "ObjC.import('AppKit');" +
            "var pb = $.NSPasteboard.generalPasteboard;" +
            "var data = pb.dataForType($.NSPasteboardTypePNG);" +
            "!data.isNil()";

        var psi = new ProcessStartInfo
        {
            FileName = "osascript",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-l");
        psi.ArgumentList.Add("JavaScript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException("Failed to start process 'osascript'.");

        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        return process.ExitCode == 0
            && string.Equals(output, "true", StringComparison.OrdinalIgnoreCase);
    }

    // ── Linux image helpers ──────────────────────────────────────────────

    // True when running under a Wayland session (prefer wl-clipboard; xclip is X11-only and fails there).
    private static bool IsWayland() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

    private static string ReadImageLinux()
    {
        if (IsWayland())
        {
            EnsureToolExists("wl-paste");
            var wlBytes = RunProcessWithBinaryOutput("wl-paste", ["--type", "image/png"]);
            return wlBytes.Length == 0 ? "" : Convert.ToBase64String(wlBytes);
        }

        EnsureToolExists("xclip");
        byte[] pngBytes = RunProcessWithBinaryOutput("xclip", ["-selection", "clipboard", "-t", "image/png", "-o"]);
        return pngBytes.Length == 0 ? "" : Convert.ToBase64String(pngBytes);
    }

    private static void WriteImageLinux(byte[] pngBytes)
    {
        if (IsWayland())
        {
            EnsureToolExists("wl-copy");
            RunProcessWithBinaryInput("wl-copy", ["--type", "image/png"], pngBytes);
            return;
        }

        EnsureToolExists("xclip");
        RunProcessWithBinaryInput("xclip", ["-selection", "clipboard", "-t", "image/png"], pngBytes);
    }

    private static bool HasImageLinux()
    {
        try
        {
            if (IsWayland())
            {
                EnsureToolExists("wl-paste");
                var types = RunProcess("wl-paste", "--list-types");
                return types.Contains("image/png", StringComparison.Ordinal);
            }

            EnsureToolExists("xclip");
            var output = RunProcess("xclip", "-selection clipboard -t TARGETS -o");
            return output.Contains("image/png", StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    // ── Windows image helpers ────────────────────────────────────────────

    private static string ReadImageWindows()
    {
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms;" +
            "Add-Type -AssemblyName System.Drawing;" +
            "$img = [System.Windows.Forms.Clipboard]::GetImage();" +
            "if ($img) {" +
            "  $ms = New-Object System.IO.MemoryStream;" +
            "  $img.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png);" +
            "  [Convert]::ToBase64String($ms.ToArray())" +
            "}";

        return RunProcess("powershell", $"-command {script}").Trim();
    }

    private static void WriteImageWindows(byte[] pngBytes)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"ryn_clip_{Environment.ProcessId}.png");
        try
        {
            File.WriteAllBytes(tempPath, pngBytes);

            var script =
                "Add-Type -AssemblyName System.Windows.Forms;" +
                "Add-Type -AssemblyName System.Drawing;" +
                $"$img = [System.Drawing.Image]::FromFile('{tempPath.Replace("'", "''", StringComparison.Ordinal)}');" +
                "[System.Windows.Forms.Clipboard]::SetImage($img)";

            RunProcess("powershell", $"-command {script}");
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    private static bool HasImageWindows()
    {
        const string script =
            "Add-Type -AssemblyName System.Windows.Forms;" +
            "[System.Windows.Forms.Clipboard]::ContainsImage()";

        var output = RunProcess("powershell", $"-command {script}").Trim();
        return string.Equals(output, "True", StringComparison.OrdinalIgnoreCase);
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

    private static byte[] RunProcessWithBinaryOutput(string fileName, string[] arguments)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        using var ms = new System.IO.MemoryStream();
        process.StandardOutput.BaseStream.CopyTo(ms);
        process.WaitForExit();

        // Return empty if the process failed (e.g. no image on clipboard)
        return process.ExitCode != 0 ? [] : ms.ToArray();
    }

    private static void RunProcessWithBinaryInput(string fileName, string[] arguments, byte[] input)
    {
        EnsureToolExists(fileName);

        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi);
        if (process is null)
            throw new InvalidOperationException($"Failed to start process '{fileName}'.");

        process.StandardInput.BaseStream.Write(input, 0, input.Length);
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
