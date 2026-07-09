using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Ryn.Plugins.WebViewPane.Native;

/// <summary>
/// Minimal Objective-C block support: builds a heap block literal whose invoke function is an
/// <c>[UnmanagedCallersOnly]</c> static and whose single captured value is an opaque context pointer
/// (typically a GCHandle). The block carries no copy/dispose helpers — the captured pointer is plain
/// data, so the runtime's <c>Block_copy</c> byte-copy is correct. Callers free the literal after the
/// consuming API returns (the API copies the block if it outlives the call).
/// </summary>
[SupportedOSPlatform("macos")]
internal static unsafe class MacBlocks
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct BlockLiteral
    {
        public nint Isa;
        public int Flags;
        public int Reserved;
        public nint Invoke;
        public nint Descriptor;
        public nint Context; // captured payload — first (and only) captured "variable"
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BlockDescriptor
    {
        public nuint Reserved;
        public nuint Size;
    }

    private static readonly nint StackBlockIsa =
        NativeLibrary.GetExport(NativeLibrary.Load("/usr/lib/libSystem.B.dylib"), "_NSConcreteStackBlock");

    private static readonly nint SharedDescriptor = CreateDescriptor();

    private static nint CreateDescriptor()
    {
        var descriptor = (BlockDescriptor*)Marshal.AllocHGlobal(sizeof(BlockDescriptor));
        descriptor->Reserved = 0;
        descriptor->Size = (nuint)sizeof(BlockLiteral);
        return (nint)descriptor;
    }

    /// <summary>Builds a block. Free with <see cref="Free"/> once the consuming call has returned.</summary>
    public static nint Create(nint invoke, nint context)
    {
        var block = (BlockLiteral*)Marshal.AllocHGlobal(sizeof(BlockLiteral));
        block->Isa = StackBlockIsa;
        block->Flags = 0;
        block->Reserved = 0;
        block->Invoke = invoke;
        block->Descriptor = SharedDescriptor;
        block->Context = context;
        return (nint)block;
    }

    public static void Free(nint block) => Marshal.FreeHGlobal(block);

    /// <summary>Reads the captured context pointer from inside an invoke callback.</summary>
    public static nint GetContext(nint block) => ((BlockLiteral*)block)->Context;

    // Block_copy/Block_release live in libSystem but are exported as _Block_copy/_Block_release; the exact
    // dlsym name varies by OS version, so resolve at runtime trying both spellings.
    private static readonly delegate* unmanaged<nint, nint> s_blockCopy = ResolveBlockFn<nint>("Block_copy");
    private static readonly delegate* unmanaged<nint, void> s_blockRelease = ResolveBlockFn2("Block_release");

    private static delegate* unmanaged<nint, nint> ResolveBlockFn<T>(string name)
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        if (NativeLibrary.TryGetExport(lib, name, out var p) || NativeLibrary.TryGetExport(lib, "_" + name, out p))
            return (delegate* unmanaged<nint, nint>)p;
        return null;
    }

    private static delegate* unmanaged<nint, void> ResolveBlockFn2(string name)
    {
        var lib = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        if (NativeLibrary.TryGetExport(lib, name, out var p) || NativeLibrary.TryGetExport(lib, "_" + name, out p))
            return (delegate* unmanaged<nint, void>)p;
        return null;
    }

    /// <summary>Copies an ObjC block to the heap so it outlives the callback that delivered it.</summary>
    public static nint Copy(nint block) => s_blockCopy is not null ? s_blockCopy(block) : block;

    /// <summary>Releases a heap block obtained from <see cref="Copy"/>.</summary>
    public static void ReleaseCopy(nint block) { if (s_blockRelease is not null) s_blockRelease(block); }

    /// <summary>Invokes a block of signature <c>void (^)(void*)</c> (e.g. a completion handler).</summary>
    public static void InvokeVoidPtr(nint block, nint arg)
    {
        var invoke = ((BlockLiteral*)block)->Invoke;
        ((delegate* unmanaged[Cdecl]<nint, nint, void>)invoke)(block, arg);
    }
}
