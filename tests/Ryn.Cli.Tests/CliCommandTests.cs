using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ryn.Cli.Tests;

public sealed class CliCommandTests
{
    private static readonly string CliProject = FindCliProject();

    [Fact]
    public async Task Version_PrintsVersionString()
    {
        var result = await RunCliAsync("--version");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().StartWith("ryn ");
    }

    [Fact]
    public async Task Help_PrintsUsage()
    {
        var result = await RunCliAsync("--help");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("Commands:");
        result.Stdout.Should().Contain("new");
        result.Stdout.Should().Contain("dev");
        result.Stdout.Should().Contain("build");
        result.Stdout.Should().Contain("bundle");
        result.Stdout.Should().Contain("doctor");
    }

    [Fact]
    public async Task UnknownCommand_ReturnsError()
    {
        var result = await RunCliAsync("nonexistent");
        result.ExitCode.Should().Be(1);
        result.Stderr.Should().Contain("Unknown command");
    }

    [Fact]
    public async Task Doctor_RunsAndPrintsChecks()
    {
        var result = await RunCliAsync("doctor");
        // Exit code may be non-zero on CI (missing native libs is expected)
        result.Stdout.Should().Contain("Ryn Doctor");
        result.Stdout.Should().Contain(".NET SDK");
        result.Stdout.Should().Contain("Native libraries");
    }

