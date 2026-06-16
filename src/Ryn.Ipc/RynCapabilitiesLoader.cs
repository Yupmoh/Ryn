using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Ryn.Ipc;

public static partial class RynCapabilitiesLoader
{
    private const string AppDataVariable = "$APP_DATA";

    /// <summary>
    /// Environment variable that explicitly opts a host into permissive (allow-all) capabilities when
    /// <c>ryn.json</c> is absent or unconfigured. Set to <c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>
    /// (case-insensitive) to force the dev convenience path even in a build that does not carry a
    /// debug <see cref="DebuggableAttribute"/>. This is an <em>additional</em> signal: a debug build
    /// still gets allow-all without it (so existing dev workflows are unaffected), but it lets a
    /// developer keep working when running, say, a Release-configured build locally.
    /// </summary>
    public const string DevAllowAllVariable = "RYN_DEV_ALLOW_ALL";

    /// <summary>
    /// Loads capabilities from <c>ryn.json</c> next to the executable. When the file is missing or
    /// declares no <c>capabilities</c> section, the result depends on the build: a debug build of the
    /// host application (or any build with <c>RYN_DEV_ALLOW_ALL</c> set) falls back to permissive
    /// <see cref="RynCapabilities.AllowAll"/> for convenience, while a release build fails
    /// <em>closed</em> with <see cref="RynCapabilities.DenyAll"/> so a mis-deployed app never silently
    /// ships with all commands open. When fail-closed engages because <c>ryn.json</c> is absent, a
    /// one-time warning is emitted through <paramref name="logger"/> (if supplied) so a first-time
    /// shipper is not mystified by every command being denied. Conversely, whenever the permissive
    /// allow-all path is actually taken a loud one-time warning is emitted too, so a Debug build that
    /// gets shipped by accident cannot silently open every command without a trace in the log.
    /// </summary>
    public static RynCapabilities Load(ILogger? logger = null) =>
        Load(permissiveWhenUnconfigured: IsDevelopmentHost(), logger);

    /// <param name="permissiveWhenUnconfigured">
    /// When true, a missing file or missing <c>capabilities</c> section yields allow-all; when false it
    /// yields deny-all (fail closed). Production code should pass <c>false</c>.
    /// </param>
    /// <param name="logger">
    /// Optional logger used to emit a one-time warning when a release (fail-closed) build denies every
    /// command because <c>ryn.json</c> is missing entirely, and a complementary one-time warning when
    /// the permissive allow-all path is taken so an accidentally-shipped Debug build is never silent.
    /// </param>
    public static RynCapabilities Load(bool permissiveWhenUnconfigured, ILogger? logger = null)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ryn.json");
        if (!File.Exists(path))
        {
            // Missing file: fail-closed builds deny every plugin command — warn so a misconfigured
            // release is obvious rather than silently inert. Permissive (dev) builds instead open
            // everything; warn loudly there too so a Debug build shipped by accident leaves a trace.
            if (logger is not null)
            {
                if (permissiveWhenUnconfigured)
                    Log.PermissiveAllowAll(logger, "ryn.json is missing");
                else
                    Log.MissingRynJsonFailClosed(logger, path);
            }
            return Unconfigured(permissiveWhenUnconfigured);
        }

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            throw new InvalidOperationException($"Failed to read ryn.json at '{path}': {ex.Message}", ex);
        }

        RynCapabilities result;
        try
        {
            result = Parse(json, permissiveWhenUnconfigured);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"Invalid ryn.json at '{path}': {ex.Message}. " +
                "Expected format: { \"capabilities\": { \"pluginName\": true | false | { \"allow\": [...], \"deny\": [...] } } }",
                ex);
        }

        // A present file that yields a non-enforcing result reached the permissive allow-all path
        // (e.g. no "capabilities" section in a dev build). Warn so it is never silent.
        if (logger is not null && !result.IsEnforced)
            Log.PermissiveAllowAll(logger, "ryn.json declares no capabilities section");

        return result;
    }

    private static RynCapabilities Unconfigured(bool permissive) =>
        permissive ? RynCapabilities.AllowAll() : RynCapabilities.DenyAll();

    private static partial class Log
    {
        [LoggerMessage(Level = LogLevel.Warning, Message =
            "No ryn.json found at {Path}; this release build is failing closed and denying every plugin " +
            "command. Ship a ryn.json that grants the capabilities your frontend needs (see SECURITY.md).")]
        public static partial void MissingRynJsonFailClosed(ILogger logger, string path);

        [LoggerMessage(Level = LogLevel.Warning, Message =
            "Ryn capabilities are running in permissive ALLOW-ALL mode ({Reason}); every plugin command " +
            "is reachable from the frontend. This is intended for development only. Ship a ryn.json with " +
            "an explicit capabilities section, and only set " + DevAllowAllVariable + " when you deliberately " +
            "want allow-all in a non-debug build (see SECURITY.md).")]
        public static partial void PermissiveAllowAll(ILogger logger, string reason);
    }

    /// <summary>
    /// Detects whether the host should fall back to permissive allow-all when capabilities are
    /// unconfigured. True when the entry assembly was built in a debug configuration (the historical
    /// signal — preserved so existing dev workflows are unaffected) <em>or</em> when the
    /// <c>RYN_DEV_ALLOW_ALL</c> environment variable is explicitly set to a truthy value (an additional
    /// opt-in so a developer can keep allow-all in, say, a locally-run Release build). Any failure
    /// (including no entry assembly, e.g. unit tests) is treated as "not development" so we fail closed
    /// by default.
    /// </summary>
    private static bool IsDevelopmentHost()
    {
        if (IsDevAllowAllOptedIn())
            return true;

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

    /// <summary>
    /// True when <c>RYN_DEV_ALLOW_ALL</c> is set to a recognized truthy value
    /// (<c>1</c>/<c>true</c>/<c>yes</c>/<c>on</c>, case-insensitive). Reading an environment variable is
    /// AOT-safe and never throws here, but we guard defensively so a hostile process environment can
    /// never crash capability loading.
    /// </summary>
    private static bool IsDevAllowAllOptedIn()
    {
        string? value;
        try
        {
            value = Environment.GetEnvironmentVariable(DevAllowAllVariable);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        return trimmed.Equals("1", StringComparison.Ordinal)
            || trimmed.Equals("true", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("on", StringComparison.OrdinalIgnoreCase);
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
