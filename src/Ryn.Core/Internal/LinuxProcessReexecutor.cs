using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

/// <summary>Replaces the current Linux process while preserving its native argument vector.</summary>
internal static partial class LinuxProcessReexecutor
{
    private const string SelfExecutablePath = "/proc/self/exe";
    private const string CommandLinePath = "/proc/self/cmdline";

    internal static unsafe void ReexecuteCurrentProcess()
    {
        var arguments = ReadNullTerminatedArguments(File.ReadAllBytes(CommandLinePath));
        if (arguments.Length == 0)
            throw new InvalidOperationException("Cannot restart the process because /proc/self/cmdline is empty.");

        // A managed array avoids an argument-count-dependent stack allocation and is zero-initialized, so
        // cleanup remains safe if UTF-8 allocation fails partway through.
        var nativeArguments = new nint[arguments.Length + 1];
        try
        {
            for (var i = 0; i < arguments.Length; i++)
                nativeArguments[i] = Marshal.StringToCoTaskMemUTF8(arguments[i]);

            fixed (nint* pointer = nativeArguments)
            {
                var result = ExecV(SelfExecutablePath, (nint)pointer);
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"execv failed with result {result}.");
            }
        }
        finally
        {
            for (var i = 0; i < arguments.Length; i++)
                Marshal.FreeCoTaskMem(nativeArguments[i]);
        }
    }

    internal static string[] ReadNullTerminatedArguments(ReadOnlySpan<byte> commandLine)
    {
        var arguments = new List<string>();
        var start = 0;
        for (var i = 0; i < commandLine.Length; i++)
        {
            if (commandLine[i] != 0)
                continue;

            arguments.Add(System.Text.Encoding.UTF8.GetString(commandLine[start..i]));
            start = i + 1;
        }

        if (start < commandLine.Length)
            arguments.Add(System.Text.Encoding.UTF8.GetString(commandLine[start..]));

        return [.. arguments];
    }

    [LibraryImport("libc", EntryPoint = "execv", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int ExecV(string path, nint arguments);
}
