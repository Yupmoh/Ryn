using FluentAssertions;
using NSubstitute;
using Ryn.Core;
using Ryn.Plugins.Shell;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class ShellCommandsTests
{
    [Fact]
    public void Execute_DeniedWhenAllowlistEmpty()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = [] });

        var act = () => ShellCommands.Execute("ls", "[]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Execute_AllowedCommand_ReturnsOutput()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });

        var result = ShellCommands.Execute("echo", "[\"hello\"]");
        result.Should().Contain("hello");
        result.Should().Contain("exitCode");
    }

    [Fact]
    public void Execute_DeniedCommand_Throws()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });

        var act = () => ShellCommands.Execute("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Spawn_RejectsDisallowedCommand()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView);

        var act = () => commands.Spawn("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Kill_ReturnsErrorForUnknownPid()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        using var commands = new SpawnCommands(webView);

        var result = commands.Kill(99999);
        result.Should().BeFalse();
    }

    [Fact]
    public void FullPathAllowlist_DoesNotPermitBareInvocation()
    {
        var echoPath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\cmd.exe"
            : "/bin/echo";

        ShellCommands.Configure(new ShellOptions { AllowedCommands = [echoPath] });

        var act = () => ShellCommands.ValidateAndResolveCommand("echo");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void BareAllowlist_ResolvesToConfigTimeCanonicalPath()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });

        var resolved = ShellCommands.ValidateAndResolveCommand("echo");

        resolved.Should().NotBe("echo");
        Path.IsPathRooted(resolved).Should().BeTrue();
        File.Exists(resolved).Should().BeTrue();
    }

    [Fact]
    public void UnresolvedBareAllowlist_RejectsAtInvocation()
    {
        ShellCommands.Configure(new ShellOptions
        {
            AllowedCommands = ["this_command_does_not_exist_anywhere_in_path"]
        });

        var act = () => ShellCommands.ValidateAndResolveCommand("this_command_does_not_exist_anywhere_in_path");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void FullPathAllowlist_PermitsExactPathInvocation()
    {
        var echoPath = OperatingSystem.IsWindows()
            ? @"C:\Windows\System32\cmd.exe"
            : "/bin/echo";

        if (!File.Exists(echoPath))
            return; // skip on platforms where the path doesn't exist

        ShellCommands.Configure(new ShellOptions { AllowedCommands = [echoPath] });

        var resolved = ShellCommands.ValidateAndResolveCommand(echoPath);
        resolved.Should().Be(Path.GetFullPath(echoPath));
    }
}
