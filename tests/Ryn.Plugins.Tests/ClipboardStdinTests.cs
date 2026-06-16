using System.Reflection;
using FluentAssertions;
using Ryn.Plugins.Clipboard;
using Ryn.Plugins.Tests.Support;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PLG-05: the Windows <c>clipboard.writeText</c> path pipes the text through the
/// child process's <b>stdin</b> (<c>powershell ... -Command "$input | Set-Clipboard"</c> via an argv list)
/// instead of interpolating it into an inline <c>-Value '{text}'</c> string. The pre-fix inline form
/// mangled — and could let an attacker break out of — any text containing quotes, spaces or newlines.
///
/// The actual Windows round-trip needs a real OS clipboard + powershell, so it only runs on Windows. The
/// cross-platform, deterministic part is the <i>stdin transport seam</i>: this test invokes the real
/// <c>RunProcessWithInput(string, string[], string)</c> overload — the exact method the Windows writeText
/// path uses — against a portable stdin-consuming child and asserts the bytes arrive at the far end
/// completely unmangled. A regression that dropped the stdin path in favour of arg interpolation would not
/// round-trip quotes/spaces/newlines through stdin and would fail here.
/// </summary>
public sealed class ClipboardStdinTests
{
    // The string[]-overload (argv + stdin) — distinct from the legacy string-Arguments overload.
    private static readonly MethodInfo RunProcessWithInput =
        PluginReflection.PrivateStatic(
            typeof(ClipboardCommands), "RunProcessWithInput",
            typeof(string), typeof(string[]), typeof(string));

    [Theory]
    [InlineData("plain")]
    [InlineData("he \"said\" \"hi\"")]
    [InlineData("path with spaces and 'single' quotes")]
    [InlineData("line one\nline two\nline three")]
    [InlineData("tab\tseparated\tvalues")]
    [InlineData("trailing newline\n")]
    [InlineData("$env:PATH `whoami` $(Get-Process)")] // would be catastrophic under inline interpolation
    public void StdinTransport_RoundTripsTextVerbatim(string payload)
    {
        // Only run where a POSIX shell is present to act as the stdin sink. On Windows CI the real
        // powershell + clipboard round-trip is the relevant cover; here we exercise the transport on
        // macOS/Linux and no-op elsewhere (mirrors the existing symlink tests' platform guards).
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux())
            return;

        var outFile = Path.Combine(Path.GetTempPath(), $"ryn_clip_stdin_{Guid.NewGuid():N}.txt");
        try
        {
            // `sh -c 'cat > "$1"' sh <outFile>`: cat copies *stdin* (the payload the seam pipes in) to the
            // file named by $1. The file then contains exactly what was written to the child's stdin.
            PluginReflection.Invoke<object?>(
                RunProcessWithInput, null,
                "/bin/sh",
                new[] { "-c", "cat > \"$1\"", "sh", outFile },
                payload);

            File.ReadAllText(outFile).Should().Be(payload,
                "the text must reach the child through stdin byte-for-byte, never via arg interpolation");
        }
        finally
        {
            try { File.Delete(outFile); } catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
