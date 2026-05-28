using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Stress tests for the IPC dispatch pipeline: large payloads, concurrency,
/// sustained load, and multi-router resolution.
/// </summary>
public sealed class StressTests
{
    // ── Large payload ────────────────────────────────────────────────

    [Fact]
    public async Task LargePayload_1MB_RoundTrips()
    {
        var dispatcher = EndToEndDispatchTests.BuildDispatcher();
        var megabyteString = new string('X', 1_048_576); // 1 MB of 'X'

        var json = $"{{\"name\":\"{megabyteString}\"}}";
        var result = await dispatcher.DispatchAsync(
            "greet",
            EndToEndDispatchTests.JsonArgs(json));

        result.Should().Contain(megabyteString);
        result.Should().StartWith("\"Hello, ");
        result.Should().EndWith("!\"");
    }

    // ── Concurrent dispatch ──────────────────────────────────────────

    [Fact]
    public async Task ConcurrentDispatch_100Commands_AllSucceed()
    {
        var dispatcher = EndToEndDispatchTests.BuildDispatcher();

        var tasks = Enumerable.Range(0, 100).Select(i =>
        {
            var json = $"{{\"a\":{i},\"b\":{i}}}";
            return dispatcher.DispatchAsync(
                "add",
                EndToEndDispatchTests.JsonArgs(json)).AsTask().ContinueWith(
                    t => (Index: i, Result: t.Result),
                    TaskScheduler.Default);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        results.Should().HaveCount(100);
        foreach (var r in results)
        {
            var expected = (r.Index + r.Index).ToString(System.Globalization.CultureInfo.InvariantCulture);
            r.Result.Should().Be(expected, because: $"add({r.Index}, {r.Index}) should be {expected}");
        }
    }

    [Fact]
    public async Task ConcurrentDispatch_MixedCommands_AllSucceed()
    {
        var dispatcher = EndToEndDispatchTests.BuildDispatcher();

        var tasks = Enumerable.Range(0, 100).Select(i =>
        {
            ValueTask<string> vt;
            if (i % 3 == 0)
            {
                vt = dispatcher.DispatchAsync(
                    "greet",
                    EndToEndDispatchTests.JsonArgs($"{{\"name\":\"User{i}\"}}"));
            }
            else if (i % 3 == 1)
            {
                vt = dispatcher.DispatchAsync(
                    "add",
                    EndToEndDispatchTests.JsonArgs($"{{\"a\":{i},\"b\":1}}"));
            }
            else
            {
                vt = dispatcher.DispatchAsync(
                    "isEven",
                    EndToEndDispatchTests.JsonArgs($"{{\"n\":{i}}}"));
            }

            return vt.AsTask().ContinueWith(
                t => (Index: i, Result: t.Result),
                TaskScheduler.Default);
        });

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);
        results.Should().HaveCount(100);

        // Verify a few specific results
        results.First(r => r.Index == 0).Result.Should().Be("\"Hello, User0!\"");
        results.First(r => r.Index == 1).Result.Should().Be("2");
        results.First(r => r.Index == 2).Result.Should().Be("true");
    }

    // ── Sustained load ───────────────────────────────────────────────

    [Fact]
    public async Task SustainedLoad_1000Commands_NoFailures()
    {
        var dispatcher = EndToEndDispatchTests.BuildDispatcher();
        var failures = 0;

        for (var i = 0; i < 1000; i++)
        {
            try
            {
                var json = $"{{\"a\":{i},\"b\":1}}";
                var result = await dispatcher.DispatchAsync(
                    "add",
                    EndToEndDispatchTests.JsonArgs(json));

                var expected = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (result != expected)
                    failures++;
            }
#pragma warning disable CA1031 // Do not catch general exception types
            catch
            {
                failures++;
            }
#pragma warning restore CA1031
        }

        failures.Should().Be(0, because: "all 1000 sequential dispatches should succeed");
    }

    [Fact]
    public async Task SustainedLoad_VoidCommands_NoFailures()
    {
        var dispatcher = EndToEndDispatchTests.BuildDispatcher();

        for (var i = 0; i < 1000; i++)
        {
            var result = await dispatcher.DispatchAsync("noOp", ReadOnlyMemory<byte>.Empty);
            result.Should().Be("null");
        }
    }

    // ── Rapid command registration / multi-router dispatch ───────────

    [Fact]
    public async Task ManyRouters_DispatchesToCorrectOne()
    {
        var syntheticRouters = Enumerable.Range(0, 50)
            .Select(i => new SyntheticRouter($"synthetic{i}", $"\"{i}\""))
            .ToArray();

        var services = new ServiceCollection()
            .AddE2ETestCommands()
            .AddE2EMathCommands()
            .BuildServiceProvider();

        var generatedRouters = services.GetServices<ICommandRouter>().ToArray();
        var allRouters = generatedRouters.Concat(syntheticRouters).ToArray();

        var dispatcher = new RynCommandDispatcher(allRouters, services, RynCapabilities.AllowAll());

        // Dispatch to the last synthetic router
        var result = await dispatcher.DispatchAsync("synthetic49", ReadOnlyMemory<byte>.Empty);
        result.Should().Be("\"49\"");

        // Dispatch to the first synthetic router
        result = await dispatcher.DispatchAsync("synthetic0", ReadOnlyMemory<byte>.Empty);
        result.Should().Be("\"0\"");

        // Dispatch to a generated router (still works)
        result = await dispatcher.DispatchAsync("add", EndToEndDispatchTests.JsonArgs("{\"a\":10,\"b\":20}"));
        result.Should().Be("30");

        // Dispatch to a middle synthetic router
        result = await dispatcher.DispatchAsync("synthetic25", ReadOnlyMemory<byte>.Empty);
        result.Should().Be("\"25\"");
    }

    [Fact]
    public async Task ManyRouters_ConcurrentDispatch_AllResolveCorrectly()
    {
        var syntheticRouters = Enumerable.Range(0, 50)
            .Select(i => new SyntheticRouter($"synthetic{i}", $"\"{i}\""))
            .ToArray();

        var services = new ServiceCollection()
            .AddE2ETestCommands()
            .AddE2EMathCommands()
            .BuildServiceProvider();

        var generatedRouters = services.GetServices<ICommandRouter>().ToArray();
        var allRouters = generatedRouters.Concat(syntheticRouters).ToArray();

        var dispatcher = new RynCommandDispatcher(allRouters, services, RynCapabilities.AllowAll());

        // Fire 50 concurrent dispatches, each to a different synthetic router
        var tasks = Enumerable.Range(0, 50).Select(i =>
            dispatcher.DispatchAsync($"synthetic{i}", ReadOnlyMemory<byte>.Empty)
                .AsTask()
                .ContinueWith(
                    t => (Index: i, Result: t.Result),
                    TaskScheduler.Default));

        var results = await Task.WhenAll(tasks).ConfigureAwait(true);

        foreach (var r in results)
        {
            r.Result.Should().Be($"\"{r.Index}\"");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────

    private sealed class SyntheticRouter(string command, string result) : ICommandRouter
    {
        public bool CanRoute(string cmd) => cmd == command;

        public ValueTask<string> RouteAsync(
            string cmd, ReadOnlyMemory<byte> args, IServiceProvider services, CancellationToken ct) =>
            new(result);
    }
}
