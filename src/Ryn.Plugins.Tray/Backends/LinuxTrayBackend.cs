using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ryn.Core.Internal;

namespace Ryn.Plugins.Tray.Backends;

[SupportedOSPlatform("linux")]
// IconClicked is intentionally never raised on Linux: the StatusNotifier/AppIndicator protocol used by
// GNOME/KDE is menu-only by design — left-click opens the context menu and there is no portable
// "icon clicked" signal. This is a documented platform limitation, not a missing feature; apps should
// rely on menu items (MenuItemClicked) on Linux. Use tray.clicked only on Windows/macOS.
#pragma warning disable CS0067
internal sealed partial class LinuxTrayBackend : ITrayBackend
{
    private nint _indicator;
    private nint _menu;
    private bool _disposed;
    private bool _available;
    private Thread? _glibThread;
    private volatile bool _glibRunning;

    private readonly List<TrayMenuItem> _menuItems = [];
    private readonly Dictionary<int, string> _menuIdMap = [];
    private readonly object _lock = new();
    private readonly ManualResetEventSlim _readyEvent = new();

    // Store delegate references to prevent GC collection of callbacks
    private readonly List<GCallback> _callbackRefs = [];

    // Pending g_idle_add delegates (rebuild/quit). Each idle source posts a fresh GSourceFunc whose marshaled
    // function pointer GTK invokes later on the GTK thread; the delegate must stay rooted until then. PAP-16:
    // a single overwritable field would let a rapid second SetMenu drop the still-pending first delegate and
    // let it be collected mid-callback. We hold every pending delegate here and each removes itself once run.
    private readonly List<GSourceFunc> _pendingIdle = [];
    private readonly object _pendingIdleLock = new();

    public event Action? IconClicked;
    public event Action<string>? MenuItemClicked;

    public void Show(string? iconPath, string tooltip)
    {
        if (_indicator != 0)
        {
            if (_available)
                app_indicator_set_status(_indicator, AppIndicatorStatus.Active);
            return;
        }

        if (!TryLoadLibraries())
        {
            _available = false;
            _readyEvent.Set();
            return;
        }

        _available = true;

        _glibThread = new Thread(() => RunGLibLoop(iconPath, tooltip))
        {
            IsBackground = true,
            Name = "RynTray",
        };
        _glibThread.Start();
        _readyEvent.Wait();
    }

    public void Hide()
    {
        if (!_available || _indicator == 0) return;
        app_indicator_set_status(_indicator, AppIndicatorStatus.Passive);
    }

    public void SetTooltip(string tooltip)
    {
        if (!_available || _indicator == 0) return;
        app_indicator_set_title(_indicator, tooltip);
    }

    public void SetMenu(IReadOnlyList<TrayMenuItem> items)
    {
        if (!_available) return;

        lock (_lock)
        {
            _menuItems.Clear();
            _menuItems.AddRange(items);
        }

        // RebuildMenu touches GTK widget APIs, which must only run on the GTK thread. SetMenu can be
        // called from any thread, so marshal the rebuild onto the GTK loop via g_idle_add.
        PostIdle(RebuildMenu);
    }

    // Queue work onto the GTK main loop and keep the marshaled delegate rooted until GTK has invoked it.
    // The delegate removes itself from _pendingIdle inside the callback and returns G_SOURCE_REMOVE (0) so the
    // idle source fires exactly once. NativeGuard fences the body so a managed throw never crosses into GLib.
    private void PostIdle(Action work)
    {
        GSourceFunc? func = null;
        func = userData => NativeGuard.Invoke("LinuxTrayBackend.idle", 0, () =>
        {
            try
            {
                work();
            }
            finally
            {
                lock (_pendingIdleLock)
                {
                    // func is captured before assignment but is non-null by the time the callback runs.
                    _ = _pendingIdle.Remove(func!);
                }
            }

            return 0; // G_SOURCE_REMOVE
        });

        lock (_pendingIdleLock)
        {
            _pendingIdle.Add(func);
        }

        _ = g_idle_add(Marshal.GetFunctionPointerForDelegate(func), nint.Zero);
    }

