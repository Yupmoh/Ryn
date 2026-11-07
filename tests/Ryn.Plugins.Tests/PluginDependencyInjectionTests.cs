using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Ryn.Core;
using Ryn.Ipc;
using Ryn.Plugins.FileSystem;
using Ryn.Plugins.Shell;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Plugin configuration used to live in process-global statics (finding 6). These tests verify it now lives
/// on per-application instances resolved from DI: the generated routers resolve the command classes with
/// their injected dependencies, and two independent containers enforce independent policies.
/// </summary>
public sealed class PluginDependencyInjectionTests : IDisposable
{
    private readonly List<ServiceProvider> _providers = [];
    private readonly List<string> _tempDirs = [];

    [Fact]
    public void Resolves_AllInstanceCommandClasses_FromDI()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRynWebView>()); // SpawnCommands/PtyCommands dependency
        services.AddRynFileSystem(o => o.AllowedPaths.Add(NewTempDir()));
        services.AddRynShell(o => o.AllowedCommands.Add("echo"));

        var sp = Build(services);

        sp.GetRequiredService<PathValidator>().Should().NotBeNull();
        sp.GetRequiredService<FileSystemCommands>().Should().NotBeNull();
        sp.GetRequiredService<ShellExecutionPolicy>().Should().NotBeNull();
        sp.GetRequiredService<ShellCommands>().Should().NotBeNull();
        sp.GetRequiredService<SpawnCommands>().Should().NotBeNull();
        sp.GetRequiredService<PtyCommands>().Should().NotBeNull();
        sp.GetServices<ICommandRouter>().Should().NotBeEmpty();
    }

    [Fact]
    public async Task TerminalRegistration_BuildsThroughRealApplicationExtensions()
    {
        var builder = RynApplication.CreateBuilder();
        builder.ConfigureServices(services =>
        {
            services.AddRynShell(o => o.AllowedCommands.Add("echo"));
            services.AddRynTerminal();
        });

        await using var app = builder.Build();

        app.Services.GetRequiredService<TerminalSessionService>().Should().NotBeNull();
        app.Services.GetRequiredService<TerminalCommands>().Should().NotBeNull();
        app.Services.GetServices<ShellExecutionPolicy>().Should().ContainSingle();
        app.Services.GetServices<IRynPlugin>().Should().ContainSingle(plugin => plugin is ShellPlugin);
    }

    [Fact]
    public void TerminalRegistration_RequiresConfiguredShellPolicy()
    {
        var services = new ServiceCollection();
        var register = () => services.AddRynTerminal();

        register.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TwoContainers_EnforceIndependentFileSystemPolicies()
    {
        // The crux of finding 6: with config on per-app instances instead of a global static, two apps in
        // one process keep separate policies. App A may write under dirA; App B (scoped to dirB) may not.
        var dirA = NewTempDir();
        var dirB = NewTempDir();

        var fsA = BuildFileSystem(dirA);
        var fsB = BuildFileSystem(dirB);

        var targetInA = Path.Combine(dirA, "note.txt");
        fsA.Invoking(f => f.WriteTextFile(targetInA, "ok")).Should().NotThrow();
        fsB.Invoking(f => f.WriteTextFile(targetInA, "blocked")).Should()
            .Throw<UnauthorizedAccessException>("dirA is outside App B's policy");
    }

    [Fact]
    public void TwoContainers_EnforceIndependentShellPolicies()
    {
        // App A allows "echo"; App B's allowlist is empty — resolving the same command must differ per app.
        var policyA = BuildShellPolicy(o => o.AllowedCommands.Add("echo"));
        var policyB = BuildShellPolicy(_ => { });

        policyA.Invoking(p => p.ValidateAndResolveCommand("echo")).Should().NotThrow();
        policyB.Invoking(p => p.ValidateAndResolveCommand("echo")).Should().Throw<UnauthorizedAccessException>();
    }

    private FileSystemCommands BuildFileSystem(string allowedDir)
    {
        var services = new ServiceCollection();
        services.AddRynFileSystem(o => o.AllowedPaths.Add(allowedDir));
        return Build(services).GetRequiredService<FileSystemCommands>();
    }

    private ShellExecutionPolicy BuildShellPolicy(Action<ShellOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IRynWebView>());
        services.AddRynShell(configure);
        return Build(services).GetRequiredService<ShellExecutionPolicy>();
    }

    private ServiceProvider Build(IServiceCollection services)
    {
        var sp = services.BuildServiceProvider();
        _providers.Add(sp);
        return sp;
    }

    private string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ryn-di-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var sp in _providers) sp.Dispose();
        foreach (var dir in _tempDirs)
        {
            try { Directory.Delete(dir, true); } catch (IOException) { /* best-effort cleanup */ }
        }
    }
}
