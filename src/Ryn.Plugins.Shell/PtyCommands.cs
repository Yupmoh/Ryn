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
    private readonly TerminalSessionService _terminal;
    private readonly bool _ownsTerminal;

    // Kept local to this legacy facade so public numeric handles and event names remain unchanged. The owned
    // native session itself comes from TerminalSessionService, which is shared with terminal.*.
    private readonly ConcurrentDictionary<long, LegacySession> _sessions = new();
    private long _nextSessionId;

    public PtyCommands(IRynWebView webView, ShellExecutionPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        _webView = webView;
        _terminal = new TerminalSessionService(policy, new TerminalOptions());
        _ownsTerminal = true;
    }

    public PtyCommands(IRynWebView webView, ShellExecutionPolicy policy, TerminalSessionService terminal)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(terminal);
        _webView = webView;
        _terminal = terminal;
    }

    [RynCommand("shell.pty")]
    public long Pty(string command, string argsJson, int cols = 80, int rows = 24)
    {
        if (cols <= 0 || rows <= 0 || cols > 500 || rows > 500)
            throw new ArgumentOutOfRangeException(nameof(cols), "PTY dimensions must be between 1 and 500.");

#pragma warning disable CA2000 // The shared service read loop becomes the sole native lifetime owner immediately below.
        var native = _terminal.SpawnNative(command, argsJson, cols, rows);
#pragma warning restore CA2000
        var id = Interlocked.Increment(ref _nextSessionId);
#pragma warning disable CA2000 // LegacySession owns and disposes the batcher from the read-loop completion path.
        var legacy = new LegacySession(id, native, new EventBatcher(_webView, $"shell.pty.stdout.{id}"));
#pragma warning restore CA2000
        _sessions[id] = legacy;

        _terminal.StartLegacyRead(native, bytes => EmitOutput(legacy, bytes), exit => Finish(legacy, exit));
        return id;
    }


    [RynCommand("shell.ptyWrite")]
    public bool PtyWrite(long sessionId, string base64Data)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        ArgumentNullException.ThrowIfNull(base64Data);
        if ((long)base64Data.Length / 4 * 3 > MaxPtyWriteBytes)
            return false;
        var bytes = Convert.FromBase64String(base64Data);
        return bytes.Length <= MaxPtyWriteBytes && session.Native.Write(bytes);
    }

    [RynCommand("shell.ptyResize")]
    public bool PtyResize(long sessionId, int cols, int rows)
    {
        if (cols <= 0 || rows <= 0 || cols > 500 || rows > 500)
            return false;
        return _sessions.TryGetValue(sessionId, out var session)
            && session.Native.Resize((ushort)cols, (ushort)rows);
    }

    [RynCommand("shell.ptyMetrics")]
    public string PtyMetrics()
    {
        var entries = _sessions.Values.Select(session => new ProcessMetrics(
            session.Native.ChildPid,
            session.StdoutBatcher.AddedCount,
            session.StdoutBatcher.FlushedCount,
            session.StdoutBatcher.DroppedCount)).ToList();
        return JsonSerializer.Serialize(entries, ShellJsonContext.Default.ListProcessMetrics);
    }

    [RynCommand("shell.ptySignal")]
    public bool PtySignal(long sessionId, int signal) =>
        _sessions.TryGetValue(sessionId, out var session) && session.Native.Signal(signal);

    [RynCommand("shell.ptyKill")]
    public bool PtyKill(long sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        session.Native.RequestKill();
        return true;
    }


    private static void EmitOutput(LegacySession session, ReadOnlyMemory<byte> bytes)
    {
        var chunk = Convert.ToBase64String(bytes.Span);
        session.StdoutBatcher.Add(JsonSerializer.Serialize(chunk, ShellJsonContext.Default.String));
    }

    private void Finish(LegacySession session, int exitCode)
    {
        _sessions.TryRemove(session.Id, out _);
        session.StdoutBatcher.FlushNow();
        try
        {
            _webView.EmitEvent($"shell.pty.exit.{session.Id}",
                exitCode.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception exception) when (exception is ObjectDisposedException or InvalidOperationException)
        {
            // The webview may already be gone during orderly host teardown; the PTY lifetime still completes.
        }
        session.StdoutBatcher.Dispose();
    }

    public void Dispose()
    {
        foreach (var session in _sessions.Values)
            session.Native.RequestKill();
        if (_ownsTerminal)
            _terminal.Dispose();
    }

    private sealed record LegacySession(long Id, PtySession Native, EventBatcher StdoutBatcher);
}

