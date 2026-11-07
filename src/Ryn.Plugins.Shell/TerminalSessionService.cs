using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;

namespace Ryn.Plugins.Shell;

/// <summary>Configuration for the acknowledged, reattachable terminal transport.</summary>
public sealed class TerminalOptions
{
    public int AttachmentCreditBytes { get; set; } = 256 * 1024;
    public int SessionRetentionBytes { get; set; } = 8 * 1024 * 1024;
    public long GlobalRetentionBytes { get; set; } = 64L * 1024 * 1024;
    public int MaxReadBytes { get; set; } = 64 * 1024;
    public int MaxInputBytes { get; set; } = 8 * 1024 * 1024;
    public int MaxInputItems { get; set; } = 64;
    public int MaxCheckpointBytes { get; set; } = 1024 * 1024;
    public int MaxAttachmentsPerSession { get; set; } = 8;
    public int MaxSessions { get; set; } = 64;
    public TimeSpan ExitedSessionRetention { get; set; } = TimeSpan.FromMinutes(5);
    public TimeSpan ExitDrainTimeout { get; set; } = TimeSpan.FromSeconds(2);
}

public enum TerminalRetentionMode
{
    Streaming,
    Reattachable,
}

public enum TerminalAttachmentMode
{
    Controller,
    Mirror,
}

public enum TerminalSessionState
{
    WaitingForAttachment,
    Streaming,
    Backpressured,
    Draining,
    Exited,
    Disposed,
}

public sealed record TerminalCreateResult(string SessionId, string AccessToken);

public sealed record TerminalCheckpoint(
    string Offset,
    string Format,
    string PayloadBase64,
    int Cols,
    int Rows);

public sealed record TerminalAttachResult(
    string AttachmentId,
    string Generation,
    string State,
    string RetainedStart,
    string RetainedEnd,
    string NextOffset,
    TerminalCheckpoint? Checkpoint);

public sealed record TerminalReadResult(
    string State,
    string StartOffset,
    string EndOffset,
    string DataBase64,
    bool ProcessExited,
    int? ExitCode,
    string? FinalOffset,
    string? Error,
    bool OutputTruncated = false);

public sealed record TerminalOperationResult(bool Ok, string? Error, string State);

public sealed record TerminalLeaseResult(
    bool Ok,
    string? Error,
    string? LeaseId,
    string? LeaseGeneration);

public sealed record TerminalStatusResult(
    string State,
    string RetainedStart,
    string RetainedEnd,
    string ReadOffset,
    string DeliveredOffset,
    string AcknowledgedOffset,
    int OutstandingBytes,
    int CreditBytes,
    string BytesRead,
    string BytesSent,
    string BytesConsumed,
    int RetainedBytes,
    int QueueHighWaterBytes,
    string PauseMilliseconds,
    bool ProcessExited,
    int? ExitCode,
    string? FinalOffset,
    bool NeedsCheckpoint,
    bool HasResizeLease,
    bool OutputTruncated = false)
{
    /// <summary>Last sampled delivery-to-cumulative-ACK latency, or null before a sample completes.</summary>
    public double? AckLatencyMilliseconds { get; init; }
}

/// <summary>
/// Owns native PTYs independently of webviews. Sessions use decoded-byte credit and a bounded retained byte
/// log; the legacy <c>shell.pty*</c> facade uses the same creation and native lifetime implementation.
/// </summary>
public sealed class TerminalSessionService : IDisposable
{
    internal const string UnknownSession = "unknown_session";
    internal const string UnauthorizedAttachment = "unauthorized_attachment";
    internal const string StaleGeneration = "stale_generation";
    internal const string InvalidOffset = "invalid_offset";
    internal const string BudgetExceeded = "budget_exceeded";
    internal const string HistoryUnavailable = "history_unavailable";
    internal const string IncompatibleCheckpoint = "incompatible_checkpoint";
    internal const string TerminalExited = "terminal_exited";
    internal const string Cancelled = "cancelled";
    internal const string InvalidRequest = "invalid_request";
    internal const string ResyncRequired = "resync_required";

    private readonly ShellExecutionPolicy _policy;
    private readonly TerminalOptions _options;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<long, PtySession> _nativeSessions = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<Ryn.Core.IRynWindow, WindowAttachments> _windowAttachments = new();
    private long _nextNativeId;
    private long _retainedBytes;
    private int _disposed;
    private int _sessionSlots;

