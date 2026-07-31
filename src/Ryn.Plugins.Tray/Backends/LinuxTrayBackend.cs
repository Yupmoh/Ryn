using System.Diagnostics;
using System.Runtime.Versioning;
using Ryn.Plugins.Tray.Backends.DBus;
using Tmds.DBus.Protocol;

namespace Ryn.Plugins.Tray.Backends;

[SupportedOSPlatform("linux")]
// Saucer/WebKitGTK 6 runs on GTK 4. Loading the GTK 3 AppIndicator library in the same process corrupts
// GObject's process-wide type registry, so Linux tray integration must stay toolkit-free over D-Bus.
#pragma warning disable CS0067
internal sealed class LinuxTrayBackend : ITrayBackend
{
    private LinuxTrayItem? _item;
    private bool _disposed;

    public event Action? IconClicked;
    public event Action<string>? MenuItemClicked;

    public void Show(string? iconPath, string tooltip)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _item ??= LinuxTrayItem.Connect(iconPath, tooltip, () => IconClicked?.Invoke(), id => MenuItemClicked?.Invoke(id));
        _item.SetVisible(true);
    }

    public void Hide() => _item?.SetVisible(false);

    public void SetTooltip(string tooltip) => _item?.SetTooltip(tooltip);

    public void SetMenu(IReadOnlyList<TrayMenuItem> items) => _item?.SetMenu(items);

    public void ShowNotification(string title, string message)
    {
        try
        {
            var psi = new ProcessStartInfo("notify-send")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("--");
            psi.ArgumentList.Add(title);
            psi.ArgumentList.Add(message);
            Process.Start(psi)?.WaitForExit();
        }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _item?.Dispose();
        _item = null;
    }

    private sealed class LinuxTrayItem : DBusHandler,
        IStatusNotifierItemHandler, IStatusNotifierItemProperties, IDisposable
    {
        private const string ItemPathValue = "/StatusNotifierItem";
        private const string MenuPathValue = "/MenuBar";
        private static readonly ObjectPath ItemPath = new(ItemPathValue);
        private static readonly ObjectPath MenuPath = new(MenuPathValue);

        private readonly DBusConnection _connection;
        private readonly string _serviceName;
        private readonly Action _activate;
        private readonly Action<string> _menuActivated;
        private readonly object _lock = new();
        private IReadOnlyList<TrayMenuItem> _items = [];
        private uint _revision = 1;
        private bool _disposed;

        public string Category => "ApplicationStatus";
        public string Id => "ryn";
        public string Title { get; private set; }
        public string Status { get; private set; } = "Active";
        public uint WindowId => 0;
        public string IconThemePath { get; private set; }
        public string IconName { get; private set; }
        public string OverlayIconName => string.Empty;
        public string AttentionIconName => string.Empty;
        public string AttentionMovieName => string.Empty;
        public ObjectPath Menu => MenuPath;
        public bool ItemIsMenu => false;
        private LinuxTrayItem(DBusConnection connection, string serviceName, string? iconPath, string tooltip,
            Action activate, Action<string> menuActivated)
            : base(connection, ItemPathValue, handlesChildPaths: false, handleOnCapturedContext: false)
        {
            _connection = connection;
            _serviceName = serviceName;
            _activate = activate;
            _menuActivated = menuActivated;
            Title = tooltip;
            IconName = System.IO.Path.GetFileNameWithoutExtension(iconPath) ?? "application-default-icon";
            IconThemePath = System.IO.Path.GetDirectoryName(iconPath) ?? string.Empty;
        }

        public static LinuxTrayItem Connect(string? iconPath, string tooltip, Action activate, Action<string> menuActivated)
            => ConnectAsync(iconPath, tooltip, activate, menuActivated).GetAwaiter().GetResult();

        private static async Task<LinuxTrayItem> ConnectAsync(string? iconPath, string tooltip, Action activate, Action<string> menuActivated)
        {
            var connection = new DBusConnection(DBusAddress.Session!);
            try
            {
                await connection.ConnectAsync().ConfigureAwait(false);
                var serviceName = $"org.kde.StatusNotifierItem-{Environment.ProcessId}-1";
                var item = new LinuxTrayItem(connection, serviceName, iconPath, tooltip, activate, menuActivated);
                connection.AddMethodHandler(item);
                connection.AddMethodHandler(new MenuHandler(item));
                await connection.RequestNameAsync(serviceName, RequestNameOptions.ReplaceExisting).ConfigureAwait(false);
                await new StatusNotifierWatcher(connection, "org.kde.StatusNotifierWatcher", new ObjectPath("/StatusNotifierWatcher"))
                    .RegisterStatusNotifierItemAsync(serviceName).ConfigureAwait(false);
                return item;
            }
            catch
            {
                connection.Dispose();
                throw;
            }
        }

        public void SetVisible(bool visible)
        {
            Status = visible ? "Active" : "Passive";
            _connection.EmitNewStatus(ItemPath, Status);
        }

        public void SetTooltip(string tooltip)
        {
            Title = tooltip;
            _connection.EmitNewTitle(ItemPath);
            _connection.EmitNewToolTip(ItemPath);
        }

        public void SetMenu(IReadOnlyList<TrayMenuItem> items)
        {
            lock (_lock) _items = items.ToArray();
            _revision++;
            _connection.EmitLayoutUpdated(MenuPath, _revision, 0);
        }

        ValueTask IStatusNotifierItemHandler.HandleGetPropertyAsync(IStatusNotifierItemHandler.GetPropertyContext context) => context.Handle(this);
        ValueTask IStatusNotifierItemHandler.HandleGetAllPropertiesAsync(IStatusNotifierItemHandler.GetAllPropertiesContext context) => context.Handle(this);
        ValueTask IStatusNotifierItemHandler.ContextMenuAsync(int x, int y) => default;
        ValueTask IStatusNotifierItemHandler.ActivateAsync(int x, int y) { _activate(); return default; }
        ValueTask IStatusNotifierItemHandler.SecondaryActivateAsync(int x, int y) => default;
        ValueTask IStatusNotifierItemHandler.ScrollAsync(int delta, string orientation) => default;

        internal (uint Revision, (int, Dictionary<string, VariantValue>, VariantValue[]) Layout) GetLayout()
        {
            IReadOnlyList<TrayMenuItem> items;
            lock (_lock) items = _items;
            var children = new VariantValue[items.Count];
            for (var i = 0; i < items.Count; i++)
            {
                var properties = new Dict<string, VariantValue>(GetProperties(i + 1, items[i]));
                children[i] = VariantValue.Struct((VariantValue)(i + 1), properties, new Tmds.DBus.Protocol.Array<VariantValue>());
            }
            var root = new Dictionary<string, VariantValue> { ["children-display"] = "submenu" };
            return (_revision, (0, root, children));
        }

        private static Dictionary<string, VariantValue> GetProperties(int id, TrayMenuItem item)
        {
            var properties = new Dictionary<string, VariantValue>();
            if (item.Separator) properties["type"] = "separator";
            else
            {
                properties["label"] = item.Label;
                if (!item.Enabled) properties["enabled"] = false;
            }
            return properties;
        }

        internal (int, Dictionary<string, VariantValue>)[] GetGroupProperties(int[] ids)
        {
            IReadOnlyList<TrayMenuItem> items;
            lock (_lock) items = _items;
            var selected = ids.Length == 0 ? Enumerable.Range(1, items.Count) : ids;
            return selected.Where(id => id > 0 && id <= items.Count)
                .Select(id => (id, GetProperties(id, items[id - 1]))).ToArray();
        }

        internal VariantValue GetProperty(int id, string name)
        {
            IReadOnlyList<TrayMenuItem> items;
            lock (_lock) items = _items;
            if (id <= 0 || id > items.Count) return VariantValue.String(string.Empty);
            return GetProperties(id, items[id - 1]).TryGetValue(name, out var value)
                ? value : VariantValue.String(string.Empty);
        }

        internal void ActivateMenuItem(int id)
        {
            IReadOnlyList<TrayMenuItem> items;
            lock (_lock) items = _items;
            if (id <= 0 || id > items.Count) return;
            var item = items[id - 1];
            if (!item.Separator && item.Enabled) _menuActivated(item.Id);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _connection.RemoveMethodHandlers([ItemPathValue, MenuPathValue]);
            _connection.ReleaseNameAsync(_serviceName).GetAwaiter().GetResult();
            _connection.Dispose();
        }

        private sealed class MenuHandler : DBusHandler, IdbusmenuHandler, IdbusmenuProperties
        {
            private readonly LinuxTrayItem _owner;
            public MenuHandler(LinuxTrayItem owner)
                : base(owner._connection, MenuPathValue, handlesChildPaths: false, handleOnCapturedContext: false) => _owner = owner;
            public uint Version => 4;
            public string TextDirection => "ltr";
            public string Status => "normal";
            public string[] IconThemePath => [];
            ValueTask IdbusmenuHandler.HandleGetPropertyAsync(IdbusmenuHandler.GetPropertyContext context) => context.Handle(this);
            ValueTask IdbusmenuHandler.HandleGetAllPropertiesAsync(IdbusmenuHandler.GetAllPropertiesContext context) => context.Handle(this);
            ValueTask<(uint Revision, (int, Dictionary<string, VariantValue>, VariantValue[]) Layout)> IdbusmenuHandler.GetLayoutAsync(int parentId, int recursionDepth, string[] propertyNames) => new(_owner.GetLayout());
            ValueTask<(int, Dictionary<string, VariantValue>)[]> IdbusmenuHandler.GetGroupPropertiesAsync(int[] ids, string[] propertyNames) => new(_owner.GetGroupProperties(ids));
            ValueTask<VariantValue> IdbusmenuHandler.GetPropertyAsync(int id, string name) => new(_owner.GetProperty(id, name));
            ValueTask IdbusmenuHandler.EventAsync(int id, string eventId, VariantValue data, uint timestamp) { if (eventId == "clicked") _owner.ActivateMenuItem(id); return default; }
            ValueTask<int[]> IdbusmenuHandler.EventGroupAsync((int, string, VariantValue, uint)[] events) { foreach (var e in events) if (e.Item2 == "clicked") _owner.ActivateMenuItem(e.Item1); return new([]); }
            ValueTask<bool> IdbusmenuHandler.AboutToShowAsync(int id) => new(false);
            ValueTask<(int[] UpdatesNeeded, int[] IdErrors)> IdbusmenuHandler.AboutToShowGroupAsync(int[] ids) => new(([], []));
        }
    }
}
