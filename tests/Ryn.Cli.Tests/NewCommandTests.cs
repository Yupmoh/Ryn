using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ryn.Cli.Tests;

public sealed class NewCommandTests : IDisposable
{
    private readonly string _tempDir;
    private static readonly string CliProjectPath = Path.GetFullPath(
        Path.Combine(FindRepoRoot(), "src", "Ryn.Cli", "Ryn.Cli.csproj"));

    public NewCommandTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ryn-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); }
            catch (IOException) { /* best effort cleanup */ }
        }
    }

    [Fact]
    public void New_CreatesProjectFiles()
    {
        var projectName = "TestApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        var projectDir = Path.Combine(_tempDir, projectName);
        Directory.Exists(projectDir).Should().BeTrue("project directory should be created");

        File.Exists(Path.Combine(projectDir, $"{projectName}.csproj")).Should().BeTrue("csproj should exist");
        File.Exists(Path.Combine(projectDir, "Program.cs")).Should().BeTrue("Program.cs should exist");
        File.Exists(Path.Combine(projectDir, "Commands.cs")).Should().BeTrue("Commands.cs should exist");
        File.Exists(Path.Combine(projectDir, "wwwroot", "index.html")).Should().BeTrue("index.html should exist");
        File.Exists(Path.Combine(projectDir, "appsettings.json")).Should().BeTrue("appsettings.json should exist");
        File.Exists(Path.Combine(projectDir, "ryn.json")).Should().BeTrue("ryn.json should exist");
    }

    [Fact]
    public void New_CreatesProjectFiles_WithCorrectContent()
    {
        var projectName = "MyApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        var projectDir = Path.Combine(_tempDir, projectName);

        // csproj should contain the project name as root namespace
        var csprojContent = File.ReadAllText(Path.Combine(projectDir, $"{projectName}.csproj"));
        csprojContent.Should().Contain($"<RootNamespace>{projectName}</RootNamespace>");
        csprojContent.Should().Contain("<TargetFramework>net10.0</TargetFramework>");
        csprojContent.Should().Contain("<OutputType>Exe</OutputType>");

        // Program.cs should reference the project namespace
        var programContent = File.ReadAllText(Path.Combine(projectDir, "Program.cs"));
        programContent.Should().Contain($"using {projectName};");

        // Commands.cs should use the project namespace
        var commandsContent = File.ReadAllText(Path.Combine(projectDir, "Commands.cs"));
        commandsContent.Should().Contain($"namespace {projectName};");

        // index.html should contain the project name as title
        var htmlContent = File.ReadAllText(Path.Combine(projectDir, "wwwroot", "index.html"));
        htmlContent.Should().Contain($"<title>{projectName}</title>");

        // appsettings.json should contain the project name
        var settingsContent = File.ReadAllText(Path.Combine(projectDir, "appsettings.json"));
        settingsContent.Should().Contain($"\"Title\": \"{projectName}\"");
    }

    [Theory]
    [InlineData("")]
    [InlineData("123invalid")]
    [InlineData("my-app")]
    [InlineData("app.name")]
    public void New_InvalidName_ReturnsError(string name)
    {
        var args = string.IsNullOrEmpty(name)
            ? new[] { "new" }
            : new[] { "new", name };

        var result = RunCli(args);

        result.ExitCode.Should().Be(1, because: $"invalid name '{name}' should be rejected");
    }

    [Fact]
    public void New_ExistingDirectory_ReturnsError()
    {
        var projectName = "ExistingApp";
        var existingDir = Path.Combine(_tempDir, projectName);
        Directory.CreateDirectory(existingDir);

        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("already exists");
    }

    [Fact]
    public void New_ProjectReferences_DetectedFromSource()
    {
        // When running from within the Ryn repo, the generated csproj
        // should contain ProjectReference rather than PackageReference.
        // Since tests run from the repo, AppContext.BaseDirectory will
        // resolve up to Ryn.slnx, triggering project references.
        var projectName = "RefTestApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        var csprojContent = File.ReadAllText(
            Path.Combine(_tempDir, projectName, $"{projectName}.csproj"));

        // When running from within the Ryn repo, FindRynSourceRoot walks up
        // from AppContext.BaseDirectory and finds Ryn.slnx, so we get
        // ProjectReference entries instead of PackageReference to Ryn packages.
        csprojContent.Should().Contain("ProjectReference");
        csprojContent.Should().Contain("Ryn.Core.csproj");
        csprojContent.Should().Contain("Ryn.Ipc.csproj");
        csprojContent.Should().Contain("Ryn.Ipc.Generator.csproj");
    }

    [Fact]
    public void New_ValidNames_AreAccepted()
    {
        // Verify various valid project names work
        var validNames = new[] { "App", "MyApp", "App123", "my_app", "A" };
        foreach (var name in validNames)
        {
            var result = RunCli("new", name);
            result.ExitCode.Should().Be(0, because: $"'{name}' is a valid project name. stderr: {result.StdErr}");

            // Verify the directory was created
            Directory.Exists(Path.Combine(_tempDir, name)).Should().BeTrue(
                $"directory for '{name}' should exist");
        }
    }

    [Fact]
    public void New_NoArgs_ReturnsError()
    {
        var result = RunCli("new");

        result.ExitCode.Should().Be(1);
        result.StdErr.Should().Contain("Usage:");
    }

    [Fact]
    public void New_CreatesWwwrootDirectory()
    {
        var projectName = "WwwApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        Directory.Exists(Path.Combine(_tempDir, projectName, "wwwroot"))
            .Should().BeTrue("wwwroot directory should be created");
    }

    [Fact]
    public void New_OutputContainsSuccessMessage()
    {
        var projectName = "OutputApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");
        result.StdOut.Should().Contain("created successfully");
        result.StdOut.Should().Contain(projectName);
    }

    [Fact]
    public void New_RynJsonContainsCapabilities()
    {
        var projectName = "CapApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        var rynJson = File.ReadAllText(Path.Combine(_tempDir, projectName, "ryn.json"));
        rynJson.Should().Contain("capabilities");
        rynJson.Should().Contain("fs");
        rynJson.Should().Contain("clipboard");
        rynJson.Should().Contain("notification");
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

        // Fallback: walk up from the test source file
        dir = Path.GetDirectoryName(typeof(NewCommandTests).Assembly.Location);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Ryn.slnx")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find Ryn repository root (Ryn.slnx)");
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}
