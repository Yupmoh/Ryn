using Ryn.Interop;

namespace Ryn.Core;

/// <summary>
/// Specifies whether page script may only read a shared buffer or may also modify it.
/// </summary>
public enum RynSharedBufferAccess
{
    /// <summary>
    /// Page script can only read the buffer. Writing to a read-only buffer from script causes an access
    /// violation in the WebView2 renderer process and crashes it.
    /// </summary>
    ReadOnly = 0,

    /// <summary>Page script can read and write the buffer.</summary>
    ReadWrite = 1,
}

/// <summary>
/// A shared-memory buffer created by <see cref="IRynWebView.CreateSharedBufferAsync"/>. On Windows this is
/// backed by the WebView2 SharedBuffer API (an OS file mapping), so the host and the page access the same
/// memory without serialization or network-stack copies. The page receives it as an <c>ArrayBuffer</c> via
/// <c>chrome.webview</c>'s <c>sharedbufferreceived</c> event after
/// <see cref="IRynWebView.PostSharedBufferToScriptAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// The buffer size is fixed at creation; growing requires creating a new buffer and posting it again.
/// Callers must not write into <see cref="Buffer"/> after the buffer has been posted until the page has
/// consumed the frame (use double-buffering or an explicit handshake), and must never write after
/// <see cref="Dispose"/>.
/// </para>
/// <para>
/// Disposing the buffer on the host side does not affect page-side access; page script releases its own
/// view with <c>chrome.webview.releaseBuffer</c>. The underlying memory is freed by the OS once both sides
/// have released it.
/// </para>
/// </remarks>
public sealed class RynSharedBuffer : IDisposable
{
    private nint _native;
    private int _disposed;

    internal RynSharedBuffer(nint native, ulong size, nint buffer)
    {
        _native = native;
        Size = size;
        Buffer = buffer;
    }

    /// <summary>Gets the buffer size in bytes (fixed at creation).</summary>
    public ulong Size { get; }

    /// <summary>
    /// Gets the native memory address of the buffer, for direct writes (e.g. a typed-array view over the
    /// same layout the page will interpret). Only valid until <see cref="Dispose"/>.
    /// </summary>
    public nint Buffer { get; }

    internal nint Native => _native;

    internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>
    /// Releases the backing shared memory and the COM reference. The shared-buffer object is a simple
    /// file-mapping wrapper (not tied to the WebView2 UI thread), so this is safe from any thread — matching
    /// how the WebView2 managed SDK disposes the same object.
    /// </summary>
    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var native = Interlocked.Exchange(ref _native, 0);
        if (native == 0) return;

        Saucer.saucer_webview_shared_buffer_close((void*)native);
    }
}
