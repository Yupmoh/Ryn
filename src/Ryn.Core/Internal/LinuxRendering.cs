namespace Ryn.Core.Internal;

/// <summary>Applies process-wide GTK and WebKitGTK compatibility settings before native initialization.</summary>
internal static class LinuxRendering
{
    internal const string DisplayBackendVariable = "GDK_BACKEND";
    internal const string ForceSharedMemoryVariable = "WEBKIT_DMABUF_RENDERER_FORCE_SHM";

    internal static void Configure(LinuxDisplayBackend backend, LinuxRenderingMode renderingMode) =>
        Configure(backend, renderingMode, OperatingSystem.IsLinux(), Environment.SetEnvironmentVariable);

    internal static void Configure(
        LinuxDisplayBackend backend,
        LinuxRenderingMode renderingMode,
        bool isLinux,
        Action<string, string?> setEnvironmentVariable)
    {
        if (!isLinux)
            return;

        if (backend != LinuxDisplayBackend.Auto)
            setEnvironmentVariable(DisplayBackendVariable, backend == LinuxDisplayBackend.Wayland ? "wayland" : "x11");

        if (renderingMode == LinuxRenderingMode.SharedMemory)
            setEnvironmentVariable(ForceSharedMemoryVariable, "1");
    }
}
