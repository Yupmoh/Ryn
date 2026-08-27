using System.Runtime.InteropServices;
using System.Text;

namespace Ryn.Plugins.Shell;

/// <summary>Owns a null-terminated UTF-8 <c>char**</c> prepared before the native PTY forks.</summary>
internal sealed unsafe class NativeStringArray : IDisposable
{
    private readonly nint[] _strings;
    private readonly nint _array;

    internal NativeStringArray(IReadOnlyList<string> items)
    {
        _strings = new nint[items.Count];
        _array = Marshal.AllocHGlobal((items.Count + 1) * nint.Size);

        try
        {
            for (var i = 0; i < items.Count; i++)
            {
                _strings[i] = Utf8ToHGlobal(items[i]);
                Marshal.WriteIntPtr(_array, i * nint.Size, _strings[i]);
            }

            Marshal.WriteIntPtr(_array, items.Count * nint.Size, nint.Zero);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal nint Pointer => _array;

    internal static NativeStringArray CreateEnvironment(IReadOnlyDictionary<string, string> environment)
    {
        var entries = new string[environment.Count];
        var index = 0;
        foreach (var pair in environment)
            entries[index++] = $"{pair.Key}={pair.Value}";

        return new NativeStringArray(entries);
    }

    private static nint Utf8ToHGlobal(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var pointer = Marshal.AllocHGlobal(byteCount + 1);
        var destination = new Span<byte>((void*)pointer, byteCount);
        Encoding.UTF8.GetBytes(value, destination);
        ((byte*)pointer)[byteCount] = 0;
        return pointer;
    }

    public void Dispose()
    {
        foreach (var pointer in _strings)
        {
            if (pointer != nint.Zero)
                Marshal.FreeHGlobal(pointer);
        }

        Marshal.FreeHGlobal(_array);
    }
}
