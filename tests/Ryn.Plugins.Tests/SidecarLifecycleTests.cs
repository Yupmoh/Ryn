using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Ryn.Core;
using Ryn.Plugins.Sidecar;
using Xunit;

namespace Ryn.Plugins.Tests;

public sealed class SidecarLifecycleTests : IAsyncLifetime
{
    private readonly string _tempDirectory = Path.Combine(Path.GetTempPath(), $"ryn-sidecar-{Guid.NewGuid():N}");
    private readonly ConcurrentBag<SidecarManager> _managers = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_tempDirectory);
        return Task.CompletedTask;
    }

    [UnixFact]
    public async Task ConcurrentStartReturnsOneOwnedProcessAndStopEscalatesIt()
    {
        var manager = CreateManager(Definition(
            "single",
            "printf 'ready:%s\\n' \"$RYN_SIDECAR_INSTANCE_TOKEN\"; while :; do sleep 1; done",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready:", RegexOptions.CultureInvariant)),
            identity: TokenIdentity()));

        var starts = Enumerable.Range(0, 8)
            .Select(_ => manager.StartAsync(new SidecarId("single")).AsTask());
        var statuses = await Task.WhenAll(starts).WaitAsync(TimeSpan.FromSeconds(10));

        statuses.Should().OnlyContain(status => status.State == SidecarState.ReadyOwned && status.IsOwned);
        statuses.Select(status => status.Identity!.ProcessId).Distinct().Should().ContainSingle();
        var processId = statuses[0].Identity!.ProcessId;

        await manager.StopAsync(new SidecarId("single")).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        manager.GetStatus(new SidecarId("single")).State.Should().Be(SidecarState.Stopped);
        IsRunning(processId).Should().BeFalse();
    }

    [UnixFact]
    public async Task UnresponsiveStopProtocolStillEscalatesOwnedProcess()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = CreateManager(Definition("hung-stop", "echo ready; while :; do sleep 1; done",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready$", RegexOptions.CultureInvariant)),
            identity: TokenIdentity(),
            gracefulStop: new SidecarCallbackStopProtocol((_, _) => new ValueTask(release.Task))));
        var started = await manager.StartAsync(new SidecarId("hung-stop"));
        try
        {
            await manager.StopAsync(new SidecarId("hung-stop")).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            IsRunning(started.Identity!.ProcessId).Should().BeFalse();
            manager.GetStatus(new SidecarId("hung-stop")).State.Should().Be(SidecarState.Stopped);
        }
        finally
        {
            release.TrySetResult();
        }
    }


    [UnixFact]
    public async Task SeparateManagersCannotLaunchDuplicateOwnedInstance()
    {
        var definition = Definition("shared-lock",
            "printf 'ready:%s\\n' \"$RYN_SIDECAR_INSTANCE_TOKEN\"; while :; do sleep 1; done",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready:", RegexOptions.CultureInvariant)),
            identity: TokenIdentity(), startupTimeout: TimeSpan.FromMilliseconds(500));
        var options = new SidecarOptions();
        options.Definitions.Add(definition.Id, definition);
        var application = new RynOptions { ApplicationId = $"com.ryn.tests.sidecar.{Guid.NewGuid():N}" };
        await using var first = new SidecarManager(options, application, new TestPaths(_tempDirectory));
        await using var second = new SidecarManager(options, application, new TestPaths(_tempDirectory));
        var owned = await first.StartAsync(definition.Id);
        var duplicate = async () => await second.StartAsync(definition.Id);
        await duplicate.Should().ThrowAsync<SidecarStartupException>();
        IsRunning(owned.Identity!.ProcessId).Should().BeTrue();
        second.GetStatus(definition.Id).IsOwned.Should().BeFalse();
        await first.StopAsync(definition.Id);
        var replacement = await second.StartAsync(definition.Id);
        replacement.IsOwned.Should().BeTrue();
        replacement.Identity!.ProcessId.Should().NotBe(owned.Identity.ProcessId);
    }

    [UnixFact]
    public async Task OwnedStopTerminatesDescendantProcess()
    {
        var childFile = Path.Combine(_tempDirectory, "descendant.pid");
        var manager = CreateManager(Definition("descendant",
            $"sleep 30 & echo $! > '{childFile}'; printf 'ready\\n'; wait",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready", RegexOptions.CultureInvariant)),
            identity: TokenIdentity()));
        await manager.StartAsync(new SidecarId("descendant"));
        var childPid = int.Parse(await File.ReadAllTextAsync(childFile), System.Globalization.CultureInfo.InvariantCulture);
        using var child = Process.GetProcessById(childPid);
        await manager.StopAsync(new SidecarId("descendant"));
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        child.HasExited.Should().BeTrue();
    }
    [UnixFact]
    public async Task UnixCrashOwnershipKillsOwnedRootAndDescendant()
    {
        var childFile = Path.Combine(_tempDirectory, "crash-descendant.pid");
        var id = new SidecarId("unix-crash-owned");
        var manager = CreateManager(Definition(
            id.Value,
            $"sleep 30 & echo $! > '{childFile}'; echo ready; wait",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready$", RegexOptions.CultureInvariant)),
            identity: TokenIdentity()));

        var started = await manager.StartAsync(id);
        var descendantPid = int.Parse(await File.ReadAllTextAsync(childFile), CultureInfo.InvariantCulture);
        manager.SimulateUnixHostCrashForTest(id);

        await WaitUntilAsync(
            () => !IsRunning(started.Identity!.ProcessId) && !IsRunning(descendantPid),
            TimeSpan.FromSeconds(5));
    }

    [UnixFact]
    public async Task StartupTimeoutReturnsBoundedRedactedDiagnosticsAndKillsOwnedProcess()
    {
        var pidFile = Path.Combine(_tempDirectory, "timed-out.pid");
        const string secret = "top-secret-value";
        var manager = CreateManager(Definition(
            "timeout",
            $"echo $$ > '{pidFile}'; i=0; while [ $i -lt 30 ]; do echo '{secret}-'$i; i=$((i+1)); done; while :; do sleep 1; done",
            readiness: new SidecarCallbackReadinessProbe(static async (_, token) =>
                await Task.Delay(Timeout.InfiniteTimeSpan, token)),
            identity: TokenIdentity(),
            startupTimeout: TimeSpan.FromSeconds(3),
            environment: new Dictionary<string, string> { ["SERVICE_TOKEN"] = secret }),
            maxLogEntries: 5,
            maxLogLineChars: 24);

        var exception = await Assert.ThrowsAsync<SidecarStartupException>(async () =>
            await manager.StartAsync(new SidecarId("timeout")));

        exception.Status.Should().NotBeNull();
        exception.Status!.State.Should().Be(SidecarState.Failed);
        exception.Status.Logs.Should().HaveCountLessThanOrEqualTo(5);
        exception.Status.Logs.Should().OnlyContain(entry => entry.Text.Length <= 24);
        exception.Status.Logs.Should().NotContain(entry => entry.Text.Contains(secret, StringComparison.Ordinal));
        var processId = int.Parse(await File.ReadAllTextAsync(pidFile), CultureInfo.InvariantCulture);
        IsRunning(processId).Should().BeFalse();
    }

    [UnixFact]
    public async Task ExternalAttachmentIsAuthenticatedAndNeverKilledOrSignalled()
    {
        var externalExecutable = ExternalExecutablePath;
        var readyFile = Path.Combine(_tempDirectory, "external-ready");
        using var external = StartExternal(externalExecutable, $"echo ready > '{readyFile}'; while :; do sleep 1; done");
        try
        {
            // Process.Start can return before exec replaces the launcher. The file handshake proves the shell
            // image is running before MainModule is captured for the manager's strict executable check.
            await WaitUntilAsync(() => File.Exists(readyFile), TimeSpan.FromSeconds(3));
            var stopRequests = 0;
            var externalIdentity = new SidecarProcessIdentity(
                external.Id,
                new DateTimeOffset(external.StartTime.ToUniversalTime(), TimeSpan.Zero),
                external.MainModule?.FileName ?? throw new InvalidOperationException("External executable identity is unavailable."),
                "external-token",
                "external-service");
            var manager = CreateManager(Definition(
                "external",
                "exit 99",
                readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
                identity: new SidecarCallbackIdentity((_, _) => ValueTask.FromResult("external-service")),
                externalConnector: new SidecarCallbackExternalConnector((_, _) => ValueTask.FromResult<SidecarProcessIdentity?>(externalIdentity)),
                gracefulStop: new SidecarCallbackStopProtocol((_, _) =>
                {
                    Interlocked.Increment(ref stopRequests);
                    return ValueTask.CompletedTask;
                }),
                executablePath: externalExecutable));

            var status = await manager.AttachAsync(new SidecarId("external"));
            status.State.Should().Be(SidecarState.ReadyExternal);
            status.IsOwned.Should().BeFalse();

            await manager.StopAsync(new SidecarId("external"));
            stopRequests.Should().Be(0);
            external.HasExited.Should().BeFalse();
        }
        finally
        {
            if (!external.HasExited)
            {
                external.Kill(entireProcessTree: true);
                await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [Fact]
    public async Task DiscoveryIgnoringCancellationCannotHoldStartupPastDeadline()
    {
        var release = new TaskCompletionSource<SidecarProcessIdentity?>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Discovery outlives the deadline, so nothing launches; validation still needs an existing absolute path.
        var manager = CreateManager(Definition("hung-discovery", "exit 99",
            readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
            identity: TokenIdentity(), startupTimeout: TimeSpan.FromMilliseconds(100),
            externalConnector: new SidecarCallbackExternalConnector((_, _) => new ValueTask<SidecarProcessIdentity?>(release.Task)),
            executablePath: OperatingSystem.IsWindows() ? WindowsCommandPath : null));
        try
        {
            await Assert.ThrowsAsync<SidecarStartupException>(async () =>
                await manager.StartAsync(new SidecarId("hung-discovery")).AsTask().WaitAsync(TimeSpan.FromSeconds(2)));
        }
        finally
        {
            release.TrySetResult(null);
        }
    }

    [UnixFact]
    public async Task ExternalIdentityWithWrongStartTimeIsRejectedWithoutKillingProcess()
    {
        var readyFile = Path.Combine(_tempDirectory, "stale-identity-ready");
        using var external = StartExternal(ExternalExecutablePath,
            $"echo ready > '{readyFile}'; while :; do sleep 1; done");
        try
        {
            await WaitUntilAsync(() => File.Exists(readyFile), TimeSpan.FromSeconds(3));
            var staleIdentity = new SidecarProcessIdentity(external.Id,
                new DateTimeOffset(external.StartTime.ToUniversalTime(), TimeSpan.Zero).AddMinutes(-1),
                external.MainModule!.FileName!, "token", "service");
            var manager = CreateManager(Definition("stale-identity", "exit 99",
                readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
                identity: new SidecarCallbackIdentity(static (_, _) => ValueTask.FromResult("service")),
                externalConnector: new SidecarCallbackExternalConnector((_, _) =>
                    ValueTask.FromResult<SidecarProcessIdentity?>(staleIdentity)),
                executablePath: ExternalExecutablePath));
            await Assert.ThrowsAsync<SidecarStartupException>(async () =>
                await manager.AttachAsync(new SidecarId("stale-identity")));
            await manager.DisposeAsync();
            external.HasExited.Should().BeFalse();
        }
        finally
        {
            if (!external.HasExited)
            {
                external.Kill(entireProcessTree: true);
                await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [UnixFact]
    public async Task ExternalProcessExitingDuringReadinessCannotBecomeReady()
    {
        var readyFile = Path.Combine(_tempDirectory, "exiting-external-ready");
        using var external = StartExternal(ExternalExecutablePath,
            $"echo ready > '{readyFile}'; while :; do sleep 1; done");
        try
        {
            await WaitUntilAsync(() => File.Exists(readyFile), TimeSpan.FromSeconds(3));
            var identity = new SidecarProcessIdentity(external.Id,
                new DateTimeOffset(external.StartTime.ToUniversalTime(), TimeSpan.Zero),
                external.MainModule!.FileName!, "token", "service");
            var manager = CreateManager(Definition("exiting-external", "exit 99",
                readiness: new SidecarCallbackReadinessProbe(async (_, token) =>
                {
                    external.Kill(entireProcessTree: true);
                    await external.WaitForExitAsync(token);
                }),
                identity: new SidecarCallbackIdentity(static (_, _) => ValueTask.FromResult("service")),
                externalConnector: new SidecarCallbackExternalConnector((_, _) =>
                    ValueTask.FromResult<SidecarProcessIdentity?>(identity)),
                executablePath: ExternalExecutablePath));
            await Assert.ThrowsAsync<SidecarStartupException>(async () =>
                await manager.AttachAsync(new SidecarId("exiting-external")));
            manager.GetStatus(new SidecarId("exiting-external")).State.Should().Be(SidecarState.Failed);
        }
        finally
        {
            if (!external.HasExited)
            {
                external.Kill(entireProcessTree: true);
                await external.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
    }

    [UnixFact]
    public async Task DisposeKeepsOptInProcessButStopForUpdateStopsIt()
    {
        SidecarProcessIdentity? discoverable = null;
        var connector = new SidecarCallbackExternalConnector((_, _) =>
            ValueTask.FromResult(discoverable is not null && IsRunning(discoverable.ProcessId) ? discoverable : null));
        var childFile = Path.Combine(_tempDirectory, "persistent-child.pid");
        var definition = Definition(
            "persistent",
            $"sleep 60 & echo $! > '{childFile}'; echo ready; wait",
            readiness: new SidecarCallbackReadinessProbe(async (_, token) =>
            {
                while (!File.Exists(childFile)) await Task.Delay(10, token);
            }),
            identity: TokenIdentity(),
            externalConnector: connector,
            keepRunning: true,
            executablePath: ExternalExecutablePath);
        var first = CreateManager(definition);
        var started = await first.StartAsync(new SidecarId("persistent"));
        discoverable = started.Identity;
        var childPid = int.Parse((await File.ReadAllTextAsync(childFile)).Trim(), CultureInfo.InvariantCulture);

        await first.DisposeAsync();
        IsRunning(discoverable!.ProcessId).Should().BeTrue();
        IsRunning(childPid).Should().BeTrue("keep-running disposal must preserve descendants");

        var second = CreateManager(definition);
        var attached = await second.StartAsync(new SidecarId("persistent"));
        attached.State.Should().Be(SidecarState.ReadyExternal);
        await second.StopForUpdateAsync();
        IsRunning(discoverable.ProcessId).Should().BeTrue("external instances never acquire updater stop rights");
        IsRunning(childPid).Should().BeTrue("reattached external services retain their process tree");
        externalKill(discoverable.ProcessId);

        static void externalKill(int pid)
        {
            using var process = Process.GetProcessById(pid);
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000).Should().BeTrue();
        }
    }

    [UnixFact]
    public async Task StopForUpdateStopsOwnedKeepRunningProcess()
    {
        var manager = CreateManager(Definition(
            "update",
            "while :; do sleep 1; done",
            readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
            identity: TokenIdentity(),
            externalConnector: new SidecarCallbackExternalConnector(static (_, _) =>
                ValueTask.FromResult<SidecarProcessIdentity?>(null)),
            keepRunning: true));
        var started = await manager.StartAsync(new SidecarId("update"));

        await manager.StopForUpdateAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));

        IsRunning(started.Identity!.ProcessId).Should().BeFalse();
        manager.GetStatus(new SidecarId("update")).State.Should().Be(SidecarState.Stopped);
    }

    [UnixFact]
    public async Task StopForUpdatePermanentlyRejectsConcurrentAndLaterLaunches()
    {
        var stopEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = CreateManager(Definition(
            "barrier",
            "while :; do sleep 1; done",
            readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
            identity: TokenIdentity(),
            gracefulStop: new SidecarCallbackStopProtocol(async (_, _) =>
            {
                stopEntered.TrySetResult();
                await releaseStop.Task;
            })));
        await manager.StartAsync(new SidecarId("barrier"));

        var updateStop = manager.StopForUpdateAsync().AsTask();
        await stopEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var concurrent = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.StartAsync(new SidecarId("barrier")));
        concurrent.Message.Should().Contain("StopForUpdateAsync");
        releaseStop.TrySetResult();
        await updateStop.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.StartAsync(new SidecarId("barrier")));
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await manager.AttachAsync(new SidecarId("barrier")));
    }

    [UnixFact]
    public async Task CancelledUpdateWaitStillBlocksLaunchesAndCanBeRetried()
    {
        var manager = CreateManager(Definition(
            "cancelled-update", "while :; do sleep 1; done",
            readiness: new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
            identity: TokenIdentity()));
        var id = new SidecarId("cancelled-update");
        var started = await manager.StartAsync(id);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await manager.StopForUpdateAsync(cancellation.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.StartAsync(id));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await manager.AttachAsync(id));
        await manager.StopForUpdateAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        IsRunning(started.Identity!.ProcessId).Should().BeFalse();
        manager.GetStatus(id).State.Should().Be(SidecarState.Stopped);
    }

    [UnixTheory]
    [InlineData("stop")]
    [InlineData("dispose")]
    [InlineData("update")]
    public async Task ShutdownCancelsPendingRestart(string operation)
    {
        var launchFile = Path.Combine(_tempDirectory, "launches.txt");
        var manager = CreateManager(Definition(
            "restart",
            $"echo launch >> '{launchFile}'; echo ready; sleep 0.15; exit 7",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready$", RegexOptions.CultureInvariant)),
            identity: TokenIdentity(),
            restart: new SidecarRestartPolicy
            {
                MaxAttempts = 3,
                InitialDelay = TimeSpan.FromSeconds(3),
                MaxDelay = TimeSpan.FromSeconds(3),
            }));
        await manager.StartAsync(new SidecarId("restart"));
        await WaitUntilAsync(
            () => manager.GetStatus(new SidecarId("restart")).State == SidecarState.Restarting,
            TimeSpan.FromSeconds(5));

        if (operation == "dispose")
            await manager.DisposeAsync();
        else if (operation == "update")
            await manager.StopForUpdateAsync();
        else
            await manager.StopAsync(new SidecarId("restart"));
        // Observe beyond the configured restart delay, not before an uncancelled retry could run.
        await Task.Delay(TimeSpan.FromMilliseconds(3500));

        (await File.ReadAllLinesAsync(launchFile)).Should().HaveCount(1);
        if (operation != "dispose")
            manager.GetStatus(new SidecarId("restart")).State.Should().Be(SidecarState.Stopped);
    }

    [UnixFact]
    public async Task RepeatedUnexpectedExitStopsAtConfiguredRestartLimit()
    {
        var launchFile = Path.Combine(_tempDirectory, "bounded-restarts.txt");
        var releasePrefix = Path.Combine(_tempDirectory, "release-restart-");
        var launchCount = 0;
        var id = new SidecarId("bounded-restarts");
        var manager = CreateManager(Definition(
            id.Value,
            $"echo launch >> '{launchFile}'; while [ ! -f '{releasePrefix}'$$ ]; do sleep 0.01; done; exit 7",
            readiness: new SidecarCallbackReadinessProbe(async (context, token) =>
            {
                var expected = Interlocked.Increment(ref launchCount);
                while (!File.Exists(launchFile) || (await File.ReadAllLinesAsync(launchFile, token)).Length < expected)
                    await Task.Delay(10, token);
                await File.WriteAllTextAsync(releasePrefix + context.Process!.Id.ToString(CultureInfo.InvariantCulture), "exit", token);
            }),
            identity: TokenIdentity(),
            restart: new SidecarRestartPolicy
            {
                MaxAttempts = 2,
                InitialDelay = TimeSpan.FromMilliseconds(50),
                MaxDelay = TimeSpan.FromMilliseconds(100),
            }));
        await manager.StartAsync(id);
        await WaitUntilAsync(() => manager.GetStatus(id).State == SidecarState.Exited, TimeSpan.FromSeconds(10));
        var status = manager.GetStatus(id);
        status.RestartAttempts.Should().Be(2);
        status.ExitCode.Should().Be(7);
        (await File.ReadAllLinesAsync(launchFile)).Should().HaveCount(3);
    }

    [UnixFact]
    public async Task GracefulStopAllowsChildCleanupAndPreservesExitCode()
    {
        var stopFile = Path.Combine(_tempDirectory, "stop-request");
        var cleanupFile = Path.Combine(_tempDirectory, "cleanup-complete");
        var id = new SidecarId("graceful-cleanup");
        var manager = CreateManager(Definition(
            id.Value,
            $"echo ready; while [ ! -f '{stopFile}' ]; do sleep 0.01; done; echo cleaned > '{cleanupFile}'; exit 23",
            readiness: new SidecarLogReadinessProbe(new Regex("^ready$", RegexOptions.CultureInvariant)),
            identity: TokenIdentity(),
            gracefulStop: new SidecarCallbackStopProtocol(async (_, token) =>
                await File.WriteAllTextAsync(stopFile, "stop", token))));
        await manager.StartAsync(id);
        await manager.StopAsync(id);
        manager.GetStatus(id).State.Should().Be(SidecarState.Stopped);
        manager.GetStatus(id).ExitCode.Should().Be(23);
        (await File.ReadAllTextAsync(cleanupFile)).Trim().Should().Be("cleaned");
    }

    [UnixFact]
    public async Task OwnedHelperReceivesValidEndOfFileStandardInput()
    {
        var id = new SidecarId("stdin-eof");
        var manager = CreateManager(Definition(
            id.Value,
            "if [ ! -e /dev/fd/0 ]; then exit 31; fi; if read value; then exit 32; fi; echo EOF_READY; while :; do sleep 1; done",
            readiness: new SidecarLogReadinessProbe(new Regex("^EOF_READY$", RegexOptions.CultureInvariant)),
            identity: TokenIdentity(),
            startupTimeout: TimeSpan.FromSeconds(10)));

        var started = await manager.StartAsync(id);
        started.State.Should().Be(SidecarState.ReadyOwned);
        await manager.StopAsync(id);
        IsRunning(started.Identity!.ProcessId).Should().BeFalse();
    }

    [UnixFact]
    public async Task OwnedLaunchPreservesArgumentsEnvironmentAndWorkingDirectory()
    {
        var id = new SidecarId("launch-contract");
        var directory = Path.Combine(_tempDirectory, "directory with spaces");
        Directory.CreateDirectory(directory);
        var output = Path.Combine(directory, "observed");
        const string value = "spaces 'quotes' $literal;\\tail";
        var manager = CreateManager(new SidecarDefinition
        {
            Id = id,
            ExecutablePath = ShellPath,
            Arguments = ["-c", "printf '%s\\n%s\\n%s' \"$1\" \"$RYN_PROBE_VALUE\" \"$PWD\" > observed; echo ready; while :; do sleep 1; done", "helper", value],
            Environment = new Dictionary<string, string> { ["RYN_PROBE_VALUE"] = value },
            WorkingDirectory = directory,
            Identity = TokenIdentity(),
            Readiness = new SidecarLogReadinessProbe(new Regex("^ready$", RegexOptions.CultureInvariant)),
        });

        await manager.StartAsync(id);
        var observed = await File.ReadAllLinesAsync(output);
        observed[0].Should().Be(value);
        observed[1].Should().Be(value);
        (await File.ReadAllTextAsync(Path.Combine(observed[2], "observed"))).Should().Be(await File.ReadAllTextAsync(output));
        await manager.StopAsync(id);
    }

    [UnixFact]
    public async Task UnixLauncherOmitsNullEnvironmentValues()
    {
        var startInfo = new ProcessStartInfo(ShellPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("printf '%s' \"${RYN_OMITTED_VALUE+present}\"");
        startInfo.Environment["RYN_OMITTED_VALUE"] = null;
        using var child = UnixSidecarProcess.Start(startInfo);
        var output = child.StandardOutput.ReadToEndAsync();
        await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        (await output).Should().BeEmpty("null environment entries are omitted, not exported as empty strings");
        child.ExitCode.Should().Be(0);
    }

    [UnixFact]
    public void UnixLauncherRejectsEmbeddedNullArguments()
    {
        var startInfo = new ProcessStartInfo(ShellPath);
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0\0ignored");
        Action launch = () =>
        {
            using var child = UnixSidecarProcess.Start(startInfo);
            child.Terminate();
            child.WaitForExitAsync().GetAwaiter().GetResult();
        };
        launch.Should().Throw<ArgumentException>();
    }

    [UnixFact]
    public void UnixLauncherRejectsEmbeddedNullWorkingDirectory()
    {
        var startInfo = new ProcessStartInfo(ShellPath) { WorkingDirectory = _tempDirectory + "\0ignored" };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("exit 0");
        Action launch = () =>
        {
            using var child = UnixSidecarProcess.Start(startInfo);
            child.Terminate();
            child.WaitForExitAsync().GetAwaiter().GetResult();
        };
        launch.Should().Throw<ArgumentException>();
    }

    [WindowsFact]
    public async Task WindowsCrashOwnershipKillsOwnedButExcludesKeepRunningAndExternal()
    {
        var command = WindowsCommandPath;
        var owned = CreateManager(WindowsDefinition("windows-owned", command, keepRunning: false));
        var started = await owned.StartAsync(new SidecarId("windows-owned"));
        GetWindowsLifetime(owned, new SidecarId("windows-owned"))
            .Should().NotBeNull("owned non-keep-running children require kernel crash ownership");
        owned.SimulateWindowsHostCrashForTest(new SidecarId("windows-owned"));
        await WaitUntilAsync(() => !IsRunning(started.Identity!.ProcessId), TimeSpan.FromSeconds(5));

        SidecarProcessIdentity? discoverable = null;
        var connector = new SidecarCallbackExternalConnector((_, _) => ValueTask.FromResult(discoverable));
        var keepDefinition = WindowsDefinition("windows-keep", command, keepRunning: true, connector);
        var keepManager = CreateManager(keepDefinition);
        var kept = await keepManager.StartAsync(keepDefinition.Id);
        discoverable = kept.Identity! with { ServiceIdentity = "windows-service" };
        try
        {
            GetWindowsLifetime(keepManager, keepDefinition.Id).Should().BeNull();
            await keepManager.DisposeAsync();
            IsRunning(kept.Identity!.ProcessId).Should().BeTrue();

            var externalManager = CreateManager(keepDefinition);
            var attached = await externalManager.AttachAsync(keepDefinition.Id);
            attached.IsOwned.Should().BeFalse();
            GetWindowsLifetime(externalManager, keepDefinition.Id).Should().BeNull();
            await externalManager.DisposeAsync();
            IsRunning(kept.Identity.ProcessId).Should().BeTrue();
        }
        finally
        {
            using var cleanup = Process.GetProcessById(kept.Identity!.ProcessId);
            cleanup.Kill(entireProcessTree: true);
            await cleanup.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [WindowsFact]
    public void WindowsOwnedLauncherReportsMissingExecutable()
    {
        var missing = Path.Combine(_tempDirectory, "does-not-exist.exe");
        var startInfo = new ProcessStartInfo(missing)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        var launch = () => WindowsSidecarProcess.Start(startInfo);

        launch.Should().Throw<System.ComponentModel.Win32Exception>()
            .Which.NativeErrorCode.Should().Be(2);
    }

    public async Task DisposeAsync()
    {
        foreach (var manager in _managers)
        {
            try
            {
                await manager.DisposeAsync();
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                // Best-effort fixture cleanup; individual tests assert lifecycle outcomes.
            }
        }
        try
        {
            Directory.Delete(_tempDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

#pragma warning disable CA2000, CA2025 // Listener lifetime is bounded by finally; tasks complete or are cancelled before teardown.
    [Fact]
    public async Task AuthenticatedHttpProbeRejectsRedirectWithoutFollowingIt()
    {
        var target = new TcpListener(IPAddress.Loopback, 0);
        var source = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            target.Start();
            var targetUri = ListenerUri(target);
            source.Start();
            var followed = 0;
            var targetTask = ServeOnceAsync(target, _ =>
            {
                Interlocked.Increment(ref followed);
                return "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\nidentity";
            });
            var sourceTask = ServeOnceAsync(source, _ =>
                $"HTTP/1.1 302 Found\r\nLocation: {targetUri}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            using var probe = new SidecarAuthenticatedHttpProbe(ListenerUri(source), "identity", "token");

            await Assert.ThrowsAsync<HttpRequestException>(async () =>
                await probe.VerifyAsync(ProbeContext(), CancellationToken.None));
            await sourceTask.WaitAsync(TimeSpan.FromSeconds(2));
            followed.Should().Be(0);
            target.Stop();
            await Assert.ThrowsAnyAsync<Exception>(async () => await targetTask.WaitAsync(TimeSpan.FromMilliseconds(200)));
        }
        finally
        {
            source.Stop();
            target.Stop();
        }
    }

    [Fact]
    public async Task AuthenticatedHttpProbeRejectsOversizedStreamedIdentity()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            var oversized = new string('x', 5000);
            var serverTask = ServeOnceAsync(listener, _ =>
                $"HTTP/1.1 200 OK\r\nConnection: close\r\n\r\n{oversized}");
            using var probe = new SidecarAuthenticatedHttpProbe(ListenerUri(listener), "identity", "token");

            await Assert.ThrowsAsync<InvalidDataException>(async () =>
                await probe.VerifyAsync(ProbeContext(), CancellationToken.None));
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task AuthenticatedHttpProbeTimesOutAfterHeadersWhenBodyStalls()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var releaseBody = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            listener.Start();
            var serverTask = ServeStalledBodyAsync(listener, headersSent, releaseBody.Task);
            // Generous deadline: a cold runner's first request must still reach the stalled body before it expires.
            using var probe = new SidecarAuthenticatedHttpProbe(
                ListenerUri(listener),
                "identity",
                "token",
                requestTimeout: TimeSpan.FromSeconds(2));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                await probe.VerifyAsync(ProbeContext(), CancellationToken.None));
            headersSent.Task.IsCompletedSuccessfully.Should().BeTrue("the deadline must expire while the body stalls, after the headers arrived");
            releaseBody.TrySetResult();
            await serverTask.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            releaseBody.TrySetResult();
            listener.Stop();
        }
    }
#pragma warning restore CA2000, CA2025


    private static SidecarProbeContext ProbeContext() => CreateProbeContext();

    private static SidecarProbeContext CreateProbeContext()
    {
        var constructor = typeof(SidecarProbeContext).GetConstructors(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Single();
        return (SidecarProbeContext)constructor.Invoke([
            null!, null!, string.Empty, (Func<IReadOnlyList<SidecarLogEntry>>)(static () => [])]);
    }

    private static Uri ListenerUri(TcpListener listener) =>
        new($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/identity");

    private static async Task ServeOnceAsync(TcpListener listener, Func<string, string> response)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var requestBuffer = new byte[4096];
        var read = await stream.ReadAsync(requestBuffer);
        var request = Encoding.ASCII.GetString(requestBuffer, 0, read);
        var bytes = Encoding.ASCII.GetBytes(response(request));
        await stream.WriteAsync(bytes);
    }

    private static async Task ServeStalledBodyAsync(TcpListener listener, TaskCompletionSource headersSent, Task releaseBody)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();
        var requestBuffer = new byte[4096];
        _ = await stream.ReadAsync(requestBuffer);
        var headers = Encoding.ASCII.GetBytes(
            "HTTP/1.1 200 OK\r\nContent-Length: 8\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(headers);
        await stream.FlushAsync();
        headersSent.TrySetResult();
        await releaseBody;
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("identity"));
        }
        catch (IOException)
        {
        }
    }

    private SidecarManager CreateManager(
        SidecarDefinition definition,
        int maxLogEntries = 20,
        int maxLogLineChars = 256)
    {
        var options = new SidecarOptions
        {
            MaxLogEntries = maxLogEntries,
            MaxLogLineChars = maxLogLineChars,
        };
        options.Definitions.Add(definition.Id, definition);
        var manager = new SidecarManager(
            options,
            new RynOptions { ApplicationId = $"com.ryn.tests.sidecar.{Guid.NewGuid():N}" },
            new TestPaths(_tempDirectory));
        _managers.Add(manager);
        return manager;
    }

    private static SidecarDefinition Definition(
        string id,
        string script,
        ISidecarReadinessProbe readiness,
        ISidecarIdentity identity,
        TimeSpan? startupTimeout = null,
        IReadOnlyDictionary<string, string>? environment = null,
        ISidecarExternalConnector? externalConnector = null,
        ISidecarStopProtocol? gracefulStop = null,
        bool keepRunning = false,
        SidecarRestartPolicy? restart = null,
        string? executablePath = null) => new()
        {
            Id = new SidecarId(id),
            ExecutablePath = executablePath ?? ShellPath,
            Arguments = ["-c", script],
            Environment = environment ?? new Dictionary<string, string>(),
            Identity = identity,
            Readiness = readiness,
            ExternalConnector = externalConnector,
            GracefulStop = gracefulStop,
            StartupTimeout = startupTimeout ?? TimeSpan.FromSeconds(3),
            StopTimeout = TimeSpan.FromMilliseconds(500),
            KeepRunning = keepRunning,
            AllowReassociation = keepRunning,
            Restart = restart ?? SidecarRestartPolicy.Disabled,
        };

    private static SidecarDefinition WindowsDefinition(
        string id,
        string executablePath,
        bool keepRunning,
        ISidecarExternalConnector? externalConnector = null) => new()
        {
            Id = new SidecarId(id),
            ExecutablePath = executablePath,
            Arguments = ["/d", "/s", "/c", "echo ready & ping -n 120 127.0.0.1 > nul"],
            Identity = new SidecarCallbackIdentity((context, _) => ValueTask.FromResult(
                context.Process is null ? "windows-service" : context.InstanceToken)),
            Readiness = new SidecarCallbackReadinessProbe(static (_, _) => ValueTask.CompletedTask),
            ExternalConnector = externalConnector,
            KeepRunning = keepRunning,
            AllowReassociation = keepRunning,
            StartupTimeout = TimeSpan.FromSeconds(5),
            StopTimeout = TimeSpan.FromSeconds(1),
        };

    private static object? GetWindowsLifetime(SidecarManager manager, SidecarId id)
    {
        var sidecars = (System.Collections.IDictionary)typeof(SidecarManager)
            .GetField("_sidecars", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(manager)!;
        var managed = sidecars[id]!;
        return managed.GetType()
            .GetProperty("WindowsLifetime", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)!
            .GetValue(managed);
    }


    private static SidecarCallbackIdentity TokenIdentity() =>
        new(static (context, _) => ValueTask.FromResult(context.InstanceToken));

    private static Process StartExternal(string executablePath, string script)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start test process.");
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }


    private static string ExternalExecutablePath => OperatingSystem.IsMacOS() ? "/bin/bash" : ShellPath;
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        using var deadline = new CancellationTokenSource(timeout);
        while (!predicate())
            await Task.Delay(20, deadline.Token);
    }

    private sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute()
        {
            if (!OperatingSystem.IsWindows())
                Skip = "Windows Job Object lifecycle fixture.";
        }
    }

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Unix shell lifecycle fixture.";
        }
    }

    private sealed class UnixTheoryAttribute : TheoryAttribute
    {
        public UnixTheoryAttribute()
        {
            if (OperatingSystem.IsWindows())
                Skip = "Unix shell lifecycle fixture.";
        }
    }

    private static string ShellPath => File.Exists("/bin/sh") ? "/bin/sh" : "/usr/bin/sh";

    private static string WindowsCommandPath => Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");

    private sealed class TestPaths(string root) : IRynPaths
    {
        public string LocalAppData { get; } = root;
        public string RoamingAppData { get; } = root;
        public string Documents { get; } = root;
        public string Cache { get; } = root;
        public string Temp { get; } = root;
        public string ResourceDirectory { get; } = root;
        public string InstallDirectory { get; } = root;
    }
}
