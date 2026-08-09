using System.Collections.Concurrent;
using System.Security.Cryptography;
using Ryn.Core.Internal;

namespace Ryn.Core;

public enum FileAccessOperation { Read, Enumerate, CreateWrite, Delete }

public interface IFileAccessGrants
{
    public string Grant(string path);
    public string Grant(string path, FileAccessOperation operation, bool allowMissing = false);
    public string Resolve(string tokenOrPath);
    public string Resolve(string tokenOrPath, FileAccessOperation operation);
}

public sealed class FileAccessGrants : IFileAccessGrants
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    public string Grant(string path) => Grant(path, FileAccessOperation.Read);
    public string Grant(string path, FileAccessOperation operation, bool allowMissing = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Canonicalize(path);
        var directory = Directory.Exists(canonical);
        if (!allowMissing && !directory && !File.Exists(canonical)) throw new FileNotFoundException("Cannot grant a missing filesystem entry.", path);
        if (allowMissing && operation != FileAccessOperation.CreateWrite) throw new FileNotFoundException("Only save targets may be absent.", path);
        var token = "ryn-grant-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        _entries[token] = new Entry(canonical, directory, operation);
        return token;
    }
    public string Resolve(string tokenOrPath) => Resolve(tokenOrPath, FileAccessOperation.Read);
    public string Resolve(string tokenOrPath, FileAccessOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenOrPath);
        foreach (var pair in _entries)
        {
            if (!tokenOrPath.StartsWith(pair.Key, StringComparison.Ordinal)) continue;
            var suffix = tokenOrPath[pair.Key.Length..];
            Require(pair.Value, operation);
            if (suffix.Length == 0) return pair.Value.Path;
            if (!pair.Value.IsDirectory || suffix[0] is not ('/' or '\\')) break;
            var child = Canonicalize(Path.Combine(pair.Value.Path, suffix.TrimStart('/', '\\')));
            if (RynPath.IsContainedIn(child, Canonicalize(pair.Value.Path), RynPath.HostComparison)) return child;
            break;
        }
        throw new UnauthorizedAccessException("Access denied: invalid or expired filesystem grant");
    }
    private static void Require(Entry e, FileAccessOperation op)
    {
        if (e.Operation == op || (e.Operation == FileAccessOperation.Read && op == FileAccessOperation.Enumerate)) return;
        throw new UnauthorizedAccessException("Access denied: grant does not authorize this operation");
    }
    private static string Canonicalize(string path) => RynPath.Canonicalize(path);
    private sealed record Entry(string Path, bool IsDirectory, FileAccessOperation Operation);
}
