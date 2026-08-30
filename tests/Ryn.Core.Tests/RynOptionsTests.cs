using FluentAssertions;
using Ryn.Core.Internal;
using Xunit;

namespace Ryn.Core.Tests;

public sealed class RynOptionsTests
{
    [Fact]
    public void Defaults_AreReasonable()
    {
        var options = new RynOptions();

        options.ApplicationId.Should().Be("com.ryn.app");
        options.Title.Should().Be("Ryn Application");
        options.Width.Should().Be(800);
        options.Height.Should().Be(600);
        options.X.Should().Be(0);
        options.Y.Should().Be(0);
        options.IsMaximized.Should().BeFalse();
        options.IsSet(nameof(RynOptions.X)).Should().BeFalse();
        options.IsSet(nameof(RynOptions.Y)).Should().BeFalse();
        options.IsSet(nameof(RynOptions.IsMaximized)).Should().BeFalse();
        options.Resizable.Should().BeTrue();
        options.TitleBarStyle.Should().Be(TitleBarStyle.Native);
        options.Transparent.Should().BeFalse();
        options.Backdrop.Should().Be(BackdropMaterial.None);
        options.ClickThrough.Should().BeFalse();
        options.Url.Should().BeNull();
        options.DevTools.Should().BeFalse();
        options.HardwareAcceleration.Should().BeTrue();
        options.LinuxRenderingMode.Should().Be(LinuxRenderingMode.Auto);
        options.IsSet(nameof(RynOptions.LinuxRenderingMode)).Should().BeFalse();
        options.LinuxDisplayBackend.Should().Be(LinuxDisplayBackend.Auto);
        options.IsSet(nameof(RynOptions.LinuxDisplayBackend)).Should().BeFalse();
    }

    [Fact]
    public void Backdrop_IsTrackedAsExplicitlySet()
    {
        var options = new RynOptions();
        options.IsSet(nameof(RynOptions.Backdrop)).Should().BeFalse();

        options.Backdrop = BackdropMaterial.Mica;

        options.Backdrop.Should().Be(BackdropMaterial.Mica);
        options.IsSet(nameof(RynOptions.Backdrop)).Should().BeTrue();
    }

    [Fact]
    public void Placement_IsTrackedAsExplicitlySet()
    {
        var options = new RynOptions();

        options.X = 200;
        options.Y = 120;
        options.IsMaximized = true;

        options.X.Should().Be(200);
        options.Y.Should().Be(120);
        options.IsMaximized.Should().BeTrue();
        options.IsSet(nameof(RynOptions.X)).Should().BeTrue();
        options.IsSet(nameof(RynOptions.Y)).Should().BeTrue();
        options.IsSet(nameof(RynOptions.IsMaximized)).Should().BeTrue();
    }

    [Fact]
    public void LinuxRenderingMode_IsTrackedAsExplicitlySet()
    {
        var options = new RynOptions { LinuxRenderingMode = LinuxRenderingMode.SharedMemory };

        options.LinuxRenderingMode.Should().Be(LinuxRenderingMode.SharedMemory);
        options.IsSet(nameof(RynOptions.LinuxRenderingMode)).Should().BeTrue();
    }

    [Fact]
    public void SharedMemoryMode_SetsEnvironmentAndReexecutesWhenNotInherited()
    {
        var environment = new Dictionary<string, string?>();
        var reexecutions = 0;

        Action act = () => LinuxRendering.Configure(
            LinuxDisplayBackend.Auto,
            LinuxRenderingMode.SharedMemory,
            isLinux: true,
            name => environment.GetValueOrDefault(name),
            (name, value) => environment[name] = value,
            () => reexecutions++);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("Linux process re-execution returned without replacing the process.");
        environment.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string?>(LinuxRendering.ForceSharedMemoryVariable, "1"));
        reexecutions.Should().Be(1);
    }

    [Fact]
    public void SharedMemoryMode_DoesNotReexecuteWhenAlreadyInherited()
    {
        var environment = new Dictionary<string, string?>
        {
            [LinuxRendering.ForceSharedMemoryVariable] = "1",
        };
        var reexecutions = 0;

        LinuxRendering.Configure(
            LinuxDisplayBackend.Auto,
            LinuxRenderingMode.SharedMemory,
            isLinux: true,
            name => environment.GetValueOrDefault(name),
            (name, value) => environment[name] = value,
            () => reexecutions++);

        reexecutions.Should().Be(0);
    }

    [Theory]
    [InlineData(LinuxDisplayBackend.X11, "x11")]
    [InlineData(LinuxDisplayBackend.Wayland, "wayland")]
    public void DisplayBackend_SetsGtkEnvironmentBeforeInitialization(LinuxDisplayBackend backend, string expected)
    {
        var environment = new Dictionary<string, string?>();

        LinuxRendering.Configure(
            backend,
            LinuxRenderingMode.Auto,
            isLinux: true,
            name => environment.GetValueOrDefault(name),
            (name, value) => environment[name] = value,
            () => throw new InvalidOperationException("Reexecution is not expected."));

        environment.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<string, string?>(LinuxRendering.DisplayBackendVariable, expected));
    }

    [Theory]
    [InlineData(LinuxDisplayBackend.Auto, LinuxRenderingMode.Auto, true)]
    [InlineData(LinuxDisplayBackend.X11, LinuxRenderingMode.SharedMemory, false)]
    public void LinuxModes_DoNotChangeEnvironmentWhenInactive(
        LinuxDisplayBackend backend,
        LinuxRenderingMode renderingMode,
        bool isLinux)
    {
        var environment = new Dictionary<string, string?>();

        LinuxRendering.Configure(
            backend,
            renderingMode,
            isLinux,
            name => environment.GetValueOrDefault(name),
            (name, value) => environment[name] = value,
            () => throw new InvalidOperationException("Reexecution is not expected."));

        environment.Should().BeEmpty();
    }

    [Fact]
    public void ReexecutionArguments_PreserveEmptyAndUtf8Arguments()
    {
        byte[] commandLine =
        [
            .. "dotnet\0app.dll\0\0hello world\0caf"u8,
            0xC3, 0xA9, 0,
        ];

        LinuxProcessReexecutor.ReadNullTerminatedArguments(commandLine)
            .Should().Equal("dotnet", "app.dll", "", "hello world", "caf\u00e9");
    }
}
