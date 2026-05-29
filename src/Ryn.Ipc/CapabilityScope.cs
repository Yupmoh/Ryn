namespace Ryn.Ipc;

/// <summary>
/// Resource-level scope for a plugin, parsed from <c>ryn.json</c>. Beyond the coarse
/// command allow/deny rules, a scope can constrain <em>what</em> a command may touch:
/// filesystem paths (glob-capable), the exact binaries a shell command may launch
/// <em>and the arguments it may pass</em>, and the URL schemes <c>shell.open</c> may use.
/// </summary>
public sealed class CapabilityScope
{
    public static CapabilityScope Empty { get; } = new(null, null);

    /// <summary>Allowed filesystem paths. Entries may be literal directories or globs (<c>*</c>, <c>**</c>, <c>?</c>).</summary>
    public IReadOnlyList<string>? AllowedPaths { get; }

    /// <summary>
    /// Legacy binary-name allowlist (any arguments permitted). Prefer <see cref="CommandScopes"/>,
    /// which also constrains arguments. Allowlisting an interpreter (bash, sh, cmd, powershell, env, …)
    /// here effectively disables the shell sandbox.
    /// </summary>
    public IReadOnlyList<string>? AllowedCommands { get; }

    /// <summary>Per-binary scopes that additionally validate each argument (Tauri-style argv templates).</summary>
    public IReadOnlyList<CommandScope>? CommandScopes { get; }

    /// <summary>URL schemes <c>shell.open</c> is allowed to launch (e.g. <c>http</c>, <c>https</c>, <c>mailto</c>).</summary>
    public IReadOnlyList<string>? AllowedSchemes { get; }

    public CapabilityScope(
        IReadOnlyList<string>? allowedPaths,
        IReadOnlyList<string>? allowedCommands,
        IReadOnlyList<CommandScope>? commandScopes = null,
        IReadOnlyList<string>? allowedSchemes = null)
    {
        AllowedPaths = allowedPaths;
        AllowedCommands = allowedCommands;
        CommandScopes = commandScopes;
        AllowedSchemes = allowedSchemes;
    }

    public bool HasPathPolicy => AllowedPaths is not null;
    public bool HasCommandPolicy => AllowedCommands is not null || CommandScopes is not null;
    public bool HasSchemePolicy => AllowedSchemes is not null;
}

/// <summary>A single allowed binary plus the constraints on the arguments it may receive.</summary>
public sealed class CommandScope
{
    public CommandScope(string name, IReadOnlyList<ArgRule>? args)
    {
        Name = name;
        Args = args;
    }

    /// <summary>The binary / command name (matched against the requested command name, ordinal).</summary>
    public string Name { get; }

    /// <summary>
    /// Positional argument rules. <c>null</c> means "any arguments allowed" (discouraged).
    /// When non-null, the requested argv must have exactly this length and every argument must
    /// satisfy its corresponding rule.
    /// </summary>
    public IReadOnlyList<ArgRule>? Args { get; }

    public bool AllowsAnyArgs => Args is null;

    /// <summary>True if <paramref name="args"/> satisfies this scope's argument policy.</summary>
    public bool ArgumentsAllowed(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (Args is null) return true;          // explicit any-args
        if (args.Count != Args.Count) return false;
        for (var i = 0; i < args.Count; i++)
        {
            if (!Args[i].Matches(args[i]))
                return false;
        }
        return true;
    }
}

/// <summary>Matches a single command argument either by exact literal or by full-string regex.</summary>
public sealed class ArgRule
{
    private readonly System.Text.RegularExpressions.Regex? _regex;

    private ArgRule(string? value, System.Text.RegularExpressions.Regex? regex)
    {
        Value = value;
        _regex = regex;
    }

    /// <summary>Exact literal the argument must equal (ordinal).</summary>
    public string? Value { get; }

    public static ArgRule Literal(string value) => new(value, null);

    public static ArgRule Pattern(string pattern) => new(
        null,
        new System.Text.RegularExpressions.Regex(
            pattern,
            System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100)));

    public bool Matches(string arg)
    {
        ArgumentNullException.ThrowIfNull(arg);
        if (Value is not null)
            return string.Equals(arg, Value, StringComparison.Ordinal);
        if (_regex is not null)
        {
            try
            {
                var m = _regex.Match(arg);
                return m.Success && m.Index == 0 && m.Length == arg.Length;
            }
            catch (System.Text.RegularExpressions.RegexMatchTimeoutException)
            {
                return false; // fail closed on pathological input
            }
        }
        return false;
    }
}
