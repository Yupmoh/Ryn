using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ryn.Plugins.FileSystem;

/// <summary>
/// Resolves the real (symlink-free) filesystem path of an already-open file handle — the inode the kernel
/// actually opened, not a name that can be re-walked. This is the missing piece that lets the filesystem
/// plugin close the validate→open TOCTOU window fully (PLG-03): after opening, the handle's real path is
/// compared against the authorized scope, so an attacker who swapped a path component for an escaping
/// symlink between validation and open is caught even though a by-name re-check could not see it.
/// </summary>
/// <remarks>
/// Each platform exposes this differently: macOS <c>fcntl(F_GETPATH)</c>, Linux <c>readlink(/proc/self/fd/N)</c>,
/// Windows <c>GetFinalPathNameByHandle</c>. Returns <c>null</c> when the path cannot be determined (an
/// unsupported platform, a closed handle, or a kernel error), which the caller treats as "fd-realpath
/// unavailable" and falls back to the narrower by-name re-check rather than failing the operation.
/// </remarks>
internal static class HandleRealPath
{
    internal static string? TryGet(SafeFileHandle? handle)
    {
        if (handle is null || handle.IsInvalid || handle.IsClosed)
            return null;

        var added = false;
        try
        {
            handle.DangerousAddRef(ref added);

            if (OperatingSystem.IsMacOS())
                return MacGetPath((int)handle.DangerousGetHandle());
            if (OperatingSystem.IsLinux())
                return LinuxProcSelfFd((int)handle.DangerousGetHandle());
            if (OperatingSystem.IsWindows())
                return WindowsFinalPath(handle);

            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Native entry point missing on this host — degrade to the by-name re-check.
            return null;
        }
        finally
        {
            if (added)
                handle.DangerousRelease();
        }
    }

    // ── macOS: proc_pidfdinfo(PROC_PIDFDVNODEPATHINFO) fills a vnode_fdinfowithpath for an open fd ──
    //
    // Deliberately NOT fcntl(fd, F_GETPATH, buf): fcntl is variadic, and on Apple arm64 a variadic argument
    // must be passed on the stack while a fixed-signature P/Invoke passes it in a register — so fcntl writes
    // the path through a garbage pointer and corrupts memory (an AccessViolation). proc_pidfdinfo is a
    // fixed-arity function, so it marshals correctly. Its result struct ends with `char vip_path[MAXPATHLEN]`,
    // so the path lives in the LAST MAXPATHLEN bytes written — we read it from there rather than hardcoding
    // the (version-specific) offset of the earlier struct fields.

    private const int PROC_PIDFDVNODEPATHINFO = 2;
    private const int MAXPATHLEN = 1024;

    private static string? MacGetPath(int fd)
    {
        var buf = new byte[8192]; // comfortably larger than sizeof(vnode_fdinfowithpath) (~1.2 KiB)
        var n = proc_pidfdinfo(Environment.ProcessId, fd, PROC_PIDFDVNODEPATHINFO, buf, buf.Length);
        if (n < MAXPATHLEN)
            return null; // error, or too short to contain the trailing path field

        var start = n - MAXPATHLEN;
        var end = Array.IndexOf(buf, (byte)0, start);
        if (end < 0)
            end = n;
        var len = end - start;
        return len <= 0 ? null : Encoding.UTF8.GetString(buf, start, len);
    }

    // System32 is the tightest safe search path for a well-known system library (a harmless no-op on Unix,
    // where the OS resolver loads the dylib by name); it satisfies CA5392 without the unsafe values CA5393
    // flags. Mirrors the per-import hardening on the ryn-pty shim (INT-05).
    [DllImport("libproc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int proc_pidfdinfo(int pid, int fd, int flavor, byte[] buffer, int buffersize);

    // ── Linux: /proc/self/fd/<fd> is a symlink to the real path of the open descriptor ──

    private static string? LinuxProcSelfFd(int fd)
    {
        // Pass the path as a NUL-terminated UTF-8 byte[] rather than a marshalled string, so there is no
        // ambiguous string marshaling for CA2101 to flag.
        var path = Encoding.UTF8.GetBytes($"/proc/self/fd/{fd}\0");
        var buf = new byte[4096];
        var n = readlink(path, buf, buf.Length);
        if (n <= 0 || n >= buf.Length)
            return null; // error, or a truncated (untrustworthy) path
        return Encoding.UTF8.GetString(buf, 0, (int)n);
    }

    [DllImport("libc", SetLastError = true, EntryPoint = "readlink")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint readlink(byte[] path, byte[] buf, nint bufsiz);

    // ── Windows: GetFinalPathNameByHandle returns the normalized real path of the open handle ──

    private const int FILE_NAME_NORMALIZED = 0x0;
    private const int VOLUME_NAME_DOS = 0x0;
    private const string ExtendedLengthPrefix = @"\\?\";

    private static string? WindowsFinalPath(SafeFileHandle handle)
    {
        var buf = new char[1024];
        var len = GetFinalPathNameByHandleW(handle, buf, buf.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        if (len > buf.Length)
        {
            buf = new char[len];
            len = GetFinalPathNameByHandleW(handle, buf, buf.Length, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
        }
        if (len <= 0)
            return null;

        var path = new string(buf, 0, len);
        // GetFinalPathNameByHandle prepends the \\?\ extended-length prefix; strip it so the result compares
        // equal to the canonical paths the validator produces.
        return path.StartsWith(ExtendedLengthPrefix, StringComparison.Ordinal)
            ? path[ExtendedLengthPrefix.Length..]
            : path;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetFinalPathNameByHandleW(SafeFileHandle hFile, char[] lpszFilePath, int cchFilePath, int dwFlags);
}
