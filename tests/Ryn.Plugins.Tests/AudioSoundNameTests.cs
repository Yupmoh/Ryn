using System.Reflection;
using FluentAssertions;
using Ryn.Plugins.Tests.Support;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PAP-18: <c>MacOsAudioBackend.PlaySystem</c> interpolates the JS-supplied sound
/// <c>name</c> into <c>/System/Library/Sounds/{name}.aiff</c>, so before interpolating it must reject any
/// name carrying a path separator or <c>..</c> traversal. <c>IsSafeSystemSoundName</c> is that guard. The
/// method is an internal static on an internal type, so it is reached via reflection (no source-visibility
/// change) and exercised directly — a regression that widened the allowed character set or dropped the
/// guard would fail these assertions on any platform (the check is pure string logic).
/// </summary>
public sealed class AudioSoundNameTests
{
    private static readonly MethodInfo IsSafeSystemSoundName = ResolveGuard();

    private static MethodInfo ResolveGuard()
    {
        var backend = typeof(Ryn.Plugins.Audio.AudioService).Assembly
            .GetType("Ryn.Plugins.Audio.Backends.MacOsAudioBackend")
            ?? throw new InvalidOperationException("MacOsAudioBackend type was not found.");
        return PluginReflection.PrivateStatic(backend, "IsSafeSystemSoundName");
    }

    private static bool IsSafe(string name) =>
        PluginReflection.Invoke<bool>(IsSafeSystemSoundName, null, name);

    [Theory]
    [InlineData("Glass")]
    [InlineData("Ping")]
    [InlineData("Funk")]
    [InlineData("Sosumi")]
    [InlineData("Tink_2")]
    [InlineData("my-sound 1")]
    public void LegitimateSystemSoundNames_AreAccepted(string name)
    {
        IsSafe(name).Should().BeTrue();
    }

    [Theory]
    [InlineData("../x")]
    [InlineData("../../etc/foo")]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("a/b")]
    [InlineData("a\\b")]
    [InlineData("/absolute")]
    [InlineData("dir/../escape")]
    [InlineData("name\0nul")]
    [InlineData("")]
    public void TraversalOrSeparatorNames_AreRejected(string name)
    {
        IsSafe(name).Should().BeFalse(
            "a name with a separator, traversal, NUL or other metacharacter must never be interpolated into the sounds path");
    }

    [Fact]
    public void NullName_IsRejected()
    {
        // string.IsNullOrEmpty short-circuits null to a rejection before any iteration.
        IsSafe(null!).Should().BeFalse();
    }
}