/// <summary>
/// Race-free native PTY lifetime. The sole read loop closes the native transport after its final read; writes
/// and resizes share the transport lock and can never target a recycled descriptor or handle.
/// </summary>
internal abstract class PtySession : IDisposable
{
    private readonly Lock _transportLock = new();
    private readonly Lock _lifecycleLock = new();
#pragma warning disable CA2213 // The sole read loop disposes this after its final native read.
    private readonly CancellationTokenSource _cts = new();
#pragma warning restore CA2213
    private volatile bool _transportClosed;
    private bool _killRequested;
    private int _lifetimeCompleted;

    protected PtySession(long id) => Id = id;

    public long Id { get; }
    public abstract int ChildPid { get; }
    public CancellationToken CancellationToken => _cts.Token;

    public void RequestKill()
    {
        lock (_lifecycleLock)
        {
            if (_transportClosed || _killRequested)
                return;
            _killRequested = true;
            try { _cts.Cancel(); }
            catch (ObjectDisposedException) { }
            // Do not wait for _transportLock: a native stdin write can block until killing the child unblocks it.
            KillChild();
        }
    }

    public abstract int Read(byte[] buffer);
    public abstract bool WaitReadable(int timeoutMilliseconds);
    public abstract int Read(byte[] buffer, int count, int timeoutMilliseconds);
    public abstract int WaitForExit();

    public bool Signal(int signal)
    {
        lock (_lifecycleLock)
        {
            if (_transportClosed)
                return false;
            return SignalLocked(signal);
        }
    }

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
    public void CompleteLifetime()
    {
        if (Interlocked.Exchange(ref _lifetimeCompleted, 1) != 0)
            return;
        lock (_lifecycleLock)
        {
            _transportClosed = true;
            lock (_transportLock)
                CloseTransportLocked();
        }
        _cts.Dispose();
    }

    public void Dispose()
    {
        RequestKill();
        GC.SuppressFinalize(this);
    }

    protected abstract void KillChild();
    protected abstract bool SignalLocked(int signal);
    protected abstract bool WriteLocked(byte[] bytes);
    protected abstract bool ResizeLocked(ushort cols, ushort rows);
    protected abstract void CloseTransportLocked();
}

internal sealed class PtySessionUnix : PtySession
{
    private readonly int _childPid;
    private readonly int _masterFd;

    internal PtySessionUnix(long id, int childPid, int masterFd)
        : base(id)
    {
        _childPid = childPid;
        _masterFd = masterFd;
    }

    public override int ChildPid => _childPid;
    public override int Read(byte[] buffer) => PtyNative.Read(_masterFd, buffer, buffer.Length);
    public override int Read(byte[] buffer, int count, int timeoutMilliseconds) =>
        PtyNative.Read(_masterFd, buffer, count, timeoutMilliseconds);
    public override bool WaitReadable(int timeoutMilliseconds) =>
        PtyNative.WaitReadable(_masterFd, timeoutMilliseconds);
    public override int WaitForExit() => PtyNative.WaitForExit(_childPid);
    protected override void KillChild() => _ = PtyNative.Kill(_childPid, 9);
    protected override bool SignalLocked(int signal) => PtyNative.Kill(_childPid, signal) == 0;

