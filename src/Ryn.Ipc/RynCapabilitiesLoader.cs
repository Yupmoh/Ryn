using System.Text.Json;

namespace Ryn.Ipc;

public static class RynCapabilitiesLoader
{
    private const string AppDataVariable = "$APP_DATA";

    public static RynCapabilities Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ryn.json");
        if (!File.Exists(path))
            return RynCapabilities.AllowAll();

        var json = File.ReadAllText(path);
        return Parse(json);
    }

    internal static RynCapabilities Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (!root.TryGetProperty("capabilities", out var caps))
            return RynCapabilities.AllowAll();

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

        if (paths is null && commands is null)
            return null;

        return new CapabilityScope(
            paths?.AsReadOnly() ?? (IReadOnlyList<string>)[],
            commands?.AsReadOnly() ?? (IReadOnlyList<string>)[]);
    }

    internal static string ResolveScopePath(string raw)
    {
        if (raw.StartsWith(AppDataVariable, StringComparison.Ordinal))
        {
            var suffix = raw[AppDataVariable.Length..];
            return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, suffix.TrimStart('/', '\\')));
        }

        return Path.GetFullPath(raw);
    }
}
