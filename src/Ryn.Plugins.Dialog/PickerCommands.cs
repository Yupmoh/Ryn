using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.Dialog;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class PickerCommands
#pragma warning restore CA1812
{
    [RynCommand("dialog.openFile")]
    public static string OpenFile(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose file default location \"{EscapeAppleScript(initialPath)}\")");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("OpenFileDialog", initialPath, "FileName");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection", initialPath);

        return string.Empty;
    }

    [RynCommand("dialog.openFolder")]
    public static string OpenFolder(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose folder default location \"{EscapeAppleScript(initialPath)}\")");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("FolderBrowserDialog", initialPath, "SelectedPath");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection --directory", initialPath);

        return string.Empty;
    }

    [RynCommand("dialog.openFiles")]
    public static string OpenFiles(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
        {
            var script = "set paths to {}\n" +
                         $"set chosen to (choose file default location \"{EscapeAppleScript(initialPath)}\" with multiple selections allowed)\n" +
                         "repeat with f in chosen\nset end of paths to POSIX path of f\nend repeat\n" +
                         "set text item delimiters to \"\\n\"\npaths as text";
            var result = RunOsascript(script);
            return PathsToJsonArray(result);
        }

        if (OperatingSystem.IsWindows())
        {
            var script = "Add-Type -AssemblyName System.Windows.Forms; " +
                         "$dlg = New-Object System.Windows.Forms.OpenFileDialog; " +
                         "$dlg.Multiselect = $true; " +
                         (string.IsNullOrEmpty(initialPath) ? "" : $"$dlg.InitialDirectory = '{EscapePowerShell(initialPath)}'; ") +
                         "if ($dlg.ShowDialog() -eq 'OK') { $dlg.FileNames -join \"`n\" }";
            var result = RunPowerShell(script);
            return PathsToJsonArray(result);
        }

        if (OperatingSystem.IsLinux())
        {
            var result = RunLinuxPicker("--file-selection --multiple", initialPath);
            return PathsToJsonArray(result);
        }

        return "[]";
    }

    [RynCommand("dialog.save")]
    public static string Save(string initialPath)
    {
        if (OperatingSystem.IsMacOS())
            return RunOsascript($"POSIX path of (choose file name default location \"{EscapeAppleScript(initialPath)}\")");

        if (OperatingSystem.IsWindows())
            return RunWindowsDialog("SaveFileDialog", initialPath, "FileName");

        if (OperatingSystem.IsLinux())
            return RunLinuxPicker("--file-selection --save", initialPath);

        return string.Empty;
    }

    private static string RunOsascript(string script)
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

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (InvalidOperationException) { return string.Empty; }
        catch (System.ComponentModel.Win32Exception) { return string.Empty; }
    }

    private static string RunWindowsDialog(string dialogType, string initialPath, string resultProp)
    {
        var script = $"Add-Type -AssemblyName System.Windows.Forms; " +
                     $"$dlg = New-Object System.Windows.Forms.{dialogType}; " +
                     (string.IsNullOrEmpty(initialPath) ? "" : $"$dlg.InitialDirectory = '{EscapePowerShell(initialPath)}'; ") +
                     $"if ($dlg.ShowDialog() -eq 'OK') {{ $dlg.{resultProp} }}";
        return RunPowerShell(script);
    }

    private static string RunPowerShell(string script)
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

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (InvalidOperationException) { return string.Empty; }
        catch (System.ComponentModel.Win32Exception) { return string.Empty; }
    }

    private static string RunLinuxPicker(string flags, string initialPath)
    {
        var tool = FindLinuxTool();
        if (tool is null) return string.Empty;

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
            if (!string.IsNullOrEmpty(initialPath))
                psi.ArgumentList.Add($"--filename={initialPath}/");
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

            psi.ArgumentList.Add(initialPath ?? ".");

            if (isMulti)
            {
                psi.ArgumentList.Add("--multiple");
                psi.ArgumentList.Add("--separate-output");
            }
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null) return string.Empty;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            return process.ExitCode == 0 ? output : string.Empty;
        }
        catch (InvalidOperationException) { return string.Empty; }
        catch (System.ComponentModel.Win32Exception) { return string.Empty; }
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
