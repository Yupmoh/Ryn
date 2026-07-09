using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.MenuBar;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class AcceleratorParserTests
{
    [Theory]
    [InlineData("Cmd+Q", true, false, false, false, "q")]
    [InlineData("Ctrl+Shift+A", false, true, false, true, "a")]
    [InlineData("Alt+F4", false, false, true, false, "f4")]
    [InlineData("Cmd+Alt+H", true, false, true, false, "h")]
    [InlineData("Shift+Escape", false, false, false, true, "escape")]
    [InlineData("cmd+shift+z", true, false, false, true, "z")]
    [InlineData("Option+Space", false, false, true, false, "space")]
    [InlineData("Ctrl+PgDn", false, true, false, false, "pagedown")]
    public void Parses_ModifiersAndKeys(
        string accelerator, bool command, bool control, bool alt, bool shift, string key)
    {
        AcceleratorParser.TryParse(accelerator, preferCommand: true, out var parsed).Should().BeTrue();
        parsed.Command.Should().Be(command);
        parsed.Control.Should().Be(control);
        parsed.Alt.Should().Be(alt);
        parsed.Shift.Should().Be(shift);
        parsed.Key.Should().Be(key);
    }

    [Fact]
    public void CmdOrCtrl_ResolvesPerPlatformPreference()
    {
        AcceleratorParser.TryParse("CmdOrCtrl+C", preferCommand: true, out var mac).Should().BeTrue();
        mac.Command.Should().BeTrue();
        mac.Control.Should().BeFalse();

        AcceleratorParser.TryParse("CmdOrCtrl+C", preferCommand: false, out var win).Should().BeTrue();
        win.Command.Should().BeFalse();
        win.Control.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Cmd+")]
    [InlineData("Cmd")]
    [InlineData("Cmd+Foo")]
    [InlineData("Cmd+A+B")]
    [InlineData("F25")]
    public void Rejects_InvalidAccelerators(string? accelerator)
    {
        AcceleratorParser.TryParse(accelerator, preferCommand: true, out _).Should().BeFalse();
    }

    [Fact]
    public void TrailingPlus_MeansThePlusKey()
    {
        AcceleratorParser.TryParse("Cmd++", preferCommand: true, out var parsed).Should().BeTrue();
        parsed.Command.Should().BeTrue();
        parsed.Key.Should().Be("+");
    }

    [Fact]
    public void DisplayString_ReadsNaturally()
    {
        AcceleratorParser.TryParse("CmdOrCtrl+Shift+A", preferCommand: false, out var parsed).Should().BeTrue();
        parsed.ToDisplayString().Should().Be("Ctrl+Shift+A");

        AcceleratorParser.TryParse("Alt+PageDown", preferCommand: false, out var named).Should().BeTrue();
        named.ToDisplayString().Should().Be("Alt+Pagedown");
    }
}

public sealed class MenuBarDefaultsTests
{
    [Fact]
    public void CreateDefault_HasAppEditAndWindowMenus()
    {
        var menus = MenuBarDefaults.CreateDefault("TestApp");

        menus.Should().HaveCount(3);
        menus[0].Label.Should().Be("TestApp");
        menus[1].Label.Should().Be("Edit");
        menus[2].Label.Should().Be("Window");
        menus[0].Items.Should().Contain(i => i.Role == "quit");
        menus[1].Items.Should().Contain(i => i.Role == "copy").And.Contain(i => i.Role == "paste");
        menus[2].Items.Should().Contain(i => i.Role == "minimize").And.Contain(i => i.Role == "close");
    }

    [Fact]
    public void ExpandTopLevelRoles_ReplacesConvenienceRoles_AndKeepsCustomMenus()
    {
        var custom = new MenuBarItem
        {
            Label = "Workspace",
            Items = [new MenuBarItem { Id = "open", Label = "Open…" }],
        };
        var items = new[]
        {
            new MenuBarItem { Role = "appMenu" },
            custom,
            new MenuBarItem { Role = "editMenu" },
        };

        var expanded = MenuBarDefaults.ExpandTopLevelRoles(items, "TestApp");

        expanded.Should().HaveCount(3);
        expanded[0].Label.Should().Be("TestApp");
        expanded[0].Items.Should().Contain(i => i.Role == "quit");
        expanded[1].Should().BeSameAs(custom);
        expanded[2].Label.Should().Be("Edit");
    }

    [Fact]
    public void ExpandTopLevelRoles_PassesThroughUnchanged_WhenNothingToExpand()
    {
        var items = new[] { new MenuBarItem { Label = "File", Items = [] } };
        MenuBarDefaults.ExpandTopLevelRoles(items, "TestApp").Should().BeSameAs(items);
    }

    [Fact]
    public void EveryRole_UsedByDefaultMenus_IsKnown()
    {
        var menus = MenuBarDefaults.CreateDefault("TestApp");
        foreach (var role in menus.SelectMany(m => m.Items!).Where(i => i.Role is not null))
        {
            MenuBarRoles.TryGet(role.Role!, out _).Should().BeTrue($"role '{role.Role}' must exist");
        }
    }
}

public sealed class MenuBarRolesTests
{
    [Fact]
    public void ResolveLabel_PrefersExplicitLabel_AndFormatsAppName()
    {
        MenuBarRoles.TryGet("quit", out var quit).Should().BeTrue();
        MenuBarRoles.ResolveLabel(quit, null, "Cove").Should().Be("Quit Cove");
        MenuBarRoles.ResolveLabel(quit, "Leave", "Cove").Should().Be("Leave");
    }

    [Fact]
    public void RoleLookup_IsCaseInsensitive()
    {
        MenuBarRoles.TryGet("selectall", out var role).Should().BeTrue();
        role.Name.Should().Be("selectAll");
    }
}

public sealed class MenuBarServiceTests : IDisposable
{
    private sealed class FakeMenuBarBackend : IMenuBarBackend
    {
        public IReadOnlyList<MenuBarItem>? LastMenu { get; private set; }
        public int SetMenuCalls { get; private set; }

        public event Action<string>? MenuItemClicked;
        public event Action<string>? RoleActivated;

        public void SetMenu(IReadOnlyList<MenuBarItem> items)
        {
            LastMenu = items;
            SetMenuCalls++;
        }

        public void Click(string id) => MenuItemClicked?.Invoke(id);
        public void Activate(string role) => RoleActivated?.Invoke(role);
        public void Dispose() { }
    }

    private readonly ServiceProvider _provider;
    private readonly FakeMenuBarBackend _backend = new();
    private readonly MenuBarService _service;

    public MenuBarServiceTests()
    {
        _provider = new ServiceCollection().BuildServiceProvider();
        _service = new MenuBarService(new MenuBarOptions { AppName = "TestApp" }, _provider, _backend);
    }

    [Fact]
    public void SetMenu_ExpandsTopLevelRoles_BeforeReachingBackend()
    {
        _service.SetMenu([new MenuBarItem { Role = "editMenu" }]);

        _backend.LastMenu.Should().NotBeNull();
        _backend.LastMenu![0].Label.Should().Be("Edit");
        _backend.LastMenu[0].Items.Should().Contain(i => i.Role == "copy");
    }

    [Fact]
    public void CustomItemClick_EmitsJsonEncodedId()
    {
        var events = new List<(string Name, string Json)>();
        _service.EmitEvent = (name, json) => events.Add((name, json));

        _backend.Click("""open-"quoted"-id""");

        events.Should().ContainSingle();
        events[0].Name.Should().Be("menubar.itemClicked");
        JsonDocument.Parse(events[0].Json).RootElement.GetString().Should().Be("""open-"quoted"-id""");
    }

    [Fact]
    public void QuitRole_RequestsShutdown()
    {
        var lifetime = new RecordingLifetime();
        using var provider = new ServiceCollection()
            .AddSingleton<IRynApplicationLifetime>(lifetime)
            .BuildServiceProvider();
        using var backend = new FakeMenuBarBackend();
        using var service = new MenuBarService(new MenuBarOptions(), provider, backend);

        backend.Activate("quit");

        lifetime.ShutdownRequested.Should().BeTrue();
    }

    [Fact]
    public void UnknownRole_IsIgnored()
    {
        _backend.Invoking(b => b.Activate("nonsense")).Should().NotThrow();
    }

    private sealed class RecordingLifetime : IRynApplicationLifetime
    {
        public bool ShutdownRequested { get; private set; }
        public void RequestShutdown() => ShutdownRequested = true;
    }

    public void Dispose()
    {
        _service.Dispose();
        _backend.Dispose();
        _provider.Dispose();
    }
}

public sealed class MenuBarDependencyInjectionTests
{
    [Fact]
    public void AddRynMenuBar_RegistersServiceCommandsAndPlugin()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynMenuBar(o => o.AppName = "TestApp");

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<MenuBarService>().Should().NotBeNull();
        sp.GetRequiredService<MenuBarCommands>().Should().NotBeNull();
        sp.GetServices<IRynPlugin>().Should().Contain(p => p.Name == "menubar");
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
        sp.GetRequiredService<MenuBarOptions>().AppName.Should().Be("TestApp");
    }

    [Fact]
    public void SetMenuCommand_DeserializesCamelCaseItems()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynMenuBar();
        using var sp = services.BuildServiceProvider();

        var commands = sp.GetRequiredService<MenuBarCommands>();
        using var doc = JsonDocument.Parse(
            """[{"label":"File","items":[{"id":"open","label":"Open","accelerator":"CmdOrCtrl+O"},{"separator":true},{"role":"quit"}]}]""");

        commands.Invoking(c => c.SetMenu(doc.RootElement)).Should().NotThrow();
    }

    private sealed class NoopDispatcher : IMainThreadDispatcher
    {
        public void Post(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
    }
}
