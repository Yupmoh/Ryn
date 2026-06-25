using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Tests for <see cref="EmbeddedContentStore"/>, which loads the bundler-embedded content zip into memory so the
/// ryn:// scheme handler can serve it from a byte[] with no on-disk extraction.
/// </summary>
public sealed class EmbeddedContentStoreTests
{
    private static MemoryStream MakeZip(params (string Path, string Content)[] files)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, content) in files)
            {
                using var entry = archive.CreateEntry(path).Open();
                var bytes = Encoding.UTF8.GetBytes(content);
                entry.Write(bytes, 0, bytes.Length);
            }
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void LoadFrom_ServesEveryFileByItsForwardSlashPath()
    {
        using var zip = MakeZip(("index.html", "<h1>hi</h1>"), ("assets/app.js", "console.log(1)"));

        var store = EmbeddedContentStore.LoadFrom(zip);

        store.Should().NotBeNull();
        Encoding.UTF8.GetString(store!.Get("index.html")!).Should().Be("<h1>hi</h1>");
        Encoding.UTF8.GetString(store.Get("assets/app.js")!).Should().Be("console.log(1)");
    }

    [Fact]
    public void Get_ReturnsNull_ForAMissingPath()
    {
        using var zip = MakeZip(("index.html", "x"));

        EmbeddedContentStore.LoadFrom(zip)!.Get("missing.css").Should().BeNull();
    }

    [Fact]
    public void LoadFrom_ReturnsNull_ForACorruptArchive()
    {
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("this is not a zip archive"));

        EmbeddedContentStore.LoadFrom(garbage).Should().BeNull();
    }

    [Fact]
    public void LoadFrom_ReturnsNull_WhenTheArchiveHasNoFiles()
    {
        using var empty = MakeZip();

        EmbeddedContentStore.LoadFrom(empty).Should().BeNull();
    }
}
