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
}
