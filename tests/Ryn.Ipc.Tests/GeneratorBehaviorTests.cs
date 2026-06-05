using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ryn.Ipc;
using Ryn.Ipc.Generator;
using Xunit;

namespace Ryn.Ipc.Tests;

/// <summary>Assertion-based (non-snapshot) tests for generator behaviors added during hardening.</summary>
public sealed class GeneratorBehaviorTests
{
    [Fact]
    public void Task_ReturnType_IsTreatedAsAsync()
    {
        var (sources, diagnostics) = Run("""
            using System.Threading.Tasks;
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static async Task<int> GetN() { await Task.Delay(1); return 7; }
            }
            """);

        // Use Where(...) (a Func, not an expression tree) so modern pattern syntax is allowed here.
        diagnostics.Where(d => d.Id is "RYN002" && d.Severity is DiagnosticSeverity.Error).Should().BeEmpty();
        var router = sources.Single(s => s.HintName.Contains("Router", System.StringComparison.Ordinal)).Source;
        router.Should().Contain("case \"getN\":");
        router.Should().Contain("await"); // awaited, not serialized as a Task
    }

    [Fact]
    public void NonGenericTask_IsTreatedAsAsyncVoid()
    {
        var (sources, diagnostics) = Run("""
            using System.Threading.Tasks;
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static async Task DoIt() { await Task.Delay(1); }
            }
            """);

        diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error).Should().BeEmpty();
        var router = sources.Single(s => s.HintName.Contains("Router", System.StringComparison.Ordinal)).Source;
        router.Should().Contain("await");
        router.Should().Contain("return \"null\";");
    }

    [Fact]
    public void ByteArray_Parameter_IsSupported()
    {
        var (_, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static int Sum(byte[] data) => 0;
            }
            """);

        // byte[] used to be rejected (RYN001); now byte is a supported primitive element type.
        diagnostics.Where(d => d.Id is "RYN001").Should().BeEmpty();
    }

    [Fact]
    public void UIntAndDecimal_Parameters_AreSupported()
    {
        var (_, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class C
            {
                [RynCommand] public static decimal Calc(uint count, decimal price) => 0m;
            }
            """);

        diagnostics.Where(d => d.Id is "RYN001" or "RYN002").Should().BeEmpty();
    }

    [Fact]
    public void DuplicateCommandName_AcrossClasses_RaisesRYN006()
    {
        var (_, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class A { [RynCommand("foo.bar")] public static int X() => 1; }
            public class B { [RynCommand("foo.bar")] public static int Y() => 2; }
            """);

        diagnostics.Any(d => d.Id is "RYN006").Should().BeTrue();
    }

    [Fact]
    public void UniqueCommandNames_AcrossClasses_DoNotRaiseRYN006()
    {
        var (_, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace TestApp;
            public class A { [RynCommand("foo.bar")] public static int X() => 1; }
            public class B { [RynCommand("foo.baz")] public static int Y() => 2; }
            """);

        diagnostics.Where(d => d.Id is "RYN006").Should().BeEmpty();
    }

    [Fact]
    public void SameTypeName_InDifferentNamespaces_ProducesDistinctRouters()
    {
        // Two command classes both named `Commands` in different namespaces. The hint name used to be the
        // simple type name, so both emitted `CommandsRouter.g.cs` — a duplicate-hint-name collision that
        // made the generator throw. Qualifying the hint name with the namespace keeps them distinct.
        var (sources, diagnostics) = Run("""
            using Ryn.Ipc;
            namespace App.Alpha { public class Commands { [RynCommand("alpha.ping")] public static int Ping() => 1; } }
            namespace App.Beta  { public class Commands { [RynCommand("beta.ping")]  public static int Ping() => 2; } }
            """);

        diagnostics.Where(d => d.Severity is DiagnosticSeverity.Error).Should().BeEmpty();

        var routers = sources
            .Where(s => s.HintName.Contains("Router", System.StringComparison.Ordinal))
            .ToArray();
        routers.Should().HaveCount(2);
        routers.Select(r => r.HintName).Distinct(System.StringComparer.Ordinal).Should().HaveCount(2);

        var allRouterSource = string.Join("\n", routers.Select(r => r.Source));
        allRouterSource.Should().Contain("case \"alpha.ping\":");
        allRouterSource.Should().Contain("case \"beta.ping\":");
    }

    private static (System.Collections.Generic.IReadOnlyList<(string HintName, string Source)> Sources,
        System.Collections.Generic.IReadOnlyList<Diagnostic> Diagnostics) Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(RynCommandAttribute).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")),
            MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonElement).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location),
        };

        var compilation = CSharpCompilation.Create(
            "TestAssembly", [syntaxTree], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

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
}
