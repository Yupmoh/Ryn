using FluentAssertions;
using Ryn.Core;
using Ryn.Plugins.FileSystem;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class FileSystemCommandsTests : IDisposable
{
    private readonly string _testDir;
    private readonly FileSystemCommands _fs;

    public FileSystemCommandsTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"ryn-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
        _fs = new FileSystemCommands(new PathValidator(new FileSystemOptions { AllowedPaths = [_testDir] }));
    }

    [Fact]
    public void WriteAndReadTextFile_RoundTrips()
    {
        var path = Path.Combine(_testDir, "test.txt");
        _fs.WriteTextFile(path, "hello ryn");

        var content = _fs.ReadTextFile(path);
        content.Should().Be("hello ryn");
    }

    [Fact]
    public void Exists_ReturnsTrueForExistingFile()
    {
        var path = Path.Combine(_testDir, "exists.txt");
        File.WriteAllText(path, "x");

        _fs.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Exists_ReturnsFalseForMissing()
    {
        _fs.Exists(Path.Combine(_testDir, "nope.txt")).Should().BeFalse();
    }

    [Fact]
    public void MkDir_CreatesDirectory()
    {
        var path = Path.Combine(_testDir, "subdir");
        _fs.MkDir(path);

        Directory.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Remove_DeletesFile()
    {
        var path = Path.Combine(_testDir, "todelete.txt");
        File.WriteAllText(path, "x");

        _fs.Remove(path);
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void ReadDir_ReturnsJsonEntries()
    {
        File.WriteAllText(Path.Combine(_testDir, "a.txt"), "a");
        Directory.CreateDirectory(Path.Combine(_testDir, "sub"));

        var json = _fs.ReadDir(_testDir);
        json.Should().Contain("a.txt");
        json.Should().Contain("sub");
    }

    [Fact]
    public void Stat_ReturnsJsonInfo()
    {
        var path = Path.Combine(_testDir, "stat.txt");
        File.WriteAllText(path, "content");

        var json = _fs.Stat(path);
        json.Should().Contain("stat.txt");
        json.Should().Contain("\"isDirectory\":false");
    }

    [Fact]
    public void WriteAndReadFile_BinaryRoundTrips()
    {
        var path = Path.Combine(_testDir, "binary.bin");
        var bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(256);
        var base64 = Convert.ToBase64String(bytes);

        _fs.WriteFile(path, base64);

        var result = _fs.ReadFile(path);
        Convert.FromBase64String(result).Should().Equal(bytes);
    }

    [Fact]
    public void WriteFile_CreatesParentDirectories()
    {
        var path = Path.Combine(_testDir, "nested", "deep", "file.bin");
        var data = Convert.ToBase64String([0x01, 0x02, 0x03]);

        var resolved = _fs.WriteFile(path, data);

        File.Exists(resolved).Should().BeTrue();
        File.ReadAllBytes(resolved).Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public void PathTraversal_Rejected()
    {
        var act = () => _fs.ReadTextFile(Path.Combine(_testDir, "..", "..", "etc", "passwd"));
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void SymlinkEscape_ReadThroughLink_Rejected()
    {
        // A secret outside the allowed scope.
        var outsideDir = Path.Combine(Path.GetTempPath(), $"ryn-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);
        var secret = Path.Combine(outsideDir, "secret.txt");
        File.WriteAllText(secret, "top secret");

        // A symlink that lives *inside* the allowed scope but targets the secret outside it.
        var link = Path.Combine(_testDir, "link.txt");
        try
        {
            File.CreateSymbolicLink(link, secret);
        }
        catch (UnauthorizedAccessException)
        {
            return; // platform doesn't permit symlink creation in this environment
        }

        try
        {
            // Lexically the link is in-scope; its real target is not. Must be rejected.
            var act = () => _fs.ReadTextFile(link);
            act.Should().Throw<UnauthorizedAccessException>();
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void SymlinkEscape_WriteThroughLinkedDirectory_Rejected()
    {
        var outsideDir = Path.Combine(Path.GetTempPath(), $"ryn-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outsideDir);

        // A symlinked directory inside scope that points outside scope.
        var linkedDir = Path.Combine(_testDir, "escape");
        try
        {
            Directory.CreateSymbolicLink(linkedDir, outsideDir);
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        try
        {
            // Writing to a not-yet-existing file under a symlinked-out parent must be rejected.
            var act = () => _fs.WriteTextFile(Path.Combine(linkedDir, "planted.txt"), "x");
            act.Should().Throw<UnauthorizedAccessException>();
            File.Exists(Path.Combine(outsideDir, "planted.txt")).Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(outsideDir, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void MkDir_ReadGrant_IsRejected()
    {
        var grants = new FileAccessGrants();
        var token = grants.Grant(_testDir, FileAccessOperation.Read);
        var fs = new FileSystemCommands(new PathValidator(new FileSystemOptions(), grants));

        FluentActions.Invoking(() => fs.MkDir(token + "/created"))
            .Should().Throw<UnauthorizedAccessException>();
        Directory.Exists(Path.Combine(_testDir, "created")).Should().BeFalse();
    }

    [Fact]
    public void ReadFile_ExceedingSizeLimit_Rejected()
    {
        // A separate command instance with its own policy — no global state to reset afterwards.
        var limited = new FileSystemCommands(
            new PathValidator(new FileSystemOptions { AllowedPaths = [_testDir], MaxReadBytes = 16 }));

        var path = Path.Combine(_testDir, "big.bin");
        File.WriteAllBytes(path, new byte[64]);

        var act = () => limited.ReadFile(path);
        act.Should().Throw<UnauthorizedAccessException>().WithMessage("*read limit*");
    }

    public void Dispose()
    {
        try { Directory.Delete(_testDir, true); } catch (IOException) { /* cleanup */ }
    }
}
