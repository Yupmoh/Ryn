using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Runtime (not snapshot) coverage that drives the REAL generated routers end-to-end, locking in the
/// generator's value-binding and serialization contract (findings GEN-08/TST-08 and the GEN nullable/
/// default/non-finite contract). These fixtures did not previously exist at runtime — only as snapshots —
/// so a regression in the emitted code would now fail an executing assertion, not just a text diff.
/// </summary>
public sealed class GeneratorRuntimeContractTests
{
    // ── Non-finite double result serializes as JSON null (GEN, Emitter GetScalarSerializer) ──

    [Fact]
    public async Task NonFiniteDouble_SerializesAsJsonNull()
    {
        var dispatcher = BuildDispatcher();

        var nan = await dispatcher.DispatchAsync("divide", Args("{\"a\":0.0,\"b\":0.0}"));
        var posInf = await dispatcher.DispatchAsync("divide", Args("{\"a\":1.0,\"b\":0.0}"));
        var negInf = await dispatcher.DispatchAsync("divide", Args("{\"a\":-1.0,\"b\":0.0}"));

        nan.Should().Be("null");     // 0/0 = NaN -> JSON null (NaN is not valid JSON)
        posInf.Should().Be("null");  // +Infinity -> JSON null
        negInf.Should().Be("null");  // -Infinity -> JSON null
    }

    [Fact]
    public async Task FiniteDouble_SerializesNumerically()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("divide", Args("{\"a\":7.0,\"b\":2.0}"));

        result.Should().Be("3.5");
    }

    // ── Nullable reference param (string?) accepts JSON null / missing as null (GEN-03) ──

    [Fact]
    public async Task NullableRefParam_JsonNull_BindsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("describe", Args("{\"label\":null}"));

        result.Should().Be("\"(none)\"");
    }

    [Fact]
    public async Task NullableRefParam_Missing_BindsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("describe", Args("{}"));

        result.Should().Be("\"(none)\"");
    }

    [Fact]
    public async Task NullableRefParam_Present_BindsValue()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("describe", Args("{\"label\":\"x\"}"));

        result.Should().Be("\"label=x\"");
    }

    // ── C# default parameter value used when the arg is omitted (GEN-02) ──

    [Fact]
    public async Task DefaultValueParam_Omitted_UsesDefault()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("scale", Args("{\"n\":3}"));

        result.Should().Be("6"); // factor defaults to 2
    }

    [Fact]
    public async Task DefaultValueParam_Present_UsesProvided()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("scale", Args("{\"n\":3,\"factor\":10}"));

        result.Should().Be("30");
    }

    // ── Nullable VALUE-type param with a NON-NULL C# default (int? count = 10) ──
    // The default-value fix must reach the Nullable<T> branch: a MISSING arg binds the declared default,
    // an explicit JSON null still binds null, and a present value reads normally. The command returns a
    // string ("<value>" or "null") so each binding is asserted unambiguously.

    [Fact]
    public async Task NullableValueDefaultParam_Omitted_BindsDefault()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("countOrDefault", Args("{}"));

        result.Should().Be("\"10\""); // count defaults to 10, not null
    }

    [Fact]
    public async Task NullableValueDefaultParam_ExplicitNull_BindsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("countOrDefault", Args("{\"count\":null}"));

        result.Should().Be("\"null\""); // an explicit JSON null overrides the default
    }

    [Fact]
    public async Task NullableValueDefaultParam_Present_BindsValue()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("countOrDefault", Args("{\"count\":7}"));

        result.Should().Be("\"7\"");
    }

    // ── Instance-method command resolved from DI (TST-08) ──

    [Fact]
    public async Task InstanceMethodCommand_ResolvedFromDi()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("counter.next", ReadOnlyMemory<byte>.Empty);

        // The singleton instance keeps state across calls, proving it came from DI (not a fresh static).
        result.Should().Be("1");
        var again = await dispatcher.DispatchAsync("counter.next", ReadOnlyMemory<byte>.Empty);
        again.Should().Be("2");
    }

    // ── int[] / string[] parameter + return round-trip (TST-08) ──

    [Fact]
    public async Task IntArray_ParamAndReturn_RoundTrip()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("doubleAll", Args("{\"values\":[1,2,3]}"));

        result.Should().Be("[2,4,6]");
    }

    [Fact]
    public async Task StringArray_ParamAndReturn_RoundTrip()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("shout", Args("{\"words\":[\"a\",\"b\"]}"));

        result.Should().Be("[\"A!\",\"B!\"]");
    }

    // ── byte[] parameter + return (TST-08) ──

    [Fact]
    public async Task ByteArray_ParamAndReturn_RoundTrip()
    {
        var dispatcher = BuildDispatcher();

        // JSON numeric array of bytes in; reversed byte array back out.
        var result = await dispatcher.DispatchAsync("reverseBytes", Args("{\"data\":[1,2,3]}"));

        result.Should().Be("[3,2,1]");
    }

    // ── [RynJsonContext] DTO round-trip: complex param in, complex result out (TST-08) ──

    [Fact]
    public async Task ComplexDto_ParamAndReturn_RoundTrip()
    {
        var dispatcher = BuildDispatcher();

        // The DTO is serialized via the user-supplied JsonSerializerContext with STJ defaults, so the
        // JSON property names match the C# record property names (PascalCase) on the way in and out.
        var result = await dispatcher.DispatchAsync(
            "makeBadge", Args("{\"input\":{\"Name\":\"Ada\",\"Value\":7}}"));

        using var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("Label").GetString().Should().Be("Ada");
        doc.RootElement.GetProperty("Count").GetInt32().Should().Be(7);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static RynCommandDispatcher BuildDispatcher()
    {
        var services = new ServiceCollection()
            .AddContractCommands()
            .AddCounterCommands()
            .AddArrayContractCommands()
            .AddDtoContractCommands()
            .BuildServiceProvider();

        var routers = services.GetServices<ICommandRouter>().ToArray();
        return new RynCommandDispatcher(routers, services, RynCapabilities.AllowAll());
    }

    private static ReadOnlyMemory<byte> Args(string json) => Encoding.UTF8.GetBytes(json);
}

