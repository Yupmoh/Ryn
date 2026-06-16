using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Ryn.Core.Internal;

/// <summary>
/// Marshals a managed <see cref="string"/> to a NUL-terminated UTF-8 buffer whose pointer
/// is valid for the lifetime of the (stack-bound) <see cref="Utf8String"/> instance.
/// </summary>
/// <remarks>
/// This is a <c>ref struct</c> so an instance can never escape the stack frame that created it.
/// Short strings are written into the caller-supplied <paramref name="stackBuffer"/>; longer
/// strings fall back to a pinned pooled array. Always call <see cref="Dispose"/> when done so the
/// pooled buffer (if any) is unpinned and returned.
/// </remarks>
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

    /// <summary>
    /// Encodes <paramref name="value"/> as a NUL-terminated UTF-8 C string.
    /// </summary>
    /// <param name="value">The string to marshal. Must not contain an embedded NUL (U+0000).</param>
    /// <param name="stackBuffer">
    /// A scratch buffer that, when large enough, holds the encoded bytes. This buffer
    /// <b>must</b> be stack-allocated (e.g. <c>stackalloc byte[n]</c>) by the caller: the returned
    /// pointer aliases it, and stack memory is implicitly fixed and outlives this <c>ref struct</c>.
    /// Passing a heap-backed span (an array or pooled span) would let the GC relocate the buffer
    /// while native code holds the pointer, so it is rejected in debug builds.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="value"/> contains an embedded NUL. saucer treats the buffer as a C string, so an
    /// embedded NUL would otherwise silently truncate the value at the native boundary.
    /// </exception>
    internal static Utf8String Create(string value, Span<byte> stackBuffer)
    {
        ArgumentNullException.ThrowIfNull(value);

        // saucer reads these buffers as NUL-terminated C strings. An embedded NUL would make it
        // stop at the first one, silently truncating titles/URLs/headers. Reject rather than truncate.
        if (value.AsSpan().IndexOf('\0') >= 0)
            throw new ArgumentException("Value contains an embedded NUL (U+0000), which cannot be marshalled to a C string.", nameof(value));

        var byteCount = Encoding.UTF8.GetByteCount(value);
        var totalBytes = byteCount + 1;

        if (totalBytes <= stackBuffer.Length)
        {
            AssertStackAllocated(stackBuffer);

            Encoding.UTF8.GetBytes(value, stackBuffer);
            stackBuffer[byteCount] = 0;

            // The caller owns the stackalloc backing store; stack memory is not GC-relocatable, so
            // taking its address here is well-defined and the pointer stays valid for as long as this
            // ref struct lives (it cannot outlive the caller's frame).
            var stackPtr = (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference(stackBuffer));
            return new Utf8String(stackPtr, byteCount, null, default);
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

    /// <summary>
    /// Debug-only guard: the fast path requires <paramref name="buffer"/> to be stack-allocated, because the
    /// returned pointer aliases it without pinning. A heap-backed span could be relocated by the GC.
    /// </summary>
    /// <remarks>
    /// There is no portable, exact "is this span on the stack?" API. We approximate by comparing the
    /// buffer's address with the address of a local in this frame: stack-allocated buffers sit within a
    /// few stack frames (kilobytes) of a local, whereas heap arrays land megabytes away in a different
    /// region. The window is deliberately generous to avoid false positives; it only needs to catch the
    /// obvious "someone passed an array/pooled span" mistake during development.
    /// </remarks>
    [Conditional("DEBUG")]
    private static void AssertStackAllocated(Span<byte> buffer)
    {
        if (buffer.IsEmpty)
            return;

        nint bufferAddr = (nint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer));
        byte probe = 0;
        nint frameAddr = (nint)Unsafe.AsPointer(ref probe);
        nint distance = bufferAddr > frameAddr ? bufferAddr - frameAddr : frameAddr - bufferAddr;

        const nint StackProximityWindow = 1 << 20; // 1 MiB — well under a default stack, well under heap distance
        Debug.Assert(
            distance < StackProximityWindow,
            "Utf8String.Create requires a stack-allocated buffer; the supplied span appears to be heap-backed and could be moved by the GC while native code holds the pointer.");
    }

    private static int strlen(sbyte* ptr)
    {
        var p = ptr;
        while (*p != 0) p++;
        return (int)(p - ptr);
    }
}