    public void ShowNotification(string title, string message)
    {
        var escapedTitle = EscapeShellArg(title);
        var escapedMessage = EscapeShellArg(message);

        try
        {
            var psi = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(escapedTitle);
            psi.ArgumentList.Add(escapedMessage);
            Process.Start(psi)?.WaitForExit();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void RunGLibLoop(string? iconPath, string tooltip)
    {
        var argc = 0;
        gtk_init(ref argc, nint.Zero);

        var iconName = "application-default-icon";
        if (iconPath is not null && File.Exists(iconPath))
            iconName = iconPath;

        _indicator = app_indicator_new(
            $"ryn-app-{Environment.ProcessId}",
            iconName,
            AppIndicatorCategory.ApplicationStatus);

        app_indicator_set_status(_indicator, AppIndicatorStatus.Active);
        app_indicator_set_title(_indicator, tooltip);

        // Create an initial empty menu (required by AppIndicator)
        _menu = gtk_menu_new();
        gtk_widget_show_all(_menu);
        app_indicator_set_menu(_indicator, _menu);

        _glibRunning = true;
        _readyEvent.Set();

        // Event-driven GTK main loop — blocks efficiently until events arrive (no CPU busy-poll, no
        // Thread.Sleep spin). Shutdown is requested from Dispose() via g_idle_add, which marshals
        // gtk_main_quit onto THIS (the GTK) thread — calling GTK from another thread is unsafe.
        gtk_main();
        _glibRunning = false;
    }

    private void RebuildMenu()
    {
        if (_indicator == 0) return;

        var newMenu = gtk_menu_new();

        // Build the new menu's activate callbacks into a fresh list, then swap it in for the live one only
        // after the new menu is installed. The previous menu's callbacks stay rooted (in _callbackRefs) until
        // the swap, so a pending "activate" on the old menu can't invoke a collected delegate while GTK
        // finishes tearing the old menu down.
        var newCallbacks = new List<GCallback>();

        lock (_lock)
        {
            _menuIdMap.Clear();

            for (var i = 0; i < _menuItems.Count; i++)
            {
                var item = _menuItems[i];
                var menuId = i;
                _menuIdMap[menuId] = item.Id;

                nint menuItem;
                if (item.Separator)
                {
                    menuItem = gtk_separator_menu_item_new();
                }
                else
                {
                    menuItem = gtk_menu_item_new_with_label(item.Label);

                    if (!item.Enabled)
                        gtk_widget_set_sensitive(menuItem, false);

                    var capturedId = item.Id;
                    GCallback callback = (_, _) => OnMenuItemActivated(capturedId);
                    newCallbacks.Add(callback);

                    g_signal_connect_data(
                        menuItem,
                        "activate",
                        Marshal.GetFunctionPointerForDelegate(callback),
                        nint.Zero,
                        nint.Zero,
                        0);
                }

                gtk_menu_shell_append(newMenu, menuItem);
            }
        }

        gtk_widget_show_all(newMenu);

        _menu = newMenu;
        app_indicator_set_menu(_indicator, _menu);

        // The new menu is live; the old menu's callbacks can no longer be invoked, so replace the rooted set.
        lock (_lock)
        {
            _callbackRefs.Clear();
            _callbackRefs.AddRange(newCallbacks);
        }
    }

    // GTK "activate" callback body — fenced so a managed throw never unwinds across the GLib boundary.
    private void OnMenuItemActivated(string itemId)
        => NativeGuard.Invoke("LinuxTrayBackend.OnMenuItemActivated",
            () => MenuItemClicked?.Invoke(itemId));

    private static string EscapeShellArg(string value)
    {
        return value.Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static bool TryLoadLibraries()
    {
        // Try ayatana (modern) first, then legacy appindicator
        if (NativeLibrary.TryLoad("libayatana-appindicator3.so.1", out _))
            return true;

        if (NativeLibrary.TryLoad("libappindicator3.so.1", out _))
            return true;

        return false;
    }

    ~LinuxTrayBackend() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        GC.SuppressFinalize(this);

        if (_available && _glibThread is not null && _glibRunning)
        {
            // Marshal gtk_main_quit onto the GTK thread. PostIdle keeps the marshaled delegate rooted until
            // GTK invokes it (it returns G_SOURCE_REMOVE so it runs once); the join below waits for the loop
            // to actually exit, after which the delegate has already run and removed itself.
            PostIdle(gtk_main_quit);
        }
        _glibRunning = false;

        if (_glibThread is not null)
        {
            _glibThread.Join(TimeSpan.FromSeconds(2));
            _glibThread = null;
        }

        lock (_lock)
        {
            _callbackRefs.Clear();
        }

        lock (_pendingIdleLock)
        {
            _pendingIdle.Clear();
        }

        _indicator = 0;
        _menu = 0;

        _readyEvent.Dispose();
    }

    // --- Enums ---

    private enum AppIndicatorCategory
    {
        ApplicationStatus = 0,
    }

    private enum AppIndicatorStatus
    {
        Passive = 0,
        Active = 1,
        Attention = 2,
    }

    // --- Callback delegate ---

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GCallback(nint widget, nint userData);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GSourceFunc(nint userData);

    // --- libayatana-appindicator3 / libappindicator3 P/Invoke ---
    //
    // LibraryImport uses the NativeLibrary resolver, so loading either
    // libayatana-appindicator3.so.1 or libappindicator3.so.1 first
    // satisfies the "appindicator3" name via the DllImportResolver below.

    private static readonly string[] IndicatorLibNames =
    [
        "libayatana-appindicator3.so.1",
        "libappindicator3.so.1",
    ];

    private static readonly string[] GtkLibNames =
    [
        "libgtk-3.so.0",
        "libgtk-3.so",
    ];

    private static readonly string[] GLibLibNames =
    [
        "libglib-2.0.so.0",
        "libglib-2.0.so",
    ];

    private static readonly string[] GObjectLibNames =
    [
        "libgobject-2.0.so.0",
        "libgobject-2.0.so",
    ];

    static LinuxTrayBackend()
    {
        NativeLibrary.SetDllImportResolver(typeof(LinuxTrayBackend).Assembly, ResolveLibrary);
    }

    private static nint ResolveLibrary(string libraryName, System.Reflection.Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        var candidates = libraryName switch
        {
            "appindicator3" => IndicatorLibNames,
            "gtk-3" => GtkLibNames,
            "glib-2.0" => GLibLibNames,
            "gobject-2.0" => GObjectLibNames,
            _ => null,
        };

        if (candidates is null)
            return nint.Zero;

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, assembly, searchPath, out var handle))
                return handle;
        }

