using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Ryn.Interop;

namespace Ryn.Plugins.Sidecar;

internal sealed partial class UnixSidecarProcess : IDisposable
{
    private const string NativeLibraryName = "ryn-sidecar";
    private readonly object _controlGate = new();
    private readonly Process _watchdog;
    private FileStream? _control;
    private int _processDisposed;
    private readonly Task<int> _exit;

    private UnixSidecarProcess(
        Process watchdog,
        Process process,
        StreamReader standardOutput,
        StreamReader standardError,
        FileStream control)
    {
        _watchdog = watchdog;
        Process = process;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _control = control;
        // Process did not spawn this child; only waitpid can reap its native-forked watchdog.
        var watchdogPid = watchdog.Id;
        _exit = Task.Run(() => ryn_sidecar_wait(watchdogPid));
    }

    public Process Process { get; }

    public StreamReader StandardOutput { get; }

    public StreamReader StandardError { get; }

    public bool HasExited => _exit.IsCompletedSuccessfully;

    public int ExitCode => _exit.GetAwaiter().GetResult();

    public static UnixSidecarProcess Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Unix sidecar crash ownership is unavailable on Windows.");
        if (startInfo.WorkingDirectory.Contains('\0', StringComparison.Ordinal))
            throw new ArgumentException("Working directory cannot contain null characters.", nameof(startInfo));

        NativeLibraryResolver.RegisterForAssembly(typeof(UnixSidecarProcess).Assembly);

        var arguments = new string[startInfo.ArgumentList.Count + 1];
        arguments[0] = startInfo.FileName;
        for (var index = 0; index < startInfo.ArgumentList.Count; index++)
            arguments[index + 1] = startInfo.ArgumentList[index];

        using var argv = new NativeStringArray(arguments);
        using var envp = NativeStringArray.CreateEnvironment(startInfo.Environment);
        var result = ryn_sidecar_spawn(
            startInfo.FileName,
            argv.Pointer,
            envp.Pointer,
            string.IsNullOrEmpty(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
            out var watchdogPid,
            out var childPid,
            out var outputFd,
            out var errorFd,
            out var controlFd);
        if (result != 0)
            throw new Win32Exception(-result, $"Could not start sidecar executable '{startInfo.FileName}'.");

        Process? watchdog = null;
        Process? process = null;
        StreamReader? standardOutput = null;
        StreamReader? standardError = null;
        FileStream? control = null;
        try
        {
            watchdog = Process.GetProcessById(watchdogPid);
            process = Process.GetProcessById(childPid);
            _ = watchdog.SafeHandle;
            _ = process.SafeHandle;

            standardOutput = CreateReader(outputFd, startInfo.StandardOutputEncoding ?? Console.OutputEncoding);
            outputFd = -1;
            standardError = CreateReader(errorFd, startInfo.StandardErrorEncoding ?? Console.OutputEncoding);
            errorFd = -1;
            control = CreateStream(controlFd, FileAccess.Write);
            controlFd = -1;

            control.WriteByte(1);
            control.Flush();

            var lifetime = new UnixSidecarProcess(watchdog, process, standardOutput, standardError, control);
            watchdog = null;
            process = null;
            standardOutput = null;
            standardError = null;
            control = null;
            return lifetime;
        }
#pragma warning disable CA1508 // Exceptions can occur after control stream ownership transfers.
        catch
        {
            control?.Dispose();
            control = null;
            if (controlFd >= 0)
            {
                CloseDescriptor(controlFd);
                controlFd = -1;
            }
            _ = ryn_sidecar_wait(watchdogPid);
            throw;
        }
#pragma warning restore CA1508
        finally
        {
            if (outputFd >= 0)
                CloseDescriptor(outputFd);
            if (errorFd >= 0)
                CloseDescriptor(errorFd);
            standardOutput?.Dispose();
            standardError?.Dispose();
            control?.Dispose();
            process?.Dispose();
            watchdog?.Dispose();
        }
    }

    public async Task WaitForExitAsync(CancellationToken cancellationToken = default)
    {
        _ = await _exit.WaitAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Terminate()
    {
        lock (_controlGate)
        {
            _control?.Dispose();
            _control = null;
        }
    }

    public void DisposeProcess()
    {
        if (Interlocked.Exchange(ref _processDisposed, 1) != 0)
            return;

        StandardOutput.Dispose();
        StandardError.Dispose();
        Process.Dispose();
        lock (_controlGate)
        {
            _control?.Dispose();
            _control = null;
        }
        _watchdog.Dispose();
    }
    public void Dispose()
    {
        try
        {
            Terminate();
        }
        finally
        {
            DisposeProcess();
        }
    }

    internal void SimulateHostCrashForTest() => Terminate();

#pragma warning disable CA2000 // FileStream takes ownership of the SafeFileHandle on successful construction.
    private static StreamReader CreateReader(int descriptor, Encoding encoding) =>
        new(
            new FileStream(
                new SafeFileHandle((nint)descriptor, ownsHandle: true),
                FileAccess.Read,
                bufferSize: 4096,
                isAsync: false),
            encoding,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096);

    private static FileStream CreateStream(int descriptor, FileAccess access) =>
        new(
            new SafeFileHandle((nint)descriptor, ownsHandle: true),
            access,
            bufferSize: 4096,
            isAsync: false);
#pragma warning restore CA2000
    private static void CloseDescriptor(int descriptor)
    {
        using var handle = new SafeFileHandle((nint)descriptor, ownsHandle: true);
    }

    // The resolver restricts this first-party native helper to Ryn's application/runtimes directory.
#pragma warning disable CA5393
    [LibraryImport(NativeLibraryName, EntryPoint = "ryn_sidecar_spawn", StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static partial int ryn_sidecar_spawn(
        string command,
        nint argv,
        nint envp,
        string? workingDirectory,
        out int watchdogPid,
        out int childPid,
        out int standardOutputFd,
        out int standardErrorFd,
        out int controlFd);

    [LibraryImport(NativeLibraryName, EntryPoint = "ryn_sidecar_wait")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.System32)]
    private static partial int ryn_sidecar_wait(int watchdogPid);
#pragma warning restore CA5393

    private sealed unsafe class NativeStringArray : IDisposable
    {
        private readonly nint[] _strings;
        private readonly nint _array;

        public NativeStringArray(IReadOnlyList<string> items)
        {
            _strings = new nint[items.Count];
            _array = Marshal.AllocHGlobal((items.Count + 1) * nint.Size);
            try
            {
                for (var index = 0; index < items.Count; index++)
                {
                    if (items[index] is null || items[index].Contains('\0', StringComparison.Ordinal))
                        throw new ArgumentException("Native arguments and environment entries cannot contain null characters.", nameof(items));
                    _strings[index] = Marshal.StringToCoTaskMemUTF8(items[index]);
                    Marshal.WriteIntPtr(_array, index * nint.Size, _strings[index]);
                }
                Marshal.WriteIntPtr(_array, items.Count * nint.Size, nint.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public nint Pointer => _array;

        public static NativeStringArray CreateEnvironment(IDictionary<string, string?> environment)
        {
            var count = 0;
            foreach (var pair in environment)
                if (pair.Value is not null) count++;
            var entries = new string[count];
            var index = 0;
            foreach (var pair in environment)
                if (pair.Value is not null) entries[index++] = $"{pair.Key}={pair.Value}";
            return new NativeStringArray(entries);
        }

        public void Dispose()
        {
            foreach (var pointer in _strings)
            {
                if (pointer != nint.Zero)
                    Marshal.FreeCoTaskMem(pointer);
            }
            Marshal.FreeHGlobal(_array);
        }
    }
}
