using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Ryn.Plugins.Sidecar;

internal sealed partial class WindowsSidecarProcess : IDisposable
{
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint ProcThreadAttributeHandleList = 0x00020002;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint DuplicateSameAccess = 0x00000002;
    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint Infinite = 0xffffffff;
    private const int JobObjectExtendedLimitInformationClass = 9;
    private const int ErrorBadExeFormat = 193;

    private readonly object _jobGate = new();
    private nint _jobHandle;
    private int _processDisposed;

    private WindowsSidecarProcess(nint jobHandle, Process process, StreamReader standardOutput, StreamReader standardError)
    {
        _jobHandle = jobHandle;
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
    }

    public Process Process { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public static unsafe WindowsSidecarProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Job Object sidecar launch is available only on Windows.");

        nint job = 0;
        nint processHandle = 0;
        nint threadHandle = 0;
        SafeFileHandle? parentOutput = null;
        SafeFileHandle? childOutput = null;
        SafeFileHandle? parentError = null;
        SafeFileHandle? childError = null;
        SafeFileHandle? childInput = null;
        nint attributeList = 0;
        var attributeListInitialized = false;
        Process? process = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;

        try
        {
            job = CreateKillOnCloseJob();
            CreateChildOutputPipe(out parentOutput, out childOutput);
            CreateChildOutputPipe(out parentError, out childError);
            childInput = OpenInheritableNullInput();

            nuint attributeListSize = 0;
            _ = InitializeProcThreadAttributeList(0, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(checked((nint)attributeListSize));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                throw LastWin32Exception("Could not initialize the sidecar process attribute list.");
            attributeListInitialized = true;

            var inheritedHandles = stackalloc nint[3]
            {
                childInput.DangerousGetHandle(),
                childOutput.DangerousGetHandle(),
                childError.DangerousGetHandle(),
            };
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    (nint)ProcThreadAttributeHandleList,
                    (nint)inheritedHandles,
                    checked((nuint)(3 * IntPtr.Size)),
                    0,
                    0))
            {
                throw LastWin32Exception("Could not restrict inherited sidecar handles.");
            }

            var startupInfo = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = (uint)Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = childInput.DangerousGetHandle(),
                    StandardOutput = childOutput.DangerousGetHandle(),
                    StandardError = childError.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            var commandLine = BuildCommandLine(startInfo);
            var environment = BuildEnvironmentBlock(startInfo.Environment);
            var creationFlags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow;

            fixed (char* commandLinePointer = commandLine)
            fixed (char* environmentPointer = environment)
            {
                if (!CreateProcess(
                        startInfo.FileName,
                        commandLinePointer,
                        0,
                        0,
                        true,
                        creationFlags,
                        environmentPointer,
                        string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                        ref startupInfo,
                        out var processInformation))
                {
                    var error = Marshal.GetLastWin32Error();
                    var message = error == ErrorBadExeFormat
                        ? $"Sidecar executable '{startInfo.FileName}' is not a valid Windows application."
                        : $"Could not start sidecar executable '{startInfo.FileName}'.";
                    throw new Win32Exception(error, message);
                }

                processHandle = processInformation.Process;
                threadHandle = processInformation.Thread;
                if (!AssignProcessToJobObject(job, processHandle))
                    throw LastWin32Exception("Could not assign the suspended sidecar to its kill-on-close Job Object.");

                process = Process.GetProcessById(checked((int)processInformation.ProcessId));
                _ = process.SafeHandle;
                if (ResumeThread(threadHandle) == uint.MaxValue)
                    throw LastWin32Exception("Could not resume the Job Object-owned sidecar process.");
            }

            childInput.Dispose();
            childInput = null;
            childOutput.Dispose();
            childOutput = null;
            childError.Dispose();
            childError = null;
            CloseHandle(threadHandle);
            threadHandle = 0;

#pragma warning disable CA2000 // StreamReader owns each transferred SafePipeHandle.
            standardOutput = new StreamReader(
                new AnonymousPipeClientStream(
                    PipeDirection.In,
                    new SafePipeHandle(ReleaseOwnership(parentOutput), ownsHandle: true)),
                startInfo.StandardOutputEncoding ?? Console.OutputEncoding,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096);
            parentOutput = null;
            standardError = new StreamReader(
                new AnonymousPipeClientStream(
                    PipeDirection.In,
                    new SafePipeHandle(ReleaseOwnership(parentError), ownsHandle: true)),
                startInfo.StandardErrorEncoding ?? Console.OutputEncoding,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 4096);
#pragma warning restore CA2000
            parentError = null;

            var result = new WindowsSidecarProcess(job, process, standardOutput, standardError);
            job = 0;
            process = null;
            standardOutput = null;
            standardError = null;
            CloseHandle(processHandle);
            processHandle = 0;
            return result;
        }
#pragma warning disable CA1508 // Native CreateProcess mutates this handle through an out struct.
        catch
        {
            if (processHandle != 0)
            {
                _ = TerminateProcess(processHandle, 1);
                _ = WaitForSingleObject(processHandle, Infinite);
            }
            throw;
        }
#pragma warning restore CA1508
        finally
        {
            if (attributeList != 0)
            {
                if (attributeListInitialized)
                    DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            if (threadHandle != 0)
                CloseHandle(threadHandle);
            if (processHandle != 0)
                CloseHandle(processHandle);
            childInput?.Dispose();
            childOutput?.Dispose();
            childError?.Dispose();
            parentOutput?.Dispose();
            parentError?.Dispose();
            standardOutput?.Dispose();
            standardError?.Dispose();
            process?.Dispose();
            if (job != 0)
                CloseHandle(job);
        }
    }

