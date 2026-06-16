using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace Ryn.Cli.Tests;

/// <summary>
/// Anti-drift lock for CLI-16: the two scaffolders — the <c>ryn new</c> string-literal generator (NewCommand)
/// and the <c>dotnet new ryn</c> template under <c>templates/ryn-app</c> — must produce the same default
/// (HTML) project. This drives the real <c>ryn new</c> and compares its output, file by file, against the
/// template's source files (after the template's <c>RynApp</c> → name substitution). If either scaffold
/// changes without the other, a file diverges and this fails. The project file is compared on its
/// reference-INDEPENDENT sections only: from inside the repo <c>ryn new</c> emits ProjectReferences while the
/// template references the <c>Ryn</c> metapackage — an intended difference, not drift.
/// </summary>
public sealed class ScaffoldParityTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CliProjectPath =
        Path.GetFullPath(Path.Combine(RepoRoot, "src", "Ryn.Cli", "Ryn.Cli.csproj"));
    private static readonly string TemplateDir =
        Path.GetFullPath(Path.Combine(RepoRoot, "templates", "ryn-app"));

    private readonly string _tempDir;

    public ScaffoldParityTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ryn-parity-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [Fact]
    public void RynNew_And_DotnetNewTemplate_ProduceMatchingDefaultScaffold()
    {
        const string name = "ParityApp";
        var result = RunCli("new", name);
        result.ExitCode.Should().Be(0, because: $"ryn new should succeed. stderr: {result.StdErr}");

        var genDir = Path.Combine(_tempDir, name);

        // Reference-independent files must match the template byte-for-byte after the sourceName swap.
        string[] sharedFiles =
        [
            "Program.cs", "Commands.cs", "ryn.json", "appsettings.json",
            Path.Combine("wwwroot", "index.html"),
        ];
        foreach (var rel in sharedFiles)
        {
            var fromTemplate = Substitute(ReadTemplate(rel), name);
            var fromRynNew = Read(Path.Combine(genDir, rel));
            Norm(fromRynNew).Should().Be(Norm(fromTemplate),
                because: $"'{rel}' must be identical across `ryn new` and the dotnet-new template");
        }

        // The .csproj differs only in its reference ItemGroup (ProjectReference vs the Ryn metapackage). The
        // PropertyGroup and the Content ItemGroup must still match exactly.
        var tmplCsproj = Substitute(ReadTemplate("RynApp1.csproj"), name);
        var genCsproj = Read(Path.Combine(genDir, $"{name}.csproj"));
        Norm(Block(genCsproj, "<PropertyGroup>", "</PropertyGroup>"))
            .Should().Be(Norm(Block(tmplCsproj, "<PropertyGroup>", "</PropertyGroup>")),
                because: "the csproj PropertyGroup must match across both scaffolds");
        Norm(Block(genCsproj, "<ItemGroup>", "</ItemGroup>"))
            .Should().Be(Norm(Block(tmplCsproj, "<ItemGroup>", "</ItemGroup>")),
                because: "the Content ItemGroup (first in the file) must match across both scaffolds");
    }

    [Fact]
    public void Template_FrameworkDefault_MatchesRynNewHardcodedFramework()
    {
        // `ryn new` hardcodes net10.0; the template's Framework parameter defaults to the same, so the two
        // scaffolds agree at default settings. This pins that reconciliation.
        var json = File.ReadAllText(Path.Combine(TemplateDir, ".template.config", "template.json"));
        using var doc = JsonDocument.Parse(json);
        var framework = doc.RootElement
            .GetProperty("symbols").GetProperty("Framework").GetProperty("defaultValue").GetString();
        framework.Should().Be("net10.0");

        var rynNewCsproj = ReadTemplate("RynApp1.csproj");
        rynNewCsproj.Should().Contain("<TargetFramework>net10.0</TargetFramework>");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static string ReadTemplate(string relativePath) =>
        File.ReadAllText(Path.Combine(TemplateDir, relativePath));

    private static string Read(string path) => File.ReadAllText(path);

    // The template uses the literal sourceName "RynApp1"; `dotnet new` swaps it for the project name. The
    // "1" suffix keeps the token from being a prefix of the real type "RynApplication" (a naive sourceName
    // replace would otherwise corrupt it), so this substitution only touches the intended namespace/title spots.
    private static string Substitute(string content, string name) =>
        content.Replace("RynApp1", name, StringComparison.Ordinal);

    // Normalize line endings and ignore a trailing newline difference; content drift still fails.
    private static string Norm(string s) => s.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');

    private static string Block(string xml, string open, string close)
    {
        var start = xml.IndexOf(open, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, because: $"'{open}' should be present");
        var end = xml.IndexOf(close, start, StringComparison.Ordinal);
        end.Should().BeGreaterThanOrEqualTo(0, because: $"'{close}' should follow '{open}'");
        return xml.Substring(start, end - start + close.Length);
    }

    private CliResult RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"run --project \"{CliProjectPath}\" -- {string.Join(' ', args)}",
            WorkingDirectory = _tempDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(TimeSpan.FromSeconds(120));

        return new CliResult(process.ExitCode, stdout, stderr);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Ryn.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Could not find Ryn repository root (Ryn.slnx)");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* best effort cleanup */ }
        }
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}
