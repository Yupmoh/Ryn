using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.Dialog;

// Outcome contract (since 0.25.0): a picked path (or JSON array for openFiles), null when the user
// cancelled, and a thrown exception (surfaced as an IPC error) when the picker itself failed — the three
// cases used to collapse into "". The initial path is best-effort: leading ~ expands, a file path means
// its directory, and empty/relative/nonexistent paths fall back to the platform default location instead
// of being interpolated into a clause that kills the dialog (osascript rejects e.g. `default location "~"`).
#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class PickerCommands
#pragma warning restore CA1812
{
    [RynCommand("dialog.openFile")]
    public static string? OpenFile(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose file{DefaultLocationClause(initialPath)})");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("OpenFileDialog", initialPath, "FileName");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection", initialPath);

        return null;
    }

    [RynCommand("dialog.openFolder")]
    public static string? OpenFolder(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose folder{DefaultLocationClause(initialPath)})");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("FolderBrowserDialog", initialPath, "SelectedPath");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection --directory", initialPath);

        return null;
    }

    [RynCommand("dialog.openFiles")]
    public static string? OpenFiles(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
        {
            var script = "set paths to {}\n" +
                         $"set chosen to (choose file{DefaultLocationClause(initialPath)} with multiple selections allowed)\n" +
                         "repeat with f in chosen\nset end of paths to POSIX path of f\nend repeat\n" +
                         "set text item delimiters to \"\\n\"\npaths as text";
            var result = RunOsascript(script);
            return result is null ? null : PathsToJsonArray(result);
        }

        if (OperatingSystem.IsWindows())
        {
            var normalized = NormalizeInitialPath(initialPath);
            var script = "Add-Type -AssemblyName System.Windows.Forms; " +
                         "$dlg = New-Object System.Windows.Forms.OpenFileDialog; " +
                         "$dlg.Multiselect = $true; " +
                         (normalized is null ? "" : $"$dlg.InitialDirectory = '{EscapePowerShell(normalized)}'; ") +
                         "if ($dlg.ShowDialog() -eq 'OK') { $dlg.FileNames -join \"`n\" }";
            var result = RunPowerShell(script);
            return result is null ? null : PathsToJsonArray(result);
        }

        if (OperatingSystem.IsLinux())
        {
            var result = RunLinuxPicker("--file-selection --multiple", initialPath);
            return result is null ? null : PathsToJsonArray(result);
        }

        return null;
    }

    [RynCommand("dialog.save")]
    public static string? Save(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose file name{DefaultLocationClause(initialPath)})");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("SaveFileDialog", initialPath, "FileName");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection --save", initialPath);

        return null;
    }

    /// <summary>
    /// Normalizes a caller-supplied initial path into an absolute, existing directory, or null when there
    /// is no usable one (empty, relative, nonexistent — the dialog then opens at the platform default).
    /// A leading <c>~</c> expands to the user profile; an existing file resolves to its directory.
    /// </summary>
    internal static string? NormalizeInitialPath(string? initialPath)
    {
        if (string.IsNullOrWhiteSpace(initialPath)) return null;

        var path = initialPath.Trim();
        if (path == "~")
        {
            path = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }
        else if (path.StartsWith("~/", StringComparison.Ordinal) ||
                 path.StartsWith("~\\", StringComparison.Ordinal))
        {
            path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        }

        if (!Path.IsPathRooted(path)) return null;
        if (File.Exists(path)) return Path.GetDirectoryName(path);
        return Directory.Exists(path) ? path : null;
    }

    /// <summary>
    /// The AppleScript <c>default location</c> clause for a usable initial path, or an empty string to let
    /// the dialog open at its default — never an interpolated bad path (which errors the whole script).
    /// </summary>
    internal static string DefaultLocationClause(string? initialPath)
    {
        var normalized = NormalizeInitialPath(initialPath);
        return normalized is null ? "" : $" default location \"{EscapeAppleScript(normalized)}\"";
    }

    // AppleScript user-cancel is error -128, which osascript reports as a nonzero exit with the code in
    // stderr — the only nonzero exit that is not a failure.
    private static string? RunOsascript(string script)
    {
        var psi = new ProcessStartInfo("osascript")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(script);

        var (exitCode, output, error) = RunProcess(psi);
        if (exitCode == 0) return output;
        if (error.Contains("(-128)", StringComparison.Ordinal)) return null; // user cancelled
        throw new InvalidOperationException($"The file dialog failed: {error}");
    }

    private static string? RunWindowsDialog(string dialogType, string initialPath, string resultProp)
    {
        var normalized = NormalizeInitialPath(initialPath);
        // FolderBrowserDialog predates InitialDirectory on Windows PowerShell's runtime; SelectedPath is
        // the pre-selection property it actually has.
        var initialProp = dialogType == "FolderBrowserDialog" ? "SelectedPath" : "InitialDirectory";
        var script = $"Add-Type -AssemblyName System.Windows.Forms; " +
                     $"$dlg = New-Object System.Windows.Forms.{dialogType}; " +
                     (normalized is null ? "" : $"$dlg.{initialProp} = '{EscapePowerShell(normalized)}'; ") +
                     $"if ($dlg.ShowDialog() -eq 'OK') {{ $dlg.{resultProp} }}";
        return RunPowerShell(script);
    }

    // The dialog script prints the picked path only on OK, so exit 0 with empty output is a cancel.
    private static string? RunPowerShell(string script)
    {
        var psi = new ProcessStartInfo("powershell")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-NonInteractive");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(script);

        var (exitCode, output, error) = RunProcess(psi);
        if (exitCode != 0)
            throw new InvalidOperationException($"The file dialog failed: {(error.Length > 0 ? error : $"powershell exited with code {exitCode}")}");
        return output.Length > 0 ? output : null;
    }

    private static string? RunLinuxPicker(string flags, string initialPath)
    {
        var tool = FindLinuxTool()
            ?? throw new InvalidOperationException("No file dialog tool found. Install zenity or kdialog.");

        var normalized = NormalizeInitialPath(initialPath);
        var psi = new ProcessStartInfo(tool)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (tool == "zenity")
        {
            foreach (var flag in flags.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add("--separator=\n");
            if (normalized is not null)
                psi.ArgumentList.Add($"--filename={normalized}/");
        }
        else
        {
            var isDir = flags.Contains("directory", StringComparison.Ordinal);
            var isSave = flags.Contains("save", StringComparison.Ordinal);
            var isMulti = flags.Contains("multiple", StringComparison.Ordinal);

            if (isDir)
                psi.ArgumentList.Add("--getexistingdirectory");
            else if (isSave)
                psi.ArgumentList.Add("--getsavefilename");
            else
                psi.ArgumentList.Add("--getopenfilename");

            psi.ArgumentList.Add(normalized ?? ".");

            if (isMulti)
            {
                psi.ArgumentList.Add("--multiple");
                psi.ArgumentList.Add("--separate-output");
            }
        }

        // Both zenity and kdialog exit 1 on cancel; anything past 1 is a real failure (zenity uses 5 for
        // --timeout expiry, -1 for unexpected errors).
        var (exitCode, output, error) = RunProcess(psi);
        return exitCode switch
        {
            0 => output,
            1 => null,
            _ => throw new InvalidOperationException(
                $"The file dialog failed: {(error.Length > 0 ? error : $"{tool} exited with code {exitCode}")}"),
        };
    }

    private static (int ExitCode, string Stdout, string Stderr) RunProcess(ProcessStartInfo psi)
    {
        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException($"Failed to start '{psi.FileName}'.");
            var output = process.StandardOutput.ReadToEnd().Trim();
            var error = process.StandardError.ReadToEnd().Trim();
            process.WaitForExit();
            return (process.ExitCode, output, error);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new InvalidOperationException($"Failed to start '{psi.FileName}': {ex.Message}", ex);
        }
    }

    private static string? FindLinuxTool()
    {
        foreach (var tool in new[] { "zenity", "kdialog" })
        {
            var psi = new ProcessStartInfo("which")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add(tool);
            try
            {
                using var proc = Process.Start(psi);
                if (proc is null) continue;
                proc.WaitForExit();
                if (proc.ExitCode == 0) return tool;
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    // Serialize the picked paths through System.Text.Json's source-generated path
    // (PickerJsonContext) rather than hand-building the array. The previous StringBuilder
    // escaped only \ and ", producing invalid JSON for any path containing a control
    // character (e.g. a tab or newline embedded in a filename). STJ escapes \t, \r, \n and
    // every other control character correctly, so the bridge's JSON.parse never chokes.
    // The source-gen context keeps this NativeAOT-safe (no reflection-based serializer).
    private static string PathsToJsonArray(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "[]";
        var paths = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return JsonSerializer.Serialize(paths, PickerJsonContext.Default.StringArray);
    }

    private static string EscapeAppleScript(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapePowerShell(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}

// Source-generated serializer context so PathsToJsonArray can emit a correctly-escaped
// JSON string array without reflection (NativeAOT-safe), mirroring ShellJsonContext.
[JsonSerializable(typeof(string[]))]
internal sealed partial class PickerJsonContext : JsonSerializerContext;
