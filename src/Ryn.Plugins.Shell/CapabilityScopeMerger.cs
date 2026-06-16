using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

internal static class CapabilityScopeMerger
{
    /// <summary>
    /// Merges capability scopes from ryn.json into ShellOptions.
    /// If ryn.json declares command scopes, they become the maximum allowed set.
    /// Programmatic commands that aren't in the declared scope are removed.
    /// If ryn.json doesn't declare scopes, programmatic options apply as-is.
    /// </summary>
    internal static void MergeShellScope(RynCapabilities capabilities, ShellOptions options)
    {
        var scope = capabilities.GetScope("shell");
        if (scope is null)
            return;

        // ryn.json is the deployment ceiling: per-argument command scopes and the open-scheme allowlist
        // declared there are authoritative.
        if (scope.CommandScopes is not null)
        {
            options.CommandScopes.Clear();
            options.CommandScopes.AddRange(scope.CommandScopes);
        }

        if (scope.AllowedSchemes is not null)
            options.AllowedOpenSchemes = [.. scope.AllowedSchemes];

        if (!scope.HasCommandPolicy)
            return;

        // ryn.json declared a command ceiling. The deployment ceiling is the union of what the legacy
        // name-only "commands" list permits AND what the per-argument "scopedCommands" declare — anything
        // not in that union must be stripped from the programmatic options.AllowedCommands so an app-set
        // entry can never exceed the ceiling. Previously, when scopedCommands was present but "commands"
        // was absent, an early "AllowedCommands is null" return skipped this clamp and let a programmatic
        // bare command (e.g. "bash") survive a scopedCommands-only ceiling — and run with arbitrary args,
        // since EnforceArgumentPolicy waves through any command that has no matching CommandScope.
        ClampAllowedCommandsToCeiling(scope, options);
    }

    /// <summary>
    /// Removes every entry from <paramref name="options"/>.AllowedCommands that is not also permitted by the
    /// ryn.json ceiling (the legacy "commands" list, when present, or the names declared in scopedCommands).
    /// The legacy "commands" list, when present, is also the source for AllowedCommands when the app set none.
    /// </summary>
    private static void ClampAllowedCommandsToCeiling(CapabilityScope scope, ShellOptions options)
    {
        if (scope.AllowedCommands is { } declaredCommands)
        {
            // commands: [] means explicit deny-all of legacy bare commands.
            if (declaredCommands.Count == 0)
            {
                options.AllowedCommands.Clear();
                return;
            }

            // A legacy "commands" list is present: it is authoritative for AllowedCommands. Seed it when the
            // app set none, otherwise intersect the app's list with the declared ceiling.
            if (options.AllowedCommands.Count == 0)
            {
                options.AllowedCommands.AddRange(declaredCommands);
                return;
            }

            ClampTo(options.AllowedCommands, declaredCommands);
            return;
        }

        // scopedCommands-only ryn.json (no legacy "commands" array): the ceiling is the set of scoped command
        // names. A programmatic AllowedCommands entry survives only if it is also a declared scoped command;
        // otherwise it is dropped so it cannot launch (and bypass argument checks) above the ceiling.
        if (scope.CommandScopes is { Count: > 0 } scopes)
        {
            var ceiling = new string[scopes.Count];
            for (var i = 0; i < scopes.Count; i++)
                ceiling[i] = scopes[i].Name;
            ClampTo(options.AllowedCommands, ceiling);
        }
        else
        {
            // scopedCommands is non-null but empty => an explicit empty command ceiling: deny all legacy
            // bare commands. (HasCommandPolicy was true, so AllowedCommands being null landed us here.)
            options.AllowedCommands.Clear();
        }
    }

    /// <summary>Intersects <paramref name="appCommands"/> in place with <paramref name="ceiling"/> (case-insensitive).</summary>
    private static void ClampTo(List<string> appCommands, IReadOnlyList<string> ceiling)
    {
        var allowed = new HashSet<string>(ceiling, StringComparer.OrdinalIgnoreCase);
        var clamped = appCommands.Where(allowed.Contains).ToList();
        appCommands.Clear();
        appCommands.AddRange(clamped);
    }
}
