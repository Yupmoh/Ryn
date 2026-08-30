namespace Ryn.Core.Internal;

/// <summary>Applies process-wide GTK and WebKitGTK compatibility settings before native initialization.</summary>
internal static class LinuxRendering
{
    internal const string DisplayBackendVariable = "GDK_BACKEND";
    internal const string ForceSharedMemoryVariable = "WEBKIT_DMABUF_RENDERER_FORCE_SHM";

    internal static void Configure(LinuxDisplayBackend backend, LinuxRenderingMode renderingMode) =>
        Configure(
            backend,
            renderingMode,
            OperatingSystem.IsLinux(),
            Environment.GetEnvironmentVariable,
            Environment.SetEnvironmentVariable,
            LinuxProcessReexecutor.ReexecuteCurrentProcess);

    internal static void Configure(
        LinuxDisplayBackend backend,
        LinuxRenderingMode renderingMode,
        bool isLinux,
        Func<string, string?> getEnvironmentVariable,
        Action<string, string?> setEnvironmentVariable,
        Action reexecuteCurrentProcess)
    {
        if (!isLinux)
            return;

        if (backend != LinuxDisplayBackend.Auto)
            setEnvironmentVariable(DisplayBackendVariable, backend == LinuxDisplayBackend.Wayland ? "wayland" : "x11");

        if (renderingMode != LinuxRenderingMode.SharedMemory ||
            getEnvironmentVariable(ForceSharedMemoryVariable) == "1")
            return;

        setEnvironmentVariable(ForceSharedMemoryVariable, "1");
        reexecuteCurrentProcess();
        throw new InvalidOperationException("Linux process re-execution returned without replacing the process.");
    }
}
