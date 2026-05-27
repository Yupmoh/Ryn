using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ryn.Ipc.Generator;
using Xunit;

namespace Ryn.Ipc.Tests;

public sealed class GeneratorSnapshotTests
{
    [Fact]
    public Task SimpleStaticCommand()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class MathCommands
            {
                [RynCommand]
                public static int Add(int a, int b) => a + b;
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task ExplicitCommandName()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class Greeter
            {
                [RynCommand("sayHello")]
                public static string Hello(string name) => $"Hello, {name}!";
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task AsyncCommand()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Ryn.Ipc;

            namespace TestApp;

            public class AsyncCommands
            {
                [RynCommand]
                public static async ValueTask<string> Fetch(int id, CancellationToken ct)
                {
                    await Task.Delay(1, ct);
                    return id.ToString();
                }
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task VoidCommand()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class ActionCommands
            {
                [RynCommand]
                public static void Reset() { }
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task AsyncVoidCommand()
    {
        var source = """
            using System.Threading;
            using System.Threading.Tasks;
            using Ryn.Ipc;

            namespace TestApp;

            public class AsyncVoidCommands
            {
                [RynCommand]
                public static async ValueTask DoWork(CancellationToken ct)
                {
                    await Task.Delay(1, ct);
                }
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task MultipleCommands()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class Calculator
            {
                [RynCommand]
                public static int Add(int a, int b) => a + b;

                [RynCommand]
                public static int Multiply(int a, int b) => a * b;

                [RynCommand]
                public static double Divide(double a, double b) => a / b;
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task InstanceMethod()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class GreeterService
            {
                [RynCommand]
                public string Greet(string name) => $"Hello, {name}!";
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task NoParameters()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class StatusCommands
            {
                [RynCommand]
                public static string GetStatus() => "ok";
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task JsonElementParameter()
    {
        var source = """
            using System.Text.Json;
            using Ryn.Ipc;

            namespace TestApp;

            public class DataCommands
            {
                [RynCommand]
                public static string ProcessData(string name, JsonElement data) => name;
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task ArrayParameter()
    {
        var source = """
            using System.Linq;
            using Ryn.Ipc;

            namespace TestApp;

            public class ArrayCommands
            {
                [RynCommand]
                public static int Sum(int[] values) => values.Sum();
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task ArrayReturn()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class ArrayReturnCommands
            {
                [RynCommand]
                public static string[] GetTags(string prefix) => new[] { prefix + "a", prefix + "b" };
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task NullableParameter()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class NullableCommands
            {
                [RynCommand]
                public static string Format(int? value) => value.HasValue ? value.Value.ToString() : "null";
            }
            """;

        return VerifyGenerator(source);
    }

    [Fact]
    public Task NullableReturn()
    {
        var source = """
            using Ryn.Ipc;

            namespace TestApp;

            public class NullableReturnCommands
            {
                [RynCommand]
                public static int? FindIndex(string name) => name == "missing" ? null : 42;
            }
            """;

        return VerifyGenerator(source);
    }

    private static Task VerifyGenerator(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);

        var references = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(typeof(RynCommandAttribute).Assembly.Location),
        };

        // Add runtime references
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")));
        references.Add(MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Threading.Tasks.dll")));
        references.Add(MetadataReference.CreateFromFile(typeof(System.Text.Json.JsonElement).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(System.Linq.Enumerable).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "TestAssembly",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var generator = new RynCommandGenerator();
        var driver = CSharpGeneratorDriver.Create(generator);

        driver = (CSharpGeneratorDriver)driver.RunGeneratorsAndUpdateCompilation(
            compilation, out var outputCompilation, out var diagnostics);

        var runResult = driver.GetRunResult();

        return Verifier.Verify(runResult)
            .UseDirectory("Snapshots");
    }
}
