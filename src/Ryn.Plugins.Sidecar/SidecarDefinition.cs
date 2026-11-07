namespace Ryn.Plugins.Sidecar;

/// <summary>Opaque, host-defined identifier for an approved sidecar.</summary>
public readonly record struct SidecarId
{
    public SidecarId(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A sidecar ID cannot be empty.", nameof(value));
        if (value.Length > 128)
            throw new ArgumentOutOfRangeException(nameof(value), "A sidecar ID cannot exceed 128 characters.");

        Value = value;
    }

    public string Value { get; } = string.Empty;

    public override string ToString() => Value;
}

/// <summary>Host-controlled executable and lifecycle policy for one sidecar.</summary>
public sealed class SidecarDefinition
{
    public required SidecarId Id { get; init; }

    /// <summary>Absolute path to the trusted, host-packaged executable.</summary>
    public required string ExecutablePath { get; init; }

    /// <summary>Fixed argv supplied by the host. It is never interpreted by a shell.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>Explicit environment additions or overrides.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Whether to inherit the host environment before applying <see cref="Environment"/>.</summary>
    public bool InheritEnvironment { get; init; } = true;

    /// <summary>Optional trusted, bounded, cached host policy for loading additional environment values.</summary>
    public ISidecarEnvironmentProvider? EnvironmentProvider { get; init; }

    public TimeSpan EnvironmentLoadTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Additional secret values to redact from bounded stdout/stderr diagnostics.</summary>
    public IReadOnlyList<string> DiagnosticRedactions { get; init; } = [];

    public string? WorkingDirectory { get; init; }

    public required ISidecarIdentity Identity { get; init; }

    public required ISidecarReadinessProbe Readiness { get; init; }

    /// <summary>Optional authenticated discovery of an already-running external service.</summary>
    public ISidecarExternalConnector? ExternalConnector { get; init; }

    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(30);


    /// <summary>
    /// Release the launch lock during ordinary disposal so a future host can reassociate through
    /// <see cref="ExternalConnector"/>. Keep false when the service has its own persistent single-instance
    /// lock and must not race a replacement while this process is alive.
    /// </summary>
    public bool AllowReassociation { get; init; }
    public ISidecarStopProtocol? GracefulStop { get; init; }

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Leave an owned sidecar running during ordinary application disposal.</summary>
    public bool KeepRunning { get; init; }

    public SidecarRestartPolicy Restart { get; init; } = SidecarRestartPolicy.Disabled;
}

/// <summary>Bounded, opt-in restart behavior for an owned sidecar that exits unexpectedly.</summary>
public sealed class SidecarRestartPolicy
{
    public static SidecarRestartPolicy Disabled { get; } = new();

    public int MaxAttempts { get; init; }

    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>Process identity captured independently of an operating-system PID.</summary>
public sealed record SidecarProcessIdentity(
    int ProcessId,
    DateTimeOffset StartTimeUtc,
    string ExecutablePath,
    string InstanceToken,
    string ServiceIdentity);

public enum SidecarState
{
    Stopped,
    Starting,
    ReadyOwned,
    ReadyExternal,
    Restarting,
    Stopping,
    Exited,
    Failed,
    Disposed,
}

public sealed record SidecarStatus(
    SidecarId Id,
    SidecarState State,
    bool IsOwned,
    SidecarProcessIdentity? Identity,
    int RestartAttempts,
    int? ExitCode,
    string? Failure,
    IReadOnlyList<SidecarLogEntry> Logs);

public sealed record SidecarLogEntry(
    DateTimeOffset Timestamp,
    SidecarLogChannel Channel,
    string Text,
    bool IsTruncated = false);

public enum SidecarLogChannel
{
    StandardOutput,
    StandardError,
    Supervisor,
}

public sealed class SidecarOptions
{
    public IDictionary<SidecarId, SidecarDefinition> Definitions { get; } =
        new Dictionary<SidecarId, SidecarDefinition>();

    public int MaxLogEntries { get; set; } = 200;

    public int MaxLogLineChars { get; set; } = 4096;

    /// <summary>Environment-name fragments whose values are replaced in diagnostic log lines.</summary>
    public IList<string> RedactEnvironmentVariables { get; } =
        ["TOKEN", "SECRET", "PASSWORD", "PASSWD", "APIKEY", "API_KEY", "PRIVATE_KEY"];
}
