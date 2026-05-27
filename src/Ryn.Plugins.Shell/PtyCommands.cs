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
    private readonly IRynWebView _webView;
    private readonly ConcurrentDictionary<int, PtySession> _sessions = new();

    public PtyCommands(IRynWebView webView)
    {
        _webView = webView;
    }

    [RynCommand("shell.pty")]
    public int Pty(string command, string argsJson)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "PTY is not supported on Windows yet. ConPTY integration is planned for a future release.");

        var resolvedCommand = ShellCommands.ValidateAndResolveCommand(command);

        string[] args;
        if (string.IsNullOrEmpty(argsJson) || argsJson == "{}")
        {
            args = [resolvedCommand];
        }
        else
        {
            var parsed = JsonSerializer.Deserialize(argsJson, ShellJsonContext.Default.StringArray);
            if (parsed is null || parsed.Length == 0)
            {
                args = [resolvedCommand];
            }
            else
            {
                args = new string[parsed.Length + 1];
                args[0] = resolvedCommand;
                Array.Copy(parsed, 0, args, 1, parsed.Length);
            }
        }

        var masterFd = PtyNative.ForkWithPty(resolvedCommand, args, out var childPid);

        var pidStr = childPid.ToString(CultureInfo.InvariantCulture);

#pragma warning disable CA2000 // Batcher + CTS are owned by PtySession, disposed in exit/kill/Dispose
        var stdoutBatcher = new EventBatcher(_webView, $"shell.pty.stdout.{pidStr}");
        var cts = new CancellationTokenSource();
#pragma warning restore CA2000

        var session = new PtySession(childPid, masterFd, stdoutBatcher, cts);
        _sessions[childPid] = session;

        // Background read loop: reads raw bytes from the PTY master fd,
        // base64-encodes them, and pushes through EventBatcher.
        var token = cts.Token;
        _ = Task.Run(() => ReadLoop(session, pidStr, token), CancellationToken.None);

        return childPid;
    }

    [RynCommand("shell.ptyWrite")]
    public bool PtyWrite(int pid, string base64Data)
    {
        if (!_sessions.TryGetValue(pid, out var session))
            return false;

        var bytes = Convert.FromBase64String(base64Data);
        var offset = 0;
        while (offset < bytes.Length)
        {
            var written = PtyNative.Write(session.MasterFd, bytes, offset, bytes.Length - offset);
            if (written <= 0) return false;
            offset += written;
        }
        return true;
    }

    [RynCommand("shell.ptyResize")]
    public bool PtyResize(int pid, int cols, int rows)
    {
        if (!_sessions.TryGetValue(pid, out var session))
            return false;

        return PtyNative.SetWindowSize(session.MasterFd, (ushort)rows, (ushort)cols);
    }

    [RynCommand("shell.ptyKill")]
    public bool PtyKill(int pid)
    {
        if (!_sessions.TryRemove(pid, out var session))
            return false;

        CleanupSession(session, pid);
        return true;
    }

    private void ReadLoop(PtySession session, string pidStr, CancellationToken token)
    {
        var buf = new byte[4096];

        try
        {
            while (!token.IsCancellationRequested)
            {
                var bytesRead = PtyNative.Read(session.MasterFd, buf, buf.Length);

                if (bytesRead <= 0)
                    break; // EOF or error — child exited

                var chunk = Convert.ToBase64String(buf, 0, bytesRead);
                var json = JsonSerializer.Serialize(chunk, ShellJsonContext.Default.String);
                session.StdoutBatcher.Add(json);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on cancellation
        }

        // If we still own this session (wasn't killed), handle exit
        if (_sessions.TryRemove(session.ChildPid, out _))
        {
            session.StdoutBatcher.FlushNow();

            var exitCode = PtyNative.WaitForExit(session.ChildPid);
            var exitCodeStr = exitCode.ToString(CultureInfo.InvariantCulture);
            _webView.EmitEvent($"shell.pty.exit.{pidStr}", exitCodeStr);

            session.Dispose();
        }
    }

    private void CleanupSession(PtySession session, int pid)
    {
        session.Cts.Cancel();

        _ = PtyNative.Kill(session.ChildPid, 9); // SIGKILL

        session.StdoutBatcher.FlushNow();

        var exitCode = PtyNative.WaitForExit(session.ChildPid);
        var pidStr = pid.ToString(CultureInfo.InvariantCulture);
        _webView.EmitEvent($"shell.pty.exit.{pidStr}",
            exitCode.ToString(CultureInfo.InvariantCulture));

        session.Dispose();
    }

    public void Dispose()
    {
        foreach (var kvp in _sessions)
        {
            if (_sessions.TryRemove(kvp.Key, out var session))
                CleanupSession(session, kvp.Key);
        }
    }
}

internal sealed record PtySession(int ChildPid, int MasterFd, EventBatcher StdoutBatcher, CancellationTokenSource Cts) : IDisposable
{
    public void Dispose()
    {
        Cts.Dispose();
        StdoutBatcher.Dispose();
        _ = PtyNative.Close(MasterFd);
    }
}

/// <summary>
/// Platform-specific PTY operations via P/Invoke to libc.
/// macOS and Linux only. Windows requires ConPTY (not yet implemented).
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

    // forkpty lives in libutil on some systems, but on macOS (and glibc) it's in libc.
    // Using "libutil" as primary with "libc" fallback would complicate things;
    // on modern macOS/Linux forkpty is resolved from libc.
    [DllImport("libc", EntryPoint = "forkpty", SetLastError = true)]
    private static extern int forkpty(out int master, IntPtr name, IntPtr termp, IntPtr winp);

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint libc_read(int fd, byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "write", SetLastError = true)]
    private static extern nint libc_write(int fd, byte[] buf, nint count);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int libc_close(int fd);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int libc_kill(int pid, int sig);

    [DllImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static extern int libc_ioctl(int fd, ulong request, ref WinSize ws);

    [DllImport("libc", EntryPoint = "waitpid", SetLastError = true)]
    private static extern int libc_waitpid(int pid, out int status, int options);

    [DllImport("libc", EntryPoint = "execvp", SetLastError = true,
        BestFitMapping = false, ThrowOnUnmappableChar = true)]
    private static extern int libc_execvp(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string file,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPUTF8Str)] string?[] argv);

    [DllImport("libc", EntryPoint = "_exit")]
    private static extern void libc_exit(int status);

    /// <summary>
    /// Forks a child process with a PTY. Returns the master fd in the parent.
    /// Throws on failure.
    /// </summary>
    internal static int ForkWithPty(string command, string[] args, out int childPid)
    {
        var pid = forkpty(out var masterFd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (pid < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw new InvalidOperationException($"forkpty failed with errno {errno}");
        }

        if (pid == 0)
        {
            // Child process — exec the command
            // argv must be null-terminated for execvp
            var argv = new string?[args.Length + 1];
            Array.Copy(args, argv, args.Length);
            argv[args.Length] = null;

            _ = libc_execvp(command, argv);

            // If execvp returns, it failed
            libc_exit(127);
        }

        // Parent process
        childPid = pid;
        return masterFd;
    }

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