    public void Terminate()
    {
        lock (_jobGate)
        {
            if (_jobHandle != 0 && !TerminateJobObject(_jobHandle, 1))
                throw LastWin32Exception("Could not terminate the owned sidecar Job Object.");
        }
    }

    public void DisposeProcess()
    {
        if (Interlocked.Exchange(ref _processDisposed, 1) != 0)
            return;

        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
    }

    public void Dispose()
    {
        try
        {
            DisposeProcess();
        }
        finally
        {
            ReleaseCrashOwnership();
        }
    }

    public void ReleaseCrashOwnership()
    {
        lock (_jobGate)
        {
            var job = _jobHandle;
            _jobHandle = 0;
            if (job != 0)
                CloseHandle(job);
        }
    }
    internal void SimulateHostCrashForTest() => ReleaseCrashOwnership();

    private static nint CreateKillOnCloseJob()
    {
        var job = CreateJobObject(0, null);
        if (job == 0)
            throw LastWin32Exception("Could not create a sidecar Job Object.");

        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                    job,
                    JobObjectExtendedLimitInformationClass,
                    ref information,
                    (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw LastWin32Exception("Could not configure sidecar Job Object crash cleanup.");
            }
            return job;
        }
        catch
        {
            CloseHandle(job);
            throw;
        }
    }

    private static void CreateChildOutputPipe(out SafeFileHandle parentRead, out SafeFileHandle childWrite)
    {
        var security = new SecurityAttributes
        {
            Length = (uint)Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        if (!CreatePipe(out var read, out var write, ref security, 0))
            throw LastWin32Exception("Could not create a sidecar diagnostic pipe.");

        parentRead = new SafeFileHandle(read, ownsHandle: true);
        childWrite = new SafeFileHandle(write, ownsHandle: true);
        if (!SetHandleInformation(parentRead.DangerousGetHandle(), HandleFlagInherit, 0))
        {
            parentRead.Dispose();
            childWrite.Dispose();
            throw LastWin32Exception("Could not make the parent sidecar pipe handle private.");
        }
    }

    private static SafeFileHandle OpenInheritableNullInput()
    {
        using var input = new SafeFileHandle(
            CreateFile("NUL", GenericRead, FileShareRead, 0, OpenExisting, FileAttributeNormal, 0),
            ownsHandle: true);
        if (input.IsInvalid)
            throw LastWin32Exception("Could not open a null input handle for the sidecar.");
        var currentProcess = GetCurrentProcess();
        if (!DuplicateHandle(
                currentProcess,
                input.DangerousGetHandle(),
                currentProcess,
                out var inheritable,
                0,
                true,
                DuplicateSameAccess))
        {
            throw LastWin32Exception("Could not create the sidecar input handle.");
        }
        return new SafeFileHandle(inheritable, ownsHandle: true);
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var builder = new StringBuilder();
        AppendQuotedExecutable(builder, startInfo.FileName);
        foreach (var argument in startInfo.ArgumentList)
            AppendQuotedArgument(builder, argument);
        return builder.ToString();
    }

    private static void AppendQuotedExecutable(StringBuilder builder, string executable)
    {
        builder.Append('"');
        builder.Append(executable.Replace("\"", "\\\"", StringComparison.Ordinal));
        builder.Append('"');
    }

    private static void AppendQuotedArgument(StringBuilder builder, string argument)
    {
        builder.Append(' ');
        if (argument.Length != 0 && !argument.Any(static character => char.IsWhiteSpace(character) || character == '"'))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var index = 0;
        while (index < argument.Length)
        {
            if (argument[index] != '\\')
            {
                if (argument[index] == '"')
                    builder.Append('\\');
                builder.Append(argument[index++]);
                continue;
            }

            var slashStart = index;
            while (index < argument.Length && argument[index] == '\\')
                index++;
            var slashCount = index - slashStart;
            if (index == argument.Length)
                builder.Append('\\', slashCount * 2);
            else if (argument[index] == '"')
            {
                builder.Append('\\', slashCount * 2 + 1);
                builder.Append('"');
                index++;
            }
            else
                builder.Append('\\', slashCount);
        }
        builder.Append('"');
    }

    private static char[] BuildEnvironmentBlock(IDictionary<string, string?> environment)
    {
        var entries = environment
            .Where(static pair => pair.Value is not null)
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var length = entries.Length == 0 ? 2 : 1;
        foreach (var pair in entries)
            length = checked(length + pair.Key.Length + pair.Value!.Length + 2);

        var block = new char[length];
        var offset = 0;
        foreach (var pair in entries)
        {
            pair.Key.AsSpan().CopyTo(block.AsSpan(offset));
            offset += pair.Key.Length;
            block[offset++] = '=';
            pair.Value.AsSpan().CopyTo(block.AsSpan(offset));
            offset += pair.Value!.Length + 1;
        }
        return block;
    }

    private static Win32Exception LastWin32Exception(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Cb;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Count;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateJobObject(nint jobAttributes, string? name);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        nint job,
        int informationClass,
        ref JobObjectExtendedLimitInformation information,
        uint informationLength);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateJobObject(nint job, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out nint readPipe, out nint writePipe, ref SecurityAttributes pipeAttributes, uint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetHandleInformation(nint handle, uint mask, uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        nint sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(
        nint attributeList,
        uint attributeCount,
        uint flags,
        ref nuint size);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(
        nint attributeList,
        uint flags,
        nint attribute,
        nint value,
        nuint size,
        nint previousValue,
        nint returnSize);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint attributeList);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool CreateProcess(
        string? applicationName,
        char* commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        char* environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    private static nint ReleaseOwnership(SafeFileHandle handle)
    {
        var rawHandle = handle.DangerousGetHandle();
        handle.SetHandleAsInvalid();
        handle.Dispose();
        return rawHandle;
    }
}
