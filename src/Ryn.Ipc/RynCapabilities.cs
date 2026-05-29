namespace Ryn.Ipc;

public sealed class RynCapabilities
{
    private readonly Dictionary<string, CapabilityRule> _rules;
    private readonly Dictionary<string, CapabilityScope> _scopes;
    private readonly bool _enforced;

    private RynCapabilities(bool enforced, Dictionary<string, CapabilityRule>? rules = null, Dictionary<string, CapabilityScope>? scopes = null)
    {
        _enforced = enforced;
        _rules = rules ?? new Dictionary<string, CapabilityRule>(StringComparer.OrdinalIgnoreCase);
        _scopes = scopes ?? new Dictionary<string, CapabilityScope>(StringComparer.OrdinalIgnoreCase);
    }

    public bool IsEnforced => _enforced;

    /// <summary>
    /// Permissive mode: every command is allowed and no scope is enforced. Intended for local
    /// development only. Production builds must not fall back to this when <c>ryn.json</c> is
    /// missing — see <see cref="DenyAll"/> and <see cref="RynCapabilitiesLoader"/>.
    /// </summary>
    public static RynCapabilities AllowAll() => new(enforced: false);

    /// <summary>
    /// Fail-closed mode: enforcement is on but no plugin is configured, so every plugin command is
    /// denied. This is the safe default when <c>ryn.json</c> is absent or malformed in a release build.
    /// </summary>
    public static RynCapabilities DenyAll() => new(enforced: true);

    public static RynCapabilities FromRules(Dictionary<string, CapabilityRule> rules) =>
        new(enforced: true, rules);

    internal static RynCapabilities FromRulesAndScopes(
        Dictionary<string, CapabilityRule> rules,
        Dictionary<string, CapabilityScope> scopes) =>
        new(enforced: true, rules, scopes);

    public CapabilityScope? GetScope(string pluginPrefix)
    {
        if (!_enforced) return null;
        return _scopes.TryGetValue(pluginPrefix, out var scope) ? scope : null;
    }

    // Explicit allowlist of framework-internal commands that bypass capability checks. Using a fixed set
    // (rather than any "__ryn.*" prefix) means a future or spoofed "__ryn.whatever" command does not get
    // a free pass — only these known, side-effect-light internals do.
    private static readonly HashSet<string> InternalCommands =
        new(StringComparer.Ordinal) { "__ryn.console", "__ryn.fileDrop" };

    public void ThrowIfDenied(string command)
    {
        if (!_enforced) return;
        ArgumentNullException.ThrowIfNull(command);

        // Known framework-internal commands bypass capability checks; unknown __ryn.* names do not.
        if (InternalCommands.Contains(command))
            return;

        var dotIndex = command.IndexOf('.', StringComparison.Ordinal);
        if (dotIndex < 0)
            throw new RynCommandDeniedException(command, "Command has no plugin prefix");

        var prefix = command[..dotIndex];
        var suffix = command[(dotIndex + 1)..];

        if (!_rules.TryGetValue(prefix, out var rule))
            throw new RynCommandDeniedException(command, $"Plugin '{prefix}' is not configured in capabilities");

        if (!rule.IsAllowed(suffix))
            throw new RynCommandDeniedException(command, $"Command '{suffix}' is denied for plugin '{prefix}'");
    }
}

public sealed class CapabilityRule
{
    public bool AllowAll { get; init; }
    public HashSet<string>? Allow { get; init; }
    public HashSet<string>? Deny { get; init; }

    public bool IsAllowed(string command)
    {
        if (AllowAll)
            return Deny is null || !Deny.Contains(command);

        return Allow is not null
            && Allow.Contains(command)
            && (Deny is null || !Deny.Contains(command));
    }
}
