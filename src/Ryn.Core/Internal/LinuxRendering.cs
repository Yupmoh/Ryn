namespace Ryn.Core.Internal;

/// <summary>Applies process-wide WebKitGTK rendering compatibility settings before native initialization.</summary>
internal static class LinuxRendering
{
    internal const string ForceSharedMemoryVariable = "WEBKIT_DMABUF_RENDERER_FORCE_SHM";

    internal static void Configure(LinuxRenderingMode mode) =>
        Configure(mode, OperatingSystem.IsLinux(), Environment.SetEnvironmentVariable);

    internal static void Configure(
        LinuxRenderingMode mode,
        bool isLinux,
        Action<string, string?> setEnvironmentVariable)
    {
        if (!isLinux || mode != LinuxRenderingMode.SharedMemory)
            return;

        setEnvironmentVariable(ForceSharedMemoryVariable, "1");
    }
}
