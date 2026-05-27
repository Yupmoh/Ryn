using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

public static class FileSystemCommands
{
    [RynCommand("fs.readTextFile")]
    public static string ReadTextFile(string path)
    {
        var resolved = PathValidator.Resolve(path);
        return File.ReadAllText(resolved);
    }

    [RynCommand("fs.readFile")]
    public static string ReadFile(string path)
    {
        var resolved = PathValidator.Resolve(path);
        return Convert.ToBase64String(File.ReadAllBytes(resolved));
    }

    [RynCommand("fs.writeFile")]
    public static string WriteFile(string path, string data)
    {
        var resolved = PathValidator.Resolve(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllBytes(resolved, Convert.FromBase64String(data));
        return resolved;
    }

    [RynCommand("fs.writeTextFile")]
    public static void WriteTextFile(string path, string text)
    {
        var resolved = PathValidator.Resolve(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null) Directory.CreateDirectory(dir);
        File.WriteAllText(resolved, text);
    }

    [RynCommand("fs.exists")]
    public static bool Exists(string path)
    {
        var resolved = PathValidator.Resolve(path);
        return File.Exists(resolved) || Directory.Exists(resolved);
    }

    [RynCommand("fs.mkdir")]
    public static void MkDir(string path)
    {
        var resolved = PathValidator.Resolve(path);
        Directory.CreateDirectory(resolved);
    }

    [RynCommand("fs.remove")]
    public static void Remove(string path)
    {
        var resolved = PathValidator.Resolve(path);
        if (File.Exists(resolved))
            File.Delete(resolved);
        else if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    [RynCommand("fs.readDir")]
    public static string ReadDir(string path)
    {
        var resolved = PathValidator.Resolve(path);
        var entries = new List<FileEntry>();

        foreach (var entry in new DirectoryInfo(resolved).EnumerateFileSystemInfos())
        {
            entries.Add(new FileEntry(
                entry.Name,
                entry.FullName,
                entry is DirectoryInfo,
                entry is FileInfo fi ? fi.Length : 0,
                entry.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)));
        }

        return JsonSerializer.Serialize(entries, FsJsonContext.Default.ListFileEntry);
    }

    [RynCommand("fs.stat")]
    public static string Stat(string path)
    {
        var resolved = PathValidator.Resolve(path);
        var info = new FileInfo(resolved);
        var isDir = Directory.Exists(resolved);

        var stat = new FileStat(
            info.Name,
            info.FullName,
            isDir,
            isDir ? 0 : info.Length,
            info.CreationTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
            info.LastAccessTimeUtc.ToString("O", CultureInfo.InvariantCulture));

        return JsonSerializer.Serialize(stat, FsJsonContext.Default.FileStat);
    }
}

internal record FileEntry(string Name, string Path, bool IsDirectory, long Size, string Modified);
internal record FileStat(string Name, string Path, bool IsDirectory, long Size, string Created, string Modified, string Accessed);

[JsonSerializable(typeof(List<FileEntry>))]
[JsonSerializable(typeof(FileStat))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FsJsonContext : JsonSerializerContext;
