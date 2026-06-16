using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Ryn.Ipc;

namespace Ryn.Benchmarks;

/// <summary>
/// Benchmarks the REAL IPC dispatch pipeline a webview request travels through.
/// <para>
/// Each [RynCommand] on <see cref="BenchmarkCommands"/> is compiled by the Ryn.Ipc source
/// generator into a <c>BenchmarkCommandsRouter : ICommandRouter</c> plus an
/// <c>AddBenchmarkCommands()</c> DI extension — the exact code a shipping app runs. Setup wires that
/// generated router into a real <see cref="RynCommandDispatcher"/> via DI (mirroring
/// RynApplication's service graph), and every benchmark calls
/// <see cref="RynCommandDispatcher.DispatchAsync(string, ReadOnlyMemory{byte}, CancellationToken)"/>
/// with realistic UTF-8 JSON, so the numbers cover the production path end to end:
/// capability check -> generated <c>CanRoute</c>/<c>RouteAsync</c> -> JsonDocument parse + argument
/// binding -> handler invocation -> generated result serialization back to a JSON string.
/// </para>
/// <para>
/// Earlier this fixture dispatched through a hand-written stub router that ignored args and returned
/// a constant, so it measured none of the generated code and gated PRs with no real regression
/// signal (TST-12). It now exercises the generated router exclusively.
/// </para>
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class IpcDispatchBenchmarks
{
    private RynCommandDispatcher _dispatcher = null!;

    // Realistic UTF-8 JSON payloads, encoded once so the benchmark measures dispatch, not encoding.
    private ReadOnlyMemory<byte> _emptyArgs;
    private ReadOnlyMemory<byte> _primitiveArgs;
    private ReadOnlyMemory<byte> _multiArgs;
    private ReadOnlyMemory<byte> _complexArgs;

    [GlobalSetup]
    public void Setup()
    {
        // Build the dispatcher the same way RynApplication does: register the source-generated
        // router through its generated DI extension, resolve every ICommandRouter, and hand them to
        // the real dispatcher. AddBenchmarkCommands() is emitted by the generator from the
        // [RynCommand] handlers below — referencing it proves the generated code is in the build.
        var services = new ServiceCollection()
            .AddBenchmarkCommands()
            .BuildServiceProvider();

        var routers = services.GetServices<ICommandRouter>().ToArray();
        _dispatcher = new RynCommandDispatcher(routers, services, RynCapabilities.AllowAll());

        _emptyArgs = Encoding.UTF8.GetBytes("{}");
        _primitiveArgs = Encoding.UTF8.GetBytes("""{"value":"benchmark"}""");
        _multiArgs = Encoding.UTF8.GetBytes("""{"name":"bench","count":42,"enabled":true}""");
        _complexArgs = Encoding.UTF8.GetBytes(
            """{"point":{"label":"origin","x":12,"y":-7,"tags":["alpha","beta"]}}""");
    }

    /// <summary>
    /// No-parameter command: generated router skips JSON parsing entirely and serializes a string
    /// result. Lower bound for dispatch overhead (capability check + router lookup + serialize).
    /// </summary>
    [Benchmark(Description = "Dispatch: no-arg command (status)")]
    public ValueTask<string> DispatchNoArgs()
    {
        return _dispatcher.DispatchAsync("status", _emptyArgs);
    }

    /// <summary>
    /// Single string parameter: exercises the generated JsonDocument.Parse + TryGetProperty +
    /// GetString binding and the generated __ToJson string escaping on the return path.
    /// </summary>
    [Benchmark(Description = "Dispatch: single string param (echo)")]
    public ValueTask<string> DispatchPrimitiveArgs()
    {
        return _dispatcher.DispatchAsync("echo", _primitiveArgs);
    }

    /// <summary>
    /// Three mixed primitives (string + int + bool): exercises the generated multi-argument binding
    /// path and an integer-formatted return value.
    /// </summary>
    [Benchmark(Description = "Dispatch: three primitive params (tally)")]
    public ValueTask<string> DispatchMultipleArgs()
    {
        return _dispatcher.DispatchAsync("tally", _multiArgs);
    }

    /// <summary>
    /// Complex DTO parameter: exercises the generated source-gen JsonSerializer.Deserialize against a
    /// JsonSerializerContext (the NativeAOT-safe path) plus a string return — the heaviest realistic
    /// argument-binding shape.
    /// </summary>
    [Benchmark(Description = "Dispatch: complex DTO param (describe)")]
    public ValueTask<string> DispatchComplexArgs()
    {
        return _dispatcher.DispatchAsync("describe", _complexArgs);
    }
}

// ── Benchmark command fixtures ────────────────────────────────────────────
// The source generator turns each [RynCommand] into a real router case at compile time, so these
// drive the same generated dispatch code production apps run. Types/handlers must be public so the
// generated router (and the JsonSerializerContext for the DTO param) can reference them.

#pragma warning disable CA1515 // Consider making public types internal — required by the generated router
#pragma warning disable CA1024 // Use properties where appropriate — [RynCommand] targets methods
/// <summary>Public [RynCommand] handlers the generator compiles into BenchmarkCommandsRouter.</summary>
[RynJsonContext(typeof(BenchmarkPointContext))]
public static class BenchmarkCommands
{
    /// <summary>No-parameter command; measures baseline dispatch overhead.</summary>
    [RynCommand("status")]
    public static string Status() => "ok";

    /// <summary>Single string parameter; measures one-arg JSON binding + string escaping.</summary>
    [RynCommand("echo")]
    public static string Echo(string value) => value;

    /// <summary>Three primitive parameters; measures multi-arg binding + integer return.</summary>
    [RynCommand("tally")]
    public static int Tally(string name, int count, bool enabled)
        => enabled ? name.Length + count : count;

    /// <summary>Complex DTO parameter; measures source-gen Deserialize against a JsonSerializerContext.</summary>
    [RynCommand("describe")]
    public static string Describe(BenchmarkPoint point)
        => $"{point.Label}@({point.X},{point.Y})#{point.Tags.Length}";
}
#pragma warning restore CA1024
#pragma warning restore CA1515

/// <summary>DTO bound by the generated router for the "describe" command's complex parameter.</summary>
public sealed record BenchmarkPoint(string Label, int X, int Y, string[] Tags);

/// <summary>
/// Source-generated JSON context the generated router uses to deserialize <see cref="BenchmarkPoint"/>
/// without reflection — the same NativeAOT-safe contract production [RynCommand] DTO params require.
/// CamelCase so the realistic lower-cased JSON payload ("label"/"x"/"y"/"tags") binds to the record.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BenchmarkPoint))]
internal sealed partial class BenchmarkPointContext : JsonSerializerContext;
