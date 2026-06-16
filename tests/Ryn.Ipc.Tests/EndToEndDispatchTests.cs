using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Simulates the full JS-to-C# dispatch pipeline: serialized JSON args go through
/// RynCommandDispatcher, the source-generated router, the [RynCommand] handler,
/// and back out as serialized JSON — the same path a real webview request takes.
/// </summary>
public sealed class EndToEndDispatchTests
{
    // ── Simple string command ────────────────────────────────────────

    [Fact]
    public async Task Greet_ReturnsFormattedString()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("greet", JsonArgs("{\"name\":\"World\"}"));

        result.Should().Be("\"Hello, World!\"");
    }

    // ── Numeric types ────────────────────────────────────────────────

    [Fact]
    public async Task Add_ReturnsSum()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("add", JsonArgs("{\"a\":5,\"b\":3}"));

        result.Should().Be("8");
    }

    // ── Boolean return ───────────────────────────────────────────────

    [Fact]
    public async Task IsEven_ReturnsTrueForEvenNumber()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("isEven", JsonArgs("{\"n\":4}"));

        result.Should().Be("true");
    }

    [Fact]
    public async Task IsEven_ReturnsFalseForOddNumber()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("isEven", JsonArgs("{\"n\":7}"));

        result.Should().Be("false");
    }

    // ── Void return ──────────────────────────────────────────────────

    [Fact]
    public async Task NoOp_ReturnsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("noOp", ReadOnlyMemory<byte>.Empty);

        result.Should().Be("null");
    }

    // ── Nullable-reference parameter: null/missing accepted as null (GEN-03) ──
    // Echo(string? value) is annotated nullable, so the generator binds a JSON null OR an omitted
    // argument as null — the handler's "(nil)" fallback path. A present string binds normally. This
    // restores the pre-P5 behavior; the genuine throw is reserved for a NON-nullable `string` (see
    // EchoStrict below). Null-as-null is also honoured for value-type Nullable<T> parameters (see Square).

    [Fact]
    public async Task Echo_WithValue_ReturnsValue()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("echo", JsonArgs("{\"value\":\"hello\"}"));

        result.Should().Be("\"hello\"");
    }

    [Fact]
    public async Task Echo_WithNull_BindsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("echo", JsonArgs("{\"value\":null}"));

        // string? + JSON null binds (string?)null, so the handler returns its "(nil)" sentinel.
        result.Should().Be("\"(nil)\"");
    }

    [Fact]
    public async Task Echo_MissingArg_BindsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("echo", JsonArgs("{}"));

        // A missing string? argument also binds null, reaching the same "(nil)" path.
        result.Should().Be("\"(nil)\"");
    }

    // ── Non-nullable string parameter: null/missing still throws ──────
    // EchoStrict(string value) is NOT annotated nullable, so a JSON null or an omitted argument must
    // surface a descriptive RynIpcArgumentException naming the command + parameter rather than a
    // NullReferenceException from inside the handler.

    [Fact]
    public async Task EchoStrict_WithValue_ReturnsValue()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("echoStrict", JsonArgs("{\"value\":\"hello\"}"));

        result.Should().Be("\"hello\"");
    }

    [Fact]
    public async Task EchoStrict_WithNull_ThrowsDescriptiveArgumentException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("echoStrict", JsonArgs("{\"value\":null}")).AsTask();

        (await act.Should().ThrowAsync<RynIpcArgumentException>()
            .WithMessage("*echoStrict*value*must not be null*"))
            .Which.Should().Match<RynIpcArgumentException>(e =>
                e.Command == "echoStrict" && e.Parameter == "value");
    }

    [Fact]
    public async Task EchoStrict_MissingArg_ThrowsDescriptiveArgumentException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("echoStrict", JsonArgs("{}")).AsTask();

        await act.Should().ThrowAsync<RynIpcArgumentException>()
            .WithMessage("*echoStrict*value*missing*");
    }

    // ── C# default parameter value: omitted arg binds the default (GEN-02) ──
    // Greet2(string name = "world") binds the C# default when "name" is omitted, and the provided
    // value when present. A present explicit JSON null still throws (the default only fills a MISSING arg).

    [Fact]
    public async Task Greet2_MissingArg_UsesDefault()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("greet2", JsonArgs("{}"));

        result.Should().Be("\"Hello, world!\"");
    }

    [Fact]
    public async Task Greet2_WithValue_UsesProvidedValue()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("greet2", JsonArgs("{\"name\":\"Ada\"}"));

        result.Should().Be("\"Hello, Ada!\"");
    }

    // ── Value-type Nullable<T> parameter: null/missing accepted as null ───
    // Square(int? n) is the one shape the generator treats as nullable: a JSON null OR an omitted
    // argument both bind as (int?)null; a present value binds normally.

    [Fact]
    public async Task Square_WithValue_ReturnsSquared()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("square", JsonArgs("{\"n\":6}"));

        result.Should().Be("36");
    }

    [Fact]
    public async Task Square_WithNull_ReturnsNullSentinel()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("square", JsonArgs("{\"n\":null}"));

        result.Should().Be("-1");
    }

    [Fact]
    public async Task Square_MissingArg_TreatedAsNull()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("square", JsonArgs("{}"));

        result.Should().Be("-1");
    }

    // ── JsonElement parameter passthrough ────────────────────────────

    [Fact]
    public async Task Inspect_ReturnsJsonKind()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("inspect", JsonArgs("{\"data\":{\"key\":\"val\"}}"));

        result.Should().Be("\"Object\"");
    }

    [Fact]
    public async Task Inspect_WithArray_ReturnsArrayKind()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("inspect", JsonArgs("{\"data\":[1,2,3]}"));

        result.Should().Be("\"Array\"");
    }

    // ── Command not found ────────────────────────────────────────────

    [Fact]
    public async Task UnknownCommand_ThrowsNotFoundException()
    {
        var dispatcher = BuildDispatcher();

        var act = () => dispatcher.DispatchAsync("does.not.exist", ReadOnlyMemory<byte>.Empty).AsTask();

        await act.Should().ThrowAsync<RynCommandNotFoundException>()
            .WithMessage("*does.not.exist*");
    }

    // ── Empty / missing args ─────────────────────────────────────────

    [Fact]
    public async Task NoOp_WithEmptyArgs_Succeeds()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("noOp", ReadOnlyMemory<byte>.Empty);
        result.Should().Be("null");
    }

    [Fact]
    public async Task Status_NoParameters_ReturnsStatus()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("status", ReadOnlyMemory<byte>.Empty);
        result.Should().Be("\"ok\"");
    }

    // ── Unicode in parameters and return values ──────────────────────

    [Fact]
    public async Task Greet_Unicode_RoundTrips()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("greet", JsonArgs("{\"name\":\"☃❤🌍\"}"));

        // The generated __ToJson keeps standard Unicode chars above U+001F as-is
        result.Should().Contain("☃");
        result.Should().Contain("❤");
    }

    [Fact]
    public async Task Greet_Japanese_RoundTrips()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync(
            "greet",
            JsonArgs("{\"name\":\"こんにちは\"}"));

        result.Should().Be("\"Hello, こんにちは!\"");
    }

    [Fact]
    public async Task Greet_SpecialJsonChars_AreEscaped()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync(
            "greet",
            JsonArgs("{\"name\":\"line1\\nline2\\ttab\"}"));

        // The generated __ToJson escapes \n and \t
        result.Should().Contain("\\n");
        result.Should().Contain("\\t");
    }

    // ── Large string (1KB+) parameter and return ─────────────────────

    [Fact]
    public async Task Greet_LargeInput_RoundTrips()
    {
        var dispatcher = BuildDispatcher();
        var bigName = new string('A', 2048);

        var json = $"{{\"name\":\"{bigName}\"}}";
        var result = await dispatcher.DispatchAsync("greet", JsonArgs(json));

        result.Should().Contain(bigName);
        result.Should().StartWith("\"Hello, ");
    }

    // ── Multiple routers coexist ─────────────────────────────────────

    [Fact]
    public async Task MultipleRouters_DispatchToCorrectOne()
    {
        var dispatcher = BuildDispatcher();

        var greetResult = await dispatcher.DispatchAsync("greet", JsonArgs("{\"name\":\"A\"}"));
        var addResult = await dispatcher.DispatchAsync("add", JsonArgs("{\"a\":1,\"b\":2}"));
        var statusResult = await dispatcher.DispatchAsync("status", ReadOnlyMemory<byte>.Empty);

        greetResult.Should().Be("\"Hello, A!\"");
        addResult.Should().Be("3");
        statusResult.Should().Be("\"ok\"");
    }

    // ── Double return ────────────────────────────────────────────────

    [Fact]
    public async Task Half_ReturnsDouble()
    {
        var dispatcher = BuildDispatcher();

        var result = await dispatcher.DispatchAsync("half", JsonArgs("{\"n\":7.0}"));

        result.Should().Be("3.5");
    }

    // ── Helpers ──────────────────────────────────────────────────────

    internal static RynCommandDispatcher BuildDispatcher()
    {
        var services = new ServiceCollection()
            .AddE2ETestCommands()
            .AddE2EMathCommands()
            .BuildServiceProvider();

        var routers = services.GetServices<ICommandRouter>().ToArray();
        return new RynCommandDispatcher(routers, services, RynCapabilities.AllowAll());
    }

    internal static ReadOnlyMemory<byte> JsonArgs(string json)
    {
        return Encoding.UTF8.GetBytes(json);
    }
}

