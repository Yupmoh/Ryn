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

    // CLI-15: a project name is emitted verbatim as a `namespace`/`using`/`<RootNamespace>` (C# keywords)
    // and as a directory/file name (Windows reserved device names), so both classes of name produce an
    // unusable project and must be rejected up front with a name-validation message. The reserved-name
    // check is case-insensitive ("nul" == "NUL"). Regression for names that previously scaffolded a
    // project that could never compile / could not be created on Windows.
    [Theory]
    [InlineData("class", "keyword")]
    [InlineData("public", "keyword")]
    [InlineData("event", "keyword")]
    [InlineData("CON", "reserved")]
    [InlineData("nul", "reserved")]
    public void New_KeywordOrReservedName_ReturnsErrorWithMessage(string name, string kind)
    {
        var result = RunCli("new", name);

        result.ExitCode.Should().Be(1, because: $"'{name}' is a C# {kind} and cannot be a project name. stderr: {result.StdErr}");

        // The validation message must name the rule that was violated so the failure is actionable.
        var expectedReason = kind == "keyword"
            ? "is a C# keyword"
            : "is a reserved device name";
        result.StdErr.Should().Contain("Invalid project name");
        result.StdErr.Should().Contain(expectedReason);

        // A rejected name must not leave a half-scaffolded project directory behind.
        Directory.Exists(Path.Combine(_tempDir, name)).Should().BeFalse(
            $"a rejected name must not create the '{name}' directory");
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

        // The scaffold now grants only the demo "app" capability so a first-run project
        // can invoke its own commands without being silently denied. The old plugin grants
        // (fs/clipboard/notification) were removed because the template ships no such commands.
        rynJson.Should().Contain("capabilities");
        rynJson.Should().Contain("app");
        rynJson.Should().NotContain("fs");
        rynJson.Should().NotContain("clipboard");
        rynJson.Should().NotContain("notification");
    }

    [Fact]
    public void New_ScaffoldedRynJson_AllowsDemoCommandsButDeniesUnprefixed()
    {
        // Regression for the first-run demo: a freshly scaffolded ryn.json must grant the
        // demo commands (app.greet/app.add/app.getTime) so they work out of the box, while a
        // bare, unprefixed command id is still denied. We resolve the capabilities through the
        // same rules RynCapabilitiesLoader applies (plugin-prefixed grants, no-prefix => denied),
        // exercised against the real scaffold output. The loader itself cannot be called directly
        // here: Ryn.Cli.Tests has no reference to Ryn.Ipc (these tests drive the CLI out-of-process),
        // and Load() reads ryn.json from AppContext.BaseDirectory rather than an arbitrary path.
        var projectName = "DemoCapApp";
        var result = RunCli("new", projectName);

        result.ExitCode.Should().Be(0, because: $"CLI should succeed. stderr: {result.StdErr}");

        var rynJson = File.ReadAllText(Path.Combine(_tempDir, projectName, "ryn.json"));
        var caps = Capabilities.FromRynJson(rynJson);

        // The first-run demo command is allowed because the scaffold grants the "app" plugin.
        caps.IsAllowed("app.greet").Should().BeTrue("the scaffold grants the demo 'app' capability");
        caps.IsAllowed("app.add").Should().BeTrue("the scaffold grants the demo 'app' capability");
        caps.IsAllowed("app.getTime").Should().BeTrue("the scaffold grants the demo 'app' capability");

        // A bare command id with no plugin prefix is denied (mirrors ThrowIfDenied's "no plugin prefix").
        caps.IsAllowed("greet").Should().BeFalse("an unprefixed command has no granting plugin and is denied");
    }

    /// <summary>
    /// Minimal capability evaluator that mirrors the contract enforced by
    /// <c>Ryn.Ipc.RynCapabilitiesLoader</c>/<c>RynCapabilities.ThrowIfDenied</c>: a command is
    /// <c>plugin.command</c>; a plugin granted <c>true</c> allows all of its commands; a command
    /// without a plugin prefix is denied. Reimplemented here (rather than referenced) because this
    /// test project does not link against Ryn.Ipc; see the note in the test above.
    /// </summary>
    private sealed class Capabilities
    {
        private readonly HashSet<string> _allowAllPlugins;

        private Capabilities(HashSet<string> allowAllPlugins) => _allowAllPlugins = allowAllPlugins;

        public static Capabilities FromRynJson(string json)
        {
            var allowAll = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var doc = System.Text.Json.JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("capabilities", out var caps)
                && caps.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var plugin in caps.EnumerateObject())
                {
                    if (plugin.Value.ValueKind == System.Text.Json.JsonValueKind.True)
                        allowAll.Add(plugin.Name);
                }
            }

            return new Capabilities(allowAll);
        }

        public bool IsAllowed(string command)
        {
            var dot = command.IndexOf('.', StringComparison.Ordinal);
            if (dot < 0)
                return false; // no plugin prefix => denied, same as ThrowIfDenied

            var prefix = command[..dot];
            return _allowAllPlugins.Contains(prefix);
        }
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
