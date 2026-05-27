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
    public async Task Doctor_RunsAndReturnsZero()
    {
        var result = await RunCliAsync("doctor");
        result.ExitCode.Should().Be(0);
        result.Stdout.Should().Contain("Ryn Doctor");
        result.Stdout.Should().Contain(".NET SDK");
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

    private static async Task<CliResult> RunCliAsync(string command, string? workingDir = null)
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

    private sealed record CliResult(int ExitCode, string Stdout, string Stderr);
}
