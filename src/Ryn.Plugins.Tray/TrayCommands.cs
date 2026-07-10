using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.Tray;

// CamelCase to match the IPC convention every other plugin uses — tray.setMenu items arrive as
// {"id":…,"label":…}. Without it the required Id/Label never bind and deserialization throws.
[JsonSerializable(typeof(TrayMenuItem[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class TrayJsonContext : JsonSerializerContext { }

[RynJsonContext(typeof(TrayJsonContext))]
#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class TrayCommands
#pragma warning restore CA1812
{
    private readonly TrayService _service;

    public TrayCommands(TrayService service) => _service = service;

    [RynCommand("tray.show")]
    public void Show() => _service.Show();

    [RynCommand("tray.hide")]
    public void Hide() => _service.Hide();

    [RynCommand("tray.setTooltip")]
    public void SetTooltip(string text) => _service.SetTooltip(text);

    [RynCommand("tray.setMenu")]
    public void SetMenu(JsonElement items)
    {
        var menuItems = JsonSerializer.Deserialize(
            items.GetRawText(), TrayJsonContext.Default.TrayMenuItemArray);
        if (menuItems is not null)
            _service.SetMenu(menuItems);
    }

    [RynCommand("tray.notify")]
    public void Notify(string title, string message) => _service.ShowNotification(title, message);
}
