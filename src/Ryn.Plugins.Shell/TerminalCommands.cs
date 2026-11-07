using Ryn.Core;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

[RynJsonContext(typeof(ShellJsonContext))]
public sealed class TerminalCommands
{
    private readonly TerminalSessionService _sessions;
    private readonly CurrentWindowAccessor _windows;

    public TerminalCommands(TerminalSessionService sessions, CurrentWindowAccessor windows)
    {
        _sessions = sessions;
        _windows = windows;
    }

    [RynCommand("terminal.create")]
    public TerminalCreateResult Create(
        string command,
        string argsJson,
        int cols = 80,
        int rows = 24,
        string retentionMode = "streaming")
    {
        return _sessions.Create(command, argsJson, cols, rows, ParseRetention(retentionMode));
    }

    [RynCommand("terminal.attach")]
    public TerminalAttachResult Attach(
        string sessionId,
        string accessToken,
        string? cursor = null,
        int creditBytes = 0,
        string mode = "controller")
    {
        return _sessions.AttachForWindow(sessionId, accessToken, _windows.Current, cursor, creditBytes, ParseMode(mode));
    }

    [RynCommand("terminal.read")]
    public ValueTask<TerminalReadResult> ReadAsync(
        string sessionId,
        string attachmentId,
        string generation,
        int maxBytes = 0,
        int waitMilliseconds = 20_000,
        CancellationToken cancellationToken = default)
    {
        return _sessions.ReadAsync(
            sessionId, attachmentId, generation, WindowId, maxBytes, waitMilliseconds, cancellationToken);
    }

    [RynCommand("terminal.ack")]
    public TerminalOperationResult Ack(
        string sessionId, string attachmentId, string generation, string offset)
    {
        return _sessions.Ack(sessionId, attachmentId, generation, WindowId, offset);
    }

    [RynCommand("terminal.write")]
    public ValueTask<TerminalOperationResult> WriteAsync(
        string sessionId,
        string attachmentId,
        string generation,
        string dataBase64,
        CancellationToken cancellationToken = default)
    {
        return _sessions.WriteAsync(sessionId, attachmentId, generation, WindowId, dataBase64, cancellationToken);
    }

    [RynCommand("terminal.detach")]
    public TerminalOperationResult Detach(string sessionId, string attachmentId, string generation)
    {
        return _sessions.Detach(sessionId, attachmentId, generation, WindowId);
    }

    [RynCommand("terminal.terminate")]
    public TerminalOperationResult Terminate(string sessionId, string attachmentId, string generation)
    {
        return _sessions.Terminate(sessionId, attachmentId, generation, WindowId);
    }

    [RynCommand("terminal.checkpoint")]
    public TerminalOperationResult Checkpoint(
        string sessionId,
        string attachmentId,
        string generation,
        string offset,
        string format,
        string payloadBase64,
        int cols,
        int rows)
    {
        return _sessions.StoreCheckpoint(
            sessionId, attachmentId, generation, WindowId, offset, format, payloadBase64, cols, rows);
    }

    [RynCommand("terminal.claimResize")]
    public TerminalLeaseResult ClaimResize(string sessionId, string attachmentId, string generation)
    {
        return _sessions.ClaimResize(sessionId, attachmentId, generation, WindowId);
    }

    [RynCommand("terminal.resize")]
    public TerminalOperationResult Resize(
        string sessionId,
        string attachmentId,
        string generation,
        string leaseId,
        string leaseGeneration,
        int cols,
        int rows)
    {
        return _sessions.Resize(
            sessionId, attachmentId, generation, WindowId, leaseId, leaseGeneration, cols, rows);
    }

    [RynCommand("terminal.releaseResize")]
    public TerminalOperationResult ReleaseResize(
        string sessionId,
        string attachmentId,
        string generation,
        string leaseId,
        string leaseGeneration)
    {
        return _sessions.ReleaseResize(
            sessionId, attachmentId, generation, WindowId, leaseId, leaseGeneration);
    }

    [RynCommand("terminal.status")]
    public TerminalStatusResult Status(string sessionId, string attachmentId, string generation)
    {
        return _sessions.Status(sessionId, attachmentId, generation, WindowId);
    }

    private long WindowId => _windows.Current.Id;

    private static TerminalRetentionMode ParseRetention(string value) => value switch
    {
        "streaming" => TerminalRetentionMode.Streaming,
        "reattachable" => TerminalRetentionMode.Reattachable,
        _ => throw new TerminalProtocolException(TerminalSessionService.InvalidRequest),
    };

    private static TerminalAttachmentMode ParseMode(string value) => value switch
    {
        "controller" => TerminalAttachmentMode.Controller,
        "mirror" => TerminalAttachmentMode.Mirror,
        _ => throw new TerminalProtocolException(TerminalSessionService.InvalidRequest),
    };
}
