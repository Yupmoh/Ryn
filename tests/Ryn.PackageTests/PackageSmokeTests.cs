using System.Diagnostics;
using FluentAssertions;
using Xunit;

namespace Ryn.PackageTests;

[Trait("Category", "Package")]
public sealed class PackageSmokeTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    [Fact]
    public async Task NuGet_consumer_builds_and_generator_emits_router()
    {
        var solutionRoot = FindSolutionRoot();
        var packOutputDir = CreateTempDir("ryn-pack");
        var appDir = CreateTempDir("ryn-smoke-app");

        // Unique version per run so a stale copy in the global NuGet cache can never satisfy the
        // restore (reusing a fixed version masks packaging changes — exactly how the duplicate
        // generator hid for a while).
        var version = "99.0.0-test" + Guid.NewGuid().ToString("N")[..8];

        // Pack the metapackage (Ryn) and a plugin, plus everything they depend on. Referencing
        // Ryn + a plugin is the supported app shape AND the case that regressed: the IPC source
        // generator must come through exactly once (it ships only in Ryn.Ipc).
#pragma warning disable CA2007 // xUnit manages SynchronizationContext
        string[] projectsToPack =
        [
            Path.Combine(solutionRoot, "src", "Ryn.Interop", "Ryn.Interop.csproj"),
            Path.Combine(solutionRoot, "src", "Ryn.Ipc.Generator", "Ryn.Ipc.Generator.csproj"),
            Path.Combine(solutionRoot, "src", "Ryn.Core", "Ryn.Core.csproj"),
            Path.Combine(solutionRoot, "src", "Ryn.Ipc", "Ryn.Ipc.csproj"),
            Path.Combine(solutionRoot, "src", "Ryn", "Ryn.csproj"),
            Path.Combine(solutionRoot, "src", "Ryn.Plugins.Clipboard", "Ryn.Plugins.Clipboard.csproj"),
        ];

        foreach (var project in projectsToPack)
        {
            var packResult = await RunDotnetAsync(
                solutionRoot,
                "pack", project,
                "-c", "Release",
                $"-p:MinVerVersionOverride={version}",
                "-o", packOutputDir);

            packResult.ExitCode.Should().Be(0,
                $"dotnet pack {Path.GetFileName(project)} failed:\n{packResult.Output}");
        }

        // Verify the metapackage and plugin were produced
        var nupkgs = Directory.GetFiles(packOutputDir, "*.nupkg").Select(Path.GetFileName).ToList();
        nupkgs.Should().Contain($"Ryn.{version}.nupkg");
        nupkgs.Should().Contain($"Ryn.Plugins.Clipboard.{version}.nupkg");

        // Create a minimal consumer that references ONLY Ryn + the plugin
        WriteTempApp(appDir, packOutputDir, version);

        // Build it. If the generator were shipped twice, this fails with CS0101/CS0111
        // (duplicate AddCommands); if it didn't flow at all, AddCommands wouldn't resolve.
        var buildResult = await RunDotnetAsync(appDir, "build", "-c", "Release");
        buildResult.ExitCode.Should().Be(0, $"dotnet build failed:\n{buildResult.Output}");

        // The source generator produced exactly one router
        var generatedFiles = Directory.GetFiles(
            Path.Combine(appDir, "obj"), "*Router.g.cs", SearchOption.AllDirectories);
        generatedFiles.Should().ContainSingle(
            "the generator should emit exactly one Router.g.cs (two = the duplicate-generator bug)");

        var routerContent = await File.ReadAllTextAsync(generatedFiles[0]);
        routerContent.Should().Contain("ICommandRouter", "the router should implement ICommandRouter");
        routerContent.Should().Contain("greet", "the router should handle the 'greet' command");

        var outputDir = Path.Combine(appDir, "bin", "Release", "net10.0");
        Directory.Exists(outputDir).Should().BeTrue("the build should produce output in bin/Release/net10.0");
#pragma warning restore CA2007
    }

    private static void WriteTempApp(string appDir, string feedDir, string version)
    {
        // nuget.config — local feed + nuget.org (for the Microsoft.Extensions transitive deps);
        // an isolated packages folder so the run never touches/poisons the global cache.
        var nugetConfig = """
            <?xml version="1.0" encoding="utf-8"?>
            <configuration>
              <config>
                <add key="globalPackagesFolder" value="packages" />
              </config>
              <packageSources>
                <clear />
                <add key="local" value="FEED_DIR" />
                <add key="nuget" value="https://api.nuget.org/v3/index.json" />
              </packageSources>
            </configuration>
            """.Replace("FEED_DIR", feedDir, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(appDir, "nuget.config"), nugetConfig);

        // .csproj — references ONLY Ryn + a plugin (the supported app shape)
        File.WriteAllText(
            Path.Combine(appDir, "SmokeApp.csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <LangVersion>preview</LangVersion>
                <Nullable>enable</Nullable>
                <ImplicitUsings>enable</ImplicitUsings>
                <!-- Write source-generator output to disk so the test can inspect it -->
                <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
                <NoWarn>CA1812;CA1852</NoWarn>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="Ryn" Version="{version}" />
                <PackageReference Include="Ryn.Plugins.Clipboard" Version="{version}" />
              </ItemGroup>
            </Project>
            """);

        // Program.cs — exercises RynApplication + the generated DI extension
        File.WriteAllText(
            Path.Combine(appDir, "Program.cs"),
            """
            using Ryn.Core;
            using Ryn.Ipc;
            using SmokeApp;

            var app = RynApplication.CreateBuilder()
                .ConfigureServices(services =>
                {
                    services.AddRynCommands();
                    services.AddCommands();
                })
                .Build();
            """);

        // Commands.cs — a [RynCommand] method so the generator has work to do
        File.WriteAllText(
            Path.Combine(appDir, "Commands.cs"),
            """
            using Ryn.Ipc;

            namespace SmokeApp;

            public static class Commands
            {
                [RynCommand]
                public static string Greet(string name) => $"Hello, {name}!";
            }
            """);
    }

    private static string FindSolutionRoot()
    {
        // Walk up from the test assembly location to find Ryn.slnx
        var dir = Path.GetDirectoryName(typeof(PackageSmokeTests).Assembly.Location)!;

        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "Ryn.slnx")))
            {
                return dir;
            }

            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException(
            "Could not find Ryn.slnx -- run this test from within the Ryn repository.");
    }

    private string CreateTempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    private static async Task<ProcessResult> RunDotnetAsync(string workingDir, params string[] args)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
        {
            process.StartInfo.ArgumentList.Add(arg);
        }

        // Disable persistent build servers (MSBuild node reuse + VB/C# compiler server). On a cold
        // start they would be spawned by this build, inherit the redirected stdout/stderr handles,
        // and keep the pipe open after dotnet exits — so ReadToEndAsync never sees EOF and the test
        // hangs until the server times out (~15 min). It only "passes" when a warm server already exists.
        process.StartInfo.ArgumentList.Add("--disable-build-servers");
        process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";

        process.Start();

        // Read both streams to avoid deadlocks
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync().ConfigureAwait(false);

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, stdout + stderr);
    }

    public void Dispose()
    {
        foreach (var dir in _tempDirs)
        {
            try
            {
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
#pragma warning disable CA1031 // Best-effort temp directory cleanup
            catch
#pragma warning restore CA1031
            {
                // Cleanup is best-effort; don't fail the test over leftover temp dirs
            }
        }
    }

    private sealed record ProcessResult(int ExitCode, string Output);
}
