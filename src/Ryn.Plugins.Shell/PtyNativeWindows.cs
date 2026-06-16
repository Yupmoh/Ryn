using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ryn.Plugins.Shell;

/// <summary>
/// Windows ConPTY (Console Pseudo Terminal) operations via P/Invoke to kernel32.
/// Provides PTY semantics on Windows 10 1809+ using the ConPTY API.
/// </summary>
internal static class PtyNativeWindows
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct COORD
    {
        public short X;
        public short Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const int EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int CREATE_UNICODE_ENVIRONMENT = 0x00000400;


    private const int S_OK = 0;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(
        COORD size,
        IntPtr hInput,
        IntPtr hOutput,
        uint dwFlags,
        out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(
        IntPtr hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToRead,
        out int lpNumberOfBytesRead,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        IntPtr hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr lpAttributeList,
        int dwAttributeCount,
        int dwFlags,
        ref nint lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr lpAttributeList,
        uint dwFlags,
        IntPtr attribute,
        IntPtr lpValue,
        nint cbSize,
        IntPtr lpPreviousValue,
        IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        int dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int WaitForSingleObject(IntPtr hHandle, int dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr hProcess, out uint lpExitCode);

    private const int WAIT_OBJECT_0 = 0;
    private const int INFINITE = -1;

    private const string ConPtyUnavailableMessage =
        "shell.pty requires ConPTY, which is available on Windows 10 1809 and later. " +
        "PTY support is unavailable on this Windows build.";

    /// <summary>
    /// Holds all Windows handles for a ConPTY session.
    /// </summary>
    internal sealed class ConPtySession : IDisposable
    {
        public IntPtr PseudoConsole { get; }
        public IntPtr ProcessHandle { get; }
        public IntPtr ThreadHandle { get; }
        public int ProcessId { get; }

        /// <summary>Read end of the output pipe — read from this to get PTY output.</summary>
        public IntPtr OutputReadHandle { get; }

        /// <summary>Write end of the input pipe — write to this to send input to PTY.</summary>
        public IntPtr InputWriteHandle { get; }

        private bool _disposed;

        internal ConPtySession(
            IntPtr pseudoConsole,
            IntPtr processHandle,
            IntPtr threadHandle,
            int processId,
            IntPtr outputReadHandle,
            IntPtr inputWriteHandle)
        {
            PseudoConsole = pseudoConsole;
            ProcessHandle = processHandle;
            ThreadHandle = threadHandle;
            ProcessId = processId;
            OutputReadHandle = outputReadHandle;
            InputWriteHandle = inputWriteHandle;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Order matters: close ConPTY first so the output pipe gets EOF,
            // then close all handles.
            ClosePseudoConsole(PseudoConsole);
            CloseHandle(InputWriteHandle);
            CloseHandle(OutputReadHandle);
            CloseHandle(ThreadHandle);
            CloseHandle(ProcessHandle);
        }
    }

    /// <summary>
    /// Creates a ConPTY session: pseudo console + child process with the PTY attached.
    /// </summary>
    internal static ConPtySession CreateConPty(string commandLine, ushort cols, ushort rows)
    {
        // Create the two pipe pairs:
        // Pipe 1 (input):  we write to inputWriteEnd → ConPTY reads from inputReadEnd
        // Pipe 2 (output): ConPTY writes to outputWriteEnd → we read from outputReadEnd
        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };

        if (!CreatePipe(out var inputReadEnd, out var inputWriteEnd, ref sa, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create input pipe");

        if (!CreatePipe(out var outputReadEnd, out var outputWriteEnd, ref sa, 0))
        {
            CloseHandle(inputReadEnd);
            CloseHandle(inputWriteEnd);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to create output pipe");
        }

        // Create the pseudo console
        var size = new COORD { X = (short)cols, Y = (short)rows };
        int hr;
        IntPtr hPC;
        try
        {
            hr = CreatePseudoConsole(size, inputReadEnd, outputWriteEnd, 0, out hPC);
        }
        catch (EntryPointNotFoundException ex)
        {
            // CreatePseudoConsole was added in Windows 10 1809. On older builds the kernel32 entry point is
            // absent and the P/Invoke throws — surface a clear, actionable error that mirrors the Unix
            // missing-shim path (PtyNative.NativeShimMissingMessage) instead of leaking the raw exception.
            CloseHandle(inputReadEnd);
            CloseHandle(inputWriteEnd);
            CloseHandle(outputReadEnd);
            CloseHandle(outputWriteEnd);
            throw new PlatformNotSupportedException(ConPtyUnavailableMessage, ex);
        }

        if (hr != S_OK)
        {
            CloseHandle(inputReadEnd);
            CloseHandle(inputWriteEnd);
            CloseHandle(outputReadEnd);
            CloseHandle(outputWriteEnd);
            throw new InvalidOperationException(
                $"CreatePseudoConsole failed with HRESULT 0x{hr:X8}");
        }

        // The ConPTY now owns these pipe ends — close our copies
        CloseHandle(inputReadEnd);
        CloseHandle(outputWriteEnd);

        // Start the child process with the ConPTY attached
        IntPtr processHandle;
        IntPtr threadHandle;
        int processId;

        try
        {
            StartProcessWithPty(hPC, commandLine, out processHandle, out threadHandle, out processId);
        }
        catch
        {
            ClosePseudoConsole(hPC);
            CloseHandle(outputReadEnd);
            CloseHandle(inputWriteEnd);
            throw;
        }

        return new ConPtySession(hPC, processHandle, threadHandle, processId, outputReadEnd, inputWriteEnd);
    }

    private static void StartProcessWithPty(
        IntPtr hPC,
        string commandLine,
        out IntPtr processHandle,
        out IntPtr threadHandle,
        out int processId)
    {
        // Initialize the thread attribute list with one attribute (the pseudo console)
        nint attrListSize = 0;
        _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attrListSize);

        var attrList = Marshal.AllocHGlobal(attrListSize);
        try
        {
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref attrListSize))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "InitializeProcThreadAttributeList failed");

            // Set the pseudo console as the thread attribute
            if (!UpdateProcThreadAttribute(
                    attrList,
                    0,
                    (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    hPC,
                    IntPtr.Size, // size of a handle
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "UpdateProcThreadAttribute failed");
            }

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false, // don't inherit handles — ConPTY manages I/O
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    IntPtr.Zero,
                    null,
                    ref si,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    $"CreateProcessW failed for command: {commandLine}");
            }

            processHandle = pi.hProcess;
            threadHandle = pi.hThread;
            processId = pi.dwProcessId;
        }
        finally
        {
            DeleteProcThreadAttributeList(attrList);
            Marshal.FreeHGlobal(attrList);
        }
    }

    /// <summary>
    /// Reads from the ConPTY output pipe. Returns bytes read, or 0 on EOF/error.
    /// </summary>
    internal static int Read(IntPtr handle, byte[] buffer, int count)
    {
        if (!ReadFile(handle, buffer, count, out var bytesRead, IntPtr.Zero))
            return 0; // Pipe broken = child exited

        return bytesRead;
    }

    /// <summary>
    /// Writes to the ConPTY input pipe. Returns bytes written, or 0 on error.
    /// </summary>
    internal static int Write(IntPtr handle, byte[] buffer, int offset, int count)
    {
        byte[] writeBuffer;
        if (offset == 0 && count == buffer.Length)
        {
            writeBuffer = buffer;
        }
        else
        {
            writeBuffer = new byte[count];
            Array.Copy(buffer, offset, writeBuffer, 0, count);
        }

        if (!WriteFile(handle, writeBuffer, count, out var bytesWritten, IntPtr.Zero))
            return 0;

        return bytesWritten;
    }

    /// <summary>
    /// Resizes the pseudo console.
    /// </summary>
    internal static bool Resize(IntPtr hPC, ushort cols, ushort rows)
    {
        var size = new COORD { X = (short)cols, Y = (short)rows };
        return ResizePseudoConsole(hPC, size) == S_OK;
    }

    /// <summary>
    /// Terminates the child process.
    /// </summary>
    internal static void Kill(IntPtr processHandle)
    {
        _ = TerminateProcess(processHandle, 1);
    }

    /// <summary>
    /// Waits for the process to exit and returns the exit code.
    /// Returns -1 if the wait or exit code retrieval fails.
    /// </summary>
    internal static int WaitForExit(IntPtr processHandle)
    {
        var result = WaitForSingleObject(processHandle, INFINITE);
        if (result != WAIT_OBJECT_0)
            return -1;

        if (!GetExitCodeProcess(processHandle, out var exitCode))
            return -1;

        return (int)exitCode;
    }
}