    public TerminalSessionService(ShellExecutionPolicy policy, TerminalOptions options)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(options);
        ValidateOptions(options);
        _policy = policy;
        _options = options;
    }

    internal PtySession SpawnNative(string command, string argsJson, int cols, int rows)
    {
        ValidateDimensions(cols, rows, throwOnInvalid: true);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var parsed = ShellExecutionPolicy.ParseArgs(argsJson);
        var resolvedCommand = _policy.ValidateInvocation(command, parsed);
        var args = new string[parsed.Length + 1];
        args[0] = resolvedCommand;
        Array.Copy(parsed, 0, args, 1, parsed.Length);
        var id = Interlocked.Increment(ref _nextNativeId);
        var environment = _policy.BuildProcessEnvironment();

        PtySession session;
        if (OperatingSystem.IsWindows())
        {
            var commandLine = PtyCommandLine.QuoteWindows(resolvedCommand, args);
            var conPty = PtyNativeWindows.CreateConPty(
                commandLine, (ushort)cols, (ushort)rows, _policy.Options.WorkingDirectory, environment);
            session = new PtySessionWindows(id, conPty);
        }
        else
        {
            var masterFd = PtyNative.ForkWithPty(
                resolvedCommand, args, environment, _policy.Options.WorkingDirectory,
                (ushort)cols, (ushort)rows, out var childPid);
            session = new PtySessionUnix(id, childPid, masterFd);
        }

        _nativeSessions[id] = session;
        return session;
    }

    public TerminalCreateResult Create(
        string command,
        string argsJson,
        int cols = 80,
        int rows = 24,
        TerminalRetentionMode retentionMode = TerminalRetentionMode.Streaming)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ScavengeExited();
        if (Interlocked.Increment(ref _sessionSlots) > _options.MaxSessions)
        {
            Interlocked.Decrement(ref _sessionSlots);
            throw new TerminalProtocolException(BudgetExceeded);
        }

        PtySession? native = null;
        string? registeredId = null;
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            native = SpawnNative(command, argsJson, cols, rows);
            var id = NewOpaqueId();
            var token = NewOpaqueId();
            var session = new Session(id, token, native, retentionMode, cols, rows, _options);
            if (!_sessions.TryAdd(id, session))
                throw new InvalidOperationException("Failed to register terminal session.");
            registeredId = id;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            session.Start(
                bytes => ReserveRetention(bytes),
                bytes => ReleaseRetention(bytes),
                () => _nativeSessions.TryRemove(native.Id, out _));
            return new TerminalCreateResult(id, token);
        }
        catch
        {
            if (registeredId is not null && _sessions.TryRemove(registeredId, out var registered))
                registered.Dispose();
            else
                native?.RequestKill();
            Interlocked.Decrement(ref _sessionSlots);
            throw;
        }
    }


    public TerminalAttachResult Attach(
        string sessionId,
        string accessToken,
        long windowId,
        string? cursor,
        int creditBytes = 0,
        TerminalAttachmentMode mode = TerminalAttachmentMode.Controller)
    {
        var session = RequireAuthorizedSession(sessionId, accessToken);
        ulong? start = cursor is null ? null : ParseOffset(cursor);
        var credit = creditBytes == 0 ? _options.AttachmentCreditBytes : creditBytes;
        if (credit <= 0 || credit > _options.AttachmentCreditBytes)
            throw new TerminalProtocolException(BudgetExceeded);
        return session.Attach(windowId, start, credit, mode);
    }

    internal TerminalAttachResult AttachForWindow(
        string sessionId, string accessToken, Ryn.Core.IRynWindow window,
        string? cursor, int creditBytes, TerminalAttachmentMode mode)
    {
        lock (_windowAttachments)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (!_windowAttachments.TryGetValue(window, out var owner))
            {
                owner = new WindowAttachments();
                owner.ClosedHandler = (_, _) =>
                {
                    lock (_windowAttachments)
                    {
                        owner.Closed = true;
                        DetachWindow(window.Id);
                        window.Closed -= owner.ClosedHandler;
                    }
                };
                _windowAttachments.Add(window, owner);
                window.Closed += owner.ClosedHandler;
            }
            if (owner.Closed || window.WaitForCloseAsync().AsTask().IsCompleted)
                throw new TerminalProtocolException(UnauthorizedAttachment);
            return Attach(sessionId, accessToken, window.Id, cursor, creditBytes, mode);
        }
    }

    private sealed class WindowAttachments
    {
        internal bool Closed;
        internal EventHandler? ClosedHandler;
    }


    public async ValueTask<TerminalReadResult> ReadAsync(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        int maxBytes = 0,
        int waitMilliseconds = 20_000,
        CancellationToken cancellationToken = default)
    {
        return await ReadAuthorizedAsync(sessionId, attachmentId, generation, windowId,
            maxBytes, waitMilliseconds, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TerminalReadResult> ReadAuthorizedAsync(
        string sessionId, string attachmentId, string generation, long windowId,
        int maxBytes, int waitMilliseconds, CancellationToken cancellationToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return ErrorRead(UnknownSession);
        if (!TryParseOffset(generation, out var parsedGeneration))
            return ErrorRead(StaleGeneration);
        var requested = maxBytes == 0 ? _options.MaxReadBytes : maxBytes;
        if (requested <= 0 || requested > _options.MaxReadBytes || waitMilliseconds < 0 || waitMilliseconds > 20_000)
            return ErrorRead(InvalidRequest);
        return await session.ReadAsync(
            attachmentId, parsedGeneration, windowId, requested,
            TimeSpan.FromMilliseconds(waitMilliseconds), cancellationToken).ConfigureAwait(false);
    }


    public TerminalOperationResult Ack(
        string sessionId, string attachmentId, string generation, long windowId, string offset)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        if (!TryParseOffset(offset, out var parsedOffset))
            return ErrorOperation(InvalidOffset);
        return session.Ack(attachment, parsedOffset);
    }



    public async ValueTask<TerminalOperationResult> WriteAsync(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        string dataBase64,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        if (!TryDecodeBounded(dataBase64, _options.MaxInputBytes, out var data))
            return ErrorOperation(BudgetExceeded, session.StateName);
        return await session.EnqueueInputAsync(attachment, data, cancellationToken).ConfigureAwait(false);
    }



    public TerminalOperationResult Detach(string sessionId, string attachmentId, string generation, long windowId)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        return session.Detach(attachment);
    }



    public TerminalOperationResult Terminate(
        string sessionId, string attachmentId, string generation, long windowId)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        return session.Terminate(attachment);
    }



    public TerminalOperationResult StoreCheckpoint(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        string offset,
        string format,
        string payloadBase64,
        int cols,
        int rows)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        if (!TryParseOffset(offset, out var parsedOffset))
            return ErrorOperation(InvalidOffset, session.StateName);
        if (string.IsNullOrWhiteSpace(format) || format.Length > 128 ||
            format.Any(character => character < 0x21 || character > 0x7E) ||
            !ValidateDimensions(cols, rows, false))
            return ErrorOperation(IncompatibleCheckpoint, session.StateName);
        if (!TryDecodeBounded(payloadBase64, _options.MaxCheckpointBytes, out var payload))
            return ErrorOperation(BudgetExceeded, session.StateName);
        return session.StoreCheckpoint(attachment, parsedOffset, format, payload, cols, rows);
    }



    public TerminalLeaseResult ClaimResize(
        string sessionId, string attachmentId, string generation, long windowId)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorLease(error);
        return session.ClaimResize(attachment);
    }


    public TerminalOperationResult Resize(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        string leaseId,
        string leaseGeneration,
        int cols,
        int rows)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        if (!TryParseOffset(leaseGeneration, out var parsedLeaseGeneration))
            return ErrorOperation(StaleGeneration, session.StateName);
        if (!ValidateDimensions(cols, rows, false))
            return ErrorOperation(InvalidRequest, session.StateName);
        return session.Resize(attachment, leaseId, parsedLeaseGeneration, cols, rows);
    }



    public TerminalOperationResult ReleaseResize(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        string leaseId,
        string leaseGeneration)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            return ErrorOperation(error);
        if (!TryParseOffset(leaseGeneration, out var parsedLeaseGeneration))
            return ErrorOperation(StaleGeneration, session.StateName);
        return session.ReleaseResize(attachment, leaseId, parsedLeaseGeneration);
    }



    public TerminalStatusResult Status(string sessionId, string attachmentId, string generation, long windowId)
    {
        if (!TryGetAttachment(sessionId, attachmentId, generation, windowId, out var session, out var attachment, out var error))
            throw new TerminalProtocolException(error);
        return session.Status(attachment);
    }

    internal void DetachWindow(long windowId)
    {
        foreach (var session in _sessions.Values)
            session.DetachWindow(windowId);
    }

    internal bool TryGetNative(long id, out PtySession session) => _nativeSessions.TryGetValue(id, out session!);

    internal void StartLegacyRead(PtySession native, Action<ReadOnlyMemory<byte>> output, Action<int> exited)
    {
        _ = Task.Run(() =>
        {
            var buffer = new byte[4096];
            try
            {
                while (!native.CancellationToken.IsCancellationRequested)
                {
                    var read = native.Read(buffer, buffer.Length, 50);
                    if (read == -2) continue;
                    if (read <= 0)
                        break;
                    output(buffer.AsMemory(0, read));
                }
            }
            finally
            {
                _nativeSessions.TryRemove(native.Id, out _);
                var exit = native.WaitForExit();
                native.CompleteLifetime();
                exited(exit);
            }
        }, CancellationToken.None);
    }


    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        lock (_windowAttachments)
        {
            foreach (var entry in _windowAttachments)
                entry.Key.Closed -= entry.Value.ClosedHandler;
            _windowAttachments.Clear();
        }
        foreach (var session in _sessions.Values)
            session.Dispose();
        foreach (var native in _nativeSessions.Values)
            native.RequestKill();
    }

    private bool TryGetAttachment(
        string sessionId,
        string attachmentId,
        string generation,
        long windowId,
        out Session session,
        out Attachment attachment,
        out string error)
    {
        attachment = null!;
        if (!_sessions.TryGetValue(sessionId, out session!))
        {
            error = UnknownSession;
            return false;
        }
        if (!TryParseOffset(generation, out var parsedGeneration))
        {
            error = StaleGeneration;
            return false;
        }
        return session.TryAuthorizeAttachment(
            attachmentId, parsedGeneration, windowId, out attachment, out error);
    }
    private Session RequireAuthorizedSession(string sessionId, string accessToken)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new TerminalProtocolException(UnknownSession);
        var expected = System.Text.Encoding.UTF8.GetBytes(session.AccessToken);
        var presented = System.Text.Encoding.UTF8.GetBytes(accessToken);
        if (!CryptographicOperations.FixedTimeEquals(expected, presented))
            throw new TerminalProtocolException(UnauthorizedAttachment);
        return session;
    }

    private bool ReserveRetention(int bytes) => TryAdjustRetention(bytes);

    private bool TryAdjustRetention(int delta)
    {
        while (true)
        {
            var current = Volatile.Read(ref _retainedBytes);
            var updated = current + delta;
            if (updated < 0 || updated > _options.GlobalRetentionBytes)
                return false;
            if (Interlocked.CompareExchange(ref _retainedBytes, updated, current) == current)
                return true;
        }
    }

    private void ReleaseRetention(int bytes) =>
        _ = TryAdjustRetention(-bytes);

    private void ScavengeExited()
    {
        var cutoff = Stopwatch.GetTimestamp() - (long)(_options.ExitedSessionRetention.TotalSeconds * Stopwatch.Frequency);
        foreach (var pair in _sessions)
        {
            if (pair.Value.ExitedTimestamp is not { } exited || exited > cutoff)
                continue;
            if (_sessions.TryRemove(pair.Key, out var removed))
            {
                removed.Dispose();
                Interlocked.Decrement(ref _sessionSlots);
            }
        }
    }

    private static string NewOpaqueId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24));

    private static ulong ParseOffset(string value) =>
        TryParseOffset(value, out var parsed) ? parsed : throw new TerminalProtocolException(InvalidOffset);

    private static bool TryParseOffset(string value, out ulong parsed) =>
        ulong.TryParse(value, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out parsed);

    private static bool TryDecodeBounded(string encoded, int maxBytes, out byte[] bytes)
    {
        bytes = [];
        if (encoded is null || encoded.Length > ((long)maxBytes + 2) / 3 * 4)
            return false;
        try
        {
            bytes = Convert.FromBase64String(encoded);
            return bytes.Length <= maxBytes;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool ValidateDimensions(int cols, int rows, bool throwOnInvalid)
    {
        var valid = cols is > 0 and <= 500 && rows is > 0 and <= 500;
        if (!valid && throwOnInvalid)
            throw new ArgumentOutOfRangeException(nameof(cols), "PTY dimensions must be between 1 and 500.");
        return valid;
    }

    private static void ValidateOptions(TerminalOptions options)
    {
        if (options.AttachmentCreditBytes <= 0 || options.SessionRetentionBytes < options.AttachmentCreditBytes ||
            options.GlobalRetentionBytes < options.SessionRetentionBytes || options.MaxReadBytes <= 0 ||
            options.MaxReadBytes > options.AttachmentCreditBytes || options.MaxInputBytes <= 0 ||
            options.MaxInputItems <= 0 || options.MaxCheckpointBytes <= 0 || options.MaxAttachmentsPerSession <= 0 ||
            options.MaxSessions <= 0 || options.ExitDrainTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), "Terminal capacities and drain timeout must be positive and internally consistent.");
    }

    private static TerminalOperationResult SuccessOperation(string state) => new(true, null, state);
    private static TerminalOperationResult ErrorOperation(string error, string state = "disposed") => new(false, error, state);
    private static TerminalLeaseResult ErrorLease(string error) => new(false, error, null, null);
    private static TerminalReadResult ErrorRead(string error) => new(
        "disposed", "0", "0", string.Empty, false, null, null, error);

    private sealed class Session : IDisposable
    {
        private readonly object _gate = new();
        private readonly PtySession _native;
        private readonly TerminalRetentionMode _retentionMode;
        private readonly TerminalOptions _options;
        private readonly List<Segment> _segments = [];
        private readonly Dictionary<string, Attachment> _attachments = new(StringComparer.Ordinal);
        private readonly Channel<byte[]> _input;
#pragma warning disable CA2213 // Disposing while in-flight reads observe Token is unsafe; cancel and let tasks release it.
        private readonly CancellationTokenSource _lifetime = new();
#pragma warning restore CA2213
        private Action<int>? _releaseGlobal;
        private Func<int, bool>? _reserveGlobal;
        private ulong _retainedStart;
        private ulong _endOffset;
        private int _retainedBytes;
        private int _queuedInputBytes;
        private int _queueHighWater;
        private long _generation;
        private bool _processExited;
        private int? _exitCode;
        private long? _exitedTimestamp;
        private TerminalCheckpoint? _checkpoint;
        private int _checkpointBytes;
        private string? _resizeOwner;
        private string? _leaseId;
        private ulong _leaseGeneration;
        private bool _disposed;
        private long _bytesSent;
        private long _bytesConsumed;
        private long _pauseStarted;
        private long _pauseTicks;
        private bool _needsCheckpoint;
        private bool _readCompleted;
        private bool _outputTruncated;
        private long _drainDeadline;
        private int _reservedReadBytes;

        internal Session(
            string id,
            string accessToken,
            PtySession native,
            TerminalRetentionMode retentionMode,
            int cols,
            int rows,
            TerminalOptions options)
        {
            Id = id;
            AccessToken = accessToken;
            _native = native;
            _retentionMode = retentionMode;
            _options = options;
            Cols = cols;
            Rows = rows;
            _input = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(options.MaxInputItems)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false,
            });
        }

        internal string Id { get; }
        internal string AccessToken { get; }
        internal int Cols { get; private set; }
        internal int Rows { get; private set; }
        internal long? ExitedTimestamp { get { lock (_gate) return _exitedTimestamp; } }
        internal string StateName { get { lock (_gate) return StateText(GetStateLocked()); } }

        internal void Start(Func<int, bool> reserveGlobal, Action<int> releaseGlobal, Action nativeDone)
        {
            _reserveGlobal = reserveGlobal;
            _releaseGlobal = releaseGlobal;
            _ = Task.Run(InputLoopAsync, CancellationToken.None);
            _ = Task.Run(() => WaitForExitLoop(nativeDone), CancellationToken.None);
            _ = Task.Run(() => ReadLoop(reserveGlobal, releaseGlobal), CancellationToken.None);
        }

        internal TerminalAttachResult Attach(
            long windowId, ulong? requestedCursor, int creditBytes, TerminalAttachmentMode mode)
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (requestedCursor is null && _retainedStart != 0 && _checkpoint is null)
                    throw new TerminalProtocolException(HistoryUnavailable);
                var cursor = requestedCursor ?? (_checkpoint is null ? _retainedStart : ParseOffset(_checkpoint.Offset));
                if (_attachments.Count >= _options.MaxAttachmentsPerSession)
                    throw new TerminalProtocolException(BudgetExceeded);
                if (cursor < _retainedStart || cursor > _endOffset)
                    throw new TerminalProtocolException(HistoryUnavailable);
                if (cursor != _endOffset && _checkpoint is null && cursor != _retainedStart)
                    throw new TerminalProtocolException(HistoryUnavailable);
                if (_checkpoint is not null && cursor < ParseOffset(_checkpoint.Offset))
                    throw new TerminalProtocolException(HistoryUnavailable);

                if (mode == TerminalAttachmentMode.Controller)
                {
                    foreach (var current in _attachments.Values)
                        current.CanWrite = false;
                    if (_resizeOwner is not null)
                        RevokeLeaseLocked();
                }
                var attachment = new Attachment(
                    NewOpaqueId(), checked((ulong)Interlocked.Increment(ref _generation)), windowId,
                    cursor, creditBytes, mode == TerminalAttachmentMode.Controller);
                _attachments.Add(attachment.Id, attachment);
                SignalDataLocked();
                return new TerminalAttachResult(
                    attachment.Id,
                    attachment.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    StateText(GetStateLocked()), Format(_retainedStart), Format(_endOffset), Format(cursor),
                    _checkpoint is not null && cursor == ParseOffset(_checkpoint.Offset) ? _checkpoint : null);
            }
        }

        internal bool TryAuthorizeAttachment(
            string id, ulong generation, long windowId,
            out Attachment attachment, out string error)
        {
            lock (_gate)
            {
                if (!_attachments.TryGetValue(id, out attachment!))
                {
                    error = UnauthorizedAttachment;
                    return false;
                }
                if (attachment.Generation != generation)
                {
                    error = StaleGeneration;
                    return false;
                }
                if (attachment.WindowId != windowId)
                {
                    error = UnauthorizedAttachment;
                    return false;
                }
                error = string.Empty;
                return true;
            }
        }

        internal async ValueTask<TerminalReadResult> ReadAsync(
            string attachmentId,
            ulong generation,
            long windowId,
            int maxBytes,
            TimeSpan wait,
            CancellationToken cancellationToken)
        {
            Attachment attachment;
            lock (_gate)
            {
                if (!TryAuthorizeAttachment(attachmentId, generation, windowId, out attachment, out var error))
                    return ErrorRead(error);
                if (Interlocked.CompareExchange(ref attachment.ReadActive, 1, 0) != 0)
                    return ErrorRead(InvalidRequest);
            }
            var readOwner = attachment;

            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
                timeout.CancelAfter(wait);
                while (true)
                {
                    Task signal;
                    lock (_gate)
                    {
                        if (!TryAuthorizeAttachment(attachmentId, generation, windowId, out attachment, out var error))
                            return ErrorRead(error);
                        var ready = ReadLocked(attachment, maxBytes);
                        if (ready is not null)
                            return ready;
                        signal = attachment.Signal.WaitAsync(timeout.Token);
                    }

                    try
                    {
                        await signal.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        lock (_gate)
                        {
                            if (_disposed && !cancellationToken.IsCancellationRequested)
                                return ErrorRead(Cancelled);
                            return EmptyReadLocked(attachment, cancellationToken.IsCancellationRequested ? Cancelled : null);
                        }
                    }
                }
            }
            finally
            {
                Volatile.Write(ref readOwner.ReadActive, 0);
            }
        }

        internal TerminalOperationResult Ack(Attachment attachment, ulong offset)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment))
                    return ErrorOperation(UnauthorizedAttachment, StateText(GetStateLocked()));
                if (offset > attachment.DeliveredOffset)
                    return ErrorOperation(InvalidOffset, StateName);
                if (offset <= attachment.AcknowledgedOffset)
                    return SuccessOperation(StateName);
                var consumed = offset - attachment.AcknowledgedOffset;
                attachment.AcknowledgedOffset = offset;
                if (attachment.AckSampleStarted != 0 && offset >= attachment.AckSampleOffset)
                {
                    attachment.AckLatencyMilliseconds = Stopwatch.GetElapsedTime(attachment.AckSampleStarted).TotalMilliseconds;
                    attachment.AckSampleStarted = 0;
                }
                _bytesConsumed += checked((long)consumed);
                PruneLocked();
                Monitor.PulseAll(_gate);
                SignalDataLocked();
                return SuccessOperation(StateName);
            }
        }

        internal ValueTask<TerminalOperationResult> EnqueueInputAsync(Attachment attachment, byte[] bytes, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment) || !attachment.CanWrite)
                    return ValueTask.FromResult(ErrorOperation(UnauthorizedAttachment, StateText(GetStateLocked())));
                if (_processExited || _disposed)
                    return ValueTask.FromResult(ErrorOperation(TerminalExited, StateText(GetStateLocked())));
                if (cancellationToken.IsCancellationRequested)
                    return ValueTask.FromResult(ErrorOperation(Cancelled, StateText(GetStateLocked())));
                if (_queuedInputBytes > _options.MaxInputBytes - bytes.Length)
                    return ValueTask.FromResult(ErrorOperation(BudgetExceeded, StateText(GetStateLocked())));
                _queuedInputBytes += bytes.Length;
                if (_input.Writer.TryWrite(bytes))
                    return ValueTask.FromResult(SuccessOperation(StateText(GetStateLocked())));
                _queuedInputBytes -= bytes.Length;
                return ValueTask.FromResult(ErrorOperation(BudgetExceeded, StateText(GetStateLocked())));
            }
        }
        internal TerminalOperationResult StoreCheckpoint(
            Attachment attachment, ulong offset, string format, byte[] payload, int cols, int rows)
        {
            var replacement = new TerminalCheckpoint(
                Format(offset), format, Convert.ToBase64String(payload), cols, rows);
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment) || !attachment.CanWrite)
                    return ErrorOperation(UnauthorizedAttachment, StateText(GetStateLocked()));
                if (offset != attachment.AcknowledgedOffset || offset < _retainedStart || offset > _endOffset)
                    return ErrorOperation(InvalidOffset, StateName);
                while (_reservedReadBytes > 0 && !_disposed)
                    Monitor.Wait(_gate, TimeSpan.FromMilliseconds(50));
                if (_disposed)
                    return ErrorOperation(TerminalExited, StateText(GetStateLocked()));
                if (!IsCurrentLocked(attachment) || !attachment.CanWrite)
                    return ErrorOperation(UnauthorizedAttachment, StateText(GetStateLocked()));
                if (offset != attachment.AcknowledgedOffset || offset < _retainedStart || offset > _endOffset)
                    return ErrorOperation(InvalidOffset, StateName);

                var prunableBytes = CalculatePrunableBytesLocked(offset);
                var retainedAfterReplacement = _retainedBytes - prunableBytes + payload.Length;
                if (retainedAfterReplacement > _options.SessionRetentionBytes)
                    return ErrorOperation(BudgetExceeded, StateName);
                var globalDelta = payload.Length - _checkpointBytes - prunableBytes;
                if (_reserveGlobal is null || !_reserveGlobal(globalDelta))
                    return ErrorOperation(BudgetExceeded, StateName);

                _checkpoint = replacement;
                _checkpointBytes = payload.Length;
                _needsCheckpoint = false;
                PruneLocked(releaseGlobal: false);
                _queueHighWater = Math.Max(_queueHighWater, retainedAfterReplacement);
                Monitor.PulseAll(_gate);
                return SuccessOperation(StateName);
            }
        }

        internal TerminalLeaseResult ClaimResize(Attachment attachment)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment) || !attachment.CanWrite)
                    return ErrorLease(UnauthorizedAttachment);
                _resizeOwner = attachment.Id;
                _leaseId = NewOpaqueId();
                _leaseGeneration++;
                return new TerminalLeaseResult(true, null, _leaseId, Format(_leaseGeneration));
            }
        }

        internal TerminalOperationResult Resize(
            Attachment attachment, string leaseId, ulong leaseGeneration, int cols, int rows)
        {
            lock (_gate)
            {
                if (!LeaseMatchesLocked(attachment, leaseId, leaseGeneration))
                    return ErrorOperation(StaleGeneration, StateName);
                if (!_native.Resize((ushort)cols, (ushort)rows))
                    return ErrorOperation(TerminalExited, StateName);
                Cols = cols;
                Rows = rows;
                return SuccessOperation(StateName);
            }
        }

        internal TerminalOperationResult ReleaseResize(
            Attachment attachment, string leaseId, ulong leaseGeneration)
        {
            lock (_gate)
            {
                if (!LeaseMatchesLocked(attachment, leaseId, leaseGeneration))
                    return ErrorOperation(StaleGeneration, StateName);
                RevokeLeaseLocked();
                return SuccessOperation(StateName);
            }
        }

        internal TerminalOperationResult Detach(Attachment attachment)
        {
            lock (_gate)
            {
                if (_attachments.Remove(attachment.Id))
                {
                    if (_resizeOwner == attachment.Id)
                        RevokeLeaseLocked();
                    attachment.Wake();
                    SignalDataLocked();
                }
                return SuccessOperation(StateText(GetStateLocked()));
            }
        }

        internal void DetachWindow(long windowId)
        {
            lock (_gate)
            {
                foreach (var id in _attachments.Where(pair => pair.Value.WindowId == windowId).Select(pair => pair.Key).ToArray())
                {
                    var attachment = _attachments[id];
                    _attachments.Remove(id);
                    if (_resizeOwner == id)
                        RevokeLeaseLocked();
                    attachment.Wake();
                }
                SignalDataLocked();
            }
        }

        internal TerminalOperationResult Terminate(Attachment attachment)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment) || !attachment.CanWrite)
                    return ErrorOperation(UnauthorizedAttachment, StateText(GetStateLocked()));
                if (!_processExited)
                    _native.RequestKill();
                Monitor.PulseAll(_gate);
                SignalDataLocked();
                return SuccessOperation(StateText(GetStateLocked()));
            }
        }

        internal TerminalStatusResult Status(Attachment attachment)
        {
            lock (_gate)
            {
                if (!IsCurrentLocked(attachment))
                    throw new TerminalProtocolException(UnauthorizedAttachment);
                var outstanding = checked((int)(attachment.DeliveredOffset - attachment.AcknowledgedOffset));
                var pauseTicks = _pauseTicks + (_pauseStarted == 0 ? 0 : Stopwatch.GetTimestamp() - _pauseStarted);
                return new TerminalStatusResult(
                    StateText(GetStateLocked()), Format(_retainedStart), Format(_endOffset),
                    Format(attachment.ReadOffset), Format(attachment.DeliveredOffset), Format(attachment.AcknowledgedOffset),
                    outstanding, attachment.CreditBytes, Format(_endOffset),
                    _bytesSent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _bytesConsumed.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _retainedBytes + _checkpointBytes, _queueHighWater,
                    ((long)(pauseTicks * 1000.0 / Stopwatch.Frequency)).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    _processExited, _exitCode, FinalOffsetLocked(), _needsCheckpoint,
                    _resizeOwner == attachment.Id, _outputTruncated)
                {
                    AckLatencyMilliseconds = attachment.AckLatencyMilliseconds,
                };
            }
        }

        private void ReadLoop(Func<int, bool> reserveGlobal, Action<int> releaseGlobal)
        {
            var buffer = new byte[Math.Min(_options.MaxReadBytes, 64 * 1024)];
            try
            {
                while (!_native.CancellationToken.IsCancellationRequested)
                {
                    if (!_native.WaitReadable(50))
                    {
                        lock (_gate)
                        {
                            if (StopReadLocked())
                                break;
                        }
                        continue;
                    }
                    var reservedBytes = 0;
                    lock (_gate)
                    {
                        while ((reservedBytes = ReserveReadCapacityLocked(reserveGlobal, buffer.Length)) == 0
                               && !_disposed && !DrainDeadlineExpiredLocked()
                               && !_native.CancellationToken.IsCancellationRequested)
                        {
                            BeginPauseLocked();
                            Monitor.Wait(_gate, TimeSpan.FromMilliseconds(50));
                        }
                        EndPauseLocked();
                        if (StopReadLocked())
                        {
                            if (reservedBytes > 0)
                                ReleaseReadReservationLocked(releaseGlobal, reservedBytes);
                            break;
                        }
                    }

                    var read = _native.Read(buffer, reservedBytes, 50);
                    if (read == -2)
                    {
                        lock (_gate)
                            ReleaseReadReservationLocked(releaseGlobal, reservedBytes);
                        continue;
                    }
                    if (read <= 0)
                    {
                        lock (_gate)
                            ReleaseReadReservationLocked(releaseGlobal, reservedBytes);
                        break;
                    }
                    lock (_gate)
                        ReleaseReadReservationLocked(releaseGlobal, reservedBytes - read);
                    lock (_gate)
                    {
                        if (StopReadLocked())
                        {
                            ReleaseReadReservationLocked(releaseGlobal, read);
                            break;
                        }
                        var bytes = buffer.AsSpan(0, read).ToArray();
                        _segments.Add(new Segment(_endOffset, bytes));
                        _endOffset += (uint)read;
                        _retainedBytes += read;
                        _reservedReadBytes -= read;
                        Monitor.PulseAll(_gate);
                        _queueHighWater = Math.Max(_queueHighWater, _retainedBytes + _checkpointBytes);
                        SignalDataLocked();
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _readCompleted = true;
                    SignalDataLocked();
                }
                TryCompleteNativeLifetime();
            }
        }

        private void WaitForExitLoop(Action nativeDone)
        {
            var exit = _native.WaitForExit();
            lock (_gate)
            {
                _processExited = true;
                _exitCode = exit;
                _exitedTimestamp = Stopwatch.GetTimestamp();
                _drainDeadline = _exitedTimestamp.Value
                    + (long)(_options.ExitDrainTimeout.TotalSeconds * Stopwatch.Frequency);
                _input.Writer.TryComplete();
                Monitor.PulseAll(_gate);
                SignalDataLocked();
            }
            nativeDone();
            TryCompleteNativeLifetime();
        }

        private bool DrainDeadlineExpiredLocked() =>
            _processExited && !_readCompleted && Stopwatch.GetTimestamp() >= _drainDeadline;

        private bool StopReadLocked()
        {
            if (DrainDeadlineExpiredLocked() && !_disposed)
                _outputTruncated = true;
            return _disposed || _native.CancellationToken.IsCancellationRequested || _outputTruncated;
        }

        private string? FinalOffsetLocked() =>
            _processExited && _readCompleted ? Format(_endOffset) : null;

        private void TryCompleteNativeLifetime()
        {
            lock (_gate)
            {
                if (!_processExited || !_readCompleted)
                    return;
            }
            _native.CompleteLifetime();
        }

        private async Task InputLoopAsync()
        {
            try
            {
                await foreach (var bytes in _input.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    var written = _native.Write(bytes);
                    lock (_gate)
                        _queuedInputBytes -= bytes.Length;
                    if (!written)
                        break;
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                lock (_gate)
                {
                    _input.Writer.TryComplete();
                    while (_input.Reader.TryRead(out _)) { }
                    _queuedInputBytes = 0;
                }
            }
        }

        private TerminalReadResult? ReadLocked(Attachment attachment, int maxBytes)
        {
            if (attachment.ReadOffset < _retainedStart)
                return new TerminalReadResult(StateName, Format(attachment.ReadOffset), Format(attachment.ReadOffset),
                    string.Empty, _processExited, _exitCode, FinalOffsetLocked(), ResyncRequired,
                    _outputTruncated);
            var outstanding = attachment.DeliveredOffset - attachment.AcknowledgedOffset;
            var availableCredit = (ulong)attachment.CreditBytes - Math.Min((ulong)attachment.CreditBytes, outstanding);
            if (attachment.ReadOffset < _endOffset && availableCredit > 0)
            {
                var count = checked((int)Math.Min((ulong)maxBytes, Math.Min(availableCredit, _endOffset - attachment.ReadOffset)));
                var data = CopyLocked(attachment.ReadOffset, count);
                var start = attachment.ReadOffset;
                attachment.ReadOffset += (uint)data.Length;
                attachment.DeliveredOffset = attachment.ReadOffset;
                if (attachment.AckSampleStarted == 0)
                {
                    attachment.AckSampleStarted = Stopwatch.GetTimestamp();
                    attachment.AckSampleOffset = attachment.DeliveredOffset;
                }
                _bytesSent += data.Length;
                return new TerminalReadResult(StateName, Format(start), Format(attachment.ReadOffset),
                    Convert.ToBase64String(data), _processExited, _exitCode, FinalOffsetLocked(), null,
                    _outputTruncated);
            }
            if (_processExited || _disposed)
                return EmptyReadLocked(attachment, _disposed ? Cancelled : null);
            return null;
        }

        private TerminalReadResult EmptyReadLocked(Attachment attachment, string? error) => new(
            StateText(GetStateLocked()), Format(attachment.ReadOffset), Format(attachment.ReadOffset),
            string.Empty, _processExited, _exitCode, FinalOffsetLocked(), error,
            _outputTruncated);

        private byte[] CopyLocked(ulong offset, int count)
        {
            var result = new byte[count];
            var written = 0;
            foreach (var segment in _segments)
            {
                var segmentEnd = segment.Offset + (uint)segment.Bytes.Length;
                if (offset >= segmentEnd)
                    continue;
                var from = offset <= segment.Offset ? 0 : checked((int)(offset - segment.Offset));
                var take = Math.Min(segment.Bytes.Length - from, count - written);
                segment.Bytes.AsSpan(from, take).CopyTo(result.AsSpan(written));
                written += take;
                offset += (uint)take;
                if (written == count)
                    break;
            }
            return written == count ? result : result.AsSpan(0, written).ToArray();
        }

        private bool CanAcceptReadLocked()
        {
            if (_retainedBytes + _checkpointBytes < _options.SessionRetentionBytes)
                return true;
            PruneLocked();
            if (_retainedBytes + _checkpointBytes < _options.SessionRetentionBytes)
                return true;
            if (_retentionMode == TerminalRetentionMode.Reattachable)
                _needsCheckpoint = true;
            return false;
        }

        private int ReserveReadCapacityLocked(Func<int, bool> reserveGlobal, int maximum)
        {
            PruneLocked();
            var sessionAvailable = _options.SessionRetentionBytes - _checkpointBytes - _retainedBytes - _reservedReadBytes;
            if (sessionAvailable <= 0)
            {
                if (_retentionMode == TerminalRetentionMode.Reattachable)
                    _needsCheckpoint = true;
                return 0;
            }
            var requested = Math.Min(maximum, sessionAvailable);
            while (requested > 0)
            {
                if (reserveGlobal(requested))
                {
                    _reservedReadBytes += requested;
                    return requested;
                }
                requested /= 2;
            }
            return 0;
        }

        private void ReleaseReadReservationLocked(Action<int> releaseGlobal, int bytes)
        {
            if (bytes <= 0)
                return;
            _reservedReadBytes -= bytes;
            releaseGlobal(bytes);
            Monitor.PulseAll(_gate);
        }
        private int CalculatePrunableBytesLocked(ulong checkpointOffset)
        {
            var pruneTo = checkpointOffset;
            foreach (var attachment in _attachments.Values)
            {
                if (attachment.CanWrite)
                    pruneTo = Math.Min(pruneTo, attachment.AcknowledgedOffset);
            }
            var released = 0;
            foreach (var segment in _segments)
            {
                if (segment.Offset + (uint)segment.Bytes.Length > pruneTo)
                    break;
                released += segment.Bytes.Length;
            }
            return released;
        }

        private void PruneLocked(bool releaseGlobal = true)
        {
            ulong? controllerAck = null;
            foreach (var attachment in _attachments.Values)
            {
                if (attachment.CanWrite)
                    controllerAck = Math.Min(controllerAck ?? ulong.MaxValue, attachment.AcknowledgedOffset);
            }
            var pruneTo = _retentionMode == TerminalRetentionMode.Streaming
                ? controllerAck ?? _retainedStart
                : Math.Min(_checkpoint is null ? _retainedStart : ParseOffset(_checkpoint.Offset),
                    controllerAck ?? ulong.MaxValue);

            var released = 0;
            var removedCount = 0;
            while (removedCount < _segments.Count)
            {
                var segment = _segments[removedCount];
                var end = segment.Offset + (uint)segment.Bytes.Length;
                if (end > pruneTo)
                    break;
                removedCount++;
                _retainedStart = end;
                _retainedBytes -= segment.Bytes.Length;
                released += segment.Bytes.Length;
            }

            if (released > 0)
            {
                _segments.RemoveRange(0, removedCount);
                if (releaseGlobal)
                    _releaseGlobal?.Invoke(released);
            }
        }

        private bool IsCurrentLocked(Attachment attachment) =>
            _attachments.TryGetValue(attachment.Id, out var current) && ReferenceEquals(current, attachment);

        private bool LeaseMatchesLocked(Attachment attachment, string leaseId, ulong generation) =>
            _resizeOwner == attachment.Id && _leaseId == leaseId && _leaseGeneration == generation;

        private void RevokeLeaseLocked()
        {
            _resizeOwner = null;
            _leaseId = null;
            _leaseGeneration++;
        }

        private void SignalDataLocked()
        {
            foreach (var attachment in _attachments.Values)
                attachment.Wake();
        }

        private TerminalSessionState GetStateLocked()
        {
            if (_disposed) return TerminalSessionState.Disposed;
            if (_processExited)
                return _readCompleted ? TerminalSessionState.Exited : TerminalSessionState.Draining;
            if (_retainedBytes + _checkpointBytes >= _options.SessionRetentionBytes)
                return TerminalSessionState.Backpressured;
            if (_attachments.Count == 0) return TerminalSessionState.WaitingForAttachment;
            return TerminalSessionState.Streaming;
        }

        private void BeginPauseLocked()
        {
            if (_pauseStarted == 0)
                _pauseStarted = Stopwatch.GetTimestamp();
        }

        private void EndPauseLocked()
        {
            if (_pauseStarted == 0)
                return;
            _pauseTicks += Stopwatch.GetTimestamp() - _pauseStarted;
            _pauseStarted = 0;
        }

        private static string StateText(TerminalSessionState state) => state switch
        {
            TerminalSessionState.WaitingForAttachment => "waiting_for_attachment",
            TerminalSessionState.Streaming => "streaming",
            TerminalSessionState.Backpressured => "backpressured",
            TerminalSessionState.Draining => "draining",
            TerminalSessionState.Exited => "exited",
            TerminalSessionState.Disposed => "disposed",
            _ => throw new ArgumentOutOfRangeException(nameof(state)),
        };

        private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _input.Writer.TryComplete();
                _lifetime.Cancel();
                _native.RequestKill();
                var retained = _retainedBytes + _checkpointBytes;
                _retainedBytes = 0;
                _checkpointBytes = 0;
                _checkpoint = null;
                _segments.Clear();
                _releaseGlobal?.Invoke(retained);
                Monitor.PulseAll(_gate);
                SignalDataLocked();
            }
            GC.SuppressFinalize(this);
        }

        private static string Format(ulong value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    internal sealed class Attachment(
        string id,
        ulong generation,
        long windowId,
        ulong cursor,
        int creditBytes,
        bool canWrite)
    {
        internal string Id { get; } = id;
        internal ulong Generation { get; } = generation;
        internal long WindowId { get; } = windowId;
        internal int CreditBytes { get; } = creditBytes;
        internal bool CanWrite { get; set; } = canWrite;
        internal ulong ReadOffset { get; set; } = cursor;
        internal ulong DeliveredOffset { get; set; } = cursor;
        internal ulong AcknowledgedOffset { get; set; } = cursor;
        internal SemaphoreSlim Signal { get; } = new(0, 1);
        internal int ReadActive;
        internal long AckSampleStarted;
        internal ulong AckSampleOffset;
        internal double? AckLatencyMilliseconds;

        internal void Wake()
        {
            if (Signal.CurrentCount == 0)
                Signal.Release();
        }
    }

    private sealed record Segment(ulong Offset, byte[] Bytes);
}

public sealed class TerminalProtocolException : InvalidOperationException
{
    public TerminalProtocolException() { }
    public TerminalProtocolException(string message) : base(message) => Code = message;
    public TerminalProtocolException(string message, Exception innerException) : base(message, innerException) { }
    public string? Code { get; }
}

internal static class PtyCommandLine
{
    internal static string QuoteWindows(string command, string[] args)
    {
        var builder = new System.Text.StringBuilder();
        builder.Append(QuoteWindowsArg(command));
        for (var i = 1; i < args.Length; i++)
        {
            builder.Append(' ');
            builder.Append(QuoteWindowsArg(args[i]));
        }
        return builder.ToString();
    }

    private static string QuoteWindowsArg(string arg)
    {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"'))
            return arg;
        var builder = new System.Text.StringBuilder(arg.Length + 2);
        builder.Append('"');
        var backslashes = 0;
        foreach (var character in arg)
        {
            if (character == '\\')
            {
                backslashes++;
            }
            else if (character == '"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('"');
                backslashes = 0;
            }
            else
            {
                builder.Append('\\', backslashes);
                builder.Append(character);
                backslashes = 0;
            }
        }
        builder.Append('\\', backslashes * 2);
        builder.Append('"');
        return builder.ToString();
    }
}
