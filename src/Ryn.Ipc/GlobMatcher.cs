using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Ryn.Ipc;

/// <summary>
/// Minimal, allocation-light glob matcher for path scopes. Supports <c>*</c> (any run of
/// characters except the directory separator), <c>**</c> (any run including separators),
/// and <c>?</c> (a single non-separator character). Matching is performed against the
/// already-canonicalized, separator-normalized full path. AOT-safe (interpreted regex, with
/// a match timeout to bound pathological patterns).
/// </summary>
public static class GlobMatcher
{
    // Scope globs come from ryn.json and are fixed at startup, so the same handful of
    // (pattern, ignoreCase) keys are matched repeatedly on hot paths (PathValidator,
    // CapabilityScopeMerger). Compiling the regex once and caching it avoids re-parsing the
    // pattern on every IsMatch. The cache is bounded so an attacker-influenced pattern stream
    // (e.g. via a malicious ryn.json or scope-merge input) cannot grow it without limit — once
    // full we stop adding entries and fall back to building a throwaway regex for the overflow
    // keys, preserving identical match semantics either way.
    private const int MaxCachedRegexes = 1024;

    private static readonly ConcurrentDictionary<RegexKey, Regex> RegexCache = new();

    private readonly record struct RegexKey(string NormalizedPattern, bool IgnoreCase);

    /// <summary>True if <paramref name="pattern"/> contains any glob metacharacter.</summary>
    public static bool IsGlob(string pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        return pattern.Contains('*', StringComparison.Ordinal) || pattern.Contains('?', StringComparison.Ordinal);
    }

    /// <summary>
    /// True if <paramref name="path"/> matches <paramref name="pattern"/>. Both are compared after
    /// normalizing directory separators to '/'. Case sensitivity follows <paramref name="ignoreCase"/>.
    /// </summary>
    public static bool IsMatch(string pattern, string path, bool ignoreCase)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(path);
        var normalizedPattern = Normalize(pattern);
        var normalizedPath = Normalize(path);
        var regex = GetOrBuildRegex(normalizedPattern, ignoreCase);
        try
        {
            return regex.IsMatch(normalizedPath);
        }
        catch (RegexMatchTimeoutException)
        {
            return false; // fail closed
        }
    }

    private static string Normalize(string value) =>
        value.Replace('\\', '/');

    private static Regex GetOrBuildRegex(string normalizedPattern, bool ignoreCase)
    {
        var key = new RegexKey(normalizedPattern, ignoreCase);
        if (RegexCache.TryGetValue(key, out var cached))
            return cached;

        var built = ToRegex(normalizedPattern, ignoreCase);

        // Bound the cache: only cache while under the cap. Once full, hand back the freshly built
        // regex without storing it so pathological/attacker-supplied pattern variety can't grow the
        // dictionary without limit. Match semantics are identical whether cached or not.
        if (RegexCache.Count < MaxCachedRegexes)
            return RegexCache.GetOrAdd(key, built);

        return built;
    }

    private static Regex ToRegex(string glob, bool ignoreCase)
    {
        var sb = new StringBuilder(glob.Length * 2 + 4);
        sb.Append('^');
        for (var i = 0; i < glob.Length; i++)
        {
            var c = glob[i];

            // "/**" matches the directory itself AND anything under it (zero or more segments),
            // so the leading slash is optional: "/data/**" matches "/data" and "/data/a/b".
            if (c == '/' && i + 2 < glob.Length && glob[i + 1] == '*' && glob[i + 2] == '*')
            {
                sb.Append("(?:/.*)?");
                i += 2;
                if (i + 1 < glob.Length && glob[i + 1] == '/')
                    i++;
                continue;
            }

            switch (c)
            {
                case '*':
                    if (i + 1 < glob.Length && glob[i + 1] == '*')
                    {
                        // '**' — match across directory separators
                        sb.Append(".*");
                        i++;
                        if (i + 1 < glob.Length && glob[i + 1] == '/')
                            i++;
                    }
                    else
                    {
                        sb.Append("[^/]*");
                    }
                    break;
                case '?':
                    sb.Append("[^/]");
                    break;
                default:
                    sb.Append(Regex.Escape(c.ToString()));
                    break;
            }
        }
        sb.Append('$');
        var options = RegexOptions.CultureInvariant;
        if (ignoreCase) options |= RegexOptions.IgnoreCase;
        return new Regex(sb.ToString(), options, TimeSpan.FromMilliseconds(100));
    }
}
