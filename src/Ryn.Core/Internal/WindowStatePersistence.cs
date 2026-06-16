using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ryn.Core.Internal;

/// <summary>
/// Persists and restores the last window placement (position, size, maximized flag) to a JSON file
/// under the per-user LocalApplicationData folder. Used by <see cref="RynWindow"/> when
/// <c>RynOptions.PersistWindowState</c> is enabled.
/// </summary>
/// <remarks>
/// Every operation is best-effort: a missing, unreadable, locked, or corrupt state file must never
/// propagate an exception into window startup or shutdown. <see cref="Load"/> returns <c>null</c> and
/// <see cref="Save"/> silently no-ops when the filesystem is unavailable. Construction never touches the
/// disk, so building the persistence object on a read-only or sandboxed profile cannot crash window
/// creation; the directory is created lazily on the first successful <see cref="Save"/>.
/// </remarks>
internal sealed class WindowStatePersistence
{
    private readonly string _directory;
    private readonly string _filePath;

    internal WindowStatePersistence(string appId)
    {
        _directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ryn", appId);
        _filePath = Path.Combine(_directory, "window-state.json");
    }

    internal WindowStateData? Load()
    {
        try
        {
            if (!File.Exists(_filePath)) return null;
            var json = File.ReadAllText(_filePath);
            return JsonSerializer.Deserialize(json, WindowStateJsonContext.Default.WindowStateData);
        }
        // A bad state file (corrupt JSON, locked, permission-denied, or an unsupported path on the
        // current profile) must degrade to "no saved state" rather than throwing into startup.
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    internal void Save(WindowStateData state)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            var json = JsonSerializer.Serialize(state, WindowStateJsonContext.Default.WindowStateData);

            // Write to a sibling temp file and atomically swap it in, so a crash or full disk mid-write
            // never leaves a half-written file that would fail to parse on the next Load.
            var tempPath = _filePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _filePath, overwrite: true);
        }
        // Persisting placement is a convenience, never a correctness requirement: swallow every
        // filesystem/serialization failure so window shutdown can still complete cleanly.
        catch (Exception ex) when (ex is JsonException or IOException
            or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
        }
    }
}

/// <summary>
/// Persisted window placement. Field names are a pinned cross-component contract: the capture-at-close
/// and clamped-restore logic in <see cref="RynWindow"/> reads and writes exactly these members.
/// </summary>
internal sealed class WindowStateData
{
    /// <summary>Last non-maximized window X position, in screen coordinates.</summary>
    public int X { get; set; }

    /// <summary>Last non-maximized window Y position, in screen coordinates.</summary>
    public int Y { get; set; }

    /// <summary>Last non-maximized window width, in logical pixels. Doubles as the pre-maximize width to restore to.</summary>
    public int Width { get; set; }

    /// <summary>Last non-maximized window height, in logical pixels. Doubles as the pre-maximize height to restore to.</summary>
    public int Height { get; set; }

    /// <summary>Whether the window was maximized at close time, so it can be re-maximized on restore.</summary>
    public bool IsMaximized { get; set; }
}

[JsonSerializable(typeof(WindowStateData))]
internal sealed partial class WindowStateJsonContext : JsonSerializerContext;
