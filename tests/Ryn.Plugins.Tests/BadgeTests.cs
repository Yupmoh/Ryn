using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.Badge;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class BadgeServiceTests
{
    private sealed class FakeBadgeBackend : IBadgeBackend
    {
        public List<string?> Labels { get; } = [];
        public void SetLabel(string? label) => Labels.Add(label);
        public void Dispose() { }
    }

    [Theory]
    [InlineData(-5, null)]
    [InlineData(0, null)]
    [InlineData(1, "1")]
    [InlineData(42, "42")]
    [InlineData(99, "99")]
    [InlineData(100, "99+")]
    [InlineData(int.MaxValue, "99+")]
    public void FormatCount_FollowsPlatformConventions(int count, string? expected)
    {
        BadgeService.FormatCount(count).Should().Be(expected);
    }

    [Fact]
    public void Set_NormalizesEmptyToClear()
    {
        using var backend = new FakeBadgeBackend();
        using var service = new BadgeService(backend);

        service.Set("3");
        service.Set("");
        service.Set(null);

        backend.Labels.Should().Equal("3", null, null);
    }

    [Fact]
    public void SetCount_And_Clear_MapToLabels()
    {
        using var backend = new FakeBadgeBackend();
        using var service = new BadgeService(backend);

        service.SetCount(7);
        service.SetCount(150);
        service.SetCount(0);
        service.Set("•");
        service.Clear();

        backend.Labels.Should().Equal("7", "99+", null, "•", null);
    }
}

public sealed class BadgeDependencyInjectionTests
{
    [Fact]
    public void AddRynBadge_RegistersServiceCommandsAndPlugin()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IMainThreadDispatcher>(new NoopDispatcher());
        services.AddRynBadge();

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<BadgeService>().Should().NotBeNull();
        sp.GetRequiredService<BadgeCommands>().Should().NotBeNull();
        sp.GetServices<IRynPlugin>().Should().Contain(p => p.Name == "badge");
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
    }

    private sealed class NoopDispatcher : IMainThreadDispatcher
    {
        public void Post(Action action) { }
        public Task InvokeAsync(Action action) => Task.CompletedTask;
    }
}
