using System.Runtime.InteropServices;

namespace Ryn.Core.Internal;

internal static class NativeCallbackHelper
{
    internal static unsafe void* Alloc(object target) =>
        (void*)GCHandle.ToIntPtr(GCHandle.Alloc(target));

    internal static T Resolve<T>(nint userdata) where T : class =>
        (T)GCHandle.FromIntPtr(userdata).Target!;

    internal static unsafe T Resolve<T>(void* userdata) where T : class =>
        Resolve<T>((nint)userdata);

    internal static void Free(nint userdata) =>
        GCHandle.FromIntPtr(userdata).Free();

    internal static unsafe void Free(void* userdata) =>
        Free((nint)userdata);
}
