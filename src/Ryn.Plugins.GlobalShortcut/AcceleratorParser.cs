namespace Ryn.Plugins.GlobalShortcut;

internal readonly record struct ParsedAccelerator(bool Command, bool Control, bool Alt, bool Shift, string Key)
{
    /// <summary>
    /// Canonical form used as the registration key, so "CmdOrCtrl+Shift+A", "shift+cmdorctrl+a", and
    /// "Cmd+Shift+A" (on macOS) all refer to the same hotkey: modifiers in fixed order plus the normalized key.
    /// </summary>
    public string ToCanonicalString()
    {
        var parts = new List<string>(5);
        if (Control) parts.Add("ctrl");
        if (Command) parts.Add("cmd");
        if (Alt) parts.Add("alt");
        if (Shift) parts.Add("shift");
        parts.Add(Key);
        return string.Join('+', parts);
    }
}

/// <summary>
/// Parses accelerator strings like <c>"CmdOrCtrl+Shift+A"</c> into modifier flags plus a normalized key.
/// Same syntax as the MenuBar plugin's accelerators. Keys normalize to a single lowercase character
/// (letters, digits, punctuation) or a lowercase name: <c>f1</c>–<c>f24</c>, <c>escape</c>, <c>enter</c>,
/// <c>tab</c>, <c>space</c>, <c>backspace</c>, <c>delete</c>, <c>up</c>, <c>down</c>, <c>left</c>,
/// <c>right</c>, <c>home</c>, <c>end</c>, <c>pageup</c>, <c>pagedown</c>.
/// </summary>
internal static class AcceleratorParser
{
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["esc"] = "escape",
        ["return"] = "enter",
        ["del"] = "delete",
        ["pgup"] = "pageup",
        ["pgdown"] = "pagedown",
        ["pgdn"] = "pagedown",
        ["arrowup"] = "up",
        ["arrowdown"] = "down",
        ["arrowleft"] = "left",
        ["arrowright"] = "right",
        ["plus"] = "+",
    };

    private static readonly HashSet<string> NamedKeys = new(StringComparer.Ordinal)
    {
        "escape", "enter", "tab", "space", "backspace", "delete",
        "up", "down", "left", "right", "home", "end", "pageup", "pagedown",
    };

    // CA1308: lowercase IS the normalized key form this parser's contract promises; this is not a
    // round-trippable normalization where uppercase would be safer.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase",
        Justification = "Lowercase is the documented normalized form for accelerator keys.")]
    public static bool TryParse(string? accelerator, bool preferCommand, out ParsedAccelerator result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(accelerator)) return false;

        bool command = false, control = false, alt = false, shift = false;
        string? key = null;

        // "Cmd++" (Cmd + plus key) would produce empty segments; treat a trailing '+' as the literal key.
        var span = accelerator.AsSpan().Trim();
        if (span.EndsWith("++", StringComparison.Ordinal))
        {
            span = span[..^2];
            key = "+";
        }

        foreach (var rawPart in span.ToString().Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "cmd" or "command" or "super" or "meta":
                    command = true;
                    continue;
                case "ctrl" or "control":
                    control = true;
                    continue;
                case "cmdorctrl" or "commandorcontrol":
                    if (preferCommand) command = true; else control = true;
                    continue;
                case "alt" or "option" or "opt":
                    alt = true;
                    continue;
                case "shift":
                    shift = true;
                    continue;
            }

            if (key is not null) return false; // two non-modifier keys

            var part = KeyAliases.TryGetValue(rawPart, out var alias) ? alias : rawPart.ToLowerInvariant();
            if (part.Length == 1)
            {
                key = part;
            }
            else if (NamedKeys.Contains(part) || IsFunctionKey(part))
            {
                key = part;
            }
            else
            {
                return false;
            }
        }

        // A global hotkey without any modifier would swallow plain typing system-wide; require at least one.
        if (key is null || (!command && !control && !alt && !shift)) return false;
        result = new ParsedAccelerator(command, control, alt, shift, key);
        return true;
    }

    /// <summary>True for "f1" through "f24".</summary>
    internal static bool IsFunctionKey(string key) =>
        key.Length is 2 or 3
        && key[0] == 'f'
        && int.TryParse(key.AsSpan(1), out var n)
        && n is >= 1 and <= 24;
}
