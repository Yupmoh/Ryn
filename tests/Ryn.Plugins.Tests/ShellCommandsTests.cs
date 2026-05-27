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
        var commands = new ShellCommands(webView);

        var act = () => commands.Spawn("rm", "[\"-rf\", \"/\"]");
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void Kill_ReturnsErrorForUnknownPid()
    {
        ShellCommands.Configure(new ShellOptions { AllowedCommands = ["echo"] });
        var webView = Substitute.For<IRynWebView>();
        var commands = new ShellCommands(webView);

        var result = commands.Kill(99999);
        result.Should().Contain("\"success\":false");
        result.Should().Contain("No spawned process with pid 99999");
    }
}
