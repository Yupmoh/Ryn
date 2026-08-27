using FluentAssertions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Ryn.Callbacks.Generator;
using Ryn.Core;
using Xunit;

namespace Ryn.Callbacks.Tests;

public sealed class GeneratorDiagnosticTests
{
    [Fact]
    public void AsyncCallback_ReportsRYNCB004()
    {
        var diagnostics = Run("""
            using System.Threading.Tasks;
            using Ryn.Callbacks;
            using Ryn.Core;
            namespace TestApp;

            public static class Callbacks
            {
                [RynCallback(RynCallbackKind.WebViewNavigating)]
                public static async Task<NavigationDecision> OnNavigating(WebViewNavigatingContext context)
                {
                    await Task.Yield();
                    return NavigationDecision.Allow;
                }
            }
            """);

        diagnostics.Should().Contain(d =>
            d.Id == "RYNCB004" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WrongContextParameter_ReportsRYNCB005()
    {
        var diagnostics = Run("""
            using Ryn.Callbacks;
            using Ryn.Core;
            namespace TestApp;

            public static class Callbacks
            {
                [RynCallback(RynCallbackKind.WebViewNavigating)]
                public static NavigationDecision OnNavigating(WebViewNavigatedContext context) => NavigationDecision.Allow;
            }
            """);

        diagnostics.Should().Contain(d =>
            d.Id == "RYNCB005" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void WrongReturnType_ReportsRYNCB006()
    {
        var diagnostics = Run("""
            using Ryn.Callbacks;
            using Ryn.Core;
            namespace TestApp;

            public static class Callbacks
            {
                [RynCallback(RynCallbackKind.WebViewNavigated)]
                public static NavigationDecision OnNavigated(WebViewNavigatedContext context) => NavigationDecision.Allow;
            }
            """);

        diagnostics.Should().Contain(d =>
            d.Id == "RYNCB006" && d.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void DuplicateKindInOneContainingType_ReportsRYNCB007()
    {
        var diagnostics = Run("""
            using Ryn.Callbacks;
            using Ryn.Core;
            namespace TestApp;

            public static class Callbacks
            {
                [RynCallback(RynCallbackKind.WebViewNavigated)]
                public static void First(WebViewNavigatedContext context) { }

                [RynCallback(RynCallbackKind.WebViewNavigated)]
                public static void Second(WebViewNavigatedContext context) { }
            }
            """);

        diagnostics.Should().Contain(d =>
            d.Id == "RYNCB007" && d.Severity == DiagnosticSeverity.Error);
    }

    private static Diagnostic[] Run(string source)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(source);
        var references = new List<MetadataReference>();
        var trustedPlatformAssemblies =
            (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? string.Empty;

        foreach (var path in trustedPlatformAssemblies.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                references.Add(MetadataReference.CreateFromFile(path));
        }

        references.Add(MetadataReference.CreateFromFile(typeof(RynCallbackAttribute).Assembly.Location));
        references.Add(MetadataReference.CreateFromFile(typeof(WebViewNavigatingContext).Assembly.Location));

        var compilation = CSharpCompilation.Create(
            "CallbackDiagnosticTests",
            [syntaxTree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        var driver = (CSharpGeneratorDriver)CSharpGeneratorDriver.Create(new RynCallbackGenerator())
            .RunGeneratorsAndUpdateCompilation(compilation, out _, out _);

        return driver.GetRunResult().Results
            .SelectMany(result => result.Diagnostics)
            .ToArray();
    }
}
