using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests.Internal;

/// <summary>
/// Regression tests for <see cref="RynPath.IsContainedIn"/> (PAP-23) — the single canonical path-containment
/// rule shared by the content-directory guard, the local server's traversal check, and the FileSystem
/// plugin's scope validation. The rule must be the fuller "exact-root OR child-with-separator" form so a
/// sibling-prefix (e.g. <c>/a</c> vs <c>/ab</c>) is NOT treated as contained, and it must honor whatever
/// case policy the caller passes (ordinal on Linux, ignore-case on macOS/Windows).
/// </summary>
public sealed class RynPathContainmentTests
{
    // Build absolute paths from segments using the host separator so the tests are valid on every OS.
    private static string P(params string[] segments)
    {
        var root = OperatingSystem.IsWindows() ? @"C:\" : "/";
        return Path.GetFullPath(Path.Combine(root, Path.Combine(segments)));
    }

    private static string WithTrailingSep(string path) => path + Path.DirectorySeparatorChar;

    [Fact]
    public void ExactRoot_IsContained()
    {
        var root = P("content");
        RynPath.IsContainedIn(root, root, StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void DirectChild_IsContained()
    {
        var root = P("content");
        var child = P("content", "index.html");
        RynPath.IsContainedIn(child, root, StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void DeepDescendant_IsContained()
    {
        var root = P("content");
        var child = P("content", "assets", "css", "site.css");
        RynPath.IsContainedIn(child, root, StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void SiblingPrefix_IsNotContained()
    {
        // The classic bare-StartsWith false positive: "/content-evil" starts with "/content" textually but is
        // a different directory. The trailing-separator rule must reject it.
        var root = P("content");
        var sibling = P("content-evil", "secret.txt");
        RynPath.IsContainedIn(sibling, root, StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void ShortSiblingPrefix_a_vs_ab_IsNotContained()
    {
        // "/a" must not contain "/ab" (the doc's explicit /a-vs-/ab case).
        RynPath.IsContainedIn(P("ab"), P("a"), StringComparison.Ordinal).Should().BeFalse();
        RynPath.IsContainedIn(P("ab", "x"), P("a"), StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void Ancestor_IsNotContainedInDescendant()
    {
        var root = P("content", "assets");
        var ancestor = P("content");
        RynPath.IsContainedIn(ancestor, root, StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void TrailingSeparatorOnRoot_IsTrimmedAndStillContained()
    {
        var root = P("content");
        var child = P("content", "app.js");
        RynPath.IsContainedIn(child, WithTrailingSep(root), StringComparison.Ordinal).Should().BeTrue();
    }

    [Fact]
    public void TrailingSeparatorOnBothExactRoot_IsContained()
    {
        var root = P("content");
        RynPath.IsContainedIn(WithTrailingSep(root), WithTrailingSep(root), StringComparison.Ordinal)
            .Should().BeTrue();
    }

    [Fact]
    public void TrailingSeparatorDoesNotTurnSiblingIntoChild()
    {
        var root = P("content");
        var sibling = P("content-evil");
        RynPath.IsContainedIn(WithTrailingSep(sibling), WithTrailingSep(root), StringComparison.Ordinal)
            .Should().BeFalse();
    }

    [Fact]
    public void Ordinal_CaseSensitive_DistinctCasingIsNotContained()
    {
        // Linux policy: "/Allowed" and "/allowed" are different scopes.
        var root = P("Allowed");
        var differentCase = P("allowed", "file.txt");
        RynPath.IsContainedIn(differentCase, root, StringComparison.Ordinal).Should().BeFalse();
    }

    [Fact]
    public void OrdinalIgnoreCase_DistinctCasingIsContained()
    {
        // macOS/Windows policy: case-insensitive volumes treat "/Allowed" and "/allowed" as the same scope.
        var root = P("Allowed");
        var differentCase = P("allowed", "file.txt");
        RynPath.IsContainedIn(differentCase, root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
    }

    [Fact]
    public void HostComparison_MatchesPlatformPolicy()
    {
        // The shared host policy must be ordinal on Linux and ignore-case on macOS/Windows.
        var expected = OperatingSystem.IsLinux()
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;
        RynPath.HostComparison.Should().Be(expected);

        var root = P("Scope");
        var differentCase = P("scope", "child.txt");
        // Under the host policy, distinct casing is contained iff the host is case-insensitive.
        RynPath.IsContainedIn(differentCase, root, RynPath.HostComparison)
            .Should().Be(!OperatingSystem.IsLinux());
    }

    [Theory]
    // candidate-relative, root-relative, ordinal-expected, ignorecase-expected
    [InlineData("content", "content", true, true)]                       // exact root
    [InlineData("content/index.html", "content", true, true)]            // child
    [InlineData("content-evil/x", "content", false, false)]              // sibling prefix
    [InlineData("ab", "a", false, false)]                                // short sibling prefix
    [InlineData("Content/x", "content", false, true)]                    // case differs only
    public void Parameterized_Containment(string candidateRel, string rootRel, bool ordinalExpected, bool ignoreCaseExpected)
    {
        ArgumentNullException.ThrowIfNull(candidateRel);
        ArgumentNullException.ThrowIfNull(rootRel);
        var candidate = P(candidateRel.Split('/'));
        var root = P(rootRel.Split('/'));

        RynPath.IsContainedIn(candidate, root, StringComparison.Ordinal).Should().Be(ordinalExpected);
        RynPath.IsContainedIn(candidate, root, StringComparison.OrdinalIgnoreCase).Should().Be(ignoreCaseExpected);
    }
}
