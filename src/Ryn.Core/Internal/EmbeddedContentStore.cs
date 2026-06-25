using System.IO.Compression;
using System.Reflection;

namespace Ryn.Core.Internal;

/// <summary>
/// Holds the app's embedded web content (the wwwroot the bundler packed into <c>ryn_embedded_content.zip</c>)
/// fully in memory, so the ryn:// scheme handler serves it straight from a <c>byte[]</c> — no on-disk
/// extraction, no per-user cache directory, and no disk I/O per request. Decompressed once at startup. AOT-safe:
/// manifest resources survive trimming, so no reflection over types is involved.
/// </summary>
internal sealed class EmbeddedContentStore
{
    private const string ZipResourceName = "ryn_embedded_content.zip";

    // Forward-slash relative path (the zip entry name, e.g. "assets/app.js") -> decompressed bytes. Ordinal:
    // web builds reference their assets by exact name, and the embedded payload is identical on every OS.
    private readonly Dictionary<string, byte[]> _files;

    private EmbeddedContentStore(Dictionary<string, byte[]> files) => _files = files;

    /// <summary>Loads the embedded content zip from the entry assembly into memory, or null if there is none.</summary>
    internal static EmbeddedContentStore? TryLoad()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return null;

        using var stream = assembly.GetManifestResourceStream(ZipResourceName);
        return stream is null ? null : LoadFrom(stream);
    }

    /// <summary>Decompresses every file entry of a content zip into memory. Returns null on a corrupt/empty
    /// archive (the caller then behaves as if there were no embedded content).</summary>
    internal static EmbeddedContentStore? LoadFrom(Stream zipStream)
    {
        try
        {
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                // Skip directory entries (stored with a trailing slash and zero length).
                if (entry.FullName.Length == 0 || entry.FullName.EndsWith('/'))
                    continue;

                using var entryStream = entry.Open();
                using var ms = new MemoryStream(entry.Length > 0 && entry.Length <= int.MaxValue ? (int)entry.Length : 0);
                entryStream.CopyTo(ms);
                files[entry.FullName] = ms.ToArray();
            }

            return files.Count > 0 ? new EmbeddedContentStore(files) : null;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            return null;
        }
    }

    /// <summary>Returns the bytes for a forward-slash relative path (e.g. "index.html", "assets/app.js"), or
    /// null when no such file is embedded.</summary>
    internal byte[]? Get(string relativePath) => _files.TryGetValue(relativePath, out var bytes) ? bytes : null;
}
