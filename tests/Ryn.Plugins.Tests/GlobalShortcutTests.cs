using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.GlobalShortcut;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class GlobalShortcutAcceleratorTests
{
    [Theory]
    [InlineData("Cmd+Shift+A", true, "cmd+shift+a")]
    [InlineData("shift+cmd+a", true, "cmd+shift+a")]
    [InlineData("CmdOrCtrl+P", true, "cmd+p")]
    [InlineData("CmdOrCtrl+P", false, "ctrl+p")]
    [InlineData("Ctrl+Alt+Delete", false, "ctrl+alt+delete")]
    [InlineData("Alt+F5", false, "alt+f5")]
    public void CanonicalForm_IsOrderInsensitive_AndPlatformAware(
        string accelerator, bool preferCommand, string expected)
    {
        AcceleratorParser.TryParse(accelerator, preferCommand, out var parsed).Should().BeTrue();
        parsed.ToCanonicalString().Should().Be(expected);
    }

    [Theory]
    [InlineData("A")] // no modifier — would swallow plain typing system-wide
    [InlineData("Shift")] // no key
    [InlineData("Cmd+Foo")]
    [InlineData("")]
    [InlineData(null)]
    public void Rejects_ModifierlessOrInvalidAccelerators(string? accelerator)
    {
        AcceleratorParser.TryParse(accelerator, preferCommand: true, out _).Should().BeFalse();
    }
}

// CA1416: TryMapKeyCode/TryMapVirtualKey are pure lookup tables with no OS calls; exercising both maps on
// every CI platform is exactly the point of these tests.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
    Justification = "The key maps are platform-independent pure functions on platform-attributed types.")]
public sealed class GlobalShortcutKeyMapTests
{
    [Theory]
    [InlineData("a")]
    [InlineData("z")]
    [InlineData("0")]
    [InlineData("9")]
    [InlineData("f1")]
    [InlineData("f12")]
    [InlineData("space")]
    [InlineData("escape")]
    [InlineData("up")]
    [InlineData(",")]
    [InlineData("/")]
    public void CommonKeys_MapOnBothPlatforms(string key)
    {
        Ryn.Plugins.GlobalShortcut.Backends.MacOsGlobalShortcutBackend.TryMapKeyCode(key, out _)
            .Should().BeTrue($"macOS must map '{key}'");
        Ryn.Plugins.GlobalShortcut.Backends.WindowsGlobalShortcutBackend.TryMapVirtualKey(key, out _)
            .Should().BeTrue($"Windows must map '{key}'");
    }

    [Fact]
    public void UnknownKeys_DoNotMap()
    {
        Ryn.Plugins.GlobalShortcut.Backends.MacOsGlobalShortcutBackend.TryMapKeyCode("f24", out _)
            .Should().BeFalse("macOS has no F24");
        Ryn.Plugins.GlobalShortcut.Backends.WindowsGlobalShortcutBackend.TryMapVirtualKey("§", out _)
            .Should().BeFalse();
    }
}

public sealed class GlobalShortcutServiceTests : IDisposable
{
    private sealed class FakeShortcutBackend : IGlobalShortcutBackend
    {
        public HashSet<string> Registered { get; } = [];
        public bool RejectNext { get; set; }

        public event Action<string>? Activated;

        public bool Register(ParsedAccelerator accelerator, string canonical)
        {
            if (RejectNext)
            {
                RejectNext = false;
                return false;
            }
            return Registered.Add(canonical);
        }

        public bool Unregister(string canonical) => Registered.Remove(canonical);

        public void Fire(string canonical) => Activated?.Invoke(canonical);
        public void Dispose() { }
    }

    private readonly FakeShortcutBackend _backend = new();
    private readonly GlobalShortcutService _service;

    public GlobalShortcutServiceTests() => _service = new GlobalShortcutService(_backend);

    [Fact]
    public void Register_TracksAndIsIdempotent()
    {
        _service.Register("Ctrl+Shift+A").Should().BeTrue();
        _service.Register("Shift+Ctrl+A").Should().BeTrue("same canonical hotkey is idempotent");
        _service.IsRegistered("ctrl+shift+a").Should().BeTrue();
        _backend.Registered.Should().ContainSingle();
    }

    [Fact]
    public void Register_ReturnsFalse_WhenBackendRejects()
    {
        _backend.RejectNext = true;
        _service.Register("Ctrl+F5").Should().BeFalse();
        _service.IsRegistered("Ctrl+F5").Should().BeFalse();
    }

    [Fact]
    public void Unregister_RemovesOnlyOwnedHotkeys()
    {
        _service.Register("Ctrl+1").Should().BeTrue();
        _service.Unregister("Ctrl+1").Should().BeTrue();
        _service.Unregister("Ctrl+1").Should().BeFalse("already removed");
        _service.Unregister("Ctrl+2").Should().BeFalse("never registered");
        _backend.Registered.Should().BeEmpty();
    }

    [Fact]
    public void UnregisterAll_ClearsEverything()
    {
        _service.Register("Ctrl+1");
        _service.Register("Ctrl+2");
        _service.UnregisterAll();
        _backend.Registered.Should().BeEmpty();
        _service.IsRegistered("Ctrl+1").Should().BeFalse();
    }

    [Fact]
    public void Activation_EmitsTheOriginalAcceleratorString()
    {
        var events = new List<(string Name, string Json)>();
        _service.EmitEvent = (name, json) => events.Add((name, json));

        _service.Register("Shift+CmdOrCtrl+A").Should().BeTrue();
        var canonical = OperatingSystem.IsMacOS() ? "cmd+shift+a" : "ctrl+shift+a";
        _backend.Fire(canonical);

        events.Should().ContainSingle();
        events[0].Name.Should().Be("globalShortcut.activated");
        JsonDocument.Parse(events[0].Json).RootElement.GetString().Should().Be("Shift+CmdOrCtrl+A");
    }

    [Fact]
    public void Activation_ForUnknownHotkey_IsIgnored()
    {
        var events = new List<string>();
        _service.EmitEvent = (name, _) => events.Add(name);

        _backend.Fire("ctrl+q");

        events.Should().BeEmpty();
    }

    public void Dispose()
    {
        _service.Dispose();
        _backend.Dispose();
    }
}

public sealed class GlobalShortcutDependencyInjectionTests
{
    [Fact]
    public void AddRynGlobalShortcut_RegistersServiceCommandsAndPlugin()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynGlobalShortcut();

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<GlobalShortcutService>().Should().NotBeNull();
        sp.GetRequiredService<GlobalShortcutCommands>().Should().NotBeNull();
        sp.GetServices<IRynPlugin>().Should().Contain(p => p.Name == "globalShortcut");
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
    }

    private sealed class NoopDispatcher : IMainThreadDispatcher
    {
        public void Post(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
    }
}
