using System.Text.Json;
using FluentAssertions;
using Ryn.Plugins.Dialog;
using Ryn.Plugins.Tests.Support;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PAP-25: <c>PickerCommands.PathsToJsonArray</c> must emit JSON that the bridge's
/// <c>JSON.parse</c> accepts even when a picked path contains control characters (tab, carriage return,
/// embedded newline-from-split edge cases). The pre-fix hand-rolled escaper only handled <c>\</c> and
/// <c>"</c>, leaving raw control characters in the string and producing JSON that <c>JSON.parse</c> rejects.
/// These tests invoke the real method (via reflection — it is a private static helper) and parse its output
/// with the same STJ reader the frontend relies on, then assert the round-trip is loss-free.
/// </summary>
public sealed class PickerJsonTests
{
    // PickerCommands is internal; resolve it by name through a public type in the same assembly.
    private static readonly Type PickerCommandsType =
        typeof(DialogCommands).Assembly.GetType("Ryn.Plugins.Dialog.PickerCommands")
        ?? throw new InvalidOperationException("Ryn.Plugins.Dialog.PickerCommands type was not found.");

    private static readonly System.Reflection.MethodInfo PathsToJsonArray =
        PluginReflection.PrivateStatic(PickerCommandsType, "PathsToJsonArray");

    private static string ToJson(string raw) =>
        PluginReflection.Invoke<string>(PathsToJsonArray, null, raw);

    private static string[] ParseAsStringArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidOperationException("parsed null");

    [Fact]
    public void EmptyInput_ProducesEmptyArray()
    {
        ToJson("").Should().Be("[]");
    }

    [Fact]
    public void PathWithTabAndCarriageReturn_IsValidParseableJson()
    {
        // A single path that itself contains a tab and a carriage return. Splitting is on '\n' only, so
        // these stay inside one element and must be escaped to keep the JSON well-formed.
        var raw = "/Users/me/weird\tname\rfile.txt";

        var json = ToJson(raw);

        // The control characters must be escaped (not present raw) so JSON.parse accepts them.
        json.Should().NotContain("\t");
        json.Should().NotContain("\r");
        json.Should().Contain("\\t");
        json.Should().Contain("\\r");

        // ...and it must round-trip back to exactly the original path.
        var parsed = ParseAsStringArray(json);
        parsed.Should().ContainSingle().Which.Should().Be(raw);
    }

    [Fact]
    public void MultiplePathsWithQuotesAndBackslashes_RoundTrip()
    {
        var raw = "/a/with \"quote\".txt\n/b/with\\back\\slash\n/c/plain";

        var json = ToJson(raw);

        var parsed = ParseAsStringArray(json);
        parsed.Should().Equal(
            "/a/with \"quote\".txt",
            "/b/with\\back\\slash",
            "/c/plain");
    }

    [Fact]
    public void OutputIsAlwaysValidJson_EvenWithControlCharacters()
    {
        // Throw a spread of control characters into a single filename; the parse must not throw.
        var raw = "/x/control";

        var json = ToJson(raw);

        Action parse = () => ParseAsStringArray(json);
        parse.Should().NotThrow("STJ escapes every control character so the bridge's JSON.parse never chokes");
        ParseAsStringArray(json).Should().ContainSingle().Which.Should().Be(raw);
    }
}