        return nint.Zero;
    }

    // --- appindicator ---

    [LibraryImport("appindicator3", EntryPoint = "app_indicator_new",
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint app_indicator_new(string id, string iconName,
        AppIndicatorCategory category);

    [LibraryImport("appindicator3", EntryPoint = "app_indicator_set_status")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void app_indicator_set_status(nint indicator, AppIndicatorStatus status);

    [LibraryImport("appindicator3", EntryPoint = "app_indicator_set_menu")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void app_indicator_set_menu(nint indicator, nint menu);

    [LibraryImport("appindicator3", EntryPoint = "app_indicator_set_icon",
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void app_indicator_set_icon(nint indicator, string iconName);

    [LibraryImport("appindicator3", EntryPoint = "app_indicator_set_title",
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void app_indicator_set_title(nint indicator, string title);

    // --- GTK 3 ---

    [LibraryImport("gtk-3", EntryPoint = "gtk_init")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_init(ref int argc, nint argv);

    [LibraryImport("gtk-3", EntryPoint = "gtk_main")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_main();

    [LibraryImport("gtk-3", EntryPoint = "gtk_main_quit")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_main_quit();

    [LibraryImport("gtk-3", EntryPoint = "gtk_menu_new")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint gtk_menu_new();

    [LibraryImport("gtk-3", EntryPoint = "gtk_menu_item_new_with_label",
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint gtk_menu_item_new_with_label(string label);

    [LibraryImport("gtk-3", EntryPoint = "gtk_separator_menu_item_new")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nint gtk_separator_menu_item_new();

    [LibraryImport("gtk-3", EntryPoint = "gtk_menu_shell_append")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_menu_shell_append(nint menuShell, nint child);

    [LibraryImport("gtk-3", EntryPoint = "gtk_widget_show_all")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_widget_show_all(nint widget);

    [LibraryImport("gtk-3", EntryPoint = "gtk_widget_set_sensitive")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void gtk_widget_set_sensitive(nint widget,
        [MarshalAs(UnmanagedType.Bool)] bool sensitive);

    // --- GLib ---

    [LibraryImport("glib-2.0", EntryPoint = "g_idle_add")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial uint g_idle_add(nint function, nint data);

    // --- GObject ---

    [LibraryImport("gobject-2.0", EntryPoint = "g_signal_connect_data",
        StringMarshalling = StringMarshalling.Utf8)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial nuint g_signal_connect_data(nint instance, string detailedSignal,
        nint cHandler, nint data, nint destroyData, int connectFlags);
}
