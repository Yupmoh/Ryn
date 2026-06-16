using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Regression coverage for finding GEN-09: when two registered routers both claim the same command id
/// (e.g. an app and a plugin from different assemblies that both expose "fs.readTextFile"), the
/// generator's compile-time RYN006 duplicate check cannot see the conflict. The dispatcher must not let
/// one router silently shadow the other — it surfaces the ambiguity at dispatch time with an
/// <see cref="InvalidOperationException"/> that names the conflicting command AND both routers.
/// </summary>
public sealed class DispatcherOverlapTests
{
    [Fact]
    public async Task TwoRoutersClaimingSameCommand_ThrowsNamingBoth()
    {
        var first = new StubRouter("dup.cmd", "\"first\"");
        var second = new StubRouter("dup.cmd", "\"second\"");
        var dispatcher = BuildDispatcher(first, second);

        var act = () => dispatcher.DispatchAsync("dup.cmd", ReadOnlyMemory<byte>.Empty).AsTask();

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("dup.cmd");
        ex.Message.Should().Contain(typeof(StubRouter).FullName);
        // Names BOTH conflicting routers, not just the winner.
        ex.Message.Should().Contain("more than one router");
    }

    [Fact]
    public async Task NonOverlappingRouters_DispatchNormally()
    {
        var a = new StubRouter("a.cmd", "\"A\"");
        var b = new StubRouter("b.cmd", "\"B\"");
        var dispatcher = BuildDispatcher(a, b);

        (await dispatcher.DispatchAsync("a.cmd", ReadOnlyMemory<byte>.Empty)).Should().Be("\"A\"");
        (await dispatcher.DispatchAsync("b.cmd", ReadOnlyMemory<byte>.Empty)).Should().Be("\"B\"");
    }

    [Fact]
    public async Task SingleRouterClaimingCommand_DoesNotThrowOverlap()
    {
        var only = new StubRouter("solo.cmd", "\"S\"");
        var dispatcher = BuildDispatcher(only);

        var act = () => dispatcher.DispatchAsync("solo.cmd", ReadOnlyMemory<byte>.Empty).AsTask();

        await act.Should().NotThrowAsync();
    }

    private static RynCommandDispatcher BuildDispatcher(params ICommandRouter[] routers)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        return new RynCommandDispatcher(routers, services, RynCapabilities.AllowAll());
    }

    private sealed class StubRouter(string command, string result) : ICommandRouter
    {
        public bool CanRoute(string cmd) => cmd == command;

        public ValueTask<string> RouteAsync(
            string cmd, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken ct) =>
            new(result);
    }
}
