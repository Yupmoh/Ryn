using Ryn.Ipc;

namespace Ryn.Plugins.GlobalShortcut;

#pragma warning disable CA1812 // Instantiated by generated DI code
internal sealed class GlobalShortcutCommands
#pragma warning restore CA1812
{
    private readonly GlobalShortcutService _service;

    public GlobalShortcutCommands(GlobalShortcutService service) => _service = service;

    [RynCommand("globalShortcut.register")]
    public bool Register(string accelerator) => _service.Register(accelerator);

    [RynCommand("globalShortcut.unregister")]
    public bool Unregister(string accelerator) => _service.Unregister(accelerator);

    [RynCommand("globalShortcut.isRegistered")]
    public bool IsRegistered(string accelerator) => _service.IsRegistered(accelerator);

    [RynCommand("globalShortcut.unregisterAll")]
    public void UnregisterAll() => _service.UnregisterAll();
}
