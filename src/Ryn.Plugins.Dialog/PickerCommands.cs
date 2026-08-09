using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Plugins.Dialog;

// Outcome contract (since 0.25.0): a picked path (or JSON array for openFiles), null when the user
// cancelled, and a thrown exception (surfaced as an IPC error) when the picker itself failed — the three
// cases used to collapse into "". The initial path is best-effort: leading ~ expands, a file path means
// its directory, and empty/relative/nonexistent paths fall back to the platform default location instead
// of being interpolated into a clause that kills the dialog (osascript rejects e.g. `default location "~"`).

[RynJsonContext(typeof(PickerJsonContext))]
internal sealed class SecurePickerCommands
{
    private readonly IFileAccessGrants _grants;
    public SecurePickerCommands(IFileAccessGrants grants) => _grants = grants;

    [RynCommand("dialog.openFileSecure")]
    public string? OpenFile(JsonElement options) => Grant(PickerCommands.OpenFile(PickerCommands.ParseOptions(options)), FileAccessOperation.Read);

    [RynCommand("dialog.openFolderSecure")]
    public string? OpenFolder(JsonElement options) => Grant(PickerCommands.OpenFolder(PickerCommands.ParseOptions(options)), FileAccessOperation.Enumerate);

    [RynCommand("dialog.openFilesSecure")]
    public string? OpenFiles(JsonElement options)
    {
        var result = PickerCommands.OpenFiles(PickerCommands.ParseOptions(options));
        if (result is null) return null;
        var paths = JsonSerializer.Deserialize(result, PickerJsonContext.Default.StringArray) ?? [];
        return JsonSerializer.Serialize(paths.Select(p => _grants.Grant(p, FileAccessOperation.Read)).ToArray(), PickerJsonContext.Default.StringArray);
    }

    [RynCommand("dialog.saveSecure")]
    public string? Save(JsonElement options) => Grant(PickerCommands.Save(PickerCommands.ParseOptions(options)), FileAccessOperation.CreateWrite, true);

    private string? Grant(string? path, FileAccessOperation operation, bool allowMissing = false) => path is null ? null : _grants.Grant(path, operation, allowMissing);
}

#pragma warning disable CA1812 // Instantiated by generated DI code
/// <summary>Options shared by native open, folder, multi-open, and save pickers.</summary>
public sealed class PickerOptions
{
    /// <summary>Dialog title.</summary>
    public string? Title { get; set; }
    /// <summary>Named extension filters.</summary>
    public IReadOnlyList<PickerFilter>? Filters { get; set; }
    /// <summary>Allows multiple selections.</summary>
    public bool Multiple { get; set; }
    /// <summary>Initial file or directory path.</summary>
    public string? InitialPath { get; set; }
    /// <summary>Suggested filename for save dialogs.</summary>
    public string? SuggestedFileName { get; set; }
}

/// <summary>A display name and extensions used to constrain picker results.</summary>
public sealed class PickerFilter
{
    /// <summary>Human-readable filter name.</summary>
    public string? Name { get; set; }
    /// <summary>Allowed extensions.</summary>
    public IReadOnlyList<string>? Extensions { get; set; }
}
[RynJsonContext(typeof(PickerJsonContext))]
 #pragma warning disable CA1812
 internal sealed class PickerCommands
