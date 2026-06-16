using System.Diagnostics;

namespace Ryn.Core.Internal;

/// <summary>
/// Exception barrier for every reverse-P/Invoke (<c>[UnmanagedCallersOnly]</c>) callback body in Ryn.
/// A managed exception that unwinds across the native (saucer C++/AppKit) boundary is undefined behavior
/// and, under NativeAOT, a hard fail-fast that bypasses <see cref="RynApplication"/>'s
/// <see cref="RynApplication.UnhandledException"/> net. Wrapping each callback body via
/// <see cref="Invoke(string, Action)"/> / <see cref="Invoke{T}(string, T, Func{T})"/> ensures the
/// exception is caught on the managed side, routed to Ryn's unhandled-exception surface, and the callback
/// returns a safe default instead of crossing the boundary.
/// </summary>
internal static class NativeGuard
{
    /// <summary>
    /// Sink for exceptions caught at a native callback boundary. <see cref="RynApplication"/> wires this to
    /// its <c>RaiseUnhandled</c> path so the app's <see cref="RynApplication.UnhandledException"/> event and
    /// logger see the failure. Until that runs (or if it is never set — e.g. a unit test driving the native
    /// layer directly) the fallback in <see cref="Report"/> writes to <see cref="Trace"/>/stderr so the
    /// error is never fully silent.
    /// </summary>
    internal static Action<Exception>? UnhandledSink { get; set; }

    /// <summary>Runs <paramref name="body"/> inside the exception barrier. A throw is reported and swallowed
    /// — it never crosses the native boundary. OutOfMemoryException is left to propagate (it is unrecoverable
    /// and the process is already lost), matching the rest of Ryn's catch sites.</summary>
    internal static void Invoke(string context, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(context, ex);
        }
    }

    /// <summary>Runs <paramref name="body"/> inside the exception barrier, returning its result on success or
    /// <paramref name="onError"/> if it throws. The throw is reported and swallowed — it never crosses the
    /// native boundary, so the callback can hand a safe default back to saucer.</summary>
    internal static T Invoke<T>(string context, T onError, Func<T> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Report(context, ex);
            return onError;
        }
    }

    private static void Report(string context, Exception ex)
    {
        var sink = UnhandledSink;
        if (sink is not null)
        {
            try
            {
                sink(ex);
                return;
            }
            catch (Exception sinkEx) when (sinkEx is not OutOfMemoryException)
            {
                // The sink itself faulted (e.g. a user UnhandledException handler threw); fall through to the
                // last-resort trace so we don't lose either exception or let it escape the boundary.
                Trace.TraceError($"Ryn: native callback '{context}' exception sink failed: {sinkEx}");
            }
        }

        Trace.TraceError($"Ryn: unhandled exception in native callback '{context}': {ex}");
    }
}