// ── Test command fixtures ────────────────────────────────────────────
// The source generator will produce routers for these at compile time.
// They must be public so the generated router code can reference them.

#pragma warning disable CA1515 // Consider making public types internal
#pragma warning disable CA1024 // Use properties where appropriate
public sealed class E2ETestCommands
{
    [RynCommand]
    public static string Greet(string name) => $"Hello, {name}!";

    [RynCommand]
    public static bool IsEven(int n) => n % 2 == 0;

    [RynCommand]
    public static void NoOp() { }

    // string? is nullable-annotated, so a JSON null or omitted "value" binds null -> "(nil)" (GEN-03).
    [RynCommand]
    public static string Echo(string? value) => value ?? "(nil)";

    // Plain non-nullable string: a JSON null or omitted "value" must throw RynIpcArgumentException.
    [RynCommand("echoStrict")]
    public static string EchoStrict(string value) => value;

    // C# default value: an omitted "name" binds "world"; a present value is used (GEN-02).
    [RynCommand("greet2")]
    public static string Greet2(string name = "world") => $"Hello, {name}!";

    [RynCommand]
    public static string Inspect(JsonElement data) => data.ValueKind.ToString();

    [RynCommand("status")]
    public static string GetStatus() => "ok";
}

public sealed class E2EMathCommands
{
    [RynCommand]
    public static int Add(int a, int b) => a + b;

    [RynCommand]
    public static double Half(double n) => n / 2.0;

    // Value-type Nullable<T> parameter: a JSON null or omitted "n" binds as (int?)null, which the
    // handler maps to the -1 sentinel; a present value is squared. Exercises the GEN-03 nullable path.
    [RynCommand]
    public static int Square(int? n) => n.HasValue ? n.Value * n.Value : -1;
}
#pragma warning restore CA1024
#pragma warning restore CA1515