// ── Fixtures (the generator emits routers for these) ─────────────────────
#pragma warning disable CA1515 // public so the generated router can reference it
#pragma warning disable CA1819 // arrays as IPC payloads are intentional here

public sealed class ContractCommands
{
    // Non-finite double result must serialize as JSON null.
    [RynCommand("divide")]
    public static double Divide(double a, double b) => a / b;

    // Nullable reference param binds JSON null / missing as null.
    [RynCommand("describe")]
    public static string Describe(string? label) => label is null ? "(none)" : $"label={label}";

    // C# default value fills a missing arg.
    [RynCommand("scale")]
    public static int Scale(int n, int factor = 2) => n * factor;

    // Nullable VALUE-type param with a NON-NULL C# default. A missing arg must bind the declared default
    // (10), an explicit JSON null must bind null, and a present value reads normally. The result reports
    // the bound state so the test asserts the binding unambiguously without serialization ambiguity.
    [RynCommand("countOrDefault")]
    public static string CountOrDefault(int? count = 10) => count.HasValue ? count.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
}

public sealed class CounterCommands
{
    private int _count;

    // Instance command — registered for DI as a singleton, so state persists across dispatches.
    [RynCommand("counter.next")]
    public int Next() => ++_count;
}

public sealed class ArrayContractCommands
{
    [RynCommand("doubleAll")]
    public static int[] DoubleAll(int[] values) => values.Select(v => v * 2).ToArray();

    [RynCommand("shout")]
    public static string[] Shout(string[] words) => words.Select(w => w.ToUpperInvariant() + "!").ToArray();

    [RynCommand("reverseBytes")]
    public static byte[] ReverseBytes(byte[] data) => data.Reverse().ToArray();
}

public sealed record BadgeInput(string Name, int Value);
public sealed record Badge(string Label, int Count);

[JsonSerializable(typeof(BadgeInput))]
[JsonSerializable(typeof(Badge))]
public sealed partial class BadgeJsonContext : JsonSerializerContext;

[RynJsonContext(typeof(BadgeJsonContext))]
public sealed class DtoContractCommands
{
    [RynCommand("makeBadge")]
    public static Badge MakeBadge(BadgeInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        return new Badge(input.Name, input.Value);
    }
}

#pragma warning restore CA1819
#pragma warning restore CA1515
