using System.Reflection;
using FluentAssertions;
using Ryn.Plugins.Updater;
using Ryn.Plugins.Tests.Support;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for SUP-03: SemVer comparison must order prereleases correctly
/// (<c>1.2.3-rc1 &lt; 1.2.3-rc2 &lt; 1.2.3</c>) instead of the pre-fix <see cref="Version"/>-based parse
/// that discarded the <c>-prerelease</c> suffix and collapsed every prerelease of a base version to equal.
/// It also locks the downgrade-floor rule: a prerelease tag is never recorded as the monotonic floor under
/// the default stable-only channel, so a published <c>rc</c> can never block its matching final release.
///
/// The <c>SemVer</c> type is a private nested type and the floor helpers are private instance methods, so
/// both are reached via reflection and exercised as the real implementation; a regression in either fails
/// here.
/// </summary>
public sealed class UpdaterSemVerTests
{
    private static readonly Type SemVer =
        typeof(UpdaterService).GetNestedType("SemVer", BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("UpdaterService.SemVer nested type was not found.");

    private static readonly MethodInfo ParseMethod =
        SemVer.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static)
        ?? throw new InvalidOperationException("SemVer.Parse was not found.");

    private static readonly MethodInfo CompareToMethod =
        SemVer.GetMethod("CompareTo", new[] { SemVer })
        ?? throw new InvalidOperationException("SemVer.CompareTo(SemVer) was not found.");

    private static object Parse(string version)
    {
        var parsed = ParseMethod.Invoke(null, new object?[] { version });
        parsed.Should().NotBeNull($"'{version}' must parse to a SemVer");
        return parsed!;
    }

    private static int Compare(string a, string b) =>
        (int)CompareToMethod.Invoke(Parse(a), new[] { Parse(b) })!;

    [Fact]
    public void PrereleaseOrdering_DoesNotCollapseEqual()
    {
        // The core SUP-03 invariant: rc1 < rc2 < final, strictly increasing — never all-equal.
        Compare("1.2.3-rc1", "1.2.3-rc2").Should().BeNegative();
        Compare("1.2.3-rc2", "1.2.3").Should().BeNegative();
        Compare("1.2.3-rc1", "1.2.3").Should().BeNegative();

        // ...and the reverse relations hold.
        Compare("1.2.3-rc2", "1.2.3-rc1").Should().BePositive();
        Compare("1.2.3", "1.2.3-rc2").Should().BePositive();
    }

    [Fact]
    public void ReleaseOutranksItsOwnPrereleases()
    {
        // A version WITHOUT a prerelease has higher precedence than the same core WITH one (SemVer §11).
        Compare("1.2.3", "1.2.3-rc99").Should().BePositive();
        Compare("1.2.3-rc99", "1.2.3").Should().BeNegative();
    }

    [Theory]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.2.3-rc1", "1.2.3-rc1", 0)]
    [InlineData("1.2.4", "1.2.3", 1)]
    [InlineData("2.0.0", "1.9.9", 1)]
    [InlineData("1.2.0", "1.2.0-rc.1", 1)]
    [InlineData("1.2.0-rc.2", "1.2.0-rc.10", -1)] // numeric identifiers compare numerically, not lexically
    public void CoreAndNumericIdentifierOrdering(string a, string b, int expectedSign)
    {
        Math.Sign(Compare(a, b)).Should().Be(expectedSign);
    }

    [Fact]
    public void PrereleaseFloor_DoesNotBlockTheFinal_UnderStableOnly()
    {
        // Drive the real floor persistence with an isolated VersionFloorPath so the test is deterministic
        // and writes nothing to the user's app-data. Under the default stable-only channel, persisting a
        // prerelease must be a no-op (no floor file), so the matching final is never gated by an rc.
        var floorPath = Path.Combine(Path.GetTempPath(), $"ryn_floor_{Guid.NewGuid():N}.floor");
        var options = new UpdaterOptions
        {
            GitHubOwner = "o",
            GitHubRepo = "r",
            VersionFloorPath = floorPath,
            AllowPrerelease = false,
        };

        using var service = NewService(options, lifetime: null);
        var persist = PluginReflection.PrivateInstance(typeof(UpdaterService), "PersistVersionFloor");
        var read = PluginReflection.PrivateInstance(typeof(UpdaterService), "ReadVersionFloor");

        try
        {
            // Persisting an rc records no floor under stable-only.
            persist.Invoke(service, new object?[] { "1.2.3-rc1" });
            File.Exists(floorPath).Should().BeFalse("a prerelease must not be recorded as the floor when prereleases are off");
            read.Invoke(service, null).Should().BeNull();

            // The final is recorded normally — and was never blocked by the rc above.
            persist.Invoke(service, new object?[] { "1.2.3" });
            File.Exists(floorPath).Should().BeTrue();
            read.Invoke(service, null).Should().NotBeNull();
            read.Invoke(service, null)!.ToString().Should().Be("1.2.3");
        }
        finally
        {
            try { File.Delete(floorPath); } catch (IOException) { /* cleanup */ }
        }
    }

    private static UpdaterService NewService(UpdaterOptions options, Ryn.Core.IRynApplicationLifetime? lifetime)
    {
        var ctor = typeof(UpdaterService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(UpdaterOptions), typeof(Ryn.Core.IRynApplicationLifetime) }, null)
            ?? throw new InvalidOperationException("internal UpdaterService(options, lifetime) ctor not found.");
        return (UpdaterService)ctor.Invoke(new object?[] { options, lifetime });
    }
}
