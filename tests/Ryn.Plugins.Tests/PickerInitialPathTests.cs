using FluentAssertions;
using Ryn.Plugins.Dialog;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// The picker initial path is best-effort: <c>~</c> expands, a file resolves to its directory, and
/// empty/relative/nonexistent paths yield null so the <c>default location</c> clause is omitted rather
/// than interpolated (osascript rejects e.g. <c>default location "~"</c> with error -1700, which used to
/// kill the dialog and surface as an empty string indistinguishable from user-cancel).
/// </summary>
public sealed class PickerInitialPathTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("relative/path")]
    [InlineData("./here")]
    [InlineData("/definitely/not/an/existing/path/9f8e7d")]
    public void NormalizeInitialPath_RejectsUnusablePaths(string? input)
        => PickerCommands.NormalizeInitialPath(input).Should().BeNull();

    [Fact]
    public void NormalizeInitialPath_ExpandsBareTildeToTheUserProfile()
        => PickerCommands.NormalizeInitialPath("~").Should()
            .Be(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    [Fact]
    public void NormalizeInitialPath_ExpandsTildePrefixedPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // Use a directory that exists in any home: the profile root itself via "~/.".
        PickerCommands.NormalizeInitialPath("~/.").Should().Be(Path.Combine(home, "."));
    }

    [Fact]
    public void NormalizeInitialPath_KeepsExistingAbsoluteDirectories()
    {
        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        PickerCommands.NormalizeInitialPath(dir).Should().Be(dir);
    }

    [Fact]
    public void NormalizeInitialPath_ResolvesAFileToItsDirectory()
    {
        var file = Path.Combine(Path.GetTempPath(), $"picker-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(file, "x");
        try
        {
            PickerCommands.NormalizeInitialPath(file).Should().Be(Path.GetDirectoryName(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void DefaultLocationClause_IsOmittedForUnusablePaths()
    {
        PickerCommands.DefaultLocationClause("~this-is-not-home").Should().BeEmpty();
        PickerCommands.DefaultLocationClause("").Should().BeEmpty();
        PickerCommands.DefaultLocationClause("relative").Should().BeEmpty();
    }

    [Fact]
    public void DefaultLocationClause_EmitsTheEscapedNormalizedPath()
    {
        // Windows temp paths contain backslashes, which the AppleScript escaping doubles — mirror it.
        var dir = Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar);
        var escaped = dir.Replace("\\", "\\\\", StringComparison.Ordinal);
        PickerCommands.DefaultLocationClause(dir).Should().Be($" default location \"{escaped}\"");
    }
}
