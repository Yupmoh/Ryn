using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Ryn.Core;
using Ryn.Core.Internal;
using Ryn.Ipc;

namespace Ryn.Plugins.Shell;

public sealed class PtyCommands : IDisposable
{
    /// <summary>
    /// Upper bound on a single <c>shell.ptyWrite</c> payload (decoded bytes). A PTY input write is interactive
    /// terminal input — even a large paste is far below this. The cap exists so an authorized-but-buggy (or
    /// hostile) frontend cannot hand us an unbounded base64 blob that we decode into a giant managed array and
    /// then block writing. Generous on purpose (8 MiB) so legitimate large pastes still go through.
    /// </summary>
    private const int MaxPtyWriteBytes = 8 * 1024 * 1024;

    private readonly IRynWebView _webView;
    private readonly ShellExecutionPolicy _policy;

    // Sessions are keyed by a process-monotonic id, not the OS pid. A pid is recyclable: once a child is reaped,
    // the kernel can hand the same number to an unrelated process, and since this id is also the JS handle and the
    // event-channel suffix, pid keying would let a stale handle alias a fresh session's channels. The monotonic id
    // is never reused for the lifetime of the process.
    private readonly ConcurrentDictionary<long, PtySession> _sessions = new();
    private long _nextSessionId;

    public PtyCommands(IRynWebView webView, ShellExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _webView = webView;
        _policy = policy;
    }

    [RynCommand("shell.pty")]
    public long Pty(string command, string argsJson)
    {
        // Route through the same validation choke point as execute/spawn — this also enforces the
        // argument policy, which the PTY path previously skipped entirely.
        var parsed = ShellExecutionPolicy.ParseArgs(argsJson);
        var resolvedCommand = _policy.ValidateInvocation(command, parsed);

        string[] args;
        if (parsed.Length == 0)
        {
            args = [resolvedCommand];
        }
        else
        {
            args = new string[parsed.Length + 1];
            args[0] = resolvedCommand;
            Array.Copy(parsed, 0, args, 1, parsed.Length);
        }

        var sessionId = Interlocked.Increment(ref _nextSessionId);
        var sessionIdStr = sessionId.ToString(CultureInfo.InvariantCulture);

        PtySession session = OperatingSystem.IsWindows()
            ? CreateWindowsSession(sessionId, sessionIdStr, resolvedCommand, args)
            : CreateUnixSession(sessionId, sessionIdStr, resolvedCommand, args);

        _sessions[sessionId] = session;

        var token = session.Cts.Token;
        _ = Task.Run(() => ReadLoop(session, sessionIdStr, token), CancellationToken.None);

        return sessionId;
    }

    private PtySessionUnix CreateUnixSession(long sessionId, string sessionIdStr, string resolvedCommand, string[] args)
    {
        var masterFd = PtyNative.ForkWithPty(resolvedCommand, args, out var childPid);

#pragma warning disable CA2000 // Batcher + CTS are owned by PtySessionUnix, disposed in exit/kill/Dispose
        var stdoutBatcher = new EventBatcher(_webView, $"shell.pty.stdout.{sessionIdStr}");
        var cts = new CancellationTokenSource();
#pragma warning restore CA2000

        return new PtySessionUnix(sessionId, childPid, masterFd, stdoutBatcher, cts);
    }

    private PtySessionWindows CreateWindowsSession(long sessionId, string sessionIdStr, string resolvedCommand, string[] args)
    {
        var commandLine = QuoteWindowsCommandLine(resolvedCommand, args);

        var conPty = PtyNativeWindows.CreateConPty(commandLine, 80, 24);

#pragma warning disable CA2000 // Batcher + CTS are owned by PtySessionWindows, disposed in exit/kill/Dispose
        var stdoutBatcher = new EventBatcher(_webView, $"shell.pty.stdout.{sessionIdStr}");
        var cts = new CancellationTokenSource();
#pragma warning restore CA2000

        return new PtySessionWindows(sessionId, conPty, stdoutBatcher, cts);
    }

    [RynCommand("shell.ptyWrite")]
    public bool PtyWrite(long sessionId, string base64Data)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        ArgumentNullException.ThrowIfNull(base64Data);

        // Reject before allocating/decoding: a base64 string of length N decodes to ~3N/4 bytes, so cap the
        // encoded length too. This bounds both the managed decode buffer and the blocking write that follows.
        if ((long)base64Data.Length / 4 * 3 > MaxPtyWriteBytes)
            return false;

        var bytes = Convert.FromBase64String(base64Data);
        if (bytes.Length > MaxPtyWriteBytes)
            return false;

