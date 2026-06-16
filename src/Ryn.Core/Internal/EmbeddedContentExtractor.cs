using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;

namespace Ryn.Core.Internal;

internal static class EmbeddedContentExtractor
{
    private const string ZipResourceName = "ryn_embedded_content.zip";
    private const string CacheRootName = "ryn-embedded";

    // Length (hex chars) of the truncated SHA-256 used to name the leaf dir. 16 bytes = 128 bits of the
    // digest, which is far more than enough to avoid collisions between distinct embedded payloads while
    // keeping the path short.
    private const int KeyLength = 32;

    internal static string? TryExtract()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return null;

        using var stream = assembly.GetManifestResourceStream(ZipResourceName);
        if (stream is null) return null;

        return TryExtract(stream);
    }

    // Extracts the embedded zip into a per-user cache dir whose leaf name is a stable content hash of the
    // zip bytes. Identical content reuses the same dir across launches (the cache actually hits); changed
    // content lands in a fresh dir and stale siblings are swept. Returns null on any I/O failure rather
    // than throwing — the caller treats a null dir as "no embedded content".
    internal static string? TryExtract(Stream stream)
    {
        // The manifest-resource stream is forward-only, and we need the bytes twice (once to hash, once to
        // unzip). Buffer it once so both passes read from memory.
        byte[] bytes;
        using (var buffer = new MemoryStream())
        {
            stream.CopyTo(buffer);
            bytes = buffer.ToArray();
        }

        var key = ComputeKey(bytes);

        var cacheRoot = GetCacheRoot();
        if (cacheRoot is null) return null;

        var dir = Path.Combine(cacheRoot, key);

        // Cache hit: identical content was already extracted on a previous launch (or by another instance).
        if (Directory.Exists(dir))
        {
            SweepStaleSiblings(cacheRoot, key);
            return dir;
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try
        {
            using var source = new MemoryStream(bytes, writable: false);
            using var archive = new ZipArchive(source, ZipArchiveMode.Read);
            archive.ExtractToDirectory(dir);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            TryDelete(dir);
            return null;
        }

        SweepStaleSiblings(cacheRoot, key);
        return dir;
    }

    private static string ComputeKey(byte[] bytes)
    {
        var digest = SHA256.HashData(bytes);
        // Convert.ToHexString returns uppercase hex; truncate to KeyLength chars for a short-but-collision-
        // safe leaf name. Casing is irrelevant downstream (the sibling sweep compares OrdinalIgnoreCase).
        return Convert.ToHexString(digest).Substring(0, KeyLength);
    }

    // Prefers a per-user cache dir (LocalApplicationData) over the world-writable system temp, which on
    // Linux removes the /tmp pre-creation squat vector. Falls back to temp only when the per-user path is
    // unavailable. Returns null if neither root can be created.
    private static string? GetCacheRoot()
    {
        var baseDir = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.Create);

        if (string.IsNullOrEmpty(baseDir))
            baseDir = Path.GetTempPath();

        if (string.IsNullOrEmpty(baseDir))
            return null;

        var root = Path.Combine(baseDir, CacheRootName);
        try
        {
            Directory.CreateDirectory(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return root;
    }

    // Opportunistically removes sibling dirs under the cache root that don't match the current content key,
    // bounding disk litter from older embedded payloads. Best-effort only: a dir that's locked or in use by
    // another running instance simply fails to delete and is left in place.
    private static void SweepStaleSiblings(string cacheRoot, string currentKey)
    {
        try
        {
            foreach (var sibling in Directory.EnumerateDirectories(cacheRoot))
            {
                var name = Path.GetFileName(sibling);
                if (string.Equals(name, currentKey, StringComparison.OrdinalIgnoreCase))
                    continue;

                TryDelete(sibling);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration can race with another instance creating/removing dirs; ignore and try again next launch.
        }
    }

    private static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
