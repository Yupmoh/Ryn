using System.Runtime;

namespace Ryn.Benchmarks;

internal static class AllocationTracker
{
    internal static AllocationResult AssertNoAllocation(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();

        if (GC.TryStartNoGCRegion(1024 * 1024))
        {
            try
            {
                action();
            }
            finally
            {
                GC.EndNoGCRegion();
            }
        }
        else
        {
            action();
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        return new AllocationResult(allocated);
    }

    internal static AllocationResult AssertNoAllocation<TState>(TState state, Action<TState> action)
    {
        ArgumentNullException.ThrowIfNull(action);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var before = GC.GetAllocatedBytesForCurrentThread();

        if (GC.TryStartNoGCRegion(1024 * 1024))
        {
            try
            {
                action(state);
            }
            finally
            {
                GC.EndNoGCRegion();
            }
        }
        else
        {
            action(state);
        }

        var after = GC.GetAllocatedBytesForCurrentThread();
        var allocated = after - before;

        return new AllocationResult(allocated);
    }
}

internal readonly record struct AllocationResult(long BytesAllocated)
{
    internal bool IsZeroAlloc => BytesAllocated == 0;

    internal void ThrowIfAllocated()
    {
        if (BytesAllocated > 0)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Expected zero allocations but {BytesAllocated} bytes were allocated."));
        }
    }
}