        // The per-session transport lock serializes this short native write against the transport close that
        // the ReadLoop performs when the session ends, so we can never write into a closed (and possibly reused)
        // fd/handle. Write returns false once the transport is closed.
        return session.Write(bytes);
    }

    [RynCommand("shell.ptyResize")]
    public bool PtyResize(long sessionId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || cols > 500 || rows > 500)
            return false;

        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        return session.Resize((ushort)cols, (ushort)rows);
    }

    [RynCommand("shell.ptyMetrics")]
    public string PtyMetrics()
    {
        var entries = new List<ProcessMetrics>();
        foreach (var kvp in _sessions)
        {
            var s = kvp.Value;
            entries.Add(new ProcessMetrics(
                s.ChildPid,
                s.StdoutBatcher.AddedCount,
                s.StdoutBatcher.FlushedCount,
                s.StdoutBatcher.DroppedCount));
        }
        return JsonSerializer.Serialize(entries, ShellJsonContext.Default.ListProcessMetrics);
    }

    [RynCommand("shell.ptyKill")]
    public bool PtyKill(long sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;

        // Kill is fire-and-forget: it SIGKILLs the child and cancels the loop's CTS, which unblocks the
        // blocking read with EOF. The ReadLoop then drains, emits the exit event exactly once, closes the
        // transport, and removes the session. We deliberately do NOT close the master fd here — closing it
        // out from under a thread that may still be inside read() is the fd-reuse hazard this fix removes.
        session.RequestKill();
        return true;
    }

    private void ReadLoop(PtySession session, string sessionIdStr, CancellationToken token)
    {
        var buf = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = session.Read(buf);
                if (bytesRead <= 0)
                    break; // EOF or error — child exited (or was killed)

                var chunk = Convert.ToBase64String(buf, 0, bytesRead);
                var json = JsonSerializer.Serialize(chunk, ShellJsonContext.Default.String);
                session.StdoutBatcher.Add(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        // The ReadLoop is the sole owner of the transport's lifetime: it is the last code that can call read(),
        // so it is the only place that may close the fd/handle. Remove from the dictionary first so no new
        // command resolves this session, then flush, emit exit once, and close the transport.
        _sessions.TryRemove(session.Id, out _);

        session.StdoutBatcher.FlushNow();

        var exitCode = session.WaitForExit();
        EmitExitOnce(session, sessionIdStr, exitCode);

        session.CloseTransport();
        session.DisposeManaged();
    }

    private void EmitExitOnce(PtySession session, string sessionIdStr, int exitCode)
    {
        if (!session.TryClaimExitEmit())
            return;

        _webView.EmitEvent($"shell.pty.exit.{sessionIdStr}",
            exitCode.ToString(CultureInfo.InvariantCulture));
    }

    private static string QuoteWindowsCommandLine(string command, string[] args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(QuoteWindowsArg(command));
        for (var i = 1; i < args.Length; i++)
        {
            sb.Append(' ');
            sb.Append(QuoteWindowsArg(args[i]));
        }
        return sb.ToString();
    }

    private static string QuoteWindowsArg(string arg)
    {
        if (arg.Length > 0 && !arg.AsSpan().ContainsAny(' ', '\t', '"'))
            return arg;

        var sb = new System.Text.StringBuilder(arg.Length + 2);
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
            }
            else
            {
                sb.Append('\\', backslashes);
                sb.Append(c);
                backslashes = 0;
            }
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public void Dispose()
    {
        // Kill every live session. We do not close transports here: each session has a running ReadLoop that
        // owns its transport and will close it once the kill unblocks its read(). Closing fds from here would
        // reintroduce the close-vs-read race this design removes.
        foreach (var kvp in _sessions)
            kvp.Value.RequestKill();
    }
}

/// <summary>
/// Base type for a live PTY session. Encapsulates the platform transport (Unix master fd / Windows ConPTY
/// handles) and the per-session lock that makes the transport's lifetime race-free: writes/resizes take the
/// lock and short-circuit once the transport is closed, and the owning <c>ReadLoop</c> is the only caller that
/// closes the transport (after its loop — and therefore the last possible <c>read()</c> — has exited).
/// </summary>
internal abstract class PtySession
{
    private readonly Lock _transportLock = new();
    private bool _transportClosed;
    private int _exitEmitted;

    protected PtySession(long id, EventBatcher stdoutBatcher, CancellationTokenSource cts)
    {
        Id = id;
        StdoutBatcher = stdoutBatcher;
        Cts = cts;
    }

    /// <summary>Process-monotonic id; also the JS handle and the event-channel suffix.</summary>
    public long Id { get; }

