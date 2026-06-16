using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Regression tests for <see cref="EmbeddedContentExtractor"/> (Cluster B — IPC-02 / ARC-12), driving the
/// internal <c>TryExtract(Stream)</c> seam:
/// <list type="bullet">
/// <item>Two extractions of <em>identical</em> zip bytes return the SAME directory (the content-hash key
/// makes the cache actually hit across launches).</item>
/// <item>Two extractions of <em>different</em> zip bytes return DIFFERENT directories.</item>
/// <item>A non-zip / corrupt stream returns <c>null</c> rather than throwing.</item>
/// </list>
/// Each test embeds a random marker in its zip so its content hash — and therefore its on-disk leaf dir — is
/// unique to the run and can be cleaned up without disturbing a real app's cache.
/// </summary>
public sealed class EmbeddedContentExtractorTests : IDisposable
{
    private readonly List<string> _createdDirs = [];

    public void Dispose()
    {
        foreach (var dir in _createdDirs.Distinct())
        {
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private void Track(string? dir)
    {
        if (dir is not null) _createdDirs.Add(dir);
    }

    private static MemoryStream MakeZip(string entryName, string content)
    {
        var ms = new MemoryStream();
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry(entryName);
            using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
            writer.Write(content);
        }
        ms.Position = 0;
        return ms;
    }

    [Fact]
    public void IdenticalContent_ExtractedTwice_ReturnsSameDirectory()
    {
        var marker = Guid.NewGuid().ToString("N");
        using var first = MakeZip("index.html", $"<html>{marker}</html>");
        using var second = MakeZip("index.html", $"<html>{marker}</html>");

        var dir1 = EmbeddedContentExtractor.TryExtract(first);
        var dir2 = EmbeddedContentExtractor.TryExtract(second);
        Track(dir1);
        Track(dir2);

        dir1.Should().NotBeNull();
        dir2.Should().NotBeNull();
        dir2.Should().Be(dir1, "identical zip content must reuse the same content-hash cache dir (a cache hit)");
        File.Exists(Path.Combine(dir1!, "index.html")).Should().BeTrue("the content was actually extracted");
    }

    [Fact]
    public void DifferentContent_YieldsDifferentDirectories()
    {
        using var zipA = MakeZip("index.html", $"<html>A-{Guid.NewGuid():N}</html>");
        using var zipB = MakeZip("index.html", $"<html>B-{Guid.NewGuid():N}</html>");

        var dirA = EmbeddedContentExtractor.TryExtract(zipA);
        var dirB = EmbeddedContentExtractor.TryExtract(zipB);
        Track(dirA);
        Track(dirB);

        dirA.Should().NotBeNull();
        dirB.Should().NotBeNull();
        dirB.Should().NotBe(dirA, "changed zip bytes must land in a fresh content-hash dir");
    }

    [Fact]
    public void CorruptStream_ReturnsNull_WithoutThrowing()
    {
        // Not a valid zip — ZipArchive construction/extraction throws InvalidDataException internally, which
        // the extractor must catch and surface as a null dir (the caller treats null as "no embedded content").
        using var garbage = new MemoryStream(Encoding.ASCII.GetBytes("this is definitely not a zip archive"));

        string? dir = null;
        var act = () => dir = EmbeddedContentExtractor.TryExtract(garbage);

        act.Should().NotThrow();
        dir.Should().BeNull();
    }

    [Fact]
    public void EmptyStream_ReturnsNull_WithoutThrowing()
    {
        using var empty = new MemoryStream([]);

        string? dir = null;
        var act = () => dir = EmbeddedContentExtractor.TryExtract(empty);

        act.Should().NotThrow();
        dir.Should().BeNull();
    }
}