    [Fact]
    public async Task Build_NoCsproj_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-build-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await RunCliAsync("build", tempDir);
            result.ExitCode.Should().Be(1);
            result.Stderr.Should().Contain("No .csproj");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_NoCsproj_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-bundle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await RunCliAsync("bundle", tempDir);
            result.ExitCode.Should().Be(1);
            result.Stderr.Should().Contain("No .csproj");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_CrossRid_RejectsWithError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-rid-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
#pragma warning disable CA1849
        File.WriteAllText(Path.Combine(tempDir, "Test.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
#pragma warning restore CA1849
        try
        {
            var wrongRid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier == "osx-arm64"
                ? "linux-x64" : "osx-arm64";
            var result = await RunCliAsync("bundle", tempDir, "--rid", wrongRid);
            result.ExitCode.Should().Be(1);
            result.Stderr.Should().Contain("Cross-RID bundling is not supported");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_Help_ListsEmbedOption()
    {
        var result = await RunCliAsync("bundle", null, "--help");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("--embed", because: "bundle must document the embed option like build does");
    }

    [Fact]
    public async Task Bundle_Embed_NoWwwroot_ReturnsErrorWithoutPublishing()
    {
        // --embed must be a recognized flag (not exit 2 "Unknown option") and must fail fast with a clear
        // message when there's nothing to embed — before any dotnet publish is attempted.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-embed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        WriteMinimalCsproj(Path.Combine(tempDir, "App.csproj"));
        try
        {
            var result = await RunCliAsync("bundle", tempDir, "--embed");

            result.ExitCode.Should().Be(1, because: $"--embed with no wwwroot is an error. stderr: {result.Stderr}");
            result.Stderr.Should().Contain("wwwroot", because: "the error must name the missing wwwroot directory");
            result.Stderr.Should().NotContain("Unknown option", because: "--embed must be a recognized bundle flag");
            result.Stdout.Should().NotContain("Building", because: "the missing-wwwroot check must run before publish");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Bundle_Embed_BakesContentIntoBinaryAndStripsLooseWwwroot()
    {
        // Full chain: `ryn bundle --embed` must stage the zip, pass -p:RynEmbedContent=true, let the targets
        // bake it into the assembly as a manifest resource, strip the loose wwwroot, and clean the zip up.
        // Regression guard for the subtle bug where ResolvePublishDir's second `--getProperty:PublishDir`
        // evaluation rebuilt the assembly without the (already-deleted) zip and silently undid the embed.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-embed-int-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempDir, "wwwroot"));
        try
        {
            var targets = Path.Combine(RepoRoot(), "src", "Ryn.Core", "build", "Ryn.Core.targets");
#pragma warning disable CA1849
            File.WriteAllText(Path.Combine(tempDir, "wwwroot", "index.html"), "<html>EMBED-ME</html>");
            File.WriteAllText(Path.Combine(tempDir, "App.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">"
                + "<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup>"
                + "<ItemGroup><Content Include=\"wwwroot/**\" CopyToOutputDirectory=\"PreserveNewest\" /></ItemGroup>"
                + $"<Import Project=\"{targets}\" /></Project>");
            File.WriteAllText(Path.Combine(tempDir, "Program.cs"), "class P{static void Main(){}}");
#pragma warning restore CA1849

            var result = await RunCliAsync("bundle", tempDir, "--project", Path.Combine(tempDir, "App.csproj"), "--embed");

            result.ExitCode.Should().Be(0, because: $"the bundle must succeed. stdout:\n{result.Stdout}\nstderr:\n{result.Stderr}");

            // The transient staging zip must not survive the build.
            File.Exists(Path.Combine(tempDir, "ryn_embedded_content.zip")).Should().BeFalse("the staged zip is cleaned up");

            // The published assembly must carry the embedded resource (and the loose content must be gone).
            var publishedDll = Directory
                .EnumerateFiles(Path.Combine(tempDir, "bin"), "App.dll", SearchOption.AllDirectories)
                .FirstOrDefault(p => p.Contains("publish", StringComparison.Ordinal));
            publishedDll.Should().NotBeNull("publish must have produced App.dll");
#pragma warning disable CA1849
            var dllBytes = File.ReadAllBytes(publishedDll!);
#pragma warning restore CA1849
            ContainsAscii(dllBytes, "ryn_embedded_content.zip")
                .Should().BeTrue("the embedded zip resource must be baked into the assembly");

            var publishDir = Path.GetDirectoryName(publishedDll)!;
            File.Exists(Path.Combine(publishDir, "index.html"))
                .Should().BeFalse("the loose wwwroot content must be stripped from the publish output when embedding");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Dev_NoCsproj_ReturnsError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-dev-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await RunCliAsync("dev", tempDir);
            result.ExitCode.Should().Be(1);
            result.Stderr.Should().Contain("No .csproj");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- CLI-13: multiple .csproj must report "Multiple" (not the misleading "No .csproj") ----

    [Fact]
    public async Task Build_MultipleCsproj_ReportsMultipleAndNamesBoth()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-multi-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // Two non-Ryn projects in one directory: the resolver cannot pick a single Ryn app, so the
            // choice is genuinely ambiguous and must surface an actionable error rather than "No .csproj".
            WriteMinimalCsproj(Path.Combine(tempDir, "Alpha.csproj"));
            WriteMinimalCsproj(Path.Combine(tempDir, "Beta.csproj"));

            var result = await RunCliAsync("build", tempDir);

            result.ExitCode.Should().Be(1);
            result.Stderr.Should().Contain("Multiple .csproj", because: $"two projects are present. stderr: {result.Stderr}");
            result.Stderr.Should().NotContain("No .csproj", because: "the old misleading message must be gone");
            // Both candidates must be named so the user knows what to choose between.
            result.Stderr.Should().Contain("Alpha.csproj");
            result.Stderr.Should().Contain("Beta.csproj");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Build_MultipleCsproj_ProjectFlagSelectsOne()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-pick-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            WriteMinimalCsproj(Path.Combine(tempDir, "Alpha.csproj"));
            WriteMinimalCsproj(Path.Combine(tempDir, "Beta.csproj"));

            // --project disambiguates: resolution must succeed and the build must target Alpha. We assert
            // on the resolution outcome (it builds Alpha, not the ambiguity error) without depending on the
            // full publish succeeding — the resolver runs and prints "Building Alpha ..." before publish.
            var result = await RunCliAsync("build", tempDir, "--project", "Alpha.csproj");

            result.Stderr.Should().NotContain("Multiple .csproj", because: $"--project resolved the ambiguity. stdout: {result.Stdout} stderr: {result.Stderr}");
            result.Stderr.Should().NotContain("No .csproj");
            result.Stdout.Should().Contain("Building Alpha", because: "--project selected Alpha.csproj");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- CLI-11: per-command --help exits 0 without building; unknown flag exits 2 ----

    [Fact]
    public async Task Build_Help_PrintsUsageAndExitsZeroWithoutBuilding()
    {
        // Run inside a dir with NO project: if --help were ignored and the command ran, it would fail
        // with "No .csproj" and a nonzero exit. Exit 0 + usage proves --help short-circuits before Execute.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-help-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await RunCliAsync("build", tempDir, "--help");

            result.ExitCode.Should().Be(0, because: $"--help must not build. stderr: {result.Stderr}");
            result.Stdout.Should().Contain("Usage: ryn build");
            result.Stdout.Should().NotContain("Building", because: "--help must not invoke publish");
            result.Stderr.Should().NotContain("No .csproj", because: "--help must not run the command");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task Build_UnknownOption_ReturnsExitTwoWithError()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-bogus-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            // An unrecognized flag must be rejected (exit 2) rather than silently ignored, and before the
            // command runs — so we never reach the "No .csproj" path.
            var result = await RunCliAsync("build", tempDir, "--bogus");

            result.ExitCode.Should().Be(2, because: $"unknown flags are a usage error. stderr: {result.Stderr}");
            result.Stderr.Should().Contain("Unknown option");
            result.Stderr.Should().Contain("--bogus");
            result.Stdout.Should().NotContain("Building", because: "a bad flag must not start a build");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- SUP-05: `ryn updater keygen --out <tmp>` exits 0, prints a base64 public key, writes the key ----

    [Fact]
    public async Task UpdaterKeygen_PrintsPublicKeyAndWritesPrivateKeyFile()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-keygen-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var keyPath = Path.Combine(tempDir, "ryn-updater.key");
        try
        {
            var result = await RunCliAsync("updater", tempDir, "keygen", "--out", keyPath);

            result.ExitCode.Should().Be(0, because: $"keygen should succeed. stderr: {result.Stderr}");

            // The private key file must exist and contain decodable base64 PKCS#8 (the shape the updater signs with).
            File.Exists(keyPath).Should().BeTrue("the private key must be written to --out");
#pragma warning disable CA2007
            var privateKeyText = (await File.ReadAllTextAsync(keyPath)).Trim();
#pragma warning restore CA2007
            FluentActions.Invoking(() => Convert.FromBase64String(privateKeyText))
                .Should().NotThrow("the private key file must hold valid base64");

            // The public key is printed to stdout (and only the public key — never the private one).
            ExtractBase64PublicKey(result.Stdout, privateKeyText)
                .Should().NotBeNull($"keygen must print a base64 public key. stdout:\n{result.Stdout}");
            result.Stdout.Should().NotContain(privateKeyText, "the private key must never be echoed to the terminal");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    // ---- CLI-10: doctor runs from outside the repo and does not FAIL on native libraries ----

    [Fact]
    public async Task Doctor_OutsideRepo_NativeLibrariesNotFatal()
    {
        // From a temp dir with no src/Ryn.Interop ancestor, the missing native libs must be a WARN, not a
        // FAIL (CLI-03/CLI-10): an installed-tool user ships natives with their app, not the global tool.
        var tempDir = Path.Combine(Path.GetTempPath(), $"ryn-doctor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var result = await RunCliAsync("doctor", tempDir);

            result.Stdout.Should().Contain("Ryn Doctor");
            result.Stdout.Should().Contain("Native libraries");

            // The "Native libraries" check must not be tagged FAIL outside the repo.
            var nativeLine = result.Stdout
                .Split('\n')
                .FirstOrDefault(l => l.Contains("Native libraries", StringComparison.Ordinal));
            nativeLine.Should().NotBeNull();
            nativeLine!.Should().NotContain("FAIL", because: $"missing native libs outside the repo are a WARN, not FAIL. line: {nativeLine}");

            // On macOS the WebView check is always OK (WebKit ships with the OS) and dotnet 10 is present,
            // so doctor exits 0 outside the repo. On Linux/Windows CI a clean runner may legitimately FAIL
            // the platform WebView check, so we only assert the exit code where it is deterministic.
            if (OperatingSystem.IsMacOS())
            {
                result.ExitCode.Should().Be(0, because: $"on macOS no check FAILs outside the repo. stdout:\n{result.Stdout}");
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static void WriteMinimalCsproj(string path)
    {
        // A syntactically valid but deliberately non-Ryn project, so the resolver treats two of them as
        // ambiguous (it only auto-picks when exactly one is a Ryn app project).
#pragma warning disable CA1849
        File.WriteAllText(
            path,
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType>"
            + "<TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
#pragma warning restore CA1849
    }

    // Returns the base64 public key line keygen printed, or null. The public key is a standalone base64
    // line (SPKI) that is not the private key and decodes to a shorter blob than the PKCS#8 private key.
    private static string? ExtractBase64PublicKey(string stdout, string privateKeyText)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 40 || line == privateKeyText)
                continue;
            // base64 SPKI for P-256 is ~120 chars and decodes cleanly; prose lines won't.
            if (!IsBase64(line))
                continue;
            var bytes = Convert.FromBase64String(line);
            if (bytes.Length is >= 80 and <= 160)
                return line;
        }

        return null;
    }

    private static bool IsBase64(string s)
    {
        Span<byte> scratch = stackalloc byte[256];
        return Convert.TryFromBase64String(s, scratch, out _);
    }

    private static async Task<CliResult> RunCliAsync(string command, string? workingDir = null, params string[] extraArgs)
    {
#pragma warning disable CA2007
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("run");
        process.StartInfo.ArgumentList.Add("--project");
        process.StartInfo.ArgumentList.Add(CliProject);
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(command);
        foreach (var arg in extraArgs)
            process.StartInfo.ArgumentList.Add(arg);

        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
#pragma warning restore CA2007
    }

    private static string FindCliProject()
    {
        var dir = Path.GetDirectoryName(typeof(CliCommandTests).Assembly.Location)!;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, "src", "Ryn.Cli", "Ryn.Cli.csproj");
            if (File.Exists(candidate)) return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot find Ryn.Cli.csproj");
    }

    /// <summary>Walks up from the test assembly to the repository root (the directory that contains <c>src/</c>).</summary>
    private static string RepoRoot()
    {
        var dir = Path.GetDirectoryName(typeof(CliCommandTests).Assembly.Location)!;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Ryn.Core", "build")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("Cannot find the repository root");
    }

    /// <summary>True if <paramref name="haystack"/> contains <paramref name="needle"/> as a contiguous ASCII run
    /// (managed manifest-resource names live in the metadata #Strings heap as UTF-8, so they appear verbatim).</summary>
    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        var pattern = System.Text.Encoding.ASCII.GetBytes(needle);
        for (var i = 0; i <= haystack.Length - pattern.Length; i++)
        {
            var match = true;
            for (var j = 0; j < pattern.Length; j++)
            {
                if (haystack[i + j] != pattern[j]) { match = false; break; }
            }
            if (match) return true;
        }
        return false;
    }

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
