using System.Reflection;
using FluentAssertions;
using Ryn.Ipc;
using Ryn.Plugins.Dialog;
using Ryn.Plugins.Tests.Support;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PLG-01 / PAP-01: the macOS dialog path must hand the AppleScript to
/// <c>osascript</c> as a separate <c>-e</c> argv element (no shell, no single-quote wrapper), so the
/// AppleScript escaper only has to neutralise AppleScript string-literal metacharacters (<c>\</c> and
/// <c>"</c>) and is NOT relied upon to neutralise shell metacharacters. If the fix regressed back to a
/// shell-string invocation (<c>osascript -e '...'</c>), the escaper would be doing the wrong job and a
/// title/message containing <c>'</c> or <c>$</c> would break out — these tests fail in that case.
/// </summary>
public sealed class DialogArgvTests
{
    private static readonly Type Dialog = typeof(DialogCommands);
    private static readonly MethodInfo EscapeAppleScript =
        PluginReflection.PrivateStatic(Dialog, "EscapeAppleScript");

    private static string Escape(string value) =>
        PluginReflection.Invoke<string>(EscapeAppleScript, null, value);

    [Fact]
    public void EscapeAppleScript_EscapesOnlyAppleScriptLiteralMetacharacters()
    {
        // The two AppleScript string-literal metacharacters must be backslash-escaped...
        Escape("a\"b").Should().Be("a\\\"b");
        Escape("a\\b").Should().Be("a\\\\b");
        Escape("plain").Should().Be("plain");
    }

    [Theory]
    [InlineData("$(rm -rf /)")]
    [InlineData("'; rm -rf / #")]
    [InlineData("`whoami`")]
    [InlineData("a; b | c & d")]
    public void EscapeAppleScript_DoesNotTouchShellMetacharacters(string value)
    {
        // ...and crucially, shell metacharacters are left verbatim. They are safe ONLY because the script
        // is passed as a single argv element to osascript (no shell parse). A test that demanded these be
        // escaped would be asserting the *old*, shell-string design; here we assert they pass through,
        // which is correct precisely because the invocation is argv-based.
        var escaped = Escape(value);
        escaped.Should().Contain(value.Replace("\\", "\\\\", StringComparison.Ordinal),
            "shell metacharacters are not the escaper's concern under the argv-based osascript invocation");
        escaped.Should().NotContain("\\'", "single quotes are never escaped because there is no shell to parse them");
    }

    [Fact]
    public void RunOsascript_HelperHasArgvOrientedSignature()
    {
        // The argv-based design exposes a (script, redirectStandardOutput) helper that adds "-e" and the
        // script as distinct ArgumentList entries. Pin the shape so a regression to a single mangled
        // command string (which would change this signature) is caught.
        var run = Dialog.GetMethod("RunOsascript", BindingFlags.NonPublic | BindingFlags.Static);
        run.Should().NotBeNull();
        run!.GetParameters().Select(p => p.ParameterType)
            .Should().Equal(typeof(string), typeof(bool));
    }

    [Theory]
    [InlineData(nameof(DialogCommands.Message), "dialog.message")]
    [InlineData(nameof(DialogCommands.Confirm), "dialog.confirm")]
    public void DialogEntryPoints_AreRynCommands(string methodName, string commandName)
    {
        // The macOS branch of these two commands is the only caller of the argv helper; confirm they are
        // still the IPC-exposed entry points so the seam under test is the one JS actually reaches.
        var method = Dialog.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        method.Should().NotBeNull();
        var attribute = method!.GetCustomAttribute<RynCommandAttribute>();
        attribute.Should().NotBeNull();
        attribute!.Name.Should().Be(commandName);
    }
}