    protected override bool WriteLocked(byte[] bytes)
    {
        var offset = 0;
        while (offset < bytes.Length)
        {
            if (CancellationToken.IsCancellationRequested) return false;
            var written = PtyNative.Write(_masterFd, bytes, offset, bytes.Length - offset);
            if (written < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == 4) continue;
                if (error == (OperatingSystem.IsMacOS() ? 35 : 11))
                {
                    PtyNative.WaitWritable(_masterFd, 50);
                    continue;
                }
                return false;
            }
            if (written == 0) return false;
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

    internal PtySessionWindows(long id, PtyNativeWindows.ConPtySession conPty)
        : base(id) => _conPty = conPty;

    public override int ChildPid => _conPty.ProcessId;
    public override int Read(byte[] buffer) => PtyNativeWindows.Read(_conPty.OutputReadHandle, buffer, buffer.Length);
    public override int Read(byte[] buffer, int count, int timeoutMilliseconds) =>
        PtyNativeWindows.Read(_conPty.OutputReadHandle, buffer, count, timeoutMilliseconds);
    public override bool WaitReadable(int timeoutMilliseconds) =>
        PtyNativeWindows.WaitReadable(_conPty.OutputReadHandle, timeoutMilliseconds);
    public override int WaitForExit() => PtyNativeWindows.WaitForExit(_conPty.ProcessHandle);
    protected override void KillChild() => PtyNativeWindows.Kill(_conPty.ProcessHandle);

    protected override bool SignalLocked(int signal)
    {
        if (signal is not (9 or 15)) return false;
        PtyNativeWindows.Kill(_conPty.ProcessHandle);
        return true;
    }

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

    protected override bool ResizeLocked(ushort cols, ushort rows) =>
        PtyNativeWindows.Resize(_conPty.PseudoConsole, cols, rows);
    protected override void CloseTransportLocked() => _conPty.Dispose();
}

/// <summary>
/// Platform-specific PTY operations via P/Invoke to libc.
/// macOS and Linux only.
/// </summary>
internal static partial class PtyNative
{
    private const short PollIn = 0x0001;

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int fd;
        public short events;
        public short revents;
    }


    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint libc_read(int fd, byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "poll", SetLastError = true)]
    private static extern int libc_poll(ref PollFd fds, nuint count, int timeoutMilliseconds);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int libc_close(int fd);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int libc_kill(int pid, int sig);

    // ioctl is variadic; its argument ABI differs on Apple Silicon.
    [DllImport("ryn-pty", EntryPoint = "ryn_pty_resize", SetLastError = true)]
    private static extern int NativeResize(int fd, ushort rows, ushort cols);

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
    internal static int ForkWithPty(
        string command,
        string[] args,
        IReadOnlyDictionary<string, string> environment,
        string? workingDirectory,
        ushort cols,
        ushort rows,
        out int childPid)
    {
        using var argv = new NativeStringArray(args);
        using var envp = NativeStringArray.CreateEnvironment(environment);

        int result;
        try
        {
            result = ryn_pty_spawn(
                command,
                argv.Pointer,
                envp.Pointer,
                workingDirectory,
                cols,
                rows,
                out var masterFd,
                out childPid);
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

        throw new InvalidOperationException(
            $"ryn_pty_spawn failed for command '{command}' (errno={-result}).");
    }

    private const string NativeShimMissingMessage =
        "shell.pty requires the native 'ryn-pty' shim, which was not found next to the application. " +
        "PTY support is unavailable on this platform/build. (The unsafe managed forkpty fallback has been removed.)";

    // CA5393: AssemblyDirectory is intentional and is the hardening, not the risk. The 'ryn-pty' shim is a
    // first-party native library that ships next to the application binary, so restricting the search to the
    // assembly directory + System32 is strictly narrower than the default probing order (which includes the
    // current working directory and PATH and is the actual hijack surface). INT-05 residual hardening.
#pragma warning disable CA5393
    [DllImport("ryn-pty", EntryPoint = "ryn_pty_spawn", CharSet = CharSet.Ansi, BestFitMapping = false, ThrowOnUnmappableChar = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static extern int ryn_pty_spawn(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string command,
        nint argv,
        nint envp,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? workingDirectory,
        ushort cols,
        ushort rows,
        out int masterFd,
        out int childPid);
#pragma warning restore CA5393

    internal static int Read(int fd, byte[] buf, int count)
    {
        while (true)
        {
            var result = (int)libc_read(fd, buf, count);
            if (result >= 0)
                return result;

            var error = Marshal.GetLastPInvokeError();
            if (error == 4) // EINTR: retry after an interrupted system call.
                continue;
            if (error == (OperatingSystem.IsMacOS() ? 35 : 11)) return -2;
            if (error == 5) // EIO: PTY masters report EOF this way after the slave closes.
                return 0;
            return -1;
        }
    }

    internal static bool WaitReadable(int fd, int timeoutMilliseconds)
    {
        var descriptor = new PollFd { fd = fd, events = PollIn };
        int ready;
        do
        {
            ready = libc_poll(ref descriptor, 1, timeoutMilliseconds);
        }
        while (ready < 0 && Marshal.GetLastPInvokeError() == 4);
        return ready > 0;
    }

    internal static void WaitWritable(int fd, int timeoutMilliseconds)
    {
        var descriptor = new PollFd { fd = fd, events = 0x0004 };
        _ = libc_poll(ref descriptor, 1, timeoutMilliseconds);
    }

    internal static int Read(int fd, byte[] buf, int count, int timeoutMilliseconds) =>
        WaitReadable(fd, timeoutMilliseconds) ? Read(fd, buf, count) : -2;

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
        return NativeResize(fd, rows, cols) == 0;
    }

    /// <summary>
    /// Waits for a child process to exit and returns the exit code.
    /// Returns -1 if the wait fails.
    /// </summary>
    internal static int WaitForExit(int pid)
    {
        int result;
        int status;
        do
        {
            result = libc_waitpid(pid, out status, 0);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4); // EINTR

        if (result < 0)
            return -1;

        // WIFEXITED: (status & 0x7f) == 0 means normal exit
        if ((status & 0x7F) == 0)
            return (status >> 8) & 0xFF; // WEXITSTATUS

        // Killed by signal — return negative signal number
        return -(status & 0x7F);
    }
}
