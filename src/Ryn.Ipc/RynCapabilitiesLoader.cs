using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

namespace Ryn.Ipc;

public static class RynCapabilitiesLoader
{
    private const string AppDataVariable = "$APP_DATA";

    /// <summary>
    /// Loads capabilities from <c>ryn.json</c> next to the executable. When the file is missing or
    /// declares no <c>capabilities</c> section, the result depends on the build: a debug build of the
    /// host application falls back to permissive <see cref="RynCapabilities.AllowAll"/> for convenience,
    /// while a release build fails <em>closed</em> with <see cref="RynCapabilities.DenyAll"/> so a
    /// mis-deployed app never silently ships with all commands open.
    /// </summary>
    public static RynCapabilities Load() => Load(permissiveWhenUnconfigured: IsDevelopmentHost());

    /// <param name="permissiveWhenUnconfigured">
    /// When true, a missing file or missing <c>capabilities</c> section yields allow-all; when false it
    /// yields deny-all (fail closed). Production code should pass <c>false</c>.
    /// </param>
    public static RynCapabilities Load(bool permissiveWhenUnconfigured)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ryn.json");
        if (!File.Exists(path))
            return Unconfigured(permissiveWhenUnconfigured);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read ryn.json at '{path}': {ex.Message}", ex);
        }

        try
        {
            return Parse(json, permissiveWhenUnconfigured);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Invalid ryn.json at '{path}': {ex.Message}. " +
                "Expected format: { \"capabilities\": { \"pluginName\": true | false | { \"allow\": [...], \"deny\": [...] } } }",
                ex);
        }
    }

    private static RynCapabilities Unconfigured(bool permissive) =>
        permissive ? RynCapabilities.AllowAll() : RynCapabilities.DenyAll();

    /// <summary>
    /// Detects whether the host application was built in a debug configuration. Used to decide the
    /// fallback when capabilities are unconfigured. Reads the entry assembly's
    /// <see cref="DebuggableAttribute"/>; any failure (including no entry assembly, e.g. unit tests)
    /// is treated as "not development" so we fail closed by default.
    /// </summary>
    private static bool IsDevelopmentHost()
    {
        try
        {
            var entry = Assembly.GetEntryAssembly();
            var dbg = entry?.GetCustomAttribute<DebuggableAttribute>();
            return dbg is not null && dbg.IsJITOptimizerDisabled;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>Test/back-compat entry point. Parses with the release (fail-closed) default.</summary>
    internal static RynCapabilities Parse(string json) => Parse(json, permissiveWhenUnconfigured: false);

    internal static RynCapabilities Parse(string json, bool permissiveWhenUnconfigured)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // A present file with no "capabilities" key is almost always a typo or stub. Fail closed in
        // release (deny-all); only fall back to allow-all when the host explicitly opts into dev mode.
        if (!root.TryGetProperty("capabilities", out var caps) || caps.ValueKind != JsonValueKind.Object)
            return Unconfigured(permissiveWhenUnconfigured);

        var rules = new Dictionary<string, CapabilityRule>(StringComparer.OrdinalIgnoreCase);
        var scopes = new Dictionary<string, CapabilityScope>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in caps.EnumerateObject())
        {
            var pluginName = prop.Name;

            if (prop.Value.ValueKind == JsonValueKind.True)
            {
                rules[pluginName] = new CapabilityRule { AllowAll = true };
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.False)
            {
                rules[pluginName] = new CapabilityRule { AllowAll = false };
                continue;
            }

            if (prop.Value.ValueKind == JsonValueKind.Object)
            {
                HashSet<string>? allow = null;
                HashSet<string>? deny = null;

                if (prop.Value.TryGetProperty("allow", out var allowArray)
                    && allowArray.ValueKind == JsonValueKind.Array)
                {
                    allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in allowArray.EnumerateArray())
                    {
                        if (item.GetString() is { } cmd)
                            allow.Add(cmd);
                    }
                }

                if (prop.Value.TryGetProperty("deny", out var denyArray)
                    && denyArray.ValueKind == JsonValueKind.Array)
                {
                    deny = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var item in denyArray.EnumerateArray())
                    {
                        if (item.GetString() is { } cmd)
                            deny.Add(cmd);
                    }
                }

                var allowAll = allow is null && deny is not null;
                rules[pluginName] = new CapabilityRule { AllowAll = allowAll, Allow = allow, Deny = deny };

                // Parse resource-level scopes
                var scope = ParseScope(prop.Value);
                if (scope is not null)
                    scopes[pluginName] = scope;

                continue;
            }

            throw new InvalidOperationException(
                $"Invalid capability value for plugin '{pluginName}': expected true, false, or object");
        }

        return RynCapabilities.FromRulesAndScopes(rules, scopes);
    }

    private static CapabilityScope? ParseScope(JsonElement pluginElement)
    {
        List<string>? paths = null;
        List<string>? commands = null;
        List<CommandScope>? commandScopes = null;
        List<string>? schemes = null;

        if (pluginElement.TryGetProperty("scope", out var scopeArray)
            && scopeArray.ValueKind == JsonValueKind.Array)
        {
            paths = [];
            foreach (var item in scopeArray.EnumerateArray())
            {
                if (item.GetString() is { } raw)
                    paths.Add(ResolveScopePath(raw));
            }
        }

        if (pluginElement.TryGetProperty("commands", out var commandsArray)
            && commandsArray.ValueKind == JsonValueKind.Array)
        {
            commands = [];
            foreach (var item in commandsArray.EnumerateArray())
            {
                if (item.GetString() is { } cmd)
                    commands.Add(cmd);
            }
        }

        // Rich per-argument command scopes (Tauri-style argv templates):
        //   "scopedCommands": [ { "name": "git", "args": ["status"] },
        //                       { "name": "git", "args": [ { "validator": "^[\\w./-]+$" } ] } ]
        if (pluginElement.TryGetProperty("scopedCommands", out var scopedArray)
            && scopedArray.ValueKind == JsonValueKind.Array)
        {
            commandScopes = [];
            foreach (var item in scopedArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                if (!item.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { } name)
                    continue;

                IReadOnlyList<ArgRule>? argRules = null;
                if (item.TryGetProperty("args", out var argsEl) && argsEl.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<ArgRule>();
                    foreach (var arg in argsEl.EnumerateArray())
                    {
                        if (arg.ValueKind == JsonValueKind.String && arg.GetString() is { } literal)
                            list.Add(ArgRule.Literal(literal));
                        else if (arg.ValueKind == JsonValueKind.Object
                                 && arg.TryGetProperty("validator", out var v)
                                 && v.GetString() is { } pattern)
                            list.Add(ArgRule.Pattern(pattern));
                        else
                            throw new InvalidOperationException(
                                $"Invalid argument rule for scoped command '{name}': expected a literal string or {{ \"validator\": \"regex\" }}");
                    }
                    argRules = list;
                }

                commandScopes.Add(new CommandScope(name, argRules));
            }
        }

        // shell.open scheme allowlist:  "open": { "schemes": ["http", "https", "mailto"] }
        if (pluginElement.TryGetProperty("open", out var openEl)
            && openEl.ValueKind == JsonValueKind.Object
            && openEl.TryGetProperty("schemes", out var schemesArray)
            && schemesArray.ValueKind == JsonValueKind.Array)
        {
            schemes = [];
            foreach (var item in schemesArray.EnumerateArray())
            {
                if (item.GetString() is { } s)
                    schemes.Add(s); // matched case-insensitively at enforcement time
            }
        }

        if (paths is null && commands is null && commandScopes is null && schemes is null)
            return null;

        return new CapabilityScope(
            paths?.AsReadOnly(),
            commands?.AsReadOnly(),
            commandScopes?.AsReadOnly(),
            schemes?.AsReadOnly());
    }

    internal static string ResolveScopePath(string raw)
    {
        // Glob patterns are stored with the literal prefix resolved but the glob portion preserved,
        // so that Path.GetFullPath never mangles '*'/'?' (and never throws on Windows).
        if (GlobMatcher.IsGlob(raw))
            return ResolveGlobScopePath(raw);

        if (raw.StartsWith(AppDataVariable, StringComparison.Ordinal))
        {
            var suffix = raw[AppDataVariable.Length..];
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, suffix.TrimStart('/', '\\')));
        }

        return Path.GetFullPath(raw);
    }

    private static string ResolveGlobScopePath(string raw)
    {
        string expanded = raw;
        if (raw.StartsWith(AppDataVariable, StringComparison.Ordinal))
        {
            var suffix = raw[AppDataVariable.Length..].TrimStart('/', '\\');
            expanded = Path.Combine(AppContext.BaseDirectory, suffix);
        }
        else if (!Path.IsPathRooted(raw))
        {
            expanded = Path.Combine(AppContext.BaseDirectory, raw);
        }

        // Normalize separators without collapsing glob segments.
        return expanded.Replace('\\', '/');
    }
}
