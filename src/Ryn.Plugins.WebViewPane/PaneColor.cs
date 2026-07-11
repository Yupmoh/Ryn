using System.Globalization;

namespace Ryn.Plugins.WebViewPane;

/// <summary>
/// Parses the CSS-style color strings accepted by <see cref="PaneOpenRequest.Background"/> and
/// <c>webviewPane.setBackground</c>: <c>#rgb</c>, <c>#rgba</c>, <c>#rrggbb</c>, <c>#rrggbbaa</c>,
/// <c>rgb(r, g, b)</c> and <c>rgba(r, g, b, a)</c> (alpha 0–1). Anything else is rejected rather than
/// guessed — a silently-wrong background defeats the whole point of killing the first-paint flash.
/// </summary>
internal static class PaneColor
{
    public static bool TryParse(string? value, out byte r, out byte g, out byte b, out byte a)
    {
        r = g = b = 0;
        a = 255;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var s = value.Trim();
        return s[0] == '#' ? TryParseHex(s.AsSpan(1), ref r, ref g, ref b, ref a)
                           : TryParseRgbFunction(s, ref r, ref g, ref b, ref a);
    }

    private static bool TryParseHex(ReadOnlySpan<char> hex, ref byte r, ref byte g, ref byte b, ref byte a)
    {
        static bool Nibble(char c, out int v)
        {
            v = c switch
            {
                >= '0' and <= '9' => c - '0',
                >= 'a' and <= 'f' => c - 'a' + 10,
                >= 'A' and <= 'F' => c - 'A' + 10,
                _ => -1,
            };
            return v >= 0;
        }

        static bool Pair(ReadOnlySpan<char> s, out byte value)
        {
            value = 0;
            if (!Nibble(s[0], out var hi) || !Nibble(s[1], out var lo)) return false;
            value = (byte)((hi << 4) | lo);
            return true;
        }

        switch (hex.Length)
        {
            case 3 or 4: // #rgb / #rgba — each nibble doubled, CSS short form
                Span<char> expanded = stackalloc char[hex.Length * 2];
                for (var i = 0; i < hex.Length; i++)
                {
                    expanded[i * 2] = hex[i];
                    expanded[i * 2 + 1] = hex[i];
                }
                return TryParseHex(expanded, ref r, ref g, ref b, ref a);

            case 6:
                return Pair(hex, out r) && Pair(hex[2..], out g) && Pair(hex[4..], out b);

            case 8:
                return Pair(hex, out r) && Pair(hex[2..], out g) && Pair(hex[4..], out b) && Pair(hex[6..], out a);

            default:
                return false;
        }
    }

    private static bool TryParseRgbFunction(string s, ref byte r, ref byte g, ref byte b, ref byte a)
    {
        var hasAlpha = s.StartsWith("rgba", StringComparison.OrdinalIgnoreCase);
        var open = s.IndexOf('(', StringComparison.Ordinal);
        if (open < 0 || !s.EndsWith(')') ||
            !(hasAlpha || s.StartsWith("rgb", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var parts = s[(open + 1)..^1].Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != (hasAlpha ? 4 : 3)) return false;

        if (!byte.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out r) ||
            !byte.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out g) ||
            !byte.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out b))
        {
            return false;
        }

        if (hasAlpha)
        {
            if (!double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var alpha) ||
                alpha is < 0 or > 1)
            {
                return false;
            }
            a = (byte)Math.Round(alpha * 255);
        }

        return true;
    }
}
