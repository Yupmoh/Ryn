using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>
/// Source-generated C-runtime interop for identifying the process initial thread. macOS provides the
/// dedicated <c>pthread_main_np</c> predicate. Linux does not; instead, the initial thread is the thread
/// whose kernel thread ID equals the process ID. The thread ID comes from <c>syscall(SYS_gettid)</c> rather
/// than glibc's newer <c>gettid</c> wrapper so supported older distributions work too. All signatures are
/// blittable and NativeAOT-safe.
/// </summary>
internal static partial class NativeThread
{
    internal static bool IsInitialThread()
    {
        if (OperatingSystem.IsMacOS()) return MacMainNp() != 0;
        if (OperatingSystem.IsLinux()) return IsLinuxInitialThread(LinuxGetProcessId(), LinuxGetThreadId());
        return false;
    }

    internal static bool IsLinuxInitialThread(int processId, nint threadId) => processId == threadId;

    internal static nint GetLinuxGetThreadIdSyscallNumber(Architecture architecture) => architecture switch
    {
        Architecture.X64 => 186,
        Architecture.Arm64 => 178,
        _ => throw new PlatformNotSupportedException(
            $"Linux main-thread detection is not supported on {architecture}.")
    };

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static nint LinuxGetThreadId() =>
        LinuxSyscall(GetLinuxGetThreadIdSyscallNumber(RuntimeInformation.ProcessArchitecture));

    [LibraryImport("libSystem", EntryPoint = "pthread_main_np")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Runtime.Versioning.SupportedOSPlatform("macos")]
    private static partial int MacMainNp();

    [LibraryImport("libc", EntryPoint = "getpid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static partial int LinuxGetProcessId();

    [LibraryImport("libc", EntryPoint = "syscall")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private static partial nint LinuxSyscall(nint number);
}
