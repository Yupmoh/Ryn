using System.Text.Json;
using System.Text.Json.Serialization;
using Ryn.Ipc;

namespace Ryn.Plugins.MenuBar;

[JsonSerializable(typeof(MenuBarItem[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class MenuBarJsonContext : JsonSerializerContext { }

[RynJsonContext(typeof(MenuBarJsonContext))]
#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class MenuBarCommands
#pragma warning restore CA1812
{
    private readonly MenuBarService _service;

    public MenuBarCommands(MenuBarService service) => _service = service;

    [RynCommand("menubar.setMenu")]
    public void SetMenu(JsonElement items)
    {
        var menuItems = JsonSerializer.Deserialize(
            items.GetRawText(), MenuBarJsonContext.Default.MenuBarItemArray);
        if (menuItems is not null)
            _service.SetMenu(menuItems);
    }

    [RynCommand("menubar.reset")]
    public void Reset() => _service.Reset();
}