#pragma warning restore CA1812
{
    [RynCommand("dialog.openFile")]
    public static string? OpenFile(JsonElement options) => OpenFile(ParseOptions(options));

    [RynCommand("dialog.openFolder")]
    public static string? OpenFolder(JsonElement options) => OpenFolder(ParseOptions(options));

    [RynCommand("dialog.openFiles")]
    public static string? OpenFiles(JsonElement options) => OpenFiles(ParseOptions(options));

    [RynCommand("dialog.save")]
    public static string? Save(JsonElement options) => Save(ParseOptions(options));

    // Typed seams retain the old string argument contract for existing callers and tests.
    internal static string? OpenFile(PickerOptions options)
    {
        if (OperatingSystem.IsMacOS()) return RunOsascript(BuildMacScript("file", options, false));
        if (OperatingSystem.IsWindows()) return RunWindowsDialog("OpenFileDialog", options, "FileName");
        if (OperatingSystem.IsLinux()) return RunLinuxPicker("open", options, false);
        return null;
    }

    internal static string? OpenFolder(PickerOptions options)
    {
        if (OperatingSystem.IsMacOS()) return RunOsascript(BuildMacScript("folder", options, false));
        if (OperatingSystem.IsWindows()) return RunWindowsDialog("FolderBrowserDialog", options, "SelectedPath");
        if (OperatingSystem.IsLinux()) return RunLinuxPicker("directory", options, false);
        return null;
    }

    internal static string? OpenFiles(PickerOptions options)
    {
        options.Multiple = true;
        if (OperatingSystem.IsMacOS())
        {
            var result = RunOsascript(BuildMacScript("file", options, true));
            return result is null ? null : PathsToJsonArray(result);
        }
        if (OperatingSystem.IsWindows())
        {
            var result = RunWindowsDialog("OpenFileDialog", options, "FileNames");
            return result is null ? null : PathsToJsonArray(result);
        }
        if (OperatingSystem.IsLinux())
        {
            var result = RunLinuxPicker("open", options, true);
            return result is null ? null : PathsToJsonArray(result);
        }
        return null;
    }

    internal static string? Save(PickerOptions options)
    {
        if (OperatingSystem.IsMacOS()) return RunOsascript(BuildMacScript("save", options, false));
        if (OperatingSystem.IsWindows()) return RunWindowsDialog("SaveFileDialog", options, "FileName");
        if (OperatingSystem.IsLinux()) return RunLinuxPicker("save", options, false);
        return null;
    }

    internal static PickerOptions ParseOptions(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
            return new PickerOptions { InitialPath = value.GetString() };
        if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return new PickerOptions();
        return JsonSerializer.Deserialize(value.GetRawText(), PickerJsonContext.Default.PickerOptions) ?? new PickerOptions();
    }

    internal static string BuildMacScript(string kind, PickerOptions options, bool multiple)
    {
        var prompt = MacString(options.Title);
        var filter = FilterSummary(options.Filters);
        if (filter.Length > 0) prompt = MacString(string.IsNullOrEmpty(options.Title) ? filter : $"{options.Title} ({filter})");
        var types = MacFileTypes(options.Filters);
        var typeClause = types.Length == 0 || kind == "folder" ? "" : $" of type {{{types}}}";
        var location = DefaultLocationClause(options.InitialPath);
        var name = kind == "folder" ? "choose folder" : kind == "save" ? "choose file name" : "choose file";
        var multi = multiple ? " with multiple selections allowed" : "";
        var saveName = kind == "save" && !string.IsNullOrWhiteSpace(options.SuggestedFileName) ? $" default name \"{EscapeAppleScript(options.SuggestedFileName!.Trim())}\"" : "";
        return kind == "folder" ? $"POSIX path of ({name}{location} with prompt {prompt})" : multiple ? $"set paths to {{}}\nset chosen to ({name}{typeClause}{location}{multi} with prompt {prompt})\nrepeat with f in chosen\nset end of paths to POSIX path of f\nend repeat\nset text item delimiters to \"\\n\"\npaths as text" : $"POSIX path of ({name}{typeClause}{location} with prompt {prompt}{saveName})";
    }

    private static string MacFileTypes(IReadOnlyList<PickerFilter>? filters) => string.Join(", ", (filters ?? []).SelectMany(f => f.Extensions ?? []).Select(e => e.Trim().TrimStart('.')).Where(e => e.Length > 0).Select(e => $"\"{EscapeAppleScript(e)}\"").Distinct(StringComparer.OrdinalIgnoreCase));

    private static string MacString(string? value) => $"\"{EscapeAppleScript(value ?? "Select") }\"";

    internal static string FilterSummary(IReadOnlyList<PickerFilter>? filters) => filters is null ? "" : string.Join(", ", filters.Where(f => !string.IsNullOrWhiteSpace(f.Name)).Select(f => $"{f.Name}: {string.Join(",", f.Extensions ?? [])}"));

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

    private static string? RunWindowsDialog(string dialogType, PickerOptions options, string resultProp)
    {
        var normalized = NormalizeInitialPath(options.InitialPath);
        var initialProp = dialogType == "FolderBrowserDialog" ? "SelectedPath" : "InitialDirectory";
        var multi = resultProp == "FileNames";
        var script = $"Add-Type -AssemblyName System.Windows.Forms; $dlg = New-Object System.Windows.Forms.{dialogType}; " +
                     (string.IsNullOrWhiteSpace(options.Title) ? "" : $"$dlg.Title = '{EscapePowerShell(options.Title!)}'; ") +
                     (normalized is null ? "" : $"$dlg.{initialProp} = '{EscapePowerShell(normalized)}'; ") +
                     BuildWindowsFilter(options.Filters) +
                     (multi ? "$dlg.Multiselect = $true; " : "") +
                     (dialogType == "SaveFileDialog" && !string.IsNullOrWhiteSpace(options.SuggestedFileName) ? $"$dlg.FileName = '{EscapePowerShell(options.SuggestedFileName!)}'; " : "") +
                     $"if ($dlg.ShowDialog() -eq 'OK') {{ $dlg.{resultProp}{(multi ? " -join \"`n\"" : "")} }}";
        return RunPowerShell(script);
    }

    private static string BuildWindowsFilter(IReadOnlyList<PickerFilter>? filters)
    {
        var parts = (filters ?? []).Where(f => (f.Extensions ?? []).Count > 0).Select(f => $"{EscapePowerShell(string.IsNullOrWhiteSpace(f.Name) ? "Files" : f.Name!)}|{string.Join(";", (f.Extensions ?? []).Select(e => "*" + (e.StartsWith('.') ? e : "." + e)))}");
        var filter = string.Join("|", parts);
        return filter.Length == 0 ? "" : $"$dlg.Filter = '{filter}'; ";
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
    private static string? RunLinuxPicker(string mode, PickerOptions options, bool multiple)
    {
        var tool = FindLinuxTool() ?? throw new InvalidOperationException("No file dialog tool found. Install zenity or kdialog.");
        var normalized = NormalizeInitialPath(options.InitialPath);
        var psi = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        if (tool == "zenity")
        {
            psi.ArgumentList.Add("--file-selection");
            if (mode == "directory") psi.ArgumentList.Add("--directory");
            if (mode == "save") psi.ArgumentList.Add("--save");
            if (multiple) psi.ArgumentList.Add("--multiple");
            if (!string.IsNullOrWhiteSpace(options.Title)) psi.ArgumentList.Add($"--title={options.Title}");
            foreach (var filter in options.Filters ?? [])
                foreach (var ext in filter.Extensions ?? []) psi.ArgumentList.Add($"--file-filter={filter.Name ?? ext} (*{(ext.StartsWith('.') ? ext : "." + ext)})");
            psi.ArgumentList.Add("--separator=\n");
            var filename = mode == "save" ? options.SuggestedFileName : null;
            if (filename is not null && normalized is not null) filename = Path.Combine(normalized, filename);
            psi.ArgumentList.Add($"--filename={filename ?? (normalized is null ? "" : normalized + "/")}");
        }
        else
        {
            if (mode == "directory") psi.ArgumentList.Add("--getexistingdirectory");
            else if (mode == "save") psi.ArgumentList.Add("--getsavefilename");
            else psi.ArgumentList.Add("--getopenfilename");
            psi.ArgumentList.Add(Path.Combine(normalized ?? ".", options.SuggestedFileName ?? ""));
            if (multiple) { psi.ArgumentList.Add("--multiple"); psi.ArgumentList.Add("--separate-output"); }
            var filter = BuildKdialogFilter(options.Filters);
            if (filter.Length > 0) psi.ArgumentList.Add(filter);
        }
        var (exitCode, output, error) = RunProcess(psi);
        return exitCode switch { 0 => output, 1 => null, _ => throw new InvalidOperationException($"The file dialog failed: {(error.Length > 0 ? error : $"{tool} exited with code {exitCode}")}") };
    }
    private static string BuildKdialogFilter(IReadOnlyList<PickerFilter>? filters) => string.Join(" | ", (filters ?? []).Where(f => (f.Extensions ?? []).Count > 0).Select(f => $"{(string.IsNullOrWhiteSpace(f.Name) ? "Files" : f.Name)} ({string.Join(" ", (f.Extensions ?? []).Select(e => "*" + (e.StartsWith('.') ? e : "." + e)))})"));

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
[JsonSerializable(typeof(PickerOptions))]
[JsonSerializable(typeof(PickerFilter))]
[JsonSerializable(typeof(PickerFilter[]))]
internal sealed partial class PickerJsonContext : JsonSerializerContext;
