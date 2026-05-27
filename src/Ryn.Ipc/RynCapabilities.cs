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

    public static RynCapabilities AllowAll() => new(enforced: false);

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

    public void ThrowIfDenied(string command)
    {
        if (!_enforced) return;
        ArgumentNullException.ThrowIfNull(command);

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
