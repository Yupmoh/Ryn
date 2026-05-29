using System;
using FluentAssertions;
using Xunit;

namespace Ryn.Ipc.Tests;

public sealed class CapabilityTests
{
    [Fact]
    public void AllowAll_PermitsEverything()
    {
        var caps = RynCapabilities.AllowAll();

        var act = () => caps.ThrowIfDenied("fs.readTextFile");
        act.Should().NotThrow();
    }

    [Fact]
    public void MissingPlugin_Denied()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>());

        var act = () => caps.ThrowIfDenied("fs.readTextFile");
        act.Should().Throw<RynCommandDeniedException>()
            .Where(e => e.Command == "fs.readTextFile");
    }

    [Fact]
    public void AllowTrue_PermitsAllCommands()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>
        {
            ["fs"] = new() { AllowAll = true },
        });

        var act = () => caps.ThrowIfDenied("fs.readTextFile");
        act.Should().NotThrow();
    }

    [Fact]
    public void AllowList_PermitsOnlyListed()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>
        {
            ["fs"] = new() { Allow = new HashSet<string> { "readTextFile", "exists" } },
        });

        var allowed = () => caps.ThrowIfDenied("fs.readTextFile");
        allowed.Should().NotThrow();

        var denied = () => caps.ThrowIfDenied("fs.remove");
        denied.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void DenyList_BlocksListed()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>
        {
            ["fs"] = new() { AllowAll = true, Deny = new HashSet<string> { "remove" } },
        });

        var allowed = () => caps.ThrowIfDenied("fs.readTextFile");
        allowed.Should().NotThrow();

        var denied = () => caps.ThrowIfDenied("fs.remove");
        denied.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void AllowAndDeny_DenyTakesPrecedence()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>
        {
            ["fs"] = new()
            {
                Allow = new HashSet<string> { "readTextFile", "remove" },
                Deny = new HashSet<string> { "remove" },
            },
        });

        var allowed = () => caps.ThrowIfDenied("fs.readTextFile");
        allowed.Should().NotThrow();

        var denied = () => caps.ThrowIfDenied("fs.remove");
        denied.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void NoPrefix_Denied()
    {
        var caps = RynCapabilities.FromRules(new Dictionary<string, CapabilityRule>());

        var act = () => caps.ThrowIfDenied("commandWithoutDot");
        act.Should().Throw<RynCommandDeniedException>()
            .WithMessage("*no plugin prefix*");
    }

    [Fact]
    public void LoadFromJson_ParsesCorrectly()
    {
        var json = """
        {
          "capabilities": {
            "fs": {
              "allow": ["readTextFile", "exists"],
              "deny": ["remove"]
            },
            "dialog": true,
            "shell": false
          }
        }
        """;

        var caps = RynCapabilitiesLoader.Parse(json);

        // fs.readTextFile — allowed
        var act1 = () => caps.ThrowIfDenied("fs.readTextFile");
        act1.Should().NotThrow();

        // fs.remove — denied
        var act2 = () => caps.ThrowIfDenied("fs.remove");
        act2.Should().Throw<RynCommandDeniedException>();

        // fs.writeTextFile — not in allow list
        var act3 = () => caps.ThrowIfDenied("fs.writeTextFile");
        act3.Should().Throw<RynCommandDeniedException>();

        // dialog.message — allowed (true = all)
        var act4 = () => caps.ThrowIfDenied("dialog.message");
        act4.Should().NotThrow();

        // shell.execute — denied (false = none)
        var act5 = () => caps.ThrowIfDenied("shell.execute");
        act5.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void MissingCapabilitiesSection_FailsClosed_InRelease()
    {
        // A present-but-empty config (no "capabilities" key) is treated as a misconfiguration and
        // fails closed: every command is denied. This prevents a mis-deployed app from silently
        // shipping with all commands open. (Parse defaults to the release/fail-closed behavior.)
        var caps = RynCapabilitiesLoader.Parse("{}");

        caps.IsEnforced.Should().BeTrue();
        var act = () => caps.ThrowIfDenied("anything.goes");
        act.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void MissingCapabilitiesSection_AllowsAll_WhenPermissiveOptIn()
    {
        // Dev opt-in keeps the old convenience behavior.
        var caps = RynCapabilitiesLoader.Parse("{}", permissiveWhenUnconfigured: true);

        caps.IsEnforced.Should().BeFalse();
        var act = () => caps.ThrowIfDenied("anything.goes");
        act.Should().NotThrow();
    }

    [Fact]
    public void DenyAll_DeniesEverything()
    {
        var caps = RynCapabilities.DenyAll();

        caps.IsEnforced.Should().BeTrue();
        var act = () => caps.ThrowIfDenied("fs.readTextFile");
        act.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void InternalCommands_OnlyKnownNamesBypass()
    {
        var caps = RynCapabilities.DenyAll();

        // Known framework internals bypass enforcement…
        ((Action)(() => caps.ThrowIfDenied("__ryn.console"))).Should().NotThrow();
        ((Action)(() => caps.ThrowIfDenied("__ryn.fileDrop"))).Should().NotThrow();

        // …but an arbitrary/spoofed "__ryn.*" name does NOT get a free pass.
        ((Action)(() => caps.ThrowIfDenied("__ryn.evil"))).Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void DenyOnlyRule_AllowsEverythingExceptDenied()
    {
        var json = """
        {
          "capabilities": {
            "fs": {
              "deny": ["remove", "writeTextFile"]
            }
          }
        }
        """;

        var caps = RynCapabilitiesLoader.Parse(json);

        var allowed = () => caps.ThrowIfDenied("fs.readTextFile");
        allowed.Should().NotThrow();

        var denied = () => caps.ThrowIfDenied("fs.remove");
        denied.Should().Throw<RynCommandDeniedException>();
    }

    [Fact]
    public void ParsesScope_PathsAndCommands()
    {
        var json = """
        {
          "capabilities": {
            "fs": {
              "allow": ["readTextFile", "writeTextFile"],
              "scope": ["/tmp/workspace", "$APP_DATA"]
            },
            "shell": {
              "allow": ["execute"],
              "commands": ["echo", "date", "git"]
            }
          }
        }
        """;

        var caps = RynCapabilitiesLoader.Parse(json);

        var fsScope = caps.GetScope("fs");
        fsScope.Should().NotBeNull();
        fsScope!.AllowedPaths.Should().NotBeNull();
        fsScope.AllowedPaths!.Should().HaveCount(2);
        fsScope.AllowedPaths![0].Should().Be(Path.GetFullPath("/tmp/workspace"));
        fsScope.AllowedPaths![1].Should().Be(Path.GetFullPath(AppContext.BaseDirectory));
        fsScope.HasPathPolicy.Should().BeTrue();
        fsScope.HasCommandPolicy.Should().BeFalse();

        var shellScope = caps.GetScope("shell");
        shellScope.Should().NotBeNull();
        shellScope!.AllowedCommands.Should().NotBeNull();
        shellScope.AllowedCommands!.Should().BeEquivalentTo(["echo", "date", "git"]);
        shellScope.HasCommandPolicy.Should().BeTrue();
        shellScope.HasPathPolicy.Should().BeFalse();
    }

    [Fact]
    public void GetScope_ReturnsNull_WhenNoScope()
    {
        var json = """
        {
          "capabilities": {
            "fs": {
              "allow": ["readTextFile"]
            },
            "dialog": true
          }
        }
        """;

        var caps = RynCapabilitiesLoader.Parse(json);

        caps.GetScope("fs").Should().BeNull();
        caps.GetScope("dialog").Should().BeNull();
        caps.GetScope("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetScope_ReturnsNull_WhenNotEnforced()
    {
        var caps = RynCapabilities.AllowAll();

        caps.GetScope("fs").Should().BeNull();
        caps.GetScope("shell").Should().BeNull();
    }

    [Fact]
    public void ParsesScope_AppDataVariable_ResolvesToBaseDirectory()
    {
        var resolved = RynCapabilitiesLoader.ResolveScopePath("$APP_DATA");
        resolved.Should().Be(Path.GetFullPath(AppContext.BaseDirectory));
    }

    [Fact]
    public void ParsesScope_AppDataVariable_WithSubpath()
    {
        var resolved = RynCapabilitiesLoader.ResolveScopePath("$APP_DATA/data/logs");
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "data", "logs"));
        resolved.Should().Be(expected);
    }

    [Fact]
    public void ParsesScope_BooleanPlugin_HasNoScope()
    {
        var json = """
        {
          "capabilities": {
            "clipboard": true,
            "notification": true
          }
        }
        """;

        var caps = RynCapabilitiesLoader.Parse(json);

        caps.GetScope("clipboard").Should().BeNull();
        caps.GetScope("notification").Should().BeNull();
    }
}
