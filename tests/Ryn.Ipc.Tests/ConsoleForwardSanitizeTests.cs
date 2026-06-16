using FluentAssertions;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Regression coverage for finding IPC-06: the page controls both the level and the message forwarded
/// through the __ryn.console bridge, so a forwarded string must never inject extra log lines or forge
/// structured log entries. <see cref="ConsoleForwardCommands.Sanitize"/> (reached via InternalsVisibleTo)
/// neutralizes CR/LF and every other control character and truncates oversized messages.
/// </summary>
public sealed class ConsoleForwardSanitizeTests
{
    private const int MaxMessageLength = 2048;
    private const char ControlReplacement = '\uFFFD'; // REPLACEMENT CHARACTER
    private const char TruncationMarker = '\u2026';   // HORIZONTAL ELLIPSIS

    [Fact]
    public void Sanitize_CrLf_AreNeutralized_NoNewlineInjection()
    {
        var injected = "real line\r\nINJECTED: fake log entry";

        var safe = ConsoleForwardCommands.Sanitize(injected);

        // No newline survives, so the output is a single log line.
        safe.Should().NotContain("\r");
        safe.Should().NotContain("\n");
        safe.Should().NotContain(Environment.NewLine);
        // The textual content is preserved with the CR/LF replaced by visible placeholders.
        safe.Should().Be($"real line{ControlReplacement}{ControlReplacement}INJECTED: fake log entry");
    }

    [Theory]
    [InlineData('\u0000')] // NUL
    [InlineData('\u0007')] // BEL
    [InlineData('\u001B')] // ESC (ANSI escape vector)
    [InlineData('\u007F')] // DEL
    [InlineData('\u0085')] // NEL (C1)
    [InlineData('\u009B')] // CSI (C1)
    public void Sanitize_ControlChars_AreReplaced(char control)
    {
        var safe = ConsoleForwardCommands.Sanitize($"a{control}b");

        safe.Should().Be($"a{ControlReplacement}b");
        safe.Should().NotContain(control.ToString());
    }

    [Fact]
    public void Sanitize_Tab_IsFlattenedToSpace()
    {
        var safe = ConsoleForwardCommands.Sanitize("col1\tcol2");

        safe.Should().Be("col1 col2");
    }

    [Fact]
    public void Sanitize_OversizeMessage_IsTruncatedWithMarker()
    {
        var huge = new string('x', MaxMessageLength + 5000);

        var safe = ConsoleForwardCommands.Sanitize(huge);

        // Capped at MaxMessageLength characters plus the single truncation marker.
        safe.Length.Should().Be(MaxMessageLength + 1);
        safe.Should().EndWith(TruncationMarker.ToString());
        safe[..MaxMessageLength].Should().Be(new string('x', MaxMessageLength));
    }

    [Fact]
    public void Sanitize_ExactlyMaxLength_IsNotTruncated()
    {
        var exact = new string('y', MaxMessageLength);

        var safe = ConsoleForwardCommands.Sanitize(exact);

        safe.Length.Should().Be(MaxMessageLength);
        safe.Should().NotContain(TruncationMarker.ToString());
    }

    [Fact]
    public void Sanitize_TruncationDropsInjectionPastTheCap()
    {
        // A newline-injection attempt that lives entirely past the cap is dropped by truncation, never
        // logged. Everything before the cap is still sanitized.
        var payload = new string('a', MaxMessageLength) + "\r\nINJECTED";

        var safe = ConsoleForwardCommands.Sanitize(payload);

        safe.Should().NotContain("INJECTED");
        safe.Should().NotContain("\n");
        safe.Should().EndWith(TruncationMarker.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Sanitize_NullOrEmpty_ReturnsEmpty(string? input)
    {
        ConsoleForwardCommands.Sanitize(input).Should().BeEmpty();
    }

    [Fact]
    public void Sanitize_OrdinaryText_IsUnchanged()
    {
        const string clean = "Hello, world! 123 ☃ こんにちは";

        ConsoleForwardCommands.Sanitize(clean).Should().Be(clean);
    }
}
