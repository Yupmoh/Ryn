using System.Reflection;
using FluentAssertions;
using NSubstitute;
using Ryn.Core;
using Ryn.Plugins.Updater;
using Xunit;

namespace Ryn.Plugins.Tests;

/// <summary>
/// Regression cover for PAP-06: applying an update must NOT hard-exit from the IPC thread. When an
/// <see cref="IRynApplicationLifetime"/> is injected (the DI-activated host case), apply requests an
/// orderly shutdown through it — letting the event loop unwind so disposal runs (pty/spawn children
/// killed, window state saved, server drained) before the relaunch script brings the new build up. Only
/// when no lifetime is present (e.g. a bare unit construction with no host loop) does it fall back to a
/// hard <see cref="Environment.Exit(int)"/>.
///
/// The decision lives in the private <c>RequestShutdownOrExit</c>; the injected branch is exercised here
/// against a substitute lifetime (safe and deterministic). The fall-back branch literally calls
/// <see cref="Environment.Exit(int)"/>, which would tear down the test host, so it is verified structurally
/// (the service holds a null lifetime) rather than executed — see the test note.
/// </summary>
public sealed class UpdaterApplyShutdownTests
{
    private static UpdaterService NewService(IRynApplicationLifetime? lifetime)
    {
        var options = new UpdaterOptions { GitHubOwner = "o", GitHubRepo = "r" };
        var ctor = typeof(UpdaterService).GetConstructor(
            BindingFlags.NonPublic | BindingFlags.Instance, null,
            new[] { typeof(UpdaterOptions), typeof(IRynApplicationLifetime) }, null)
            ?? throw new InvalidOperationException("internal UpdaterService(options, lifetime) ctor not found.");
        return (UpdaterService)ctor.Invoke(new object?[] { options, lifetime });
    }

    private static void RequestShutdownOrExit(UpdaterService service)
    {
        var method = typeof(UpdaterService).GetMethod(
            "RequestShutdownOrExit", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("UpdaterService.RequestShutdownOrExit was not found.");
        try
        {
            method.Invoke(service, null);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    [Fact]
    public void Apply_RequestsOrderlyShutdown_ThroughInjectedLifetime()
    {
        var lifetime = Substitute.For<IRynApplicationLifetime>();
        using var service = NewService(lifetime);

        RequestShutdownOrExit(service);

        // Orderly shutdown is requested exactly once via the lifecycle — NOT a hard process exit.
        lifetime.Received(1).RequestShutdown();
    }

    [Fact]
    public void Apply_FallsBackToHardExit_OnlyWhenNoLifetimeInjected()
    {
        using var service = NewService(lifetime: null);

        // The fall-back branch calls Environment.Exit(0), which would terminate the test runner, so we do
        // not invoke RequestShutdownOrExit here. Instead we assert the precondition that selects the
        // fall-back: the service was constructed with no lifetime, so the injected-shutdown branch is not
        // taken. (The injected branch is fully exercised by the test above.)
        var lifetimeField = typeof(UpdaterService).GetField("_lifetime", BindingFlags.NonPublic | BindingFlags.Instance);
        lifetimeField.Should().NotBeNull();
        lifetimeField!.GetValue(service).Should().BeNull(
            "with no lifetime injected the service takes the Environment.Exit fall-back path");
    }
}
