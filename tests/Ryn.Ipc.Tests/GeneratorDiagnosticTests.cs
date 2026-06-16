using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ryn.Ipc;
using Ryn.Ipc.Generator;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>
/// Regression coverage for finding GEN-10: the generator must report actionable diagnostics for
/// signature shapes that previously emitted non-compiling code (RYN007 async-void, RYN008 unsupported
/// shape) instead of producing a cascade of CS errors, well-formed inputs must compile to zero errors,
/// and the incremental pipeline must cache its output across an unrelated source edit.
/// </summary>
public sealed class GeneratorDiagnosticTests
{
    // ── RYN007: async void [RynCommand] is a fire-and-forget footgun ──

    [Fact]
    public void AsyncVoidCommand_RaisesRYN007_Error_AndEmitsNoRouter()
    {
        var (sources, diagnostics) = Run("""
            using System.Threading.Tasks;
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static async void FireAndForget() { await Task.Delay(1); }
            }
            """);

        diagnostics.Should().Contain(d => d.Id == "RYN007" && d.Severity == DiagnosticSeverity.Error);
        // The broken command is dropped, so no router is emitted for it (no cascade of CS errors).
        sources.Should().NotContain(s => s.HintName.Contains("Router", StringComparison.Ordinal));
    }

    // ── RYN008: a generic command method is an unsupported signature shape ──

    [Fact]
    public void GenericMethodCommand_RaisesRYN008_Error()
    {
        var (sources, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static T Identity<T>(T value) => value;
            }
            """);

        diagnostics.Should().Contain(d => d.Id == "RYN008" && d.Severity == DiagnosticSeverity.Error);
        sources.Should().NotContain(s => s.HintName.Contains("Router", StringComparison.Ordinal));
    }

    [Fact]
    public void ByRefParameterCommand_RaisesRYN008_Error()
    {
        var (_, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static void Mutate(ref int value) { value++; }
            }
            """);

        diagnostics.Should().Contain(d => d.Id == "RYN008" && d.Severity == DiagnosticSeverity.Error);
    }

    // ── A well-formed command compiles the OUTPUT compilation with zero errors (GEN-10) ──

    [Fact]
    public void WellFormedCommand_OutputCompilation_HasNoErrors()
    {
        var (outputDiagnostics, generatorDiagnostics) = RunAndCompile("""
            using System.Linq;
            using System.Text.Json;
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static int Add(int a, int b) => a + b;
                [RynCommand("greet")] public static string Greet(string name) => name;
                [RynCommand] public static int Sum(int[] values) => values.Sum();
                [RynCommand] public static string Kind(JsonElement data) => data.ValueKind.ToString();
            }
            """);

        // No generator-reported diagnostics for a valid input…
        generatorDiagnostics.Should().BeEmpty();
        // …and the resulting compilation (user code + generated router) has zero errors.
        outputDiagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Should().BeEmpty();
    }

    // ── Incrementality: an unrelated source edit leaves the generated output Cached (GEN-10) ──

    [Fact]
    public void UnrelatedEdit_LeavesSourceOutputCached()
    {
        const string commandSource = """
            using Ryn.Ipc;
            namespace TestApp;
            public class Commands
            {
                [RynCommand] public static int Add(int a, int b) => a + b;
            }
            """;

        var compilation = CreateCompilation(commandSource);

        var driver = CSharpGeneratorDriver.Create(
            generators: [new RynCommandGenerator().AsSourceGenerator()],
            driverOptions: new GeneratorDriverOptions(default, trackIncrementalGeneratorSteps: true));

        // First run populates the incremental cache.
        var driver1 = driver.RunGenerators(compilation);

        // Add an UNRELATED file (no [RynCommand]) — must not invalidate the command pipeline output.
        var edited = compilation.AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("namespace TestApp { public class Unrelated { public int X => 1; } }"));
        var driver2 = driver1.RunGenerators(edited);

        var steps = driver2.GetRunResult().Results
            .SelectMany(r => r.TrackedOutputSteps)
            .SelectMany(kvp => kvp.Value)
            .SelectMany(step => step.Outputs)
            .ToArray();

        steps.Should().NotBeEmpty();
        steps.Should().OnlyContain(o =>
            o.Reason == IncrementalStepRunReason.Cached || o.Reason == IncrementalStepRunReason.Unchanged);
    }

    // ── Harness ──────────────────────────────────────────────────────────

    private static (IReadOnlyList<(string HintName, string Source)> Sources, IReadOnlyList<Diagnostic> Diagnostics)
        Run(string source)
    {
        var compilation = CreateCompilation(source);
        var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver.Create(new RynCommandGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        var runResult = driver.GetRunResult();
        var sources = runResult.Results
            .SelectMany(r => r.GeneratedSources)
            .Select(s => (s.HintName, Source: s.SourceText.ToString()))
            .ToArray();
        var diagnostics = runResult.Results.SelectMany(r => r.Diagnostics).ToArray();
        return (sources, diagnostics);
    }

    private static (IReadOnlyList<Diagnostic> OutputDiagnostics, IReadOnlyList<Diagnostic> GeneratorDiagnostics)
        RunAndCompile(string source)
    {
        var compilation = CreateCompilation(source);
        CSharpGeneratorDriver.Create(new RynCommandGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        var generatorDiagnostics = ((CSharpGeneratorDriver)CSharpGeneratorDriver.Create(new RynCommandGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _))
            .GetRunResult().Results.SelectMany(r => r.Diagnostics).ToArray();

        return (outputCompilation.GetDiagnostics().ToArray(), generatorDiagnostics);
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        // Reference the full runtime (every trusted-platform assembly) so the OUTPUT compilation actually
        // links — the generated router pulls in IServiceProvider, the DI extensions, STJ, etc. A handful of
        // hand-picked references is enough to RUN the generator (the existing snapshot harness does that),
        // but not to COMPILE its output without CS0246s, which is exactly what GEN-10 asks us to verify.
        var references = new List<MetadataReference>();
        var tpa = (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;
        foreach (var path in tpa.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        // The Ryn.Ipc assembly that defines [RynCommand], ICommandRouter, etc. is not on the TPA list.
        references.Add(MetadataReference.CreateFromFile(typeof(RynCommandAttribute).Assembly.Location));

        return CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}
