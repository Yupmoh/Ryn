using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Plugins.WebViewPane.Native;

/// <summary>
/// Factory for C#-implemented single-method COM objects (WebView2 completed/event handlers) under
/// NativeAOT, where built-in COM interop is unavailable. Each instance is one unmanaged allocation
/// holding the object header, a 4-slot vtable (QueryInterface/AddRef/Release/Invoke), and a GCHandle
/// to a managed callback. Reference counting is real: the final Release frees the GCHandle and the
/// allocation, so handlers survive as long as WebView2 holds them.
/// </summary>
[SupportedOSPlatform("windows")]
internal static unsafe class ComCallback
{
    private const int HrOk = 0;
    private const int HrNoInterface = unchecked((int)0x80004002);
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");

    [StructLayout(LayoutKind.Sequential)]
    private struct Instance
    {
        public nint Vtbl;      // points at Slots, below
        public int RefCount;
        public Guid Iid;       // the single handler interface this object implements
        public nint Callback;  // GCHandle to the managed invoke delegate
        // followed in memory by: nint Slots[4]
    }

    /// <summary>
    /// Creates a COM object with refcount 1. <paramref name="invoke"/> is an
    /// <c>[UnmanagedCallersOnly]</c> pointer with the handler's Invoke signature (first arg: the COM
    /// this-pointer); recover the managed callback inside it via <see cref="GetCallback{T}"/>.
    /// Release with <see cref="Release"/> after handing it to WebView2 (which AddRefs it).
    /// </summary>
    public static nint Create(Guid iid, nint invoke, object callback)
    {
        var memory = Marshal.AllocHGlobal(sizeof(Instance) + 4 * sizeof(nint));
        var instance = (Instance*)memory;
        var slots = (nint*)(memory + sizeof(Instance));

        slots[0] = (nint)(delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)&QueryInterface;
        slots[1] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&AddRef;
        slots[2] = (nint)(delegate* unmanaged[Stdcall]<nint, uint>)&ReleaseImpl;
        slots[3] = invoke;

        instance->Vtbl = (nint)slots;
        instance->RefCount = 1;
        instance->Iid = iid;
        instance->Callback = GCHandle.ToIntPtr(GCHandle.Alloc(callback));
        return memory;
    }

    /// <summary>Reads the managed callback from a COM this-pointer inside an Invoke callback.</summary>
    public static T GetCallback<T>(nint comThis) where T : class =>
        (T)GCHandle.FromIntPtr(((Instance*)comThis)->Callback).Target!;

    public static void Release(nint comThis)
    {
        var release = (delegate* unmanaged[Stdcall]<nint, uint>)(*(nint**)comThis)[2];
        _ = release(comThis);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int QueryInterface(nint comThis, Guid* riid, nint* ppv)
    {
        if (ppv == null) return HrNoInterface;
        var instance = (Instance*)comThis;
        if (*riid == IidUnknown || *riid == instance->Iid)
        {
            _ = Interlocked.Increment(ref instance->RefCount);
            *ppv = comThis;
            return HrOk;
        }
        *ppv = 0;
        return HrNoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint AddRef(nint comThis) =>
        (uint)Interlocked.Increment(ref ((Instance*)comThis)->RefCount);

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static uint ReleaseImpl(nint comThis)
    {
        var instance = (Instance*)comThis;
        var remaining = Interlocked.Decrement(ref instance->RefCount);
        if (remaining == 0)
        {
            GCHandle.FromIntPtr(instance->Callback).Free();
            Marshal.FreeHGlobal(comThis);
        }
        return (uint)remaining;
    }
}
