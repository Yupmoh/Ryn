using FluentAssertions;
using Ryn.Plugins.FileSystem;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Cover for the leading <c>~</c> / <c>$HOME</c> expansion in <see cref="PathValidator"/> (a FOLLOWUPS
/// residual). Expansion is a textual convenience applied before canonicalization, so it must (a) expand the
/// documented leading forms, (b) leave <c>~user</c> / <c>$HOMER</c> / mid-path tildes literal, and (c) NEVER
/// widen scope — an expanded path is still checked against AllowedPaths. The ExpandHome cases run as a pure
/// function; the scope cases drive the real validator and a real round-trip through <see cref="FileSystemCommands"/>.
/// </summary>
public sealed class PathValidatorHomeExpansionTests : IDisposable
{
    private static readonly string Home = ResolveHome();

    private readonly string _tempScope;
    private string? _homeTemp;

    public PathValidatorHomeExpansionTests()
    {
        _tempScope = Path.Combine(Path.GetTempPath(), $"ryn-scope-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempScope);
    }

    [Fact]
    public void ExpandsLeadingTildeAndHomeForms()
    {
        Home.Should().NotBeNullOrEmpty("the test host must have a home directory");

        PathValidator.ExpandHome("~").Should().Be(Home);
        PathValidator.ExpandHome("~/docs/a.txt").Should().Be(Home + "/docs/a.txt");
        PathValidator.ExpandHome("$HOME").Should().Be(Home);
        PathValidator.ExpandHome("$HOME/docs/a.txt").Should().Be(Home + "/docs/a.txt");
        PathValidator.ExpandHome("${HOME}").Should().Be(Home);
        PathValidator.ExpandHome("${HOME}/docs").Should().Be(Home + "/docs");
    }

    // ~user is another user's home (unresolved here); $HOMER is a different variable; a ~ that is not the
    // first segment carries no home meaning. All must pass through untouched.
    [Theory]
    [InlineData("~user")]
    [InlineData("~user/secrets")]
    [InlineData("$HOMER")]
    [InlineData("$HOMER/x")]
    [InlineData("/etc/~/passwd")]
    [InlineData("./~/relative")]
    [InlineData("not~home")]
    [InlineData("foo$HOME")]
    [InlineData("")]
    public void LeavesNonLeadingOrForeignTokensLiteral(string input)
    {
        PathValidator.ExpandHome(input).Should().Be(input);
    }

    [Fact]
    public void TildePath_WithinAllowedHome_Resolves()
    {
        var validator = new PathValidator(new FileSystemOptions { AllowedPaths = [Home] });
        var name = $"ryn-home-xpand-{Guid.NewGuid():N}.txt";

        var resolved = validator.Resolve("~/" + name);

        // Resolving ~/name with home in scope yields exactly the canonical home-relative path; no file needs
        // to exist (Canonicalize handles a non-existent leaf).
        resolved.Should().Be(PathValidator.Canonicalize(Path.Combine(Home, name)));
    }

    [Fact]
    public void TildePath_OutsideAllowedScope_IsDenied()
    {
        // Scope is a temp dir that is NOT the home directory, so a ~-expanded path escapes it and is denied.
        // Expansion is purely textual: it must not grant access the literal home path would not.
        var validator = new PathValidator(new FileSystemOptions { AllowedPaths = [_tempScope] });

        var act = () => validator.Resolve("~/anything.txt");

        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void FileSystemCommands_HonorTildeForReadAndWrite()
    {
        // A throwaway directory under the real home so the ~-relative path has a safe place to land.
        var dirName = $".ryn-test-{Guid.NewGuid():N}";
        _homeTemp = Path.Combine(Home, dirName);
        Directory.CreateDirectory(_homeTemp);

        var fs = new FileSystemCommands(new PathValidator(new FileSystemOptions { AllowedPaths = [_homeTemp] }));
        var rel = $"~/{dirName}/note.txt";

        fs.WriteTextFile(rel, "hi");
        File.Exists(Path.Combine(_homeTemp, "note.txt")).Should().BeTrue();
        fs.ReadTextFile(rel).Should().Be("hi");
    }

    private static string ResolveHome()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(home) ? Environment.GetEnvironmentVariable("HOME") ?? string.Empty : home;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempScope, true); } catch (IOException) { /* cleanup */ }
        if (_homeTemp is not null)
        {
            try { Directory.Delete(_homeTemp, true); } catch (IOException) { /* cleanup */ }
        }
    }
}