    /// <summary>The OS process id of the child (for metrics only — never used as a dictionary key).</summary>
    public abstract int ChildPid { get; }

    public EventBatcher StdoutBatcher { get; }
    public CancellationTokenSource Cts { get; }

    /// <summary>One-shot guard so the exit event is emitted exactly once across kill and natural-exit paths.</summary>
    public bool TryClaimExitEmit() => Interlocked.Exchange(ref _exitEmitted, 1) == 0;

    /// <summary>SIGKILLs the child (idempotent) and cancels the read loop. Does NOT close the transport.</summary>
    public void RequestKill()
    {
        try { Cts.Cancel(); }
        catch (ObjectDisposedException) { /* loop already finished and disposed the CTS */ }

        KillChild();
    }

    /// <summary>Blocking read into <paramref name="buffer"/>. Called only from the owning ReadLoop.</summary>
    public abstract int Read(byte[] buffer);

    /// <summary>Waits for the child to exit and returns its exit code (-1 on failure).</summary>
    public abstract int WaitForExit();

    public bool Write(byte[] bytes)
    {
        lock (_transportLock)
        {
            if (_transportClosed)
                return false;
            return WriteLocked(bytes);
        }
    }

    public bool Resize(ushort cols, ushort rows)
    {
        lock (_transportLock)
        {
            if (_transportClosed)
                return false;
            return ResizeLocked(cols, rows);
        }
    }

    /// <summary>Closes the platform transport exactly once. Only the owning ReadLoop calls this.</summary>
    public void CloseTransport()
    {
        lock (_transportLock)
        {
            if (_transportClosed)
                return;
            _transportClosed = true;
            CloseTransportLocked();
        }
    }

    /// <summary>Disposes the managed helpers (CTS, batcher). Transport is closed separately via CloseTransport.</summary>
    public void DisposeManaged()
    {
        StdoutBatcher.Dispose();
        Cts.Dispose();
    }

    protected abstract void KillChild();
    protected abstract bool WriteLocked(byte[] bytes);
    protected abstract bool ResizeLocked(ushort cols, ushort rows);
    protected abstract void CloseTransportLocked();
}

internal sealed class PtySessionUnix : PtySession
{
    private readonly int _childPid;
    private readonly int _masterFd;

    internal PtySessionUnix(long id, int childPid, int masterFd, EventBatcher stdoutBatcher, CancellationTokenSource cts)
        : base(id, stdoutBatcher, cts)
    {
        _childPid = childPid;
        _masterFd = masterFd;
    }

    public override int ChildPid => _childPid;

    public override int Read(byte[] buffer) => PtyNative.Read(_masterFd, buffer, buffer.Length);

    public override int WaitForExit() => PtyNative.WaitForExit(_childPid);

    protected override void KillChild() => _ = PtyNative.Kill(_childPid, 9); // SIGKILL

    protected override bool WriteLocked(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = PtyNative.Write(_masterFd, bytes, offset, bytes.Length - offset);
            if (written <= 0) return false;
            offset += written;
        }
        return true;
    }

    protected override bool ResizeLocked(ushort cols, ushort rows) => PtyNative.SetWindowSize(_masterFd, rows, cols);

    protected override void CloseTransportLocked() => _ = PtyNative.Close(_masterFd);
}

internal sealed class PtySessionWindows : PtySession
{
    private readonly PtyNativeWindows.ConPtySession _conPty;

    internal PtySessionWindows(long id, PtyNativeWindows.ConPtySession conPty, EventBatcher stdoutBatcher, CancellationTokenSource cts)
        : base(id, stdoutBatcher, cts)
    {
        _conPty = conPty;
    }

    public override int ChildPid => _conPty.ProcessId;

    public override int Read(byte[] buffer) => PtyNativeWindows.Read(_conPty.OutputReadHandle, buffer, buffer.Length);

    public override int WaitForExit() => PtyNativeWindows.WaitForExit(_conPty.ProcessHandle);

    protected override void KillChild() => PtyNativeWindows.Kill(_conPty.ProcessHandle);

    protected override bool WriteLocked(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = PtyNativeWindows.Write(_conPty.InputWriteHandle, bytes, offset, bytes.Length - offset);
            if (written <= 0) return false;
            offset += written;
        }
        return true;
    }

    protected override bool ResizeLocked(ushort cols, ushort rows) => PtyNativeWindows.Resize(_conPty.PseudoConsole, cols, rows);

    protected override void CloseTransportLocked() => _conPty.Dispose();
}

