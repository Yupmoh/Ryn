using System.Diagnostics;

namespace Ryn.Plugins.Sidecar;

/// <summary>Context shared by readiness, identity, and stop protocols.</summary>
public sealed class SidecarProbeContext
{
    internal SidecarProbeContext(
        SidecarDefinition definition,
        Process? process,
        string instanceToken,
        Func<IReadOnlyList<SidecarLogEntry>> getLogs)
    {
        Definition = definition;
        Process = process;
        InstanceToken = instanceToken;
        GetLogs = getLogs;
    }

    public SidecarDefinition Definition { get; }

    /// <summary>The retained process handle for an owned child; null while attaching externally.</summary>
    public Process? Process { get; }

    /// <summary>Unpredictable token passed to an owned child as <c>RYN_SIDECAR_INSTANCE_TOKEN</c>.</summary>
    public string InstanceToken { get; }

    public Func<IReadOnlyList<SidecarLogEntry>> GetLogs { get; }
}

/// <summary>
/// Authenticates the actual service instance. A listening port or PID alone is not a valid implementation.
/// </summary>
public interface ISidecarIdentity
{
    public ValueTask<string> VerifyAsync(SidecarProbeContext context, CancellationToken cancellationToken);
}

/// <summary>Waits until an authenticated sidecar is ready to serve requests.</summary>
public interface ISidecarReadinessProbe
{
    public ValueTask WaitUntilReadyAsync(SidecarProbeContext context, CancellationToken cancellationToken);
}

/// <summary>Requests service-specific graceful shutdown before owned-process escalation.</summary>
public interface ISidecarStopProtocol
{
    public ValueTask RequestStopAsync(SidecarProbeContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Trusted host policy for loading an optional login-shell or platform environment. The manager bounds and
/// caches the result, then applies descriptor <see cref="SidecarDefinition.Environment"/> overrides last.
/// </summary>
public interface ISidecarEnvironmentProvider
{
    public ValueTask<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken);
}

public sealed class SidecarCallbackEnvironmentProvider : ISidecarEnvironmentProvider
{
    private readonly Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> _callback;

    public SidecarCallbackEnvironmentProvider(
        Func<CancellationToken, ValueTask<IReadOnlyDictionary<string, string>>> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        _callback = callback;
    }

    public ValueTask<IReadOnlyDictionary<string, string>> LoadAsync(CancellationToken cancellationToken) =>
        _callback(cancellationToken);
}

/// <summary>Discovers a running external service and returns identity metadata only after authentication.</summary>
public interface ISidecarExternalConnector
{
    public ValueTask<SidecarProcessIdentity?> TryAttachAsync(
        SidecarProbeContext context,
        CancellationToken cancellationToken);
}


/// <summary>Host-only sidecar lifecycle API. Browser code is not allowed to supply process definitions.</summary>
public interface ISidecarManager : IAsyncDisposable
{
    public ValueTask<SidecarStatus> StartAsync(SidecarId id, CancellationToken cancellationToken = default);

    public ValueTask<SidecarStatus> AttachAsync(SidecarId id, CancellationToken cancellationToken = default);

    public SidecarStatus GetStatus(SidecarId id);

    public ValueTask StopAsync(SidecarId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently enters update quiescence and stops every owned sidecar, including keep-running instances,
    /// before an existing updater replaces files. Once this call begins, start and attach operations fail until
    /// a new application host is created. The host must await this explicitly before calling the updater; Ryn's
    /// updater has no participant hook.
    /// </summary>
    public ValueTask StopForUpdateAsync(CancellationToken cancellationToken = default);
}
