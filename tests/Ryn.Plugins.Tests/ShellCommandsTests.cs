using FluentAssertions;
using NSubstitute;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Shell;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class ShellCommandsTests
{
    private static ShellExecutionPolicy Policy(ShellOptions options) => new(options);

    private static ShellCommands Shell(ShellOptions options) => new(new ShellExecutionPolicy(options));

    [Fact]
    public void ScopedCommand_PermitsMatchingArgs_RejectsOthers()
    {
        // echo is scoped to exactly one literal argument "hello".
        var shell = Shell(new ShellOptions
        {
            CommandScopes = [new CommandScope("echo", [ArgRule.Literal("hello")])],
        });

        var ok = () => shell.Execute("echo", "[\"hello\"]");
        ok.Should().NotThrow();

        var wrongArg = () => shell.Execute("echo", "[\"goodbye\"]");
        wrongArg.Should().Throw<UnauthorizedAccessException>();

        var wrongCount = () => shell.Execute("echo", "[\"hello\", \"world\"]");
        wrongCount.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void ScopedCommand_RegexValidator_Enforced()
    {
        var shell = Shell(new ShellOptions
        {
            CommandScopes = [new CommandScope("echo", [ArgRule.Pattern("^[a-z]+$")])],
        });

        ((Action)(() => shell.Execute("echo", "[\"abc\"]"))).Should().NotThrow();
        ((Action)(() => shell.Execute("echo", "[\"abc123\"]"))).Should().Throw<UnauthorizedAccessException>();
        // injection attempt is just a non-matching argument, passed literally (never to a shell)
        ((Action)(() => shell.Execute("echo", "[\"$(rm -rf /)\"]"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_FailsClosed_WhenNotConfigured()
    {
        // An empty allowlist is the reachable production "nothing permitted" state.
        var shell = Shell(new ShellOptions { AllowedCommands = [] });
        ((Action)(() => shell.Execute("echo", "[]"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_RejectsFileScheme()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("file:///etc/passwd"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_RejectsBarePath()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("/Applications/Calculator.app"))).Should().Throw<UnauthorizedAccessException>();
        ((Action)(() => policy.ValidateOpenTarget("Calculator.app"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Open_AllowsHttpsByDefault()
    {
        var policy = Policy(new ShellOptions());
        ((Action)(() => policy.ValidateOpenTarget("https://example.com"))).Should().NotThrow();
        ((Action)(() => policy.ValidateOpenTarget("mailto:a@b.com"))).Should().NotThrow();
    }

    [Fact]
    public void Open_HonorsConfiguredSchemeAllowlist()
    {
        var policy = Policy(new ShellOptions { AllowedOpenSchemes = ["https"] });
        ((Action)(() => policy.ValidateOpenTarget("https://example.com"))).Should().NotThrow();
        ((Action)(() => policy.ValidateOpenTarget("mailto:a@b.com"))).Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_DeniedWhenAllowlistEmpty()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = [] });

        var act = () => shell.Execute("ls", "[]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_AllowedCommand_ReturnsOutput()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = ["echo"] });

        var result = shell.Execute("echo", "[\"hello\"]");
        result.Should().Contain("hello");
        result.Should().Contain("exitCode");
    }

    [Fact]
    public void Execute_DeniedCommand_Throws()
    {
        var shell = Shell(new ShellOptions { AllowedCommands = ["echo"] });

        var act = () => shell.Execute("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_HonorsTimeout_KillsAndThrows()
    {
        // A long-running command with a tiny timeout must be terminated and surfaced as a timeout
        // rather than hanging. On Windows `timeout` exits immediately when stdin is redirected (as it
        // is under Process), so it never blocks long enough — use `ping` as the long-runner instead.
        var shell = Shell(new ShellOptions
        {
            AllowedCommands = OperatingSystem.IsWindows() ? ["ping"] : ["sleep"],
            ExecuteTimeout = TimeSpan.FromMilliseconds(250),
        });

        var (command, args) = OperatingSystem.IsWindows()
            ? ("ping", "[\"-n\", \"30\", \"127.0.0.1\"]")
            : ("sleep", "[\"30\"]");
        var act = () => shell.Execute(command, args);
        act.Should().Throw<TimeoutException>();
    }

    [Fact]
    public void Spawn_RejectsDisallowedCommand()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView, policy);

        var act = () => commands.Spawn("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Kill_ReturnsErrorForUnknownPid()
    {
        var policy = Policy(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView, policy);

        var result = commands.Kill(99999);
        result.Should().BeFalse();
    }

    [Fact]
    public void FullPathAllowlist_DoesNotPermitBareInvocation()
    {
        using var fixture = new CommandFixture("mytool");

        var policy = Policy(new ShellOptions { AllowedCommands = [fixture.FullPath] });

        var act = () => policy.ValidateAndResolveCommand("mytool");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void BareAllowlist_ResolvesToConfigTimeCanonicalPath()
    {
        using var fixture = new CommandFixture("mytool");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fixture.Directory + (OperatingSystem.IsWindows() ? ";" : ":") + originalPath);
        try
        {
            var policy = Policy(new ShellOptions { AllowedCommands = ["mytool"] });

            var resolved = policy.ValidateAndResolveCommand("mytool");

            resolved.Should().NotBe("mytool");
            Path.IsPathRooted(resolved).Should().BeTrue();
            resolved.Should().Be(fixture.FullPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    [Fact]
    public void UnresolvedBareAllowlist_RejectsAtInvocation()
    {
        var policy = Policy(new ShellOptions
        {
            AllowedCommands = ["this_command_does_not_exist_anywhere_in_path"]
        });

        var act = () => policy.ValidateAndResolveCommand("this_command_does_not_exist_anywhere_in_path");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void FullPathAllowlist_PermitsExactPathInvocation()
    {
        using var fixture = new CommandFixture("mytool");

        var policy = Policy(new ShellOptions { AllowedCommands = [fixture.FullPath] });

        var resolved = policy.ValidateAndResolveCommand(fixture.FullPath);
        resolved.Should().Be(fixture.FullPath);
    }

    [Fact]
    public void BareAllowlist_CannotBeHijackedByLaterPathEntry()
    {
        using var legitimate = new CommandFixture("mytool");
        using var malicious = new CommandFixture("mytool");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var sep = OperatingSystem.IsWindows() ? ";" : ":";
        try
        {
            // Configure (i.e. construct the policy) with legitimate first in PATH
            Environment.SetEnvironmentVariable("PATH",
                legitimate.Directory + sep + originalPath);
            var policy = Policy(new ShellOptions { AllowedCommands = ["mytool"] });

            // Now swap PATH so malicious comes first
            Environment.SetEnvironmentVariable("PATH",
                malicious.Directory + sep + originalPath);

            // Resolution is pinned to construction time — malicious binary is ignored
            var resolved = policy.ValidateAndResolveCommand("mytool");
            resolved.Should().Be(legitimate.FullPath);
            resolved.Should().NotBe(malicious.FullPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
        }
    }

    private sealed class CommandFixture : IDisposable
    {
        public string Directory { get; }
        public string FullPath { get; }

        public CommandFixture(string name)
        {
            Directory = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());
            System.IO.Directory.CreateDirectory(Directory);

            var ext = OperatingSystem.IsWindows() ? ".exe" : "";
            FullPath = Path.GetFullPath(Path.Combine(Directory, name + ext));
            File.WriteAllBytes(FullPath, [0]);

            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(FullPath, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }

        public void Dispose()
        {
            try { System.IO.Directory.Delete(Directory, recursive: true); }
            catch (IOException) { /* cleanup best-effort */ }
        }
    }
}
