using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ryn.Core;

namespace Ryn.Plugins.Sidecar;

/// <summary>
/// Supervises host-approved local sidecars. Definitions are trusted host configuration and intentionally do
/// not use the renderer-facing Shell allowlist: executable paths must be absolute and argv is passed directly
/// through <see cref="ProcessStartInfo.ArgumentList"/> without shell interpolation.
/// </summary>
public sealed class SidecarManager : ISidecarManager
{
    private const string InstanceTokenVariable = "RYN_SIDECAR_INSTANCE_TOKEN";
    private static readonly TimeSpan LockRetryDelay = TimeSpan.FromMilliseconds(50);
    private static readonly object UnixForkGate = new();

    private readonly Dictionary<SidecarId, ManagedSidecar> _sidecars;
    private readonly string _lockDirectory;
    private readonly Dictionary<ISidecarEnvironmentProvider, Task<IReadOnlyDictionary<string, string>>> _environmentCache = [];
    private int _updateStopping;
    private int _disposed;

    public SidecarManager(SidecarOptions options, RynOptions applicationOptions, IRynPaths paths)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applicationOptions);
        ArgumentNullException.ThrowIfNull(paths);
        ValidateOptions(options);

        _sidecars = new Dictionary<SidecarId, ManagedSidecar>(options.Definitions.Count);
        foreach (var pair in options.Definitions)
            _sidecars.Add(pair.Key, new ManagedSidecar(pair.Value, options));

        var applicationKey = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(applicationOptions.ApplicationId)));
        _lockDirectory = Path.Combine(paths.LocalAppData, "Ryn", "sidecars", applicationKey);
        CreatePrivateDirectory(_lockDirectory);
    }

    public async ValueTask<SidecarStatus> StartAsync(
        SidecarId id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfLaunchBlocked();
        var sidecar = GetSidecar(id);
        sidecar.CancelPendingRestart();
        await sidecar.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfLaunchBlocked();
            if (sidecar.IsLive)
                return sidecar.Snapshot();

            sidecar.ExplicitStop = false;
            sidecar.State = SidecarState.Starting;
            sidecar.Failure = null;
            sidecar.ExitCode = null;
            sidecar.RestartAttempts = 0;

            using var deadline = CreateDeadline(sidecar.Definition.StartupTimeout, cancellationToken);
            try
            {
                var external = await TryAttachCoreAsync(sidecar, deadline.Token).ConfigureAwait(false);
                if (external is not null)
                    return external;

                var launchLock = await AcquireLaunchLockAsync(sidecar, deadline.Token).ConfigureAwait(false);
                if (launchLock is null)
                    return sidecar.Snapshot();
                sidecar.LaunchLock = launchLock;

                // Another owner can become ready between the first probe and lock acquisition.
                external = await TryAttachCoreAsync(sidecar, deadline.Token).ConfigureAwait(false);
                if (external is not null)
                {
                    ReleaseLaunchLock(sidecar);
                    return external;
                }

                return await LaunchOwnedAsync(sidecar, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                await CleanupFailedStartAsync(sidecar).ConfigureAwait(false);
                var message = $"Sidecar '{id}' did not become ready within " +
                    $"{sidecar.Definition.StartupTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture)} seconds.";
                sidecar.Fail(message);
                throw new SidecarStartupException(id, message, sidecar.Snapshot(), ex);
            }
            catch (OperationCanceledException)
            {
                await CleanupFailedStartAsync(sidecar).ConfigureAwait(false);
                sidecar.Fail($"Sidecar '{id}' startup was cancelled.");
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not SidecarStartupException)
            {
                await CleanupFailedStartAsync(sidecar).ConfigureAwait(false);
                var message = $"Sidecar '{id}' failed to start: {ex.Message}";
                sidecar.Fail(message);
                throw new SidecarStartupException(id, message, sidecar.Snapshot(), ex);
            }
        }
        finally
        {
            sidecar.Gate.Release();
        }
    }

    public async ValueTask<SidecarStatus> AttachAsync(
        SidecarId id,
        CancellationToken cancellationToken = default)
    {
        ThrowIfLaunchBlocked();
        var sidecar = GetSidecar(id);
        await sidecar.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfLaunchBlocked();
            if (sidecar.IsLive)
                return sidecar.Snapshot();
            if (sidecar.Definition.ExternalConnector is null)
                throw new InvalidOperationException($"Sidecar '{id}' has no authenticated external connector.");

            sidecar.State = SidecarState.Starting;
            sidecar.Failure = null;
            using var deadline = CreateDeadline(sidecar.Definition.StartupTimeout, cancellationToken);
            try
            {
                var attached = await TryAttachCoreAsync(sidecar, deadline.Token).ConfigureAwait(false);
                if (attached is null)
                    throw new InvalidOperationException($"No authenticated external instance of sidecar '{id}' is available.");
                return attached;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                var message = $"External sidecar '{id}' did not become ready before its startup deadline.";
                sidecar.Fail(message);
                throw new SidecarStartupException(id, message, sidecar.Snapshot(), ex);
            }
            catch (OperationCanceledException)
            {
                sidecar.Fail($"External sidecar '{id}' attachment was cancelled.");
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not SidecarStartupException)
            {
                var message = $"External sidecar '{id}' is unavailable or unhealthy: {ex.Message}";
                sidecar.Fail(message);
                throw new SidecarStartupException(id, message, sidecar.Snapshot(), ex);
            }
        }
        finally
        {
            sidecar.Gate.Release();
        }
    }

    public SidecarStatus GetStatus(SidecarId id)
    {
        ThrowIfDisposed();
        return GetSidecar(id).Snapshot();
    }

    public async ValueTask StopAsync(SidecarId id, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sidecar = GetSidecar(id);
        sidecar.RequestStop();
        await sidecar.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(sidecar).ConfigureAwait(false);
        }
        finally
        {
            sidecar.Gate.Release();
        }
    }
    public async ValueTask StopForUpdateAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        Interlocked.Exchange(ref _updateStopping, 1);
        foreach (var sidecar in _sidecars.Values)
            sidecar.RequestStop();

        foreach (var sidecar in _sidecars.Values)
        {
            await sidecar.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (sidecar.IsOwned)
                    await StopCoreAsync(sidecar).ConfigureAwait(false);
            }
            finally
            {
                sidecar.Gate.Release();
            }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        Volatile.Write(ref _updateStopping, 1);
        foreach (var sidecar in _sidecars.Values)
            sidecar.RequestStop();

        List<Exception>? failures = null;
        foreach (var sidecar in _sidecars.Values)
        {
            var gateHeld = false;
            try
            {
                await sidecar.Gate.WaitAsync().ConfigureAwait(false);
                gateHeld = true;
                try
                {
                    if (sidecar.IsOwned && !sidecar.Definition.KeepRunning)
                        await StopCoreAsync(sidecar).ConfigureAwait(false);
                    else if (sidecar.IsOwned && sidecar.Definition.KeepRunning)
                        DetachKeepRunning(sidecar);
                    else
                        DetachExternal(sidecar);
                }
                catch (Exception exception) when (exception is not OutOfMemoryException)
                {
                    (failures ??= []).Add(exception);
                    try
                    {
                        await ForceDisposeOwnedAsync(sidecar).ConfigureAwait(false);
                    }
                    catch (Exception cleanupException) when (cleanupException is not OutOfMemoryException)
                    {
                        failures.Add(cleanupException);
                    }
                }
                finally
                {
                    sidecar.State = SidecarState.Disposed;
                    sidecar.Gate.Release();
                    gateHeld = false;
                }

                if (!sidecar.Definition.KeepRunning)
                    await sidecar.ExitObserverTask.ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                (failures ??= []).Add(exception);
            }
            finally
            {
                if (gateHeld)
                    sidecar.Gate.Release();
                sidecar.Dispose();
            }
        }

        if (failures is not null)
            throw new AggregateException("One or more sidecars failed during application disposal.", failures);
    }

    private static async ValueTask<SidecarStatus?> TryAttachCoreAsync(
        ManagedSidecar sidecar,
        CancellationToken cancellationToken)
    {
        var connector = sidecar.Definition.ExternalConnector;
        if (connector is null)
            return null;

        var discoveryContext = sidecar.CreateContext(process: null, instanceToken: string.Empty);
        var discovered = await connector.TryAttachAsync(discoveryContext, cancellationToken)
            .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (discovered is null)
            return null;

        ValidateExternalIdentity(sidecar.Definition, discovered);
        var context = sidecar.CreateContext(process: null, discovered.InstanceToken);
        var verifiedIdentity = await sidecar.Definition.Identity
            .VerifyAsync(context, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(verifiedIdentity) ||
            !string.Equals(verifiedIdentity, discovered.ServiceIdentity, StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException("External sidecar identity verification did not match discovery metadata.");
        }

        await sidecar.Definition.Readiness
            .WaitUntilReadyAsync(context, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        ValidateExternalIdentity(sidecar.Definition, discovered);

        sidecar.ProcessIdentity = discovered;
        sidecar.Process = null;
        sidecar.IsOwned = false;
        sidecar.State = SidecarState.ReadyExternal;
        sidecar.ExitCode = null;
        sidecar.Failure = null;
        sidecar.AppendSupervisor("Attached to authenticated external sidecar; stop rights were not acquired.");
        return sidecar.Snapshot();
    }

    private async ValueTask<FileStream?> AcquireLaunchLockAsync(
        ManagedSidecar sidecar,
        CancellationToken cancellationToken)
    {
        var lockPath = GetLockPath(sidecar.Definition.Id);
        while (true)
        {
            try
            {
                var stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 512,
                    FileOptions.Asynchronous);
                MakePrivateFile(lockPath);
                return stream;
            }
            catch (IOException)
            {
                var external = await TryAttachCoreAsync(sidecar, cancellationToken).ConfigureAwait(false);
                if (external is not null)
                    return null;
                await Task.Delay(LockRetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<SidecarStatus> LaunchOwnedAsync(
        ManagedSidecar sidecar,
        CancellationToken cancellationToken)
    {
        var definition = sidecar.Definition;
        var instanceToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var startInfo = await CreateStartInfoAsync(definition, instanceToken, cancellationToken).ConfigureAwait(false);
        Process process;
        StreamReader standardOutput;
        StreamReader standardError;
        WindowsSidecarProcess? windowsLifetime = null;
        UnixSidecarProcess? unixLifetime = null;

        if (OperatingSystem.IsWindows() && !definition.KeepRunning)
        {
            windowsLifetime = WindowsSidecarProcess.Start(startInfo);
            process = windowsLifetime.Process;
            standardOutput = windowsLifetime.StandardOutput;
            standardError = windowsLifetime.StandardError;
        }
        else if (!definition.KeepRunning)
        {
            lock (UnixForkGate)
                unixLifetime = UnixSidecarProcess.Start(startInfo);
            process = unixLifetime.Process;
            standardOutput = unixLifetime.StandardOutput;
            standardError = unixLifetime.StandardError;
        }
        else
        {
            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            lock (UnixForkGate)
            {
                if (!process.Start())
                {
                    process.Dispose();
                    throw new InvalidOperationException("Process.Start returned false.");
                }
            }
            standardOutput = process.StandardOutput;
            standardError = process.StandardError;
        }

        try
        {
            sidecar.Process = process;
            sidecar.WindowsLifetime = windowsLifetime;
            sidecar.UnixLifetime = unixLifetime;
            sidecar.IsOwned = true;
            sidecar.InstanceToken = instanceToken;
#pragma warning disable CA2025 // ManagedSidecar retains the process/streams until both drains complete.
            sidecar.OutputTasks =
            [
                DrainOutputAsync(standardOutput, SidecarLogChannel.StandardOutput, sidecar),
                DrainOutputAsync(standardError, SidecarLogChannel.StandardError, sidecar),
            ];
#pragma warning restore CA2025

            var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            var context = sidecar.CreateContext(process, instanceToken);
            await definition.Readiness.WaitUntilReadyAsync(context, cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (HasOwnedExited(sidecar, process))
                throw new InvalidOperationException(
                    $"Sidecar exited before readiness with code {SafeExitCode(sidecar, process)}.");

            var serviceIdentity = await definition.Identity.VerifyAsync(context, cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(serviceIdentity))
                throw new UnauthorizedAccessException("Sidecar identity verification returned an empty identity.");
            if (HasOwnedExited(sidecar, process))
                throw new InvalidOperationException(
                    $"Sidecar exited during identity verification with code {SafeExitCode(sidecar, process)}.");

            sidecar.ProcessIdentity = new SidecarProcessIdentity(
                process.Id,
                startTime,
                GetProcessExecutablePath(process) ?? CanonicalPath(definition.ExecutablePath),
                instanceToken,
                serviceIdentity);
            sidecar.State = SidecarState.ReadyOwned;
            sidecar.Failure = null;
            sidecar.ExitCode = null;
            WriteLockMetadata(sidecar);
            sidecar.AppendSupervisor(windowsLifetime is not null
                ? "Owned sidecar is authenticated and protected by a kill-on-close Windows Job Object."
                : unixLifetime is not null
                    ? "Owned sidecar is authenticated and protected by a native Unix crash watchdog."
                    : "Owned sidecar is authenticated and ready.");
#pragma warning disable CA2025 // The observer owns the retained Process until it exits or StopCoreAsync completes it.
            sidecar.ExitObserverTask = Task.Run(
                () => ObserveExitAsync(sidecar, process, unixLifetime), CancellationToken.None);
#pragma warning restore CA2025
            return sidecar.Snapshot();
        }
        catch
        {
            if (sidecar.Process is null)
            {
                windowsLifetime?.Terminate();
                windowsLifetime?.Dispose();
                unixLifetime?.Terminate();
                unixLifetime?.Dispose();
                if (windowsLifetime is null && unixLifetime is null)
                    process.Dispose();
            }
            throw;
        }
    }

    private async Task ObserveExitAsync(
        ManagedSidecar sidecar,
        Process observedProcess,
        UnixSidecarProcess? observedUnixLifetime)
    {
        try
        {
            if (observedUnixLifetime is not null)
                await observedUnixLifetime.WaitForExitAsync().ConfigureAwait(false);
            else
                await observedProcess.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return;
        }
        if (Volatile.Read(ref _disposed) != 0)
            return;


        await sidecar.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!ReferenceEquals(sidecar.Process, observedProcess))
                return;

            sidecar.ExitCode = SafeExitCode(sidecar, observedProcess);
            await CompleteOutputAsync(sidecar).ConfigureAwait(false);
            sidecar.Process = null;
            sidecar.IsOwned = false;
            sidecar.ProcessIdentity = null;
            DisposeExitedProcess(sidecar, observedProcess, releaseCrashOwnership: false);
            if (sidecar.ExplicitStop || Volatile.Read(ref _disposed) != 0)
            {
                sidecar.State = Volatile.Read(ref _disposed) != 0 ? SidecarState.Disposed : SidecarState.Stopped;
                ReleaseLaunchLock(sidecar);
                return;
            }

            var restart = sidecar.Definition.Restart;
            if (sidecar.RestartAttempts >= restart.MaxAttempts)
            {
                sidecar.State = SidecarState.Exited;
                sidecar.AppendSupervisor("Owned sidecar exited; restart limit reached.");
                ReleaseLaunchLock(sidecar);
                return;
            }

            sidecar.RestartAttempts++;
            sidecar.State = SidecarState.Restarting;
            var delay = GetRestartDelay(restart, sidecar.RestartAttempts);
            sidecar.AppendSupervisor($"Owned sidecar exited; restart attempt {sidecar.RestartAttempts} is delayed by {delay}.");
            sidecar.ResetRestartCancellation();
            try
            {
                await Task.Delay(delay, sidecar.RestartCancellation).ConfigureAwait(false);
                if (sidecar.ExplicitStop || Volatile.Read(ref _disposed) != 0)
                    throw new OperationCanceledException();

                sidecar.State = SidecarState.Starting;
                using var deadline = CreateDeadline(sidecar.Definition.StartupTimeout, sidecar.RestartCancellation);
                _ = await LaunchOwnedAsync(sidecar, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                sidecar.State = Volatile.Read(ref _disposed) != 0 ? SidecarState.Disposed : SidecarState.Stopped;
                ReleaseLaunchLock(sidecar);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                sidecar.Fail($"Restart attempt {sidecar.RestartAttempts} failed: {ex.Message}");
                await CleanupFailedStartAsync(sidecar).ConfigureAwait(false);
            }
        }
        finally
        {
            sidecar.Gate.Release();
        }
    }

    private static async ValueTask StopCoreAsync(ManagedSidecar sidecar)
    {
        sidecar.ExplicitStop = true;
        sidecar.CancelPendingRestart();

        if (!sidecar.IsOwned || sidecar.Process is null)
        {
            // External attachment is only forgotten; it is never signalled or killed.
            sidecar.ProcessIdentity = null;
            sidecar.State = SidecarState.Stopped;
            return;
        }

        var process = sidecar.Process;
        if (HasOwnedExited(sidecar, process))
        {
            await WaitForOwnedExitAsync(sidecar, process).ConfigureAwait(false);
            sidecar.ExitCode = SafeExitCode(sidecar, process);
            await CompleteOutputAsync(sidecar).ConfigureAwait(false);
            sidecar.Process = null;
            sidecar.IsOwned = false;
            sidecar.ProcessIdentity = null;
            sidecar.State = SidecarState.Stopped;
            DisposeExitedProcess(sidecar, process);
            ReleaseLaunchLock(sidecar);
            return;
        }
        sidecar.State = SidecarState.Stopping;
        if (sidecar.UnixLifetime is null && !MatchesOwnedIdentity(sidecar, process))
        {
            var message = "Retained process no longer matches the owned sidecar identity; refusing to stop it.";
            sidecar.Fail(message);
            throw new SidecarOwnershipException(sidecar.Definition.Id, message);
        }

        using var deadline = new CancellationTokenSource(sidecar.Definition.StopTimeout);
        var context = sidecar.CreateContext(process, sidecar.InstanceToken);
        if (sidecar.Definition.GracefulStop is not null)
        {
            try
            {
                await sidecar.Definition.GracefulStop.RequestStopAsync(context, deadline.Token)
                    .AsTask().WaitAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
            {
                sidecar.AppendSupervisor($"Graceful stop failed; escalating owned process: {ex.Message}");
            }
            catch (OperationCanceledException)
            {
                sidecar.AppendSupervisor("Graceful stop deadline elapsed; escalating owned process.");
            }
        }

        if (!HasOwnedExited(sidecar, process))
        {
            try
            {
                await WaitForOwnedExitAsync(sidecar, process, deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                if (sidecar.WindowsLifetime is null && sidecar.UnixLifetime is null &&
                    !MatchesOwnedIdentity(sidecar, process))
                {
                    throw new SidecarOwnershipException(
                        sidecar.Definition.Id,
                        "Owned process identity changed before escalation; refusing to kill it.");
                }

                TerminateOwnedProcess(sidecar, process);
                using var escalationDeadline = new CancellationTokenSource(sidecar.Definition.StopTimeout);
                await WaitForOwnedExitAsync(sidecar, process, escalationDeadline.Token).ConfigureAwait(false);
            }
        }

        sidecar.ExitCode = SafeExitCode(sidecar, process);
        await CompleteOutputAsync(sidecar).ConfigureAwait(false);
        sidecar.Process = null;
        sidecar.IsOwned = false;
        sidecar.ProcessIdentity = null;
        sidecar.State = SidecarState.Stopped;
        DisposeExitedProcess(sidecar, process);
        ReleaseLaunchLock(sidecar);
    }

    private static async ValueTask CleanupFailedStartAsync(ManagedSidecar sidecar)
    {
        var process = sidecar.Process;
        if (process is not null)
        {
            try
            {
                if (MatchesOwnedIdentity(sidecar, process) || sidecar.ProcessIdentity is null)
                {
                    if (!HasOwnedExited(sidecar, process))
                        TerminateOwnedProcess(sidecar, process);
                    using var deadline = new CancellationTokenSource(sidecar.Definition.StopTimeout);
                    await WaitForOwnedExitAsync(sidecar, process, deadline.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                sidecar.AppendSupervisor($"Failed-start cleanup could not confirm process exit: {ex.Message}");
            }

            await CompleteOutputAsync(sidecar).ConfigureAwait(false);
            sidecar.ExitCode = SafeExitCode(sidecar, process);
            sidecar.Process = null;
            sidecar.IsOwned = false;
            sidecar.ProcessIdentity = null;
            DisposeExitedProcess(sidecar, process);
        }

        ReleaseLaunchLock(sidecar);
    }

    private static async ValueTask ForceDisposeOwnedAsync(ManagedSidecar sidecar)
    {
        var process = sidecar.Process;
        if (process is null)
        {
            ReleaseLaunchLock(sidecar);
            return;
        }

        try
        {
            if (!HasOwnedExited(sidecar, process) &&
                (sidecar.ProcessIdentity is null || MatchesOwnedIdentity(sidecar, process)))
            {
                TerminateOwnedProcess(sidecar, process);
                using var deadline = new CancellationTokenSource(sidecar.Definition.StopTimeout);
                await WaitForOwnedExitAsync(sidecar, process, deadline.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            sidecar.AppendSupervisor($"Best-effort disposal cleanup failed: {exception.Message}");
        }
        finally
        {
            await CompleteOutputAsync(sidecar).ConfigureAwait(false);
            sidecar.ExitCode = SafeExitCode(sidecar, process);
            sidecar.Process = null;
            sidecar.IsOwned = false;
            sidecar.ProcessIdentity = null;
            DisposeExitedProcess(sidecar, process);
            ReleaseLaunchLock(sidecar);
        }
    }

    private static async Task DrainOutputAsync(
        StreamReader reader,
        SidecarLogChannel stream,
        ManagedSidecar sidecar)
    {
        var buffer = new char[1024];
        var line = new StringBuilder(Math.Min(sidecar.MaxLogLineChars, buffer.Length));
        var truncated = false;
        try
        {
            int read;
            while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) != 0)
            {
                for (var index = 0; index < read; index++)
                {
                    var character = buffer[index];
                    if (character == '\n')
                    {
                        if (line.Length > 0 && line[^1] == '\r')
                            line.Length--;
                        sidecar.AppendLog(stream, line.ToString(), truncated);
                        line.Clear();
                        truncated = false;
                    }
                    else if (line.Length < sidecar.MaxLogLineChars)
                    {
                        line.Append(character);
                    }
                    else
                    {
                        truncated = true;
                    }
                }
            }

            if (line.Length > 0 || truncated)
                sidecar.AppendLog(stream, line.ToString(), truncated);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            sidecar.AppendSupervisor($"{stream} diagnostic drain ended: {ex.Message}");
        }
    }

    private static async ValueTask CompleteOutputAsync(ManagedSidecar sidecar)
    {
        var tasks = sidecar.OutputTasks;
        sidecar.OutputTasks = [];
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            sidecar.AppendSupervisor($"Diagnostic drain failed: {ex.Message}");
        }
    }

    private async ValueTask<ProcessStartInfo> CreateStartInfoAsync(
        SidecarDefinition definition,
        string instanceToken,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = definition.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            CreateNoWindow = true,
        };

        foreach (var argument in definition.Arguments)
            startInfo.ArgumentList.Add(argument);

        if (!definition.InheritEnvironment)
            startInfo.Environment.Clear();

        if (definition.EnvironmentProvider is not null)
        {
            var loaded = await LoadEnvironmentAsync(definition, cancellationToken).ConfigureAwait(false);
            foreach (var pair in loaded)
                startInfo.Environment[pair.Key] = pair.Value;
        }

        foreach (var pair in definition.Environment)
            startInfo.Environment[pair.Key] = pair.Value;
        startInfo.Environment[InstanceTokenVariable] = instanceToken;

        if (definition.WorkingDirectory is not null)
            startInfo.WorkingDirectory = definition.WorkingDirectory;
        return startInfo;
    }

    private async ValueTask<IReadOnlyDictionary<string, string>> LoadEnvironmentAsync(
        SidecarDefinition definition,
        CancellationToken cancellationToken)
    {
        var provider = definition.EnvironmentProvider!;
        Task<IReadOnlyDictionary<string, string>> loadTask;
        lock (_environmentCache)
        {
            if (!_environmentCache.TryGetValue(provider, out loadTask!))
            {
                loadTask = LoadAsync();
                _environmentCache.Add(provider, loadTask);
            }
        }

        return await loadTask.WaitAsync(cancellationToken).ConfigureAwait(false);

        async Task<IReadOnlyDictionary<string, string>> LoadAsync()
        {
            using var timeout = new CancellationTokenSource(definition.EnvironmentLoadTimeout);
            return await provider.LoadAsync(timeout.Token).AsTask().WaitAsync(timeout.Token).ConfigureAwait(false);
        }
    }

    private static void ValidateOptions(SidecarOptions options)
    {
        if (options.MaxLogEntries <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxLogEntries must be positive.");
        if (options.MaxLogLineChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "MaxLogLineChars must be positive.");

        foreach (var pair in options.Definitions)
        {
            var definition = pair.Value ?? throw new ArgumentException("A sidecar definition cannot be null.", nameof(options));
            if (pair.Key != definition.Id)
                throw new ArgumentException("A sidecar definition key must match its descriptor ID.", nameof(options));
            if (!Path.IsPathFullyQualified(definition.ExecutablePath))
                throw new ArgumentException($"Sidecar '{definition.Id}' executable path must be absolute.", nameof(options));
            if (definition.ExecutablePath.Contains('"', StringComparison.Ordinal))
                throw new ArgumentException($"Sidecar '{definition.Id}' executable path cannot contain a quote.", nameof(options));
            if (!File.Exists(definition.ExecutablePath))
                throw new FileNotFoundException($"Sidecar '{definition.Id}' executable does not exist.", definition.ExecutablePath);
            if (definition.WorkingDirectory is not null && !Path.IsPathFullyQualified(definition.WorkingDirectory))
                throw new ArgumentException($"Sidecar '{definition.Id}' working directory must be absolute.", nameof(options));
            if (definition.WorkingDirectory is not null && !Directory.Exists(definition.WorkingDirectory))
                throw new DirectoryNotFoundException(definition.WorkingDirectory);
            if (definition.StartupTimeout <= TimeSpan.Zero || definition.StopTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Sidecar startup and stop timeouts must be positive.");
            if (definition.Environment.ContainsKey(InstanceTokenVariable))
                throw new ArgumentException($"Environment variable {InstanceTokenVariable} is reserved.", nameof(options));
            if (definition.EnvironmentProvider is not null && definition.EnvironmentLoadTimeout <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(options), "Sidecar environment load timeout must be positive.");
            if (definition.Restart.MaxAttempts < 0 || definition.Restart.InitialDelay < TimeSpan.Zero ||
                definition.Restart.MaxDelay < definition.Restart.InitialDelay)
                throw new ArgumentOutOfRangeException(nameof(options), "Sidecar restart bounds are invalid.");
            if (definition.KeepRunning && definition.ExternalConnector is null)
                throw new ArgumentException(
                    $"Keep-running sidecar '{definition.Id}' requires an authenticated external connector for reassociation.",
                    nameof(options));
            if (definition.AllowReassociation && !definition.KeepRunning)
                throw new ArgumentException(
                    $"Sidecar '{definition.Id}' can allow reassociation only when KeepRunning is enabled.",
                    nameof(options));
        }
    }

    private static void ValidateExternalIdentity(SidecarDefinition definition, SidecarProcessIdentity identity)
    {
        if (identity.ProcessId <= 0 || identity.StartTimeUtc == default ||
            string.IsNullOrWhiteSpace(identity.InstanceToken) || string.IsNullOrWhiteSpace(identity.ServiceIdentity))
        {
            throw new UnauthorizedAccessException("External connector returned incomplete identity metadata.");
        }

        var expected = CanonicalPath(definition.ExecutablePath);
        var actual = CanonicalPath(identity.ExecutablePath);
        var comparison = OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.Equals(expected, actual, comparison))
            throw new UnauthorizedAccessException("External sidecar executable identity does not match its host definition.");

        using var process = Process.GetProcessById(identity.ProcessId);
        var executable = GetProcessExecutablePath(process);
        if (process.HasExited ||
            new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero) != identity.StartTimeUtc ||
            executable is null || !string.Equals(expected, CanonicalPath(executable), comparison))
        {
            throw new UnauthorizedAccessException("External sidecar process no longer matches discovery metadata.");
        }
    }

    private static bool MatchesOwnedIdentity(ManagedSidecar sidecar, Process process)
    {
        var identity = sidecar.ProcessIdentity;
        if (identity is null || !ReferenceEquals(process, sidecar.Process) || process.HasExited ||
            process.Id != identity.ProcessId)
        {
            return false;
        }

        try
        {
            var expectedPath = CanonicalPath(identity.ExecutablePath);
            var actualPath = GetProcessExecutablePath(process);
            if (actualPath is null || !string.Equals(
                expectedPath,
                CanonicalPath(actualPath),
                OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var startTime = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
            return startTime == identity.StartTimeUtc &&
                string.Equals(identity.InstanceToken, sidecar.InstanceToken, StringComparison.Ordinal);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static string CanonicalPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var segments = fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        var resolved = root;

        foreach (var segment in segments)
        {
            var candidate = Path.Combine(resolved, segment);
            var target = File.ResolveLinkTarget(candidate, returnFinalTarget: true);
            resolved = Path.GetFullPath(target?.FullName ?? candidate);
        }

        return resolved;
    }

    private static string? GetProcessExecutablePath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static TimeSpan GetRestartDelay(SidecarRestartPolicy policy, int attempt)
    {
        if (policy.InitialDelay == TimeSpan.Zero)
            return TimeSpan.Zero;

        var multiplier = Math.Pow(2, Math.Min(attempt - 1, 30));
        var ticks = Math.Min(policy.InitialDelay.Ticks * multiplier, policy.MaxDelay.Ticks);
        return TimeSpan.FromTicks((long)ticks);
    }

    private static CancellationTokenSource CreateDeadline(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static int? SafeExitCode(ManagedSidecar sidecar, Process process)
    {
        try
        {
            if (sidecar.UnixLifetime is not null)
                return sidecar.UnixLifetime.HasExited ? sidecar.UnixLifetime.ExitCode : null;
            return process.HasExited ? process.ExitCode : null;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static bool HasOwnedExited(ManagedSidecar sidecar, Process process)
    {
        try
        {
            return sidecar.UnixLifetime?.HasExited ?? process.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static Task WaitForOwnedExitAsync(
        ManagedSidecar sidecar,
        Process process,
        CancellationToken cancellationToken = default) =>
        sidecar.UnixLifetime?.WaitForExitAsync(cancellationToken) ?? process.WaitForExitAsync(cancellationToken);

    private static void TerminateOwnedProcess(ManagedSidecar sidecar, Process process)
    {
        if (sidecar.WindowsLifetime is not null)
            sidecar.WindowsLifetime.Terminate();
        else if (sidecar.UnixLifetime is not null)
            sidecar.UnixLifetime.Terminate();
        else
            process.Kill(entireProcessTree: true);
    }

    private static void DisposeExitedProcess(
        ManagedSidecar sidecar,
        Process process,
        bool releaseCrashOwnership = true)
    {
        var windowsLifetime = sidecar.WindowsLifetime;
        sidecar.WindowsLifetime = null;
        var unixLifetime = sidecar.UnixLifetime;
        sidecar.UnixLifetime = null;
        if (windowsLifetime is null && unixLifetime is null)
        {
            process.Dispose();
            return;
        }
        if (unixLifetime is not null)
        {
            if (releaseCrashOwnership)
                unixLifetime.Dispose();
            else
                unixLifetime.DisposeProcess();
            return;
        }
        try
        {
            windowsLifetime!.DisposeProcess();
        }
        finally
        {
            windowsLifetime!.ReleaseCrashOwnership();
        }
    }

    private string GetLockPath(SidecarId id)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.Value)));
        return Path.Combine(_lockDirectory, key + ".lock");
    }

    private static void WriteLockMetadata(ManagedSidecar sidecar)
    {
        var stream = sidecar.LaunchLock;
        var identity = sidecar.ProcessIdentity;
        if (stream is null || identity is null)
            return;

        var metadata = string.Join('\n',
            Convert.ToBase64String(Encoding.UTF8.GetBytes(sidecar.Definition.Id.Value)),
            identity.ProcessId.ToString(CultureInfo.InvariantCulture),
            identity.StartTimeUtc.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.ExecutablePath)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.InstanceToken)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(identity.ServiceIdentity)));
        var bytes = Encoding.UTF8.GetBytes(metadata);
        stream.Position = 0;
        stream.SetLength(0);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static void ReleaseLaunchLock(ManagedSidecar sidecar)
    {
        var stream = sidecar.LaunchLock;
        sidecar.LaunchLock = null;
        if (stream is null)
            return;

        var path = stream.Name;
        stream.Dispose();
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
    internal void SimulateWindowsHostCrashForTest(SidecarId id)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Job Object crash simulation is available only on Windows.");
        GetSidecar(id).WindowsLifetime?.SimulateHostCrashForTest();
    }
    internal void SimulateUnixHostCrashForTest(SidecarId id)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix watchdog crash simulation is unavailable on Windows.");
        GetSidecar(id).UnixLifetime?.SimulateHostCrashForTest();
    }




    private void ThrowIfLaunchBlocked()
    {
        ThrowIfDisposed();
        if (Volatile.Read(ref _updateStopping) != 0)
            throw new InvalidOperationException(
                "Sidecar startup and attachment are disabled after StopForUpdateAsync begins; create a new application host to resume.");
    }
    private static void DetachKeepRunning(ManagedSidecar sidecar)
    {
        sidecar.CancelPendingRestart();
        if (sidecar.WindowsLifetime is not null)
        {
            var windowsLifetime = sidecar.WindowsLifetime;
            sidecar.WindowsLifetime = null;
            windowsLifetime.ReleaseCrashOwnership();
            windowsLifetime.DisposeProcess();
        }
        else if (sidecar.UnixLifetime is not null)
        {
            throw new InvalidOperationException("Keep-running sidecars must not be launched through the Unix crash watchdog.");
        }
        else
        {
            sidecar.Process?.Dispose();
        }
        sidecar.Process = null;
        sidecar.OutputTasks = [];
        sidecar.IsOwned = false;
        sidecar.ProcessIdentity = null;
        if (sidecar.Definition.AllowReassociation)
            ReleaseLaunchLock(sidecar);
    }

    private static void DetachExternal(ManagedSidecar sidecar)
    {
        sidecar.CancelPendingRestart();
        sidecar.Process = null;
        sidecar.OutputTasks = [];
        sidecar.IsOwned = false;
        sidecar.ProcessIdentity = null;
    }
    private static void CreatePrivateDirectory(string path)
    {
        Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private static void MakePrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private ManagedSidecar GetSidecar(SidecarId id) =>
        _sidecars.TryGetValue(id, out var sidecar)
            ? sidecar
            : throw new KeyNotFoundException($"Unknown host-defined sidecar ID '{id}'.");

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private sealed class ManagedSidecar : IDisposable
    {
        private readonly object _sync = new();
        private readonly Queue<SidecarLogEntry> _logs;
        private readonly string[] _redactions;
        private CancellationTokenSource _restartCancellation = new();
        private SidecarState _state = SidecarState.Stopped;
        private bool _isOwned;
        private SidecarProcessIdentity? _processIdentity;
        private int? _exitCode;
        private string? _failure;

        public ManagedSidecar(SidecarDefinition definition, SidecarOptions options)
        {
            Definition = definition;
            MaxLogEntries = options.MaxLogEntries;
            MaxLogLineChars = options.MaxLogLineChars;
            _logs = new Queue<SidecarLogEntry>(MaxLogEntries);
            _redactions = BuildRedactions(definition, options);
        }

        public SidecarDefinition Definition { get; }
        public SemaphoreSlim Gate { get; } = new(1, 1);
        public int MaxLogEntries { get; }
        public Task ExitObserverTask { get; set; } = Task.CompletedTask;
        public int MaxLogLineChars { get; }
        public Process? Process { get; set; }
        public WindowsSidecarProcess? WindowsLifetime { get; set; }
        public UnixSidecarProcess? UnixLifetime { get; set; }
        public string InstanceToken { get; set; } = string.Empty;
        public FileStream? LaunchLock { get; set; }
        public Task[] OutputTasks { get; set; } = [];
        public bool ExplicitStop { get; set; }
        public int RestartAttempts { get; set; }
        public CancellationToken RestartCancellation => _restartCancellation.Token;

        public SidecarState State
        {
            get { lock (_sync) return _state; }
            set { lock (_sync) _state = value; }
        }

        public bool IsOwned
        {
            get { lock (_sync) return _isOwned; }
            set { lock (_sync) _isOwned = value; }
        }

        public SidecarProcessIdentity? ProcessIdentity
        {
            get { lock (_sync) return _processIdentity; }
            set { lock (_sync) _processIdentity = value; }
        }

        public int? ExitCode
        {
            get { lock (_sync) return _exitCode; }
            set { lock (_sync) _exitCode = value; }
        }

        public string? Failure
        {
            get { lock (_sync) return _failure; }
            set { lock (_sync) _failure = value; }
        }

        public bool IsLive => State is SidecarState.ReadyOwned or SidecarState.ReadyExternal;

        public void Fail(string message)
        {
            AppendSupervisor(message);
            lock (_sync)
            {
                _failure = message;
                _state = SidecarState.Failed;
            }
        }

        public void AppendSupervisor(string text) => AppendLog(SidecarLogChannel.Supervisor, text, truncated: false);

        public void AppendLog(SidecarLogChannel stream, string text, bool truncated)
        {
            foreach (var secret in _redactions)
                text = text.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(InstanceToken))
                text = text.Replace(InstanceToken, "[REDACTED]", StringComparison.Ordinal);
            if (text.Length > MaxLogLineChars)
            {
                text = text[..MaxLogLineChars];
                truncated = true;
            }

            lock (_sync)
            {
                while (_logs.Count >= MaxLogEntries)
                    _logs.Dequeue();
                _logs.Enqueue(new SidecarLogEntry(DateTimeOffset.UtcNow, stream, text, truncated));
            }
        }

        public IReadOnlyList<SidecarLogEntry> GetLogs()
        {
            lock (_sync)
                return [.. _logs];
        }

        public SidecarStatus Snapshot()
        {
            lock (_sync)
            {
                return new SidecarStatus(
                    Definition.Id,
                    _state,
                    _isOwned,
                    _processIdentity,
                    RestartAttempts,
                    _exitCode,
                    _failure,
                    [.. _logs]);
            }
        }

        public SidecarProbeContext CreateContext(Process? process, string instanceToken) =>
            new(Definition, process, instanceToken, GetLogs);

        public void RequestStop()
        {
            ExplicitStop = true;
            CancelPendingRestart();
        }

        public void CancelPendingRestart()
        {
            try
            {
                _restartCancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void ResetRestartCancellation()
        {
            _restartCancellation.Dispose();
            _restartCancellation = new CancellationTokenSource();
        }


        private static string[] BuildRedactions(SidecarDefinition definition, SidecarOptions options)
        {
            var secrets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in definition.Environment)
            {
                if (IsSensitive(pair.Key, options.RedactEnvironmentVariables) && !string.IsNullOrEmpty(pair.Value))
                    secrets.Add(pair.Value);
            }
            foreach (var value in definition.DiagnosticRedactions)
            {
                if (!string.IsNullOrEmpty(value))
                    secrets.Add(value);
            }

            if (definition.InheritEnvironment)
            {
                foreach (DictionaryEntry pair in System.Environment.GetEnvironmentVariables())
                {
                    var key = (string)pair.Key;
                    var value = pair.Value as string;
                    if (IsSensitive(key, options.RedactEnvironmentVariables) && !string.IsNullOrEmpty(value))
                        secrets.Add(value);
                }
            }

            return [.. secrets.OrderByDescending(static value => value.Length)];
        }

        private static bool IsSensitive(string key, IList<string> markers)
        {
            foreach (var marker in markers)
            {
                if (key.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        public void Dispose()
        {
            WindowsLifetime?.Dispose();
            WindowsLifetime = null;
            UnixLifetime?.Dispose();
            UnixLifetime = null;
            _restartCancellation.Dispose();
            Gate.Dispose();
        }
    }


}

public sealed class SidecarStartupException : Exception
{
    public SidecarStartupException()
    {
    }

    public SidecarStartupException(string message)
        : base(message)
    {
    }

    public SidecarStartupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SidecarStartupException(SidecarId id, string message, SidecarStatus status, Exception innerException)
        : base(message, innerException)
    {
        SidecarId = id;
        Status = status;
    }

    public SidecarId SidecarId { get; }

    public SidecarStatus? Status { get; }
}

public sealed class SidecarOwnershipException : Exception
{
    public SidecarOwnershipException()
    {
    }

    public SidecarOwnershipException(string message)
        : base(message)
    {
    }

    public SidecarOwnershipException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal SidecarOwnershipException(SidecarId id, string message)
        : base(message)
    {
        SidecarId = id;
    }

    public SidecarId SidecarId { get; }
}