/// <summary>
/// Platform-specific PTY operations via P/Invoke to libc.
/// macOS and Linux only.
/// </summary>
internal static partial class PtyNative
{
    // TIOCSWINSZ differs per platform
    private static readonly ulong TIOCSWINSZ = OperatingSystem.IsMacOS()
        ? 0x80087467UL
        : 0x5414UL; // Linux

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize
    {
        public ushort ws_row;
        public ushort ws_col;
        public ushort ws_xpixel;
        public ushort ws_ypixel;
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint libc_read(int fd, byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int libc_close(int fd);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int libc_kill(int pid, int sig);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int libc_ioctl(int fd, ulong request, ref WinSize ws);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int libc_waitpid(int pid, out int status, int options);

    /// <summary>
    /// Forks a child process with a PTY via the native <c>ryn-pty</c> shim. Returns the master fd in the parent.
    /// </summary>
    /// <remarks>
    /// The spawn deliberately goes only through the native <c>ryn_pty_spawn</c> shim, which performs the
    /// fork+exec entirely in C with no managed work after <c>fork()</c>. We do NOT fall back to a managed
    /// <c>forkpty</c> + marshalled <c>execvp</c>: running P/Invoke string/array marshalling (which allocates and
    /// touches runtime/allocator state) in the forked child of a multithreaded .NET process is not
    /// async-signal-safe and can deadlock the child. If the shim is missing we fail loudly here instead of
    /// silently taking a working-by-luck path.
    /// </remarks>
    internal static int ForkWithPty(string command, string[] args, out int childPid)
    {
        // Build null-terminated argv for the native shim
        var argv = new string?[args.Length + 1];
        Array.Copy(args, argv, args.Length);
        argv[args.Length] = null;

        int result;
        try
        {
            result = ryn_pty_spawn(command, argv, out var masterFd, out childPid);
            if (result == 0)
                return masterFd;
        }
        catch (EntryPointNotFoundException ex)
        {
            throw new PlatformNotSupportedException(NativeShimMissingMessage, ex);
        }
        catch (DllNotFoundException ex)
        {
            throw new PlatformNotSupportedException(NativeShimMissingMessage, ex);
        }

        var errno = Marshal.GetLastPInvokeError();
        throw new InvalidOperationException(
            $"ryn_pty_spawn failed for command '{command}' (rc={result}, errno={errno}).");
    }

    private const string NativeShimMissingMessage =
        "shell.pty requires the native 'ryn-pty' shim, which was not found next to the application. " +
        "PTY support is unavailable on this platform/build. (The unsafe managed forkpty fallback has been removed.)";

    // CA5393: AssemblyDirectory is intentional and is the hardening, not the risk. The 'ryn-pty' shim is a
    // first-party native library that ships next to the application binary, so restricting the search to the
    // assembly directory + System32 is strictly narrower than the default probing order (which includes the
    // current working directory and PATH and is the actual hijack surface). INT-05 residual hardening.
#pragma warning disable CA5393
    [DllImport("ryn-pty", EntryPoint = "ryn_pty_spawn", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int ryn_pty_spawn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string command,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string?[] argv,
        out int masterFd,
        out int childPid);
#pragma warning restore CA5393

    internal static int Read(int fd, byte[] buf, int count)
    {
        return (int)libc_read(fd, buf, count);
    }

    internal static unsafe int Write(int fd, byte[] buf, int offset, int count)
    {
        fixed (byte* ptr = buf)
        {
            return (int)libc_write_ptr(fd, ptr + offset, count);
        }
    }

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern unsafe nint libc_write_ptr(int fd, byte* buf, nint count);

    internal static int Close(int fd)
    {
        return libc_close(fd);
    }

    internal static int Kill(int pid, int signal)
    {
        return libc_kill(pid, signal);
    }

    internal static bool SetWindowSize(int fd, ushort rows, ushort cols)
    {
        var ws = new WinSize
        {
            ws_row = rows,
            ws_col = cols,
            ws_xpixel = 0,
            ws_ypixel = 0,
        };
        return libc_ioctl(fd, TIOCSWINSZ, ref ws) == 0;
    }

    /// <summary>
    /// Waits for a child process to exit and returns the exit code.
    /// Returns -1 if the wait fails.
    /// </summary>
    internal static int WaitForExit(int pid)
    {
        var result = libc_waitpid(pid, out var status, 0);
        if (result < 0)
            return -1;

        // WIFEXITED: (status & 0x7f) == 0 means normal exit
        if ((status & 0x7F) == 0)
            return (status >> 8) & 0xFF; // WEXITSTATUS

        // Killed by signal — return negative signal number
        return -(status & 0x7F);
    }
}
