using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryn.Core.Internal;

internal unsafe ref struct Utf8String
{
    private readonly byte[]? _pooledBuffer;
    private readonly GCHandle _pinHandle;
    private readonly byte* _ptr;
    private readonly int _byteCount;

    private Utf8String(byte* ptr, int byteCount, byte[]? pooledBuffer, GCHandle pinHandle)
    {
        _ptr = ptr;
        _byteCount = byteCount;
        _pooledBuffer = pooledBuffer;
        _pinHandle = pinHandle;
    }

    internal sbyte* Pointer => (sbyte*)_ptr;

    internal int ByteCount => _byteCount;

    internal static Utf8String Create(string value, Span<byte> stackBuffer)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        var totalBytes = byteCount + 1;

        if (totalBytes <= stackBuffer.Length)
        {
            Encoding.UTF8.GetBytes(value, stackBuffer);
            stackBuffer[byteCount] = 0;
            fixed (byte* ptr = stackBuffer)
            {
                return new Utf8String(ptr, byteCount, null, default);
            }
        }

        var pooled = ArrayPool<byte>.Shared.Rent(totalBytes);
        Encoding.UTF8.GetBytes(value, pooled);
        pooled[byteCount] = 0;
        var pin = GCHandle.Alloc(pooled, GCHandleType.Pinned);
        var pinnedPtr = (byte*)pin.AddrOfPinnedObject();
        return new Utf8String(pinnedPtr, byteCount, pooled, pin);
    }

    internal static string ToManaged(sbyte* ptr)
    {
        if (ptr == null) return string.Empty;
        return new string(ptr, 0, strlen(ptr), Encoding.UTF8);
    }

    internal static string ToManaged(sbyte* ptr, int length)
    {
        if (ptr == null || length <= 0) return string.Empty;
        return new string(ptr, 0, length, Encoding.UTF8);
    }

    internal void Dispose()
    {
        if (_pinHandle.IsAllocated)
            _pinHandle.Free();

        if (_pooledBuffer != null)
            ArrayPool<byte>.Shared.Return(_pooledBuffer);
    }

    private static int strlen(sbyte* ptr)
    {
        var p = ptr;
        while (*p != 0) p++;
        return (int)(p - ptr);
    }
}
