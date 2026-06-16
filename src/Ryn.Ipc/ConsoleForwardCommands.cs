using System.Text;
using Microsoft.Extensions.Logging;

namespace Ryn.Ipc;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed partial class ConsoleForwardCommands
#pragma warning restore CA1812
{
    // Upper bound on the forwarded text we log. A page can call console.log with arbitrarily large
    // arguments; truncating keeps a single hostile or runaway message from flooding the host log.
    private const int MaxMessageLength = 2048;

    // Visible, inert stand-ins so the page can never inject control characters into the host log.
    private const char ControlReplacement = '�'; // REPLACEMENT CHARACTER
    private const char TruncationMarker = '…';    // HORIZONTAL ELLIPSIS

    private readonly ILogger<ConsoleForwardCommands> _logger;

    public ConsoleForwardCommands(ILogger<ConsoleForwardCommands> logger)
        => _logger = logger;

    // NOTE on capability gating: this command (__ryn.console) is dispatched through the same
    // RynCommandDispatcher path as every other command, so RynCapabilities.ThrowIfDenied runs for it.
    // It is in the framework's fixed InternalCommands allowlist (see RynCapabilities.cs) and is
    // therefore intentionally exempt from per-plugin allow/deny: the console bridge is a built-in,
    // side-effect-light diagnostic that is only injected when DevTools is enabled (RynWindow.cs).
    // The remaining risk is log forgery: the page controls both `level` and `message`, so we
    // neutralize control characters / newlines and cap length below before any of it reaches the log.
    [RynCommand("__ryn.console")]
    public void Log(string level, string message)
    {
        var safeMessage = Sanitize(message);
        switch (level)
        {
            case "error":
                ConsoleLog.Error(_logger, safeMessage);
                break;
            case "warn":
                ConsoleLog.Warning(_logger, safeMessage);
                break;
            case "info":
                ConsoleLog.Info(_logger, safeMessage);
                break;
            default:
                ConsoleLog.Info(_logger, safeMessage);
                break;
        }
    }

    /// <summary>
    /// Neutralizes page-supplied console text before logging: replaces CR/LF and every other control
    /// character (the C0, DEL and C1 ranges) with a visible placeholder so a forwarded string cannot
    /// inject extra log lines or forge structured log entries, and truncates to
    /// <see cref="MaxMessageLength"/> characters. Horizontal tab is preserved as a single space. The
    /// result is always a single line containing no control characters.
    /// </summary>
    internal static string Sanitize(string? message)
    {
        if (string.IsNullOrEmpty(message))
            return string.Empty;

        var truncated = message.Length > MaxMessageLength;
        var span = truncated ? message.AsSpan(0, MaxMessageLength) : message.AsSpan();

        var sb = new StringBuilder(span.Length + (truncated ? 1 : 0));
        foreach (var c in span)
        {
            // Horizontal tab is benign and common in console output; flatten it to a space so the log
            // line stays single-line and aligned.
            if (c == '\t')
            {
                sb.Append(' ');
            }
            // char.IsControl is true for exactly the C0 (U+0000..U+001F, includes CR/LF/NUL) and the
            // DEL + C1 (U+007F..U+009F) ranges, culture-independent and AOT-safe. These are the
            // newline-injection / log-forgery vectors: replace each with a visible, inert placeholder.
            else if (char.IsControl(c))
            {
                sb.Append(ControlReplacement);
            }
            else
            {
                sb.Append(c);
            }
        }

        if (truncated)
            sb.Append(TruncationMarker);

        return sb.ToString();
    }

    private static partial class ConsoleLog
    {
        [LoggerMessage(Level = LogLevel.Error, Message = "[WebView] {Message}")]
        public static partial void Error(ILogger logger, string message);

        [LoggerMessage(Level = LogLevel.Warning, Message = "[WebView] {Message}")]
        public static partial void Warning(ILogger logger, string message);

        [LoggerMessage(Level = LogLevel.Information, Message = "[WebView] {Message}")]
        public static partial void Info(ILogger logger, string message);
    }
}
