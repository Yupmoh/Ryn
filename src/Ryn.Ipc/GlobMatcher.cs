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
        var regex = ToRegex(normalizedPattern, ignoreCase);
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
