using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.FileSystem;

public sealed class FileSystemCommands
{
    private readonly PathValidator _validator;

    public FileSystemCommands(PathValidator validator)
    {
        ArgumentNullException.ThrowIfNull(validator);
        _validator = validator;
    }

    [RynCommand("fs.readTextFile")]
    public string ReadTextFile(string path)
    {
        using var stream = OpenForRead(path);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    [RynCommand("fs.readFile")]
    public string ReadFile(string path)
    {
        using var stream = OpenForRead(path);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Convert.ToBase64String(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    [RynCommand("fs.writeFile")]
    public string WriteFile(string path, string data)
    {
        // Decode before opening so a malformed payload fails before any file is created/truncated.
        var bytes = Convert.FromBase64String(data);
        var resolved = _validator.Resolve(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null) Directory.CreateDirectory(dir);
        using (var stream = OpenForWrite(path, resolved))
            stream.Write(bytes, 0, bytes.Length);
        return resolved;
    }

    [RynCommand("fs.writeTextFile")]
    public void WriteTextFile(string path, string text)
    {
        var resolved = _validator.Resolve(path);
        var dir = Path.GetDirectoryName(resolved);
        if (dir is not null) Directory.CreateDirectory(dir);
        using var stream = OpenForWrite(path, resolved);
        var bytes = Encoding.UTF8.GetBytes(text);
        stream.Write(bytes, 0, bytes.Length);
    }

    [RynCommand("fs.exists")]
    public bool Exists(string path)
    {
        var resolved = _validator.Resolve(path);
        return File.Exists(resolved) || Directory.Exists(resolved);
    }

    [RynCommand("fs.mkdir")]
    public void MkDir(string path)
    {
        var resolved = _validator.Resolve(path);
        Directory.CreateDirectory(resolved);
    }

    [RynCommand("fs.remove")]
    public void Remove(string path)
    {
        var resolved = _validator.Resolve(path);
        if (File.Exists(resolved))
            File.Delete(resolved);
        else if (Directory.Exists(resolved))
            Directory.Delete(resolved, recursive: true);
    }

    [RynCommand("fs.readDir")]
    public string ReadDir(string path)
    {
        var resolved = _validator.Resolve(path);
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
    public string Stat(string path)
    {
        var resolved = _validator.Resolve(path);
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

    // --- PLG-03: open-then-re-verify, closing the validate→re-open TOCTOU window ---------------
    //
    // PathValidator.Resolve()/ResolveForRead() return a *string* canonical (symlink-resolved) path.
    // Handing a string to File.ReadAllText/WriteAllBytes lets the open re-walk the path by name and
    // re-follow symlinks afresh, so a component swapped for an escaping symlink in the window between
    // validation and open could read/write out of scope. We close that window from the caller side:
    //   1. validate (authorize on the canonical, symlink-resolved path),
    //   2. open the file by name (a normal open that *follows* symlinks, so legitimate in-scope
    //      symlinks keep working — we deliberately do NOT pass O_NOFOLLOW, per the medium-risk
    //      register: blanket no-follow would reject valid in-scope links),
    //   3. with the handle held open, re-validate the same user path and require it to still resolve
    //      to the identical in-scope canonical target; otherwise reject with the same access-denied
    //      error and touch nothing.
    //
    // Holding the handle open across the re-check means an attacker must keep the malicious symlink
    // in place through both the open and the re-validation; the moment the re-resolved real path
    // diverges from what we authorized, we refuse. This converts the old "swap once after validate"
    // race into a much narrower one.
    //
    // FD-REALPATH (PLG-03 closed): step 3 now re-checks the real path of the OPEN file descriptor itself
    // (fcntl F_GETPATH on macOS, readlink /proc/self/fd on Linux, GetFinalPathNameByHandle on Windows —
    // see HandleRealPath), not just the user path re-walked by name. Because that path names the very inode
    // the kernel opened, the "swap to an escaping link, then swap back before re-validation" attack no
    // longer slips through: the handle's real path is the escaped target and the check fails. When the
    // platform/handle cannot supply an fd-realpath we fall back to the by-name re-check, which is strictly
    // no weaker than the prior behavior.

    private FileStream OpenForRead(string path)
    {
        // ResolveForRead authorizes AND enforces MaxReadBytes on the canonical target.
        var authorized = _validator.ResolveForRead(path);
        var stream = new FileStream(
            authorized,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        try
        {
            ReVerifyOpenedPath(path, authorized, stream.SafeFileHandle);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private FileStream OpenForWrite(string path, string authorized)
    {
        // Open non-truncating (OpenOrCreate) so the destructive truncate does not land before the
        // re-check: were a component swapped to an escaping link, a FileMode.Create open would have
        // already truncated the out-of-scope target. We re-verify first, then truncate to 0 ourselves.
        var stream = new FileStream(
            authorized,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.Read);
        try
        {
            ReVerifyOpenedPath(path, authorized, stream.SafeFileHandle);
            stream.SetLength(0);
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// With the file already open, requires the opened handle to still be the authorized in-scope target.
    /// The strong check compares the real path of the open descriptor (<see cref="HandleRealPath"/>) against
    /// <paramref name="authorized"/>; when that is unavailable it falls back to re-resolving the original
    /// user-supplied <paramref name="path"/> by name. A divergence (a path component swapped to an escaping
    /// symlink after the first validation) throws <see cref="UnauthorizedAccessException"/> with the same
    /// access-denied contract as the initial validation. See the block comment above.
    /// </summary>
    private void ReVerifyOpenedPath(string path, string authorized, Microsoft.Win32.SafeHandles.SafeFileHandle handle)
    {
        // Strongest check: the real path of the inode actually opened. Canonicalize it through the same
        // helper as `authorized` so symlink/normalization forms compare equal for a legitimate open.
        var handleReal = HandleRealPath.TryGet(handle);
        if (handleReal is not null)
        {
            if (!string.Equals(PathValidator.Canonicalize(handleReal), authorized, PathValidator.PathComparison))
                throw new UnauthorizedAccessException(
                    $"Access denied: path '{path}' changed between validation and open");
            return;
        }

        // Fallback (fd-realpath unavailable on this platform/handle): re-validate by name. This throws if
        // the path now escapes scope, and rejects a resolve to a *different* in-scope target than the one
        // we authorized and opened.
        var reResolved = _validator.Resolve(path);
        if (!string.Equals(reResolved, authorized, PathValidator.PathComparison))
            throw new UnauthorizedAccessException(
                $"Access denied: path '{path}' changed between validation and open");
    }
}

internal record FileEntry(string Name, string Path, bool IsDirectory, long Size, string Modified);
internal record FileStat(string Name, string Path, bool IsDirectory, long Size, string Created, string Modified, string Accessed);

[JsonSerializable(typeof(List<FileEntry>))]
[JsonSerializable(typeof(FileStat))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FsJsonContext : JsonSerializerContext;
