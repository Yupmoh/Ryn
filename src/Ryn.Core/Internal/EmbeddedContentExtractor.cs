using System.IO.Compression;
using System.Reflection;

namespace Ryn.Core.Internal;

internal static class EmbeddedContentExtractor
{
    private const string ZipResourceName = "ryn_embedded_content.zip";

    internal static string? TryExtract()
    {
        var assembly = Assembly.GetEntryAssembly();
        if (assembly is null) return null;

        using var stream = assembly.GetManifestResourceStream(ZipResourceName);
        if (stream is null) return null;

        var hash = (assembly.FullName ?? "ryn").GetHashCode(StringComparison.Ordinal);
        var dir = Path.Combine(Path.GetTempPath(), "ryn-embedded", $"{hash:x8}");

        if (Directory.Exists(dir))
            return dir;

        Directory.CreateDirectory(dir);
        try
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            archive.ExtractToDirectory(dir);
        }
        catch (InvalidDataException)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            return null;
        }
        catch (IOException)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
            return null;
        }

        return dir;
    }
}
