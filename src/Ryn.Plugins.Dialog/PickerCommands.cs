using System.Text;
using Ryn.Core.Internal;
using Ryn.Interop;
using Ryn.Ipc;

namespace Ryn.Plugins.Dialog;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class PickerCommands
#pragma warning restore CA1812
{
    private const int BufferSize = 4096;

    private readonly NativeApplicationAccessor _accessor;

    public PickerCommands(NativeApplicationAccessor accessor)
    {
        _accessor = accessor;
    }

    [RynCommand("dialog.openFile")]
    public unsafe string OpenFile(string initialPath)
    {
        var appPtr = (saucer_application*)_accessor.ApplicationHandle;
        if (appPtr == null)
            return string.Empty;

        var desktop = Saucer.saucer_desktop_new(appPtr);
        if (desktop == null)
            return string.Empty;

        try
        {
            var options = Saucer.saucer_picker_options_new();
            try
            {
                SetInitialPath(options, initialPath);

                var buffer = stackalloc sbyte[BufferSize];
                nuint size = (nuint)BufferSize;
                int error;
                Saucer.saucer_picker_pick_file(desktop, options, buffer, &size, &error);

                return ReadResult(buffer, size, error);
            }
            finally
            {
                Saucer.saucer_picker_options_free(options);
            }
        }
        finally
        {
            Saucer.saucer_desktop_free(desktop);
        }
    }

    [RynCommand("dialog.openFolder")]
    public unsafe string OpenFolder(string initialPath)
    {
        var appPtr = (saucer_application*)_accessor.ApplicationHandle;
        if (appPtr == null)
            return string.Empty;

        var desktop = Saucer.saucer_desktop_new(appPtr);
        if (desktop == null)
            return string.Empty;

        try
        {
            var options = Saucer.saucer_picker_options_new();
            try
            {
                SetInitialPath(options, initialPath);

                var buffer = stackalloc sbyte[BufferSize];
                nuint size = (nuint)BufferSize;
                int error;
                Saucer.saucer_picker_pick_folder(desktop, options, buffer, &size, &error);

                return ReadResult(buffer, size, error);
            }
            finally
            {
                Saucer.saucer_picker_options_free(options);
            }
        }
        finally
        {
            Saucer.saucer_desktop_free(desktop);
        }
    }

    [RynCommand("dialog.openFiles")]
    public unsafe string OpenFiles(string initialPath)
    {
        var appPtr = (saucer_application*)_accessor.ApplicationHandle;
        if (appPtr == null)
            return "[]";

        var desktop = Saucer.saucer_desktop_new(appPtr);
        if (desktop == null)
            return "[]";

        try
        {
            var options = Saucer.saucer_picker_options_new();
            try
            {
                SetInitialPath(options, initialPath);

                var buffer = stackalloc sbyte[BufferSize];
                nuint size = (nuint)BufferSize;
                int error;
                Saucer.saucer_picker_pick_files(desktop, options, buffer, &size, &error);

                if (error != 0 || size == 0)
                    return "[]";

                var raw = Utf8String.ToManaged(buffer, (int)size);
                var paths = raw.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                var sb = new StringBuilder("[");
                for (var i = 0; i < paths.Length; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append('"');
                    sb.Append(paths[i]
                        .Replace("\\", "\\\\", StringComparison.Ordinal)
                        .Replace("\"", "\\\"", StringComparison.Ordinal));
                    sb.Append('"');
                }
                sb.Append(']');
                return sb.ToString();
            }
            finally
            {
                Saucer.saucer_picker_options_free(options);
            }
        }
        finally
        {
            Saucer.saucer_desktop_free(desktop);
        }
    }

    [RynCommand("dialog.save")]
    public unsafe string Save(string initialPath)
    {
        var appPtr = (saucer_application*)_accessor.ApplicationHandle;
        if (appPtr == null)
            return string.Empty;

        var desktop = Saucer.saucer_desktop_new(appPtr);
        if (desktop == null)
            return string.Empty;

        try
        {
            var options = Saucer.saucer_picker_options_new();
            try
            {
                SetInitialPath(options, initialPath);

                var buffer = stackalloc sbyte[BufferSize];
                nuint size = (nuint)BufferSize;
                int error;
                Saucer.saucer_picker_save(desktop, options, buffer, &size, &error);

                return ReadResult(buffer, size, error);
            }
            finally
            {
                Saucer.saucer_picker_options_free(options);
            }
        }
        finally
        {
            Saucer.saucer_desktop_free(desktop);
        }
    }

    private static unsafe void SetInitialPath(saucer_picker_options* options, string initialPath)
    {
        if (string.IsNullOrEmpty(initialPath))
            return;

        Span<byte> pathBuf = stackalloc byte[1024];
        var pathStr = Utf8String.Create(initialPath, pathBuf);
        Saucer.saucer_picker_options_set_initial(options, pathStr.Pointer);
        pathStr.Dispose();
    }

    private static unsafe string ReadResult(sbyte* buffer, nuint size, int error)
    {
        if (error != 0 || size == 0)
            return string.Empty;

        return Utf8String.ToManaged(buffer, (int)size);
    }
}
